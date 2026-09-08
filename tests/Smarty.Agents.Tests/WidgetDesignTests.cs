using System.Text.Json;
using Smarty.Agents;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Showing a panel while it is still being made.
///
/// <para>
/// A build is minutes of work and for all of them the panel was a hairline and the word "Building" — accurate, and
/// worth nothing to the person waiting: they cannot see what is coming, cannot tell a build that is going well from
/// one that is lost, and get no chance to say "not like that" until it is finished. So the component and a few
/// invented values go up FIRST, dimmed, with what is happening written over them.
/// </para>
/// <para>
/// The second effect is the one that pays for it. A component that cannot render is now caught in the first minute,
/// against data the builder made up, instead of in the last minute against a source that took eight minutes to find.
/// </para>
/// <para>
/// And the whole thing turns on one rule: a design is never allowed to look finished. Invented numbers presented as
/// the answer would be worse than the hairline ever was, so every way a build can end — published, failed, restarted
/// — takes the design down.
/// </para>
/// </summary>
public class WidgetDesignTests : IDisposable
{
    private readonly List<string> _paths = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private WidgetStore NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"widgets-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        return new WidgetStore(path, Json);
    }

    private const string Code =
        "if (!data) return <Meta>Loading…</Meta>\nreturn <Stat label=\"Temperature\" value={data.temperature} />";

    private const string Sample = "{\"temperature\":22,\"where\":\"Palma\"}";

    private WidgetLibrary NewLibrary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kinds-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        return new WidgetLibrary(path, Json);
    }

    private static async Task<ToolOutput> Call(AgentTool tool, object args) =>
        await tool.InvokeAsync(
            new ToolCallArguments(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement),
            CancellationToken.None);

    /// <summary>A panel mid-build with its design already up — where every test below starts.</summary>
    private Widget Designed(WidgetStore store, string title = "Palma weather")
    {
        var w = store.Reserve(title, WidgetSizes.Tall, 70, null, null, proposed: false);
        store.Building(w.Id, "task-1");
        store.Reached(w.Id, Stages.Designing);
        Assert.True(store.Designed(w.Id, Code, Sample));
        return w;
    }

    private static WidgetKind Kind(string mode = LoaderModes.Http) => new()
    {
        Name = "city-weather",
        Title = "City weather",
        Description = "the temperature in a city",
        Model = new() { new WidgetField { Name = "temperature", Type = "number", Description = "degrees" } },
        Loader = new WidgetLoader
        {
            Mode = mode,
            Url = mode == LoaderModes.Client ? null : "https://api.example.com/weather",
        },
        Code = Code,
        Refresh = "every 2 hours",
    };

    // ── while it is being built ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_design_is_shown_but_never_called_finished()
    {
        var store = NewStore();
        var w = store.Get(Designed(store).Id)!;

        Assert.Equal(Code, w.Design);
        Assert.Equal(Sample, w.Sample);
        Assert.Equal(Stages.Feed, w.Stage);

        // The two things that would make it a lie: still building, and no working component of its own.
        Assert.Equal(WidgetStatus.Building, w.Status);
        Assert.Null(w.OwnCode);
    }

    [Fact]
    public void A_build_says_what_it_is_doing_before_it_has_anything_to_show()
    {
        // The first stage arrives with the task, not with the design — a build that is still thinking should say so
        // rather than showing nothing for its first minute.
        var store = NewStore();
        var w = store.Reserve("Palma weather", WidgetSizes.Tall, 70, null, null, proposed: false);
        store.Building(w.Id, "task-1");
        store.Reached(w.Id, Stages.Designing);

        Assert.Equal(Stages.Designing, store.Get(w.Id)!.Stage);
        Assert.Null(store.Get(w.Id)!.Design);
    }

    [Fact]
    public void A_fresh_attempt_does_not_inherit_the_last_one_s_design()
    {
        // A rebuild starts from the ask, so whatever the previous attempt drew is not this attempt's promise.
        var store = NewStore();
        var w = Designed(store);

        store.Building(w.Id, "task-2");
        var again = store.Get(w.Id)!;

        Assert.Null(again.Design);
        Assert.Null(again.Sample);
        Assert.Null(again.Stage);
    }

    [Fact]
    public void A_restart_takes_the_design_down_with_the_build()
    {
        // The run that drew it died with the process. Left up, the panel would sit dimmed under a spinner looking
        // like a build in progress, and there is no build — which is the one impression this must never give.
        var store = NewStore();
        var w = Designed(store);

        var after = new WidgetStore(_paths[^1], Json);
        var owed = after.Get(w.Id)!;

        Assert.Equal(WidgetStatus.Failed, owed.Status);
        Assert.Null(owed.Design);
        Assert.Null(owed.Stage);
    }

    // ── publishing, and the gap before it works ─────────────────────────────────────────────────────────

    [Fact]
    public void Publishing_keeps_the_design_up_and_says_it_is_being_tested()
    {
        // Published is not the same as working. For the few seconds between attaching a kind and its first load
        // landing, the alternative to the design is the component's own "Loading…".
        var store = NewStore();
        var w = Designed(store);

        Assert.True(store.Attach(w.Id, Kind(), new Dictionary<string, string>()));
        var live = store.Get(w.Id)!;

        Assert.Equal(Stages.Testing, live.Stage);
        Assert.Equal(Code, live.Design);
        Assert.Equal(Sample, live.Sample);
    }

    [Fact]
    public void A_panel_still_showing_its_design_is_due_a_load_like_any_other()
    {
        // The regression that would strand it: waiting for the first load is only honest if the first load is
        // actually coming, and both the refresher and the home page only ever look at live panels.
        var store = NewStore();
        var w = Designed(store);
        store.Attach(w.Id, Kind(), new Dictionary<string, string>());

        Assert.Equal(WidgetStatus.Live, store.Get(w.Id)!.Status);
        Assert.Contains(store.Due(DateTimeOffset.UtcNow.AddMinutes(1)), p => p.Id == w.Id);
    }

    [Fact]
    public void The_first_load_takes_the_design_down()
    {
        var store = NewStore();
        var w = Designed(store);
        store.Attach(w.Id, Kind(), new Dictionary<string, string>());

        store.Loaded(w.Id, "{\"temperature\":31}", null);
        var live = store.Get(w.Id)!;

        Assert.Null(live.Design);
        Assert.Null(live.Sample);
        Assert.Null(live.Stage);
        Assert.Equal("{\"temperature\":31}", live.Data);
    }

    [Fact]
    public void A_first_load_that_fails_takes_it_down_too()
    {
        // Otherwise the panel sits behind invented numbers reading as the answer, with the real fault hidden behind
        // them — the one outcome worse than the hairline it replaced.
        var store = NewStore();
        var w = Designed(store);
        store.Attach(w.Id, Kind(), new Dictionary<string, string>());

        store.Loaded(w.Id, null, "the source returned 503");
        var live = store.Get(w.Id)!;

        Assert.Null(live.Design);
        Assert.Null(live.Stage);
        Assert.Equal("the source returned 503", live.Error);
    }

    [Fact]
    public void A_panel_that_loads_itself_has_nothing_to_wait_for()
    {
        // Nothing on the server ever loads a client-mode panel, so there is no first load to end the pretence — the
        // real component has to go up at the moment it is published or the design would never come down.
        var store = NewStore();
        var w = Designed(store);

        store.Attach(w.Id, Kind(LoaderModes.Client), new Dictionary<string, string>());
        var live = store.Get(w.Id)!;

        Assert.Null(live.Design);
        Assert.Null(live.Sample);
        Assert.Null(live.Stage);
        Assert.Equal(WidgetStatus.Live, live.Status);
    }

    [Fact]
    public void A_build_that_gave_up_keeps_its_design_and_shows_the_reason_over_it()
    {
        // Reversed deliberately, and the Leicester build is why. It agreed what the panel would show, drew it, put
        // it on the home page — and then couldn't find a source that carried the fixtures. Taking the design down
        // meant the user lost the one part that had worked and was left with two lines of red text about ESPN.
        //
        // It is not pretending to be finished: the stage goes, so nothing spins over it, and the reason sits on top.
        // The panel says "this is what you asked for, and here is what it still needs".
        var store = NewStore();
        var w = Designed(store);

        store.Failed(w.Id, "no source carries this");
        var stopped = store.Get(w.Id)!;

        Assert.Equal(Code, stopped.Design);
        Assert.Equal(Sample, stopped.Sample);
        Assert.Null(stopped.Stage);
        Assert.Equal(WidgetStatus.Failed, stopped.Status);
        Assert.Equal("no source carries this", stopped.Error);
    }

    [Fact]
    public void A_rebuild_that_failed_shows_the_panel_that_still_works_not_the_half_built_one()
    {
        // The exception to the rule above. There is a working panel underneath, and it is the better thing to look
        // at — a design over a live panel would be a step backwards for the sake of consistency.
        var store = NewStore();
        var w = Designed(store);
        Assert.True(store.Attach(w.Id, Kind(), new Dictionary<string, string>()));
        Assert.True(store.Designed(w.Id, Code, Sample));

        store.Failed(w.Id, "the source stopped answering");
        var live = store.Get(w.Id)!;

        Assert.Null(live.Design);
        Assert.Null(live.Sample);
        Assert.Equal(WidgetStatus.Live, live.Status);
        Assert.Equal("the source stopped answering", live.Error);
    }

    // ── a name the component can actually read ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_field_name_with_a_dot_in_it_is_refused()
    {
        // The worst kind of fault, because everything works. A weather panel declared "current.temp", the loader
        // fetched, the shaping succeeded, the panel went live and every check reported it healthy — and it showed
        // "—°", because the component's data.current.temp looks INSIDE current for a temp that is not there. Nothing
        // threw, nothing failed, and nothing anywhere said why.
        var store = NewStore();
        var library = NewLibrary();
        var panel = store.Reserve("Palma weather", WidgetSizes.Tall, 70, null, null, proposed: false);

        var result = await Call(WidgetLibraryTools.PublishTool(store, library), new
        {
            id = panel.Id,
            kind = "city-weather",
            title = "City weather",
            description = "the temperature in a city",
            model = new[]
            {
                new { name = "current.temp", type = "number", description = "degrees now" },
                new { name = "today high", type = "number", description = "the high" },
            },
            loader_mode = LoaderModes.Http,
            url = "https://api.example.com/weather",
            map = new[] { new { field = "current.temp", path = "current.temperature_2m" } },
            code = Code,
            refresh = "every 2 hours",
        });

        Assert.True(result.IsError);
        Assert.Contains("current.temp", result.Content);
        Assert.Contains("today high", result.Content);
        Assert.Empty(library.All());
    }

    [Fact]
    public async Task A_flat_name_is_fine()
    {
        var store = NewStore();
        var library = NewLibrary();
        var panel = store.Reserve("Palma weather", WidgetSizes.Tall, 70, null, null, proposed: false);

        var result = await Call(WidgetLibraryTools.PublishTool(store, library), new
        {
            id = panel.Id,
            kind = "city-weather",
            title = "City weather",
            description = "the temperature in a city",
            model = new[] { new { name = "temperature", type = "number", description = "degrees now" } },
            loader_mode = LoaderModes.Http,
            url = "https://api.example.com/weather",
            map = new[] { new { field = "temperature", path = "current.temperature_2m" } },
            code = Code,
            refresh = "every 2 hours",
        });

        Assert.False(result.IsError);
        Assert.Single(library.All());
    }

    // ── reaching the worker at all ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_panel_tool_survives_the_trim_a_build_gets()
    {
        // How this feature failed its first live run, in full: the tool was written, registered, tested and briefed,
        // and a named allow-list of "what a panel build needs" quietly dropped it before the worker saw its tools. The
        // brief told it to design first and it had nothing to design with. Nothing errored — the panel appeared the
        // old way, and the only evidence anywhere was a tool that was never called.
        var borrowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "read_file", "chrome_navigate" };

        Assert.True(PanelBuild.Keep("widget_design", borrowed));
        Assert.True(PanelBuild.Keep("widget_publish", borrowed));
        Assert.True(PanelBuild.Keep("widget_fill", borrowed));
        Assert.True(PanelBuild.Keep("read_file", borrowed));

        // And it is still a trim: the point of it is that a build is not handed the other thirty.
        Assert.False(PanelBuild.Keep("download_file", borrowed));
        Assert.False(PanelBuild.Keep("build_presentation", borrowed));
    }

    // ── the tool ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Showing_a_design_does_not_put_the_panel_live()
    {
        var store = NewStore();
        var w = store.Reserve("Palma weather", WidgetSizes.Tall, 70, null, null, proposed: false);
        store.Building(w.Id, "task-1");

        var result = await Call(WidgetLibraryTools.DesignTool(store),
            new { id = w.Id, code = Code, sample = JsonDocument.Parse(Sample).RootElement });

        Assert.False(result.IsError);
        Assert.Equal(WidgetStatus.Building, store.Get(w.Id)!.Status);
        Assert.Null(store.Get(w.Id)!.Kind);
        // And it says so, because a run that thinks it has finished stops here and the panel never gets a feed.
        Assert.Contains("NOT finished", result.Content);
    }

    [Fact]
    public async Task A_component_that_could_not_render_is_refused_before_a_source_is_found()
    {
        // The whole reason designing first is worth a tool call: this is the same refusal publishing would give,
        // arriving before the expensive half of the job rather than after it.
        var store = NewStore();
        var w = store.Reserve("Palma weather", WidgetSizes.Tall, 70, null, null, proposed: false);
        store.Building(w.Id, "task-1");

        var result = await Call(WidgetLibraryTools.DesignTool(store), new
        {
            id = w.Id,
            code = "return <div className=\"text-fuchsia-500\">{data.temperature}</div>",
            sample = JsonDocument.Parse(Sample).RootElement,
        });

        Assert.True(result.IsError);
        Assert.Contains("palette", result.Content);
        Assert.Null(store.Get(w.Id)!.Design);
    }

    [Fact]
    public async Task A_design_with_nothing_in_it_is_refused()
    {
        // A component rendered against no data shows an empty box, which is what the user was already looking at.
        var store = NewStore();
        var w = store.Reserve("Palma weather", WidgetSizes.Tall, 70, null, null, proposed: false);
        store.Building(w.Id, "task-1");

        var result = await Call(WidgetLibraryTools.DesignTool(store), new { id = w.Id, code = Code });

        Assert.True(result.IsError);
        Assert.Null(store.Get(w.Id)!.Design);
    }

    [Fact]
    public async Task A_panel_that_fetches_for_itself_designs_without_a_sample()
    {
        // In client mode there is no `data` at all — the component reads its parameters and goes to the source
        // itself, so insisting on made-up data would refuse the one kind of panel that can preview for real.
        var store = NewStore();
        var w = store.Reserve("Front door", WidgetSizes.Wide, 70, null, null, proposed: false);
        store.Building(w.Id, "task-1");

        var result = await Call(WidgetLibraryTools.DesignTool(store), new
        {
            id = w.Id,
            code = "return <img className=\"h-full w-full object-cover\" src={params.streamUrl} />",
            mode = LoaderModes.Client,
        });

        Assert.False(result.IsError);
        Assert.NotNull(store.Get(w.Id)!.Design);
        Assert.Null(store.Get(w.Id)!.Sample);
    }

    [Fact]
    public async Task A_design_for_a_panel_that_is_gone_is_a_dead_end()
    {
        var store = NewStore();
        var result = await Call(WidgetLibraryTools.DesignTool(store),
            new { id = "nope", code = Code, sample = JsonDocument.Parse(Sample).RootElement });

        Assert.True(result.IsError);
        Assert.False(result.CanRetry);
    }

    public void Dispose()
    {
        foreach (var p in _paths)
            if (File.Exists(p)) File.Delete(p);
    }
}
