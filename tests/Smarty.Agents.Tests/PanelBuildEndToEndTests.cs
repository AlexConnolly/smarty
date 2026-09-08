using System.Net;
using System.Text;
using System.Text.Json;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// A whole panel build, run through the real orchestrator.
///
/// <para>
/// Everything else about the states is tested against fakes, which proves the machine and proves nothing about
/// whether it is plugged in. This runs the actual thing: the chat voice's build call, the coordinator, a child
/// worker per state with the tools the trim actually leaves it, the real tools writing to a real store, and the
/// panel ending up live with a kind behind it. The only stand-in is the model itself — an Ollama gateway on a
/// loopback port that answers according to which tools it was offered, which is also how it proves each state was
/// handed its own.
/// </para>
/// <para>
/// The second case is the Leicester build, end to end: a source that will not answer. The panel must come out of
/// it still showing the design that was drawn for it, with the reason underneath — not the empty box the real one
/// left behind.
/// </para>
/// </summary>
public class PanelBuildEndToEndTests : IDisposable
{
    private readonly List<string> _paths = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── a model that answers by which tools it can see ──────────────────────────────────────────────────

    /// <summary>
    /// An Ollama gateway that plays a build. Each turn it looks at the tools it has been offered, works out which
    /// state it is being asked to run, and calls that state's tool.
    /// </summary>
    private sealed class Gateway : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<IReadOnlyList<string>, (string Tool, object Args)?> _play;

        public string BaseUrl { get; }
        public List<IReadOnlyList<string>> Offered { get; } = new();

        public Gateway(Func<IReadOnlyList<string>, (string, object)?> play)
        {
            _play = play;
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"{BaseUrl}/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                try { await AnswerAsync(ctx); }
                catch { /* the test asserts on the outcome, not on the gateway */ }
            }
        }

        private async Task AnswerAsync(HttpListenerContext ctx)
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();

            var tools = new List<string>();
            using (var doc = JsonDocument.Parse(body))
                if (doc.RootElement.TryGetProperty("tools", out var offered))
                    foreach (var t in offered.EnumerateArray())
                        if (t.TryGetProperty("function", out var fn) && fn.TryGetProperty("name", out var n))
                            tools.Add(n.GetString() ?? "");
            lock (Offered) Offered.Add(tools);

            var move = _play(tools);
            var message = move is { } m
                ? new
                {
                    role = "assistant",
                    content = "",
                    tool_calls = new object[] { new { function = new { name = m.Tool, arguments = m.Args } } },
                }
                : (object)new { role = "assistant", content = "Done." };

            var line = JsonSerializer.Serialize(new { message, done = true, done_reason = "stop" });
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            ctx.Response.ContentType = "application/x-ndjson";
            ctx.Response.StatusCode = 200;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); } catch { /* already gone */ }
        }
    }

    // ── the build ───────────────────────────────────────────────────────────────────────────────────────

    // Reads both `data` and `params`, because the design step is shown a sample (so the real tool judges it as a
    // server-fed component, which must use `data`) and the publish is client-mode (which must use `params`). Both
    // of those refusals are the live tool's, and being caught by them is the point of running the live tool.
    private const string Code =
        "if (!data) return <Meta>Loading…</Meta>;\n" +
        "return <Panel title={params.club} icon=\"star\"><Row label={data.homeTeam} value={data.score} /></Panel>;";

    private static object Shows(string id) => new
    {
        id,
        when_empty = "A countdown to the next fixture, with both crests.",
        fields = new[]
        {
            new { name = "homeTeam", type = "string", description = "the home side", example = "Leicester City" },
            new { name = "score", type = "string", description = "the score", example = "2–1" },
        },
    };

    private static object Design(string id) => new
    {
        id,
        code = Code,
        sample = new { homeTeam = "Leicester City", score = "2–1" },
    };

    private static object Source(string id, string url) => new
    {
        id,
        subject = "Leicester City FC",
        identifier = "375",
        expect = "Leicester",
        url,
        mode = "client",
        alternatives = new[] { "the BBC fixtures page" },
    };

    private static object Proof(string id, string url, string body) => new { id, url, status = 200, body };

    private static object Publish(string id) => new
    {
        id,
        kind = "football-club-match",
        title = "Football club match",
        description = "the latest result for a football club, or a countdown to its next fixture",
        loader_mode = "client",
        code = Code,
        refresh = "every 30 minutes",
        @params = new[]
        {
            new { name = "club", description = "the club to follow", example = "Leicester City", value = "Leicester City" },
        },
    };

    [Fact]
    public async Task A_build_that_finds_its_source_ends_with_a_live_panel()
    {
        var widgets = NewStore();
        var panel = widgets.Reserve("Leicester City match", WidgetSizes.Wide, 80, null, null, proposed: false);

        using var gateway = new Gateway(tools =>
        {
            // Which state this is, decided by the one tool that finishes it — the same fact the trim enforces.
            if (tools.Contains("panel_shows")) return ("panel_shows", Shows(panel.Id));
            if (tools.Contains("widget_design")) return ("widget_design", Design(panel.Id));
            if (tools.Contains("panel_source")) return ("panel_source", Source(panel.Id, "https://api.football-data.org/v4/teams/375/matches"));
            if (tools.Contains("panel_proof"))
                return ("panel_proof", Proof(panel.Id, "https://api.football-data.org/v4/teams/375/matches",
                    "{\"matches\":[{\"homeTeam\":{\"name\":\"Leicester City FC\"},\"score\":\"2-1\"}]}"));
            if (tools.Contains("widget_publish")) return ("widget_publish", Publish(panel.Id));
            return null;
        });

        var built = await BuildAsync(gateway, widgets, panel, "The latest Leicester City result.");

        Assert.All(built.Log, a => Assert.True(a.Ok, $"{a.Step}: {a.Reason}"));

        var live = widgets.Get(panel.Id)!;
        Assert.Equal(WidgetStatus.Live, live.Status);
        Assert.Equal("football-club-match", live.Kind);
        Assert.Null(live.Error);

        // Every state ran, once, in order — the record on the panel, not a log anywhere else.
        Assert.Equal(new[]
        {
            PanelStep.Agree, PanelStep.Design, PanelStep.Research, PanelStep.Prove, PanelStep.Bind, PanelStep.Verify,
        }, built.Log.Select(a => a.Step));
        Assert.All(built.Log, a => Assert.True(a.Ok, $"{a.Step}: {a.Reason}"));

        // And what each state produced is on the record, not just in a transcript.
        Assert.Equal(new[] { "homeTeam", "score" }, built.Contract!.Fields.Select(f => f.Name));
        Assert.Equal("Leicester City FC", built.Source!.Subject);
        Assert.True(built.Proof!.Subject);

        // The tools really were scoped: no state was ever offered another state's closing call.
        foreach (var offered in gateway.Offered)
        {
            var closers = offered.Count(t => t is "panel_shows" or "widget_design" or "panel_source"
                or "panel_proof" or "widget_publish");
            Assert.True(closers <= 1, $"a state was offered {closers} states' tools: {string.Join(", ", offered)}");
        }
    }

    [Fact]
    public async Task A_build_whose_source_never_answers_keeps_the_design_on_the_page()
    {
        // The Leicester build, from the top, through the real orchestrator. ESPN answers about Reading; nothing
        // else can be found. The panel must not end up as an empty red box.
        var widgets = NewStore();
        var panel = widgets.Reserve("Leicester City match", WidgetSizes.Wide, 80, null, null, proposed: false);
        int sources = 0;

        using var gateway = new Gateway(tools =>
        {
            if (tools.Contains("panel_shows")) return ("panel_shows", Shows(panel.Id));
            if (tools.Contains("widget_design")) return ("widget_design", Design(panel.Id));
            if (tools.Contains("panel_source"))
                return ("panel_source", Source(panel.Id, $"https://site.api.espn.com/eng.{++sources}/teams/338/schedule"));
            if (tools.Contains("panel_proof"))
                // 200 OK, complete, valid — and about Reading.
                return ("panel_proof", Proof(panel.Id, "https://site.api.espn.com/eng.1/teams/338/schedule",
                    "{\"team\":{\"id\":\"338\",\"displayName\":\"Reading\"},\"events\":[]}"));
            return null;
        });

        var built = await BuildAsync(gateway, widgets, panel, "The latest Leicester City result.");

        var stopped = widgets.Get(panel.Id)!;
        Assert.Equal(PanelStep.Blocked, built.Step);
        Assert.Null(stopped.Kind);

        // The whole point. The design the user watched being drawn is still there, with the reason over it and
        // nothing spinning.
        Assert.Equal(Code, stopped.Design);
        Assert.NotNull(stopped.Sample);
        Assert.Null(stopped.Stage);
        Assert.False(string.IsNullOrWhiteSpace(stopped.Error));

        // And it stopped because it ran out of sources, not because it ran out of patience with one.
        Assert.True(built.Struck.Count >= 1);
        Assert.True(built.Log.Count(a => a.Step == PanelStep.Prove) <= PanelBudget.Sources,
            $"proved {built.Log.Count(a => a.Step == PanelStep.Prove)} times against sources that never answered");
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────────────

    private async Task<PanelBuildRecord> BuildAsync(Gateway gateway, WidgetStore widgets, Widget panel, string shows)
    {
        var orchestrator = new Orchestrator(
            "stub-model",
            gateway.BaseUrl,
            () => "WORKER PROMPT",
            Json,
            new TrainingLog(Temp("training"), Json),
            SilentBrain.Over(Temp("brain")),
            new ProjectStore(Temp("projects") + ".json", Json),
            new ProjectRunStore(Temp("runs") + ".json", Json),
            new OrchestratorOptions
            {
                Widgets = widgets,
                WidgetKinds = new WidgetLibrary(Temp("kinds") + ".json", Json),
                TurnTimeout = TimeSpan.FromSeconds(20),
                WorkspaceRoot = Temp("work"),
            });

        var session = new Session($"panel-{panel.Id}");
        var taskId = await orchestrator.BuildWidgetAsync(session, panel, shows);

        // The build runs in the background, as it does in the app.
        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until)
        {
            if (session.Tasks.TryGetValue(taskId, out var task) && task.Status is not ("running" or "waiting")) break;
            await Task.Delay(100);
        }

        var record = widgets.BuildOf(panel.Id);
        Assert.NotNull(record);
        Assert.True(record!.Step is PanelStep.Done or PanelStep.Blocked,
            $"the build was still at {record.Step} after a minute");
        return record;
    }

    private WidgetStore NewStore() => new(Temp("widgets") + ".json", Json);

    private string Temp(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}");
        _paths.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _paths)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch { /* a temp file that will not go is not a failed test */ }
        }
    }
}
