using System.Text.Json;
using Smarty.Agents;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// A panel is not published until its loader has actually RUN.
///
/// <para>
/// Everything here comes from one night's damage, and the damage was not a model behaving badly — it was four checks
/// that all validated a DESCRIPTION of data and never the data. A rename republished a working panel with an internal
/// feed called "source:any", which nothing had ever asked to exist; a weather panel was published against a
/// browser-mode loader that returned nothing at all; the only check after publishing was a photograph, which passed a
/// panel showing empty circles; and both panels' repair tasks ran in the same workspace because they had been asked
/// for in the same chat.
/// </para>
/// <para>
/// The feeds side of this system already got this right: <c>feed_publish</c> reads its source and refuses one that
/// produces nothing, because "a guess on a timer is a watcher that waits politely for ever". The same sentence is
/// true of a panel, and these are the tests that make it true of a panel.
/// </para>
/// </summary>
public class PanelProvenBeforePublishTests : IDisposable
{
    private readonly List<string> _paths = new();

    private WidgetStore NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"widgets-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        return new WidgetStore(path, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private WidgetLibrary NewLibrary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kinds-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        return new WidgetLibrary(path, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static async Task<ToolOutput> Call(AgentTool tool, object args) =>
        await tool.InvokeAsync(
            new ToolCallArguments(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement),
            CancellationToken.None);

    private const string VacuumCode =
        "if (!data) return <Meta>Loading…</Meta>\n" +
        "return <Panel title={data.name || 'Vacuum'}><Stat label=\"battery\" value={data.battery} /></Panel>";

    /// <summary>The vacuum panel's real shape, which is what the rename was published over.</summary>
    private static object VacuumArgs(string id, object? overrides = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["kind"] = "vacuum-control",
            ["title"] = "Vacuum control",
            ["description"] = "a robot vacuum's battery, what it is doing and its controls",
            ["params"] = Array.Empty<object>(),
            ["model"] = new[]
            {
                new { name = "battery", type = "number", description = "charge, 0-100" },
                new { name = "name", type = "string", description = "what the vacuum is called" },
            },
            ["loader_mode"] = "internal",
            ["internal"] = "plugin:roborock/status",
            ["map"] = new[]
            {
                new { field = "battery", path = "battery" },
                new { field = "name", path = "name" },
            },
            ["code"] = VacuumCode,
            ["refresh"] = "every 5 minutes",
        };
        if (overrides is not null)
            foreach (var p in overrides.GetType().GetProperties())
                d[p.Name] = p.GetValue(overrides);
        return d;
    }

    /// <summary>A prover that hands back whatever the source is pretending to be.</summary>
    private static Func<WidgetKind, IReadOnlyDictionary<string, string>, CancellationToken, Task<LoadResult>>
        Proves(string? json, string? error = null) => (_, _, _) => Task.FromResult(new LoadResult(json, error));

    // ── Cause A: the name of an internal feed was never checked against anything ──────────────────────────

    [Fact]
    public async Task An_internal_feed_naming_something_that_does_not_exist_is_refused()
    {
        var store = NewStore();
        var library = NewLibrary();
        var panel = store.Reserve("Vacuum control", WidgetSizes.Wide, 70, null, null, proposed: false);

        // No granted folders at all — which is the real state of the machine this happened on.
        var tool = WidgetLibraryTools.PublishTool(store, library,
            internalFault: name => WidgetInternals.Fault(name, sourceExists: _ => false));

        var result = await Call(tool, VacuumArgs(panel.Id, new { @internal = "source:any" }));

        Assert.True(result.IsError);
        Assert.Contains("any", result.Content);
        Assert.Empty(library.All());
    }

    [Fact]
    public async Task A_plugin_command_that_is_not_installed_is_refused()
    {
        var store = NewStore();
        var library = NewLibrary();
        var panel = store.Reserve("Vacuum control", WidgetSizes.Wide, 70, null, null, proposed: false);

        var tool = WidgetLibraryTools.PublishTool(store, library,
            internalFault: name => WidgetInternals.Fault(name, pluginHas: (_, _) => false));

        var result = await Call(tool, VacuumArgs(panel.Id, new { @internal = "plugin:roborock/does-not-exist" }));

        Assert.True(result.IsError);
        Assert.Empty(library.All());
    }

    [Fact]
    public async Task An_internal_feed_that_really_is_there_is_published()
    {
        var store = NewStore();
        var library = NewLibrary();
        var panel = store.Reserve("Vacuum control", WidgetSizes.Wide, 70, null, null, proposed: false);

        var tool = WidgetLibraryTools.PublishTool(store, library,
            internalFault: name => WidgetInternals.Fault(name, pluginHas: (p, c) => p == "roborock" && c == "status"),
            prove: Proves("{\"battery\":100,\"name\":\"Wilma\"}"));

        var result = await Call(tool, VacuumArgs(panel.Id));

        Assert.False(result.IsError);
        Assert.Single(library.All());
    }

    /// <summary>
    /// The shape check on its own is what let "source:any" through, so it is worth pinning that the two halves are
    /// genuinely different questions.
    /// </summary>
    [Fact]
    public void A_name_can_be_well_formed_and_still_name_nothing()
    {
        Assert.True(WidgetInternals.Known("source:any"));
        Assert.NotNull(WidgetInternals.Fault("source:any", sourceExists: _ => false));
        Assert.Null(WidgetInternals.Fault("source:photos", sourceExists: s => s == "photos"));

        // Ours are always there and must not need a registry to prove it.
        Assert.Null(WidgetInternals.Fault("agenda"));
        Assert.Null(WidgetInternals.Fault("tasks", sourceExists: _ => false, pluginHas: (_, _) => false));
    }

    // ── Cause B: nothing ever ran the loader before publishing it ─────────────────────────────────────────

    [Fact]
    public async Task A_loader_that_returns_nothing_is_refused_rather_than_published()
    {
        var store = NewStore();
        var library = NewLibrary();
        var panel = store.Reserve("London weekly weather", WidgetSizes.Tall, 70, null, null, proposed: false);

        // Exactly what the weather panel's browser loader did, every five minutes, for nine and a half hours.
        var tool = WidgetLibraryTools.PublishTool(store, library,
            prove: Proves(null, "The page returned nothing."));

        var result = await Call(tool, VacuumArgs(panel.Id, new { @internal = (string?)null, loader_mode = "browser", url = "https://api.open-meteo.com/v1/forecast", expression = "return null" }));

        Assert.True(result.IsError);
        Assert.Contains("returned nothing", result.Content);
        Assert.Empty(library.All());
        Assert.NotEqual(WidgetStatus.Live, store.Get(panel.Id)!.Status);
    }

    [Fact]
    public async Task A_response_that_does_not_fill_the_model_is_refused()
    {
        var store = NewStore();
        var library = NewLibrary();
        var panel = store.Reserve("Vacuum control", WidgetSizes.Wide, 70, null, null, proposed: false);

        // The loader answered, and answered about nothing the component was written against.
        var tool = WidgetLibraryTools.PublishTool(store, library,
            prove: Proves(null, "The source didn't carry battery, name — the loader needs a mapping for those."));

        var result = await Call(tool, VacuumArgs(panel.Id));

        Assert.True(result.IsError);
        Assert.Contains("battery", result.Content);
        Assert.Empty(library.All());
    }

    [Fact]
    public async Task A_proven_panel_keeps_the_reading_the_proof_produced()
    {
        var store = NewStore();
        var library = NewLibrary();
        var panel = store.Reserve("Vacuum control", WidgetSizes.Wide, 70, null, null, proposed: false);

        var tool = WidgetLibraryTools.PublishTool(store, library,
            prove: Proves("{\"battery\":93,\"name\":\"Wilma\"}"));

        var result = await Call(tool, VacuumArgs(panel.Id));

        Assert.False(result.IsError);
        // Proving it and then throwing the answer away would leave the panel blank until the next tick, having
        // already paid for the fetch.
        var saved = store.Get(panel.Id)!;
        Assert.Equal(WidgetStatus.Live, saved.Status);
        Assert.Contains("93", saved.Data);
        Assert.Null(saved.Error);
    }

    /// <summary>
    /// Where nothing can run the loader — a test, a client-mode panel, an instance with no loader wired — publish
    /// must behave as it always did rather than refusing everything.
    /// </summary>
    [Fact]
    public async Task With_no_prover_wired_publishing_is_unchanged()
    {
        var store = NewStore();
        var library = NewLibrary();
        var panel = store.Reserve("Vacuum control", WidgetSizes.Wide, 70, null, null, proposed: false);

        var result = await Call(WidgetLibraryTools.PublishTool(store, library), VacuumArgs(panel.Id));

        Assert.False(result.IsError);
        Assert.Single(library.All());
    }

    // ── Cause C: the only check after publishing was a photograph ─────────────────────────────────────────

    [Fact]
    public void A_panel_whose_data_never_filled_its_model_is_broken_however_it_photographs()
    {
        var kind = new WidgetKind
        {
            Name = "city-weather",
            Code = "return <List>{data.days.map(d => <Row>{d.high}</Row>)}</List>",
            Model = new()
            {
                new WidgetField { Name = "days", Type = "list", Description = "one per day" },
            },
        };

        // Seven days, every one of them with nothing in it — a path that matched nothing, which is precisely what
        // photographs as a tidy row of icons.
        var panel = new Widget
        {
            Id = "96fc223b",
            Title = "London weekly weather",
            Kind = kind.Name,
            Data = "{\"days\":[{\"high\":null,\"low\":null},{\"high\":null,\"low\":null}]}",
        };

        var verdict = WidgetLook.DataFault(panel, kind);

        Assert.NotNull(verdict);
        Assert.False(verdict!.Working);
        Assert.Equal("data", verdict.Fault);
    }

    [Fact]
    public void A_panel_whose_data_is_sound_is_left_to_the_picture()
    {
        var kind = new WidgetKind
        {
            Name = "city-weather",
            Code = "return <List>{data.days.map(d => <Row>{d.high}</Row>)}</List>",
            Model = new()
            {
                new WidgetField { Name = "days", Type = "list", Description = "one per day" },
            },
        };
        var panel = new Widget
        {
            Id = "96fc223b",
            Title = "London weekly weather",
            Kind = kind.Name,
            Data = "{\"days\":[{\"high\":24,\"low\":16},{\"high\":22,\"low\":15}]}",
        };

        // Null means "nothing the data can tell you" — the eyes still get their turn.
        Assert.Null(WidgetLook.DataFault(panel, kind));
    }

    [Fact]
    public void A_panel_with_no_data_at_all_is_not_judged_on_a_picture_either()
    {
        var kind = new WidgetKind
        {
            Name = "vacuum-control",
            Code = VacuumCode,
            Model = new() { new WidgetField { Name = "battery", Type = "number", Description = "charge" } },
        };
        var panel = new Widget { Id = "ba3ea8f7", Title = "Vacuum control", Kind = kind.Name, Data = null };

        var verdict = WidgetLook.DataFault(panel, kind);

        Assert.NotNull(verdict);
        Assert.False(verdict!.Working);
        Assert.Equal("data", verdict.Fault);
    }

    // ── Two honest ways to build a list, and the one that is a mix of both ────────────────────────────────

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static List<WidgetField> DayModel() => new()
    {
        new WidgetField { Name = "days", Type = "list", Description = "the forecast days" },
        new WidgetField { Name = "high", Type = "number", Description = "that day's high" },
    };

    /// <summary>
    /// The weather panel exactly: a map produced sibling arrays, the component read row objects. Nothing caught it,
    /// and what reached the page was seven rows of undefined.
    /// </summary>
    [Fact]
    public void A_list_of_plain_values_read_as_rows_is_broken()
    {
        var code = "return <List>{data.days.map((d, i) => <Row>{d.day}: {d.high}</Row>)}</List>";
        var data = Json("{\"days\":[\"2026-08-21\",\"2026-08-22\"],\"high\":[20.8,20.9]}");

        var wrong = WidgetContract.Broken(data, DayModel(), code);

        Assert.NotNull(wrong);
        Assert.Contains("days", wrong);
        // It has to name both idioms, or the worker cannot act on it.
        Assert.Contains("index", wrong);
    }

    /// <summary>The parallel-array idiom, which is what city-weather-hourly and football-match-tracker do.</summary>
    [Fact]
    public void The_same_data_read_by_index_is_correct()
    {
        var code = "return <List>{data.days.map((d, i) => <Row>{d}: {data.high[i]}</Row>)}</List>";
        var data = Json("{\"days\":[\"2026-08-21\",\"2026-08-22\"],\"high\":[20.8,20.9]}");

        Assert.Null(WidgetContract.Broken(data, DayModel(), code));
    }

    /// <summary>The row-object idiom, which a browser expression or an internal feed returns.</summary>
    [Fact]
    public void Real_rows_read_as_rows_are_correct()
    {
        var code = "return <List>{data.days.map((d, i) => <Row>{d.day}: {d.high}</Row>)}</List>";
        var data = Json("{\"days\":[{\"day\":\"Fri\",\"high\":20.8},{\"day\":\"Sat\",\"high\":20.9}]}");

        var model = new List<WidgetField>
        {
            new WidgetField { Name = "days", Type = "list", Description = "the forecast days" },
        };
        Assert.Null(WidgetContract.Broken(data, model, code));
    }

    /// <summary>A row name that is a prefix of another identifier must not read as a dereference.</summary>
    [Fact]
    public void Mapping_a_plain_list_without_touching_the_row_is_not_a_fault()
    {
        // `d` is never dereferenced — `data.high` is not `d.`
        var code = "return <List>{data.days.map((d, i) => <Row>{data.high[i]}</Row>)}</List>";
        var data = Json("{\"days\":[\"a\",\"b\"],\"high\":[1,2]}");

        Assert.Null(WidgetContract.Broken(data, DayModel(), code));
    }

    [Fact]
    public void An_empty_list_says_nothing_either_way()
    {
        var code = "return <List>{data.days.map((d, i) => <Row>{d.high}</Row>)}</List>";
        Assert.Null(WidgetContract.Broken(Json("{\"days\":[],\"high\":[]}"), DayModel(), code));
    }

    // ── The other half of the gate: a way to find out what DOES work ──────────────────────────────────────

    /// <summary>
    /// Refusing a bad loader is only half a capability while the only way to discover a good one is to publish and
    /// read the refusal. An adjust has six tools and none of them could call a source — the weather worker reached
    /// for a shell tool that does not exist, because fetching a url was the obvious move and nothing could do it.
    /// </summary>
    [Fact]
    public async Task A_loader_can_be_run_without_publishing_anything()
    {
        var store = NewStore();
        var library = NewLibrary();
        var panel = store.Reserve("Vacuum control", WidgetSizes.Wide, 70, null, null, proposed: false);

        var tool = WidgetLibraryTools.TryTool(store, Proves("{\"battery\":93,\"name\":\"Upstairs\"}"));
        var result = await Call(tool, new
        {
            id = panel.Id,
            loader_mode = "internal",
            @internal = "plugin:roborock/status",
            map = new[] { new { field = "battery", path = "any.battery" } },
            model = new[] { new { name = "battery", type = "number", description = "charge" } },
        });

        Assert.False(result.IsError);
        // What it returns is the point — the worker has to be able to read the shape.
        Assert.Contains("Upstairs", result.Content);
        // And nothing was saved.
        Assert.Empty(library.All());
        Assert.NotEqual(WidgetStatus.Live, store.Get(panel.Id)!.Status);
    }

    [Fact]
    public async Task A_loader_that_fails_says_so_without_it_being_a_tool_failure()
    {
        var store = NewStore();
        var panel = store.Reserve("London weekly weather", WidgetSizes.Tall, 70, null, null, proposed: false);

        var tool = WidgetLibraryTools.TryTool(store, Proves(null, "The page returned nothing."));
        var result = await Call(tool, new
        {
            id = panel.Id, loader_mode = "browser",
            url = "https://api.open-meteo.com/v1/forecast", expression = "return null",
        });

        // Ok, not Error: the question was "does this work", and "no" is the answer, not a fault in asking.
        Assert.False(result.IsError);
        Assert.Contains("DOESN'T work", result.Content);
        Assert.Contains("returned nothing", result.Content);
    }

    [Fact]
    public async Task Trying_an_internal_feed_that_names_nothing_is_answered_without_a_fetch()
    {
        var store = NewStore();
        var panel = store.Reserve("Vacuum control", WidgetSizes.Wide, 70, null, null, proposed: false);

        var ran = false;
        var tool = WidgetLibraryTools.TryTool(store,
            prove: (_, _, _) => { ran = true; return Task.FromResult(new LoadResult("{}", null)); },
            internalFault: name => WidgetInternals.Fault(name, sourceExists: _ => false));

        var result = await Call(tool, new { id = panel.Id, loader_mode = "internal", @internal = "source:any" });

        Assert.True(result.IsError);
        Assert.False(ran);
    }

    /// <summary>An adjust keeps every widget_ tool, which is what puts this in its hands without a filter change.</summary>
    [Fact]
    public void An_adjust_keeps_the_tool_that_runs_its_loader()
    {
        var borrowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "read_file", "chrome_navigate" };

        Assert.True(PanelBuild.KeepForAdjust("widget_try", borrowed));
        Assert.True(PanelBuild.KeepForAdjust("widget_publish", borrowed));
        // And still no browser: running the loader it has is not going shopping for another source.
        Assert.False(PanelBuild.KeepForAdjust("chrome_navigate", borrowed));
    }

    // ── The first cause of all: the brief never said what the loader WAS ──────────────────────────────────

    /// <summary>
    /// An adjust republishes the whole kind, so anything the brief leaves out is something the worker has to
    /// invent. This left out the internal feed's name — and the invention was "source:any", over a working feed.
    /// </summary>
    [Fact]
    public void An_adjust_is_told_what_its_loader_actually_is()
    {
        var kind = new WidgetKind
        {
            Name = "vacuum-control",
            Code = VacuumCode,
            Model = new() { new WidgetField { Name = "battery", Type = "number", Description = "charge" } },
            Loader = new WidgetLoader
            {
                Mode = LoaderModes.Internal,
                Internal = "plugin:roborock/status",
                Map = new(StringComparer.OrdinalIgnoreCase) { ["battery"] = "any.battery" },
            },
        };
        var panel = new Widget { Id = "ba3ea8f7", Title = "Vacuum control", Kind = kind.Name, Size = WidgetSizes.Wide };

        var brief = WidgetTools.AdjustBrief(panel, kind, "make the name dynamic, it's fixed on wife v2");

        Assert.Contains("plugin:roborock/status", brief);
    }

    [Fact]
    public void A_browser_panel_is_told_the_expression_it_has_to_republish()
    {
        var kind = new WidgetKind
        {
            Name = "city-weather",
            Code = "return <Meta>x</Meta>",
            Model = new() { new WidgetField { Name = "days", Type = "list", Description = "one per day" } },
            Loader = new WidgetLoader
            {
                Mode = LoaderModes.Browser,
                Url = "https://api.open-meteo.com/v1/forecast",
                Expression = "return {days: readTheTable()}",
            },
        };
        var panel = new Widget { Id = "96fc223b", Title = "London weekly weather", Kind = kind.Name, Size = WidgetSizes.Tall };

        var brief = WidgetTools.AdjustBrief(panel, kind, "the highs are wrong");

        Assert.Contains("readTheTable()", brief);
    }

    // ── Cause D: a panel's work belonged to the chat it was asked in ──────────────────────────────────────

    [Fact]
    public void Two_panels_asked_for_in_one_conversation_do_not_share_a_workspace()
    {
        var store = NewStore();
        var vacuum = store.Reserve("Vacuum control", WidgetSizes.Wide, 70, null, "chat-7", proposed: false);
        var weather = store.Reserve("London weekly weather", WidgetSizes.Tall, 70, null, "chat-7", proposed: false);

        // Which chat asked is worth keeping — it is just not where the work goes.
        Assert.Equal("chat-7", vacuum.Session);
        Assert.Equal("chat-7", weather.Session);

        Assert.NotEqual(WidgetWork.SessionFor(vacuum.Id), WidgetWork.SessionFor(weather.Id));
        Assert.DoesNotContain("chat-7", WidgetWork.SessionFor(weather.Id));
    }

    public void Dispose()
    {
        foreach (var p in _paths)
            try { if (File.Exists(p)) File.Delete(p); } catch { }
    }
}
