using System.Net;
using System.Text.Json;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// A panel whose question takes more than one source to answer.
///
/// <para>
/// The case that forced it: "show the Leicester result until the day after, then count down to the next kick-off".
/// ESPN answers a team's completed match at <c>…/schedule</c> and its 45 upcoming fixtures at the SAME url with
/// <c>?fixture=true</c>, and no combination of parameters returns both. One loader per kind meant that panel could
/// only ever show half of what was asked for — and the half it showed was a finished game, for ever.
/// </para>
/// <para>
/// The design is in the order of operations: every feed contributes the fields it maps, the pieces merge, and the
/// contract is judged ONCE at the end. Per-feed contract checks would fail every time, since no single feed
/// carries the whole model. And a feed that dies is skipped rather than fatal, so a second source cannot double a
/// panel's fragility — which it would otherwise, and then nobody should ever add one.
/// </para>
/// </summary>
public class SeveralFeedsTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly List<IDisposable> _open = new();

    /// <summary>A server that answers one canned body, or refuses.</summary>
    private sealed class Source : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Url { get; }
        public int Calls { get; private set; }

        public Source(string? body, int status = 200)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); } catch { return; }
                    Calls++;
                    ctx.Response.StatusCode = status;
                    if (body is not null)
                        await ctx.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(body));
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); } catch { /* already gone */ }
        }
    }

    private Source Answering(string? body, int status = 200)
    {
        var s = new Source(body, status);
        _open.Add(s);
        return s;
    }

    // The two halves of the fixture question, as the real endpoint splits them.
    private const string Results = "{\"events\":[{\"date\":\"2026-08-15T11:30Z\",\"competitions\":[{\"competitors\":" +
        "[{\"team\":{\"displayName\":\"Notts County\"},\"score\":{\"displayValue\":\"1\"}}," +
        "{\"team\":{\"displayName\":\"Leicester City\"},\"score\":{\"displayValue\":\"1\"}}]}]}]}";

    private const string Fixtures = "{\"events\":[{\"date\":\"2026-08-22T14:00Z\",\"competitions\":[{\"competitors\":" +
        "[{\"team\":{\"displayName\":\"Leicester City\"}},{\"team\":{\"displayName\":\"Burton Albion\"}}]}]}]}";

    private static WidgetKind Fixture(string resultsUrl, string fixturesUrl, bool nextRequired = true)
    {
        var kind = new WidgetKind
        {
            Name = "football-match-tracker",
            Title = "Football match",
            Description = "a club's last result and next fixture",
            Model = new()
            {
                new WidgetField { Name = "lastPlayed", Type = "string", Description = "when the last game was" },
                new WidgetField { Name = "lastTeams", Type = "list", Description = "who played" },
                new WidgetField { Name = "lastScore", Type = "list", Description = "the score" },
                new WidgetField { Name = "nextKickoff", Type = "string", Description = "when the next one is", Required = nextRequired },
                new WidgetField { Name = "nextTeams", Type = "list", Description = "who is next", Required = nextRequired },
            },
            Loader = new WidgetLoader { Mode = LoaderModes.Http, Url = resultsUrl },
            More =
            {
                new WidgetLoader { Mode = LoaderModes.Http, Url = fixturesUrl },
            },
            Code = "return <Row label={data.lastTeams?.[0]} value={data.nextKickoff} />",
            Refresh = "every 5 minutes",
        };
        kind.Loader.Map["lastPlayed"] = "events.0.date";
        kind.Loader.Map["lastTeams"] = "events.0.competitions.0.competitors.*.team.displayName";
        kind.Loader.Map["lastScore"] = "events.0.competitions.0.competitors.*.score.displayValue";
        kind.More[0].Map["nextKickoff"] = "events.0.date";
        kind.More[0].Map["nextTeams"] = "events.0.competitions.0.competitors.*.team.displayName";
        return kind;
    }

    private async Task<LoadResult> Load(WidgetKind kind)
    {
        var http = BrowserLike.Client(TimeSpan.FromSeconds(10));
        _open.Add(http);
        return await new WidgetDataLoader(http, (_, _) => Task.FromResult<string?>(null))
            .LoadAsync(kind, new Dictionary<string, string>(), CancellationToken.None);
    }

    private static JsonElement Read(LoadResult r) => JsonDocument.Parse(r.Json!).RootElement;

    [Fact]
    public async Task Two_feeds_answer_one_question_between_them()
    {
        var results = Answering(Results);
        var fixtures = Answering(Fixtures);

        var loaded = await Load(Fixture(results.Url, fixtures.Url));

        Assert.True(loaded.Worked, loaded.Error);
        var data = Read(loaded);
        // The result, from the first feed…
        Assert.Equal("2026-08-15T11:30Z", data.GetProperty("lastPlayed").GetString());
        Assert.Equal("1", data.GetProperty("lastScore")[0].GetString());
        // …and the next fixture, from the second, in one object the component reads without knowing there were two.
        Assert.Equal("2026-08-22T14:00Z", data.GetProperty("nextKickoff").GetString());
        Assert.Equal("Burton Albion", data.GetProperty("nextTeams")[1].GetString());
    }

    [Fact]
    public async Task A_feed_that_dies_only_costs_what_it_was_carrying()
    {
        // The whole bargain. The fixtures feed is down; everything it supplied was optional; the panel still shows
        // the result it does have rather than going dark over the half it lost.
        var results = Answering(Results);
        var fixtures = Answering(null, status: 503);

        var loaded = await Load(Fixture(results.Url, fixtures.Url, nextRequired: false));

        Assert.True(loaded.Worked, loaded.Error);
        var data = Read(loaded);
        Assert.Equal("2026-08-15T11:30Z", data.GetProperty("lastPlayed").GetString());
        Assert.False(data.TryGetProperty("nextKickoff", out _));
    }

    [Fact]
    public async Task A_feed_that_dies_carrying_something_required_fails_and_says_which()
    {
        var results = Answering(Results);
        var fixtures = Answering(null, status: 503);

        var loaded = await Load(Fixture(results.Url, fixtures.Url, nextRequired: true));

        Assert.False(loaded.Worked);
        Assert.Contains("nextKickoff", loaded.Error);
        // Named, because "the source didn't carry it" sends you looking at the wrong half of a two-feed panel.
        Assert.Contains("503", loaded.Error);
    }

    [Fact]
    public async Task Every_feed_is_actually_read()
    {
        var results = Answering(Results);
        var fixtures = Answering(Fixtures);

        await Load(Fixture(results.Url, fixtures.Url));

        Assert.Equal(1, results.Calls);
        Assert.Equal(1, fixtures.Calls);
    }

    [Fact]
    public async Task The_first_feed_to_supply_a_field_keeps_it()
    {
        // Both feeds map lastPlayed. The primary is the one the kind was built around, so a later feed quietly
        // overwriting it would be the hardest kind of fault to see: right shape, wrong answer, nothing reported.
        var results = Answering(Results);
        var fixtures = Answering(Fixtures);
        var kind = Fixture(results.Url, fixtures.Url, nextRequired: false);
        kind.More[0].Map["lastPlayed"] = "events.0.date";

        var loaded = await Load(kind);

        Assert.True(loaded.Worked, loaded.Error);
        Assert.Equal("2026-08-15T11:30Z", Read(loaded).GetProperty("lastPlayed").GetString());
    }

    [Fact]
    public async Task A_panel_whose_every_feed_is_down_says_so_once()
    {
        var results = Answering(null, status: 500);
        var fixtures = Answering(null, status: 503);

        var loaded = await Load(Fixture(results.Url, fixtures.Url));

        Assert.False(loaded.Worked);
        Assert.Contains("feeds", loaded.Error);
    }

    [Fact]
    public async Task One_feed_still_behaves_exactly_as_it_always_did()
    {
        // The path almost every panel takes. It must not have moved: same shaping, same contract, same errors.
        var only = Answering("{\"temperature\":22}");
        var kind = new WidgetKind
        {
            Name = "city-weather",
            Title = "City weather",
            Description = "the temperature",
            Model = new() { new WidgetField { Name = "temperature", Type = "number", Description = "degrees" } },
            Loader = new WidgetLoader { Mode = LoaderModes.Http, Url = only.Url },
            Code = "return <Stat label=\"Now\" value={data.temperature} />",
            Refresh = "every 2 hours",
        };

        var loaded = await Load(kind);

        Assert.True(loaded.Worked, loaded.Error);
        Assert.Equal(22, Read(loaded).GetProperty("temperature").GetInt32());
    }

    public void Dispose()
    {
        foreach (var d in _open) { try { d.Dispose(); } catch { /* closing is best-effort */ } }
    }
}
