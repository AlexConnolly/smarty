using System.Text.Json;
using Smarty.Agents;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Panels the system builds for itself, in three parts.
///
/// <para>
/// It took three attempts to get here and each failure is worth remembering. First a fixed set of templates a model
/// chose between — which cannot express a boarding pass. Then a generated component reading the source's raw
/// response, which meant every change in that response was a throw in the browser. Now: a declared MODEL, a LOADER
/// that produces it server-side, and a COMPONENT that renders it. The model is the contract, so a source that moves
/// its fields breaks the loader — testable, fixable, nowhere near the rendering.
/// </para>
/// <para>
/// And a kind is reusable. "Track this flight" asks for a flight-status kind taking a flight number, not for a
/// BA2490 tracker; the second flight is then a parameter rather than another build.
/// </para>
/// </summary>
public class WidgetTests : IDisposable
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

    private const string GoodCode =
        "if (!data) return <Meta>Loading…</Meta>\nreturn <Stat label={params.flightNumber} value={data.status} />";

    private static WidgetKind FlightKind() => new()
    {
        Name = "flight-status",
        Title = "Flight status",
        Description = "whether a flight is on time or delayed, its times and its gate",
        Params = new()
        {
            new WidgetParam { Name = "flightNumber", Description = "the flight number", Example = "BA2490" },
        },
        Model = new()
        {
            new WidgetField { Name = "status", Type = "string", Description = "on time or delayed" },
            new WidgetField { Name = "gate", Type = "string", Description = "the gate", Required = false },
        },
        Loader = new WidgetLoader
        {
            Mode = LoaderModes.Http,
            Url = "https://api.example.com/flights/{flightNumber}",
            Map = new() { ["status"] = "flight.status", ["gate"] = "flight.gate" },
        },
        Code = GoodCode,
        Refresh = "every 5 minutes",
    };

    private static async Task<ToolOutput> Call(AgentTool tool, object args) =>
        await tool.InvokeAsync(
            new ToolCallArguments(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement),
            CancellationToken.None);

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    // ── The contract: a declared model ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Only_declared_fields_come_through()
    {
        // The whole point of the model. A source that starts returning six more fields, or renaming one it doesn't
        // declare, cannot reach the component — so it cannot break the rendering.
        var kind = FlightKind();
        var source = Json("{\"flight\":{\"status\":\"On time\",\"gate\":\"B14\",\"aircraftRegistration\":\"G-EUUU\"}}");

        var (shaped, missing) = WidgetShape.Apply(source, kind.Model, kind.Loader.Map);

        Assert.Empty(missing);
        Assert.Equal("On time", shaped["status"]!.GetValue<string>());
        Assert.Equal("B14", shaped["gate"]!.GetValue<string>());
        Assert.False(shaped.ContainsKey("aircraftRegistration"));
    }

    [Fact]
    public void A_required_field_the_source_stopped_carrying_is_reported()
    {
        // Reported, because this is the loader's fault and the loader is what needs changing. Passed through, it
        // would be a panel showing a dash and a component blamed for it.
        var kind = FlightKind();
        var source = Json("{\"flight\":{\"gate\":\"B14\"}}");

        var (_, missing) = WidgetShape.Apply(source, kind.Model, kind.Loader.Map);

        Assert.Equal(new[] { "status" }, missing);
    }

    [Fact]
    public void An_optional_field_the_source_omits_is_simply_absent()
    {
        var kind = FlightKind();
        var (shaped, missing) = WidgetShape.Apply(Json("{\"flight\":{\"status\":\"Delayed\"}}"), kind.Model, kind.Loader.Map);

        Assert.Empty(missing);
        Assert.False(shaped.ContainsKey("gate"));
    }

    [Theory]
    [InlineData("a.b", "{\"a\":{\"b\":42}}", "42")]
    [InlineData("a.0", "{\"a\":[7,8,9]}", "7")]
    [InlineData("a.2", "{\"a\":[7,8,9]}", "9")]
    [InlineData("hourly.temperature_2m.0", "{\"hourly\":{\"temperature_2m\":[21.5,22]}}", "21.5")]
    [InlineData("rows.*.n", "{\"rows\":[{\"n\":1},{\"n\":2}]}", "[1,2]")]
    [InlineData("A.B", "{\"a\":{\"b\":1}}", "1")]              // case-insensitive: a rename of case shouldn't empty a panel
    public void The_path_language_reaches_what_real_feeds_actually_look_like(string path, string source, string expected)
    {
        var got = WidgetShape.Walk(Json(source), path);
        Assert.Equal(expected, got?.ToJsonString());
    }

    [Theory]
    [InlineData("a.missing", "{\"a\":{\"b\":1}}")]
    [InlineData("a.9", "{\"a\":[1]}")]
    [InlineData("a.b", "{\"a\":[1]}")]                          // an object step into an array
    [InlineData("a.*.n", "{\"a\":{\"n\":1}}")]                   // a wildcard over a non-array
    public void A_path_that_does_not_lead_anywhere_returns_nothing_rather_than_guessing(string path, string source)
    {
        Assert.Null(WidgetShape.Walk(Json(source), path));
    }

    // ── Parameters: what makes a kind reusable ──────────────────────────────────────────────────────────

    [Fact]
    public void The_loader_is_the_kind_with_this_panel_s_values_in_it()
    {
        var kind = FlightKind();
        var loader = kind.Loader.For(new Dictionary<string, string> { ["flightNumber"] = "BA2490" });

        Assert.Equal("https://api.example.com/flights/BA2490", loader.Url);
        // And the kind itself is untouched, so the next panel starts from the template again.
        Assert.Equal("https://api.example.com/flights/{flightNumber}", kind.Loader.Url);
    }

    [Fact]
    public void A_value_going_into_a_url_is_escaped_and_one_going_into_code_is_not()
    {
        // Both matter. An unescaped title with a space in it makes a request nobody intended; a percent-encoded
        // value inside a JavaScript expression is simply corrupt.
        var kind = FlightKind();
        kind.Loader.Url = "https://api.example.com/search?q={q}";
        kind.Loader.Expression = "document.querySelector('[data-q=\"{q}\"]').textContent";

        var loader = kind.Loader.For(new Dictionary<string, string> { ["q"] = "signed LP" });

        Assert.Contains("signed%20LP", loader.Url);
        Assert.Contains("signed LP", loader.Expression);
    }

    [Fact]
    public void What_is_missing_is_what_has_to_be_found_before_the_panel_can_work()
    {
        var kind = FlightKind();

        Assert.Equal(new[] { "flightNumber" }, kind.Missing(null).Select(p => p.Name));
        Assert.Empty(kind.Missing(new Dictionary<string, string> { ["flightNumber"] = "BA2490" }));
        // Present but blank is missing: an empty value substitutes into a url that fetches nothing.
        Assert.Single(kind.Missing(new Dictionary<string, string> { ["flightNumber"] = "  " }));
    }

    [Fact]
    public void The_holes_in_a_loader_are_discoverable_so_an_undeclared_one_can_be_refused()
    {
        var kind = FlightKind();
        kind.Loader.Url = "https://api.example.com/{a}/{b}?x={a}";

        Assert.Equal(new[] { "a", "b" }, kind.Loader.Holes().OrderBy(h => h));
    }

    // ── The library: build once, instantiate free ───────────────────────────────────────────────────────

    [Fact]
    public void A_request_worded_nothing_like_the_kind_still_finds_it()
    {
        var library = NewLibrary();
        library.Upsert(FlightKind());

        var found = library.Relevant("is my flight to Palma delayed, and what gate");

        Assert.Equal("flight-status", Assert.Single(found).Name);
    }

    [Fact]
    public void An_unrelated_request_matches_nothing_so_it_gets_built()
    {
        var library = NewLibrary();
        library.Upsert(FlightKind());

        Assert.Empty(library.Relevant("how much bitcoin is worth in pounds"));
    }

    [Fact]
    public void Republishing_a_name_revises_that_kind_rather_than_adding_another()
    {
        var library = NewLibrary();
        library.Upsert(FlightKind());

        var better = FlightKind();
        better.Code = "return <Meta>{data.status}</Meta>";
        library.Upsert(better);

        var only = Assert.Single(library.All());
        Assert.Contains("Meta", only.Code);
    }

    [Fact]
    public void The_model_is_told_what_each_kind_takes_and_shows()
    {
        // Without the parameters and the fields, a caller can't tell whether an existing kind covers the request —
        // and a kind it can't judge is a kind it rebuilds.
        var library = NewLibrary();
        library.Upsert(FlightKind());

        var note = library.Describe();

        Assert.Contains("flight-status", note);
        Assert.Contains("flightNumber", note);
        Assert.Contains("BA2490", note);        // the example, so a value can be recognised
        Assert.Contains("status", note);        // what it shows
        Assert.Contains("widget_add", note);    // and that reuse is the cheap path
    }

    // ── Instances ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Attaching_a_panel_to_a_kind_makes_it_live_and_due()
    {
        var store = NewStore();
        var library = NewLibrary();
        var kind = library.Upsert(FlightKind());

        var panel = store.Reserve("BA2490 to Palma", WidgetSizes.Kpi, 85, null, "chat-1", proposed: false);
        Assert.Equal(WidgetStatus.Building, panel.Status);

        store.Attach(panel.Id, kind, new Dictionary<string, string> { ["flightNumber"] = "BA2490" });

        var after = store.Get(panel.Id)!;
        Assert.Equal(WidgetStatus.Live, after.Status);
        Assert.Equal("flight-status", after.Kind);
        Assert.Equal("BA2490", after.Params["flightNumber"]);
        Assert.Equal("every 5 minutes", after.Refresh);
        Assert.NotNull(after.NextRefresh);   // loads immediately, not at the far end of its cadence
    }

    [Fact]
    public void Two_panels_of_one_kind_are_the_same_component_with_different_values()
    {
        // The whole economy of the library in one test.
        var store = NewStore();
        var library = NewLibrary();
        var kind = library.Upsert(FlightKind());

        foreach (var flight in new[] { "BA2490", "EZY8085" })
        {
            var p = store.Reserve($"{flight}", WidgetSizes.Kpi, 70, null, null, proposed: false);
            store.Attach(p.Id, kind, new Dictionary<string, string> { ["flightNumber"] = flight });
        }

        var instances = store.Instances("flight-status");
        Assert.Equal(2, instances.Count);
        Assert.All(instances, i => Assert.Equal("flight-status", i.Kind));
        Assert.Equal(new[] { "BA2490", "EZY8085" }, instances.Select(i => i.Params["flightNumber"]).OrderBy(x => x));
    }

    [Fact]
    public void Revising_a_kind_reaches_every_panel_made_from_it()
    {
        // Fixing the flight kind and leaving its siblings on the old broken component is the opposite of the point
        // of having a library.
        var store = NewStore();
        var library = NewLibrary();
        var kind = library.Upsert(FlightKind());

        var a = store.Reserve("BA2490", WidgetSizes.Kpi, 70, null, null, proposed: false);
        var b = store.Reserve("EZY8085", WidgetSizes.Kpi, 70, null, null, proposed: false);
        store.Attach(a.Id, kind, new Dictionary<string, string> { ["flightNumber"] = "BA2490" });
        store.Attach(b.Id, kind, new Dictionary<string, string> { ["flightNumber"] = "EZY8085" });
        store.Broke(a.Id, "gate is not defined");

        Assert.Equal(2, store.Requalify(kind));
        Assert.Null(store.Get(a.Id)!.RenderError);   // the fault is cleared so the fix gets judged on its own merits
        Assert.Equal(0, store.Get(a.Id)!.Fixes);
    }

    // ── Opening the page is what refreshes it ──────────────────────────────────────────────────────────

    [Fact]
    public void Opening_the_page_wakes_anything_with_nothing_to_show()
    {
        // The failure this prevents, exactly as it happened: a field rename dropped the stored data from every
        // panel, each one's next load was hours away, and the whole home page sat on "Loading…" with no way to
        // hurry it. A panel is refreshed because it is being LOOKED at, not only because a timer said so.
        var store = NewStore();
        var library = NewLibrary();
        var kind = library.Upsert(FlightKind());
        var panel = store.Reserve("BA2490", WidgetSizes.Kpi, 70, null, null, proposed: false);
        store.Attach(panel.Id, kind, new Dictionary<string, string> { ["flightNumber"] = "BA2490" });

        // Loaded, then given a cadence a long way off — the state that used to be unrecoverable.
        store.Loaded(panel.Id, "{\"status\":\"On time\"}", null);
        store.Edit(panel.Id, refresh: "daily at 06:00");
        store.Loaded(panel.Id, "{\"status\":\"On time\"}", null);
        Assert.Empty(store.Due(DateTimeOffset.UtcNow));

        // Now lose its data, as the rename did.
        store.Loaded(panel.Id, null, null);
        store.Get(panel.Id)!.Data = null;

        Assert.Equal(1, store.Nudge(DateTimeOffset.UtcNow));
        Assert.Single(store.Due(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_panel_that_is_current_is_left_alone_when_the_page_opens()
    {
        var store = NewStore();
        var library = NewLibrary();
        var kind = library.Upsert(FlightKind());
        var panel = store.Reserve("BA2490", WidgetSizes.Kpi, 70, null, null, proposed: false);
        store.Attach(panel.Id, kind, new Dictionary<string, string> { ["flightNumber"] = "BA2490" });
        store.Loaded(panel.Id, "{\"status\":\"On time\"}", null);

        Assert.Equal(0, store.Nudge(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_failed_load_keeps_the_last_good_data_and_admits_its_age()
    {
        var store = NewStore();
        var library = NewLibrary();
        var kind = library.Upsert(FlightKind());
        var panel = store.Reserve("BA2490", WidgetSizes.Kpi, 70, null, null, proposed: false);
        store.Attach(panel.Id, kind, new Dictionary<string, string> { ["flightNumber"] = "BA2490" });
        store.Loaded(panel.Id, "{\"status\":\"On time\"}", null);

        store.Loaded(panel.Id, null, "The source answered 503.");

        var after = store.Get(panel.Id)!;
        Assert.Contains("On time", after.Data);
        Assert.Equal("The source answered 503.", after.Error);
    }

    // ── Publishing: the three parts, each checked ─────────────────────────────────────────────────────

    private static object PublishArgs(string id, object? overrides = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["kind"] = "flight-status",
            ["title"] = "Flight status",
            ["description"] = "whether a flight is on time, its times and gate",
            ["params"] = new[] { new { name = "flightNumber", description = "the flight number", value = "BA2490" } },
            ["model"] = new[] { new { name = "status", type = "string", description = "on time or delayed" } },
            ["loader_mode"] = "http",
            ["url"] = "https://api.example.com/flights/{flightNumber}",
            ["map"] = new[] { new { field = "status", path = "flight.status" } },
            ["code"] = GoodCode,
            ["refresh"] = "every 5 minutes",
        };
        if (overrides is not null)
            foreach (var p in overrides.GetType().GetProperties())
                d[p.Name] = p.GetValue(overrides);
        return d;
    }

    [Fact]
    public async Task Publishing_writes_the_kind_and_lights_up_the_panel()
    {
        var store = NewStore();
        var library = NewLibrary();
        var panel = store.Reserve("BA2490", WidgetSizes.Kpi, 85, null, null, proposed: false);

        var result = await Call(WidgetLibraryTools.PublishTool(store, library), PublishArgs(panel.Id));

        Assert.False(result.IsError);
        var kind = Assert.Single(library.All());
        Assert.Equal("flight-status", kind.Name);
        Assert.Single(kind.Model);
        Assert.Equal(LoaderModes.Http, kind.Loader.Mode);
        Assert.Equal(WidgetStatus.Live, store.Get(panel.Id)!.Status);
        Assert.Equal("BA2490", store.Get(panel.Id)!.Params["flightNumber"]);
    }

    [Fact]
    public async Task A_kind_with_no_declared_model_is_refused()
    {
        // Without it there is no contract, and the component goes back to digging through the source itself.
        var store = NewStore();
        var library = NewLibrary();
        var panel = store.Reserve("BA2490", WidgetSizes.Kpi, 85, null, null, proposed: false);

        var result = await Call(WidgetLibraryTools.PublishTool(store, library),
            PublishArgs(panel.Id, new { model = Array.Empty<object>() }));

        Assert.True(result.IsError);
        Assert.Contains("contract", result.Content);
        Assert.Empty(library.All());
    }

    [Fact]
    public async Task A_hole_with_no_parameter_behind_it_is_refused()
    {
        // It can never be filled, so the loader can never work — and the symptom is a 400 that reads like the
        // source's fault, hours later.
        var store = NewStore();
        var library = NewLibrary();
        var panel = store.Reserve("BA2490", WidgetSizes.Kpi, 85, null, null, proposed: false);

        var result = await Call(WidgetLibraryTools.PublishTool(store, library),
            PublishArgs(panel.Id, new { url = "https://api.example.com/{flightNumber}/{airline}" }));

        Assert.True(result.IsError);
        Assert.Contains("airline", result.Content);
        Assert.Empty(library.All());
    }

    [Fact]
    public async Task A_required_field_the_map_does_not_cover_is_refused()
    {
        var store = NewStore();
        var library = NewLibrary();
        var panel = store.Reserve("BA2490", WidgetSizes.Kpi, 85, null, null, proposed: false);

        var result = await Call(WidgetLibraryTools.PublishTool(store, library), PublishArgs(panel.Id, new
        {
            model = new[]
            {
                new { name = "status", type = "string", description = "on time or delayed" },
                new { name = "gate", type = "string", description = "the gate" },
            },
        }));

        Assert.True(result.IsError);
        Assert.Contains("gate", result.Content);
    }

    [Fact]
    public async Task A_map_entry_for_a_field_nobody_declared_is_refused()
    {
        var store = NewStore();
        var library = NewLibrary();
        var panel = store.Reserve("BA2490", WidgetSizes.Kpi, 85, null, null, proposed: false);

        var result = await Call(WidgetLibraryTools.PublishTool(store, library), PublishArgs(panel.Id, new
        {
            map = new[]
            {
                new { field = "status", path = "flight.status" },
                new { field = "registration", path = "flight.reg" },
            },
        }));

        Assert.True(result.IsError);
        Assert.Contains("registration", result.Content);
    }

    [Fact]
    public async Task A_browser_loader_without_an_expression_is_refused()
    {
        // In browser mode the expression IS the shaping — the browser is already a JavaScript engine, so there is no
        // mapping to fall back on.
        var store = NewStore();
        var library = NewLibrary();
        var panel = store.Reserve("eBay listing", WidgetSizes.Kpi, 85, null, null, proposed: false);

        var result = await Call(WidgetLibraryTools.PublishTool(store, library),
            PublishArgs(panel.Id, new { loader_mode = "browser", map = Array.Empty<object>() }));

        Assert.True(result.IsError);
        Assert.Contains("expression", result.Content);
    }

    [Theory]
    [InlineData("import React from 'react'\nreturn <div>{data.status}</div>", "imports")]
    [InlineData("export default () => <div>{data.status}</div>", "export")]
    [InlineData("const r = await fetch('/x')\nreturn <div>{data.status}</div>", "fetch")]
    [InlineData("document.title = 'x'\nreturn <div>{data.status}</div>", "document")]
    [InlineData("const x = data.status", "return")]
    public async Task The_mistakes_that_produce_a_blank_panel_are_refused_with_the_reason(string code, string about)
    {
        var store = NewStore();
        var library = NewLibrary();
        var panel = store.Reserve("BA2490", WidgetSizes.Kpi, 85, null, null, proposed: false);

        var result = await Call(WidgetLibraryTools.PublishTool(store, library), PublishArgs(panel.Id, new { code }));

        Assert.True(result.IsError);
        Assert.Contains(about, result.Content, StringComparison.OrdinalIgnoreCase);
    }

    // ── Adding from the library ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_second_flight_is_a_parameter_not_a_build()
    {
        var store = NewStore();
        var library = NewLibrary();
        library.Upsert(FlightKind());
        var asked = new List<string>();

        var result = await Call(
            WidgetLibraryTools.AddTool(store, library, () => "chat-1",
                (_, _, _, find) => { asked.Add(find); return Task.FromResult("9"); }),
            new
            {
                kind = "flight-status", title = "EZY8085 to Palma",
                @params = new[] { new { name = "flightNumber", value = "EZY8085" } },
            });

        Assert.False(result.IsError);
        Assert.Empty(asked);                                          // no worker was needed
        var panel = Assert.Single(store.All());
        Assert.Equal(WidgetStatus.Live, panel.Status);                // live at once
        Assert.Equal("EZY8085", panel.Params["flightNumber"]);
        // Counted on use, not on publish — the number answers "is the library actually saving work".
        Assert.Equal(1, library.Get("flight-status")!.Uses);
    }

    [Fact]
    public async Task A_value_the_user_did_not_give_is_gone_and_found_rather_than_asked_for()
    {
        // "Track my latest eBay listing" names no item number, and the number is a fact about the world.
        var store = NewStore();
        var library = NewLibrary();
        library.Upsert(FlightKind());
        var asked = new List<string>();

        var result = await Call(
            WidgetLibraryTools.AddTool(store, library, () => "chat-1",
                (_, _, missing, find) => { asked.Add($"{missing[0].Name}: {find}"); return Task.FromResult("9"); }),
            new { kind = "flight-status", title = "My flight home", find = "the flight number on my booking" });

        Assert.False(result.IsError);
        Assert.Contains("flightNumber: the flight number on my booking", asked);
        // On the page immediately, saying it is being worked out.
        Assert.Equal(WidgetStatus.Building, Assert.Single(store.All()).Status);
    }

    [Fact]
    public async Task A_missing_value_with_nothing_said_about_finding_it_is_refused()
    {
        // Otherwise the panel goes up with an unsubstituted hole in its url and fetches nothing, looking built.
        var store = NewStore();
        var library = NewLibrary();
        library.Upsert(FlightKind());

        var result = await Call(
            WidgetLibraryTools.AddTool(store, library, () => null, (_, _, _, _) => Task.FromResult("9")),
            new { kind = "flight-status", title = "My flight" });

        Assert.True(result.IsError);
        Assert.Contains("flightNumber", result.Content);
        Assert.Empty(store.All());
    }

    [Fact]
    public async Task Adding_from_a_kind_that_is_not_there_says_which_ones_are()
    {
        var store = NewStore();
        var library = NewLibrary();
        library.Upsert(FlightKind());

        var result = await Call(
            WidgetLibraryTools.AddTool(store, library, () => null, (_, _, _, _) => Task.FromResult("9")),
            new { kind = "aeroplane-tracker", title = "X" });

        Assert.True(result.IsError);
        Assert.False(result.CanRetry);   // the library is in its context; guessing again is a loop
    }

    [Fact]
    public async Task Filling_in_the_found_value_lights_the_panel_up()
    {
        var store = NewStore();
        var library = NewLibrary();
        var kind = library.Upsert(FlightKind());
        var panel = store.Reserve("My flight home", WidgetSizes.Kpi, 80, null, null, proposed: false);
        store.Attach(panel.Id, kind, null);

        var result = await Call(WidgetLibraryTools.FillTool(store, library), new
        {
            id = panel.Id, @params = new[] { new { name = "flightNumber", value = "BA2490" } },
        });

        Assert.False(result.IsError);
        Assert.Equal("BA2490", store.Get(panel.Id)!.Params["flightNumber"]);
        Assert.Equal(WidgetStatus.Live, store.Get(panel.Id)!.Status);
    }

    [Fact]
    public async Task Filling_in_only_half_of_what_was_needed_is_refused()
    {
        var store = NewStore();
        var library = NewLibrary();
        var kind = FlightKind();
        kind.Params.Add(new WidgetParam { Name = "date", Description = "the date" });
        library.Upsert(kind);
        var panel = store.Reserve("My flight", WidgetSizes.Kpi, 80, null, null, proposed: false);
        store.Attach(panel.Id, kind, null);

        var result = await Call(WidgetLibraryTools.FillTool(store, library), new
        {
            id = panel.Id, @params = new[] { new { name = "flightNumber", value = "BA2490" } },
        });

        Assert.True(result.IsError);
        Assert.Contains("date", result.Content);
    }

    // ── A browser expression is JavaScript, and JavaScript is full of braces ──────────

    [Fact]
    public void A_regex_quantifier_in_an_expression_is_not_a_parameter()
    {
        // The four minutes of "Building the data feed" nobody could see into. A browser expression matching a
        // three-digit number was read as declaring a parameter called "3", so publishing was refused with "the loader
        // has {3} in it but you haven't declared that parameter" — about something that cannot be declared and must
        // never be substituted. The worker rewrote everything else twelve times over.
        var loader = new WidgetLoader
        {
            Mode = LoaderModes.Browser,
            Url = "https://www.example.com/orders/{seller}",
            Expression = @"(() => { const id = (document.title.match(/\d{3,}/) || [])[0]; " +
                         @"const code = document.body.innerText.match(/[A-Z]{3}/); return JSON.stringify({id, code}); })()",
        };

        Assert.Equal(new[] { "seller" }, loader.Holes());
    }

    [Fact]
    public void A_template_literal_in_an_expression_is_not_a_parameter()
    {
        // The same fault the other way round, and this half is silent: substituting into ${seller} turns a variable
        // reference into a syntax error in somebody's browser, with nothing anywhere to say so.
        var loader = new WidgetLoader
        {
            Mode = LoaderModes.Browser,
            Url = "https://www.example.com/{seller}",
            Expression = "(() => { const url = `https://x/${seller}/${page}`; return JSON.stringify({url}); })()",
        };

        Assert.Equal(new[] { "seller" }, loader.Holes());

        var filled = loader.For(new Dictionary<string, string> { ["seller"] = "2010techgeeks" });
        Assert.Equal("https://www.example.com/2010techgeeks", filled.Url);
        // Left alone, because it was never a hole. Substituting here would turn a variable reference into a syntax
        // error in somebody's browser, silently.
        Assert.Contains("${seller}", filled.Expression);
    }

    [Fact]
    public void Object_shorthand_in_an_expression_is_not_a_parameter()
    {
        // The case that settles why an expression cannot be policed at all: this is how half of these expressions
        // return their data, and it is indistinguishable from a parameter reference.
        var loader = new WidgetLoader
        {
            Mode = LoaderModes.Browser,
            Url = "https://www.example.com/{username}",
            Expression = "(() => { const followers = read(); return JSON.stringify({followers}); })()",
        };

        Assert.Equal(new[] { "username" }, loader.Holes());
    }

    [Fact]
    public void A_real_hole_is_still_filled_in_both_halves()
    {
        var loader = new WidgetLoader
        {
            Mode = LoaderModes.Browser,
            Url = "https://www.example.com/{seller}/sold",
            Expression = @"(() => JSON.stringify({seller: '{seller}', n: /\d{2}/.test('99')}))()",
        };

        var filled = loader.For(new Dictionary<string, string> { ["seller"] = "2010techgeeks" });

        Assert.Equal("https://www.example.com/2010techgeeks/sold", filled.Url);
        Assert.Contains("seller: '2010techgeeks'", filled.Expression);
        // The quantifier survives untouched — it is code, not a value.
        Assert.Contains(@"/\d{2}/", filled.Expression);
    }

    [Fact]
    public void An_unfilled_hole_is_left_as_it_was_so_the_loader_can_name_it()
    {
        var loader = new WidgetLoader { Mode = LoaderModes.Http, Url = "https://x/{seller}/{listing}" };

        var filled = loader.For(new Dictionary<string, string> { ["seller"] = "me" });

        Assert.Equal("https://x/me/{listing}", filled.Url);
        Assert.Equal(new[] { "listing" }, filled.Holes());
    }

    // ── Nothing repairs itself ───────────────────────────────────────────────────────

    private Widget Flight(WidgetStore store, WidgetLibrary library)
    {
        var kind = library.Upsert(FlightKind());
        var panel = store.Reserve("BA2490", WidgetSizes.Kpi, 80, null, null, proposed: false);
        store.Attach(panel.Id, kind, new Dictionary<string, string> { ["flightNumber"] = "BA2490" });
        return store.Get(panel.Id)!;
    }

    [Fact]
    public void A_throw_is_recorded_and_offered_never_acted_on()
    {
        // The whole of what used to happen automatically, and why it doesn't any more. A panel that threw once sent
        // ITSELF off to be rebuilt, on the word of whichever browser tab reported first — and a rebuild is a gamble
        // against something that was usually working a minute later. Asked for by the user after a panel they had just
        // had adjusted rewrote itself twice: once over a load that came back oddly, once over a stale tab's error.
        var store = NewStore();
        var panel = Flight(store, NewLibrary());

        Assert.True(store.Broke(panel.Id, "gate is not defined"));
        // The same fault reported again by a second render is one fault: React re-renders a boundary that caught.
        Assert.False(store.Broke(panel.Id, "gate is not defined"));

        var after = store.Get(panel.Id)!;
        Assert.Equal("gate is not defined", after.RenderError);
        // Recorded, shown, and waiting to be asked about.
        Assert.True(after.Ailing);
        Assert.Equal("gate is not defined", after.Wrong);
    }

    [Fact]
    public void A_load_that_keeps_failing_the_same_way_is_marked_and_left()
    {
        // It used to send the panel to be rewritten on the second identical failure. Sound as far as it goes — the
        // same message twice is not a bad night on the network — but "twice" is still routinely transient, and the
        // price of being wrong was a working panel replaced by a guess.
        var store = NewStore();
        var panel = Flight(store, NewLibrary());
        store.Loaded(panel.Id, "{\"status\":\"On time\"}", null);

        store.Loaded(panel.Id, null, "the page returned nothing");
        Assert.False(store.LoaderStuck(panel.Id));
        store.Loaded(panel.Id, null, "the page returned nothing");
        Assert.True(store.LoaderStuck(panel.Id));

        var after = store.Get(panel.Id)!;
        Assert.True(after.Ailing);
        // And it still has the last thing it knew, with its age on it.
        Assert.Contains("On time", after.Data);
    }

    [Fact]
    public void A_panel_seen_working_has_nothing_held_against_it()
    {
        var store = NewStore();
        var panel = Flight(store, NewLibrary());
        store.Broke(panel.Id, "gate is not defined");
        Assert.True(store.Get(panel.Id)!.Ailing);

        store.Looked(panel.Id, working: true, seen: "on time, gate B14");

        Assert.False(store.Get(panel.Id)!.Ailing);
        Assert.Null(store.Get(panel.Id)!.Wrong);
    }

    [Theory]
    // Chrome's phrasing, and Safari's. Only Chrome's was matched, so a tab on a phone running yesterday's bundle
    // reported a fault about a name that DOES exist, and a panel that had just been made to look right was sent
    // straight back to be rebuilt over it.
    [InlineData("Panel is not defined")]
    [InlineData("Can't find variable: Panel")]
    [InlineData("Pictures is not defined")]
    [InlineData("Can't find variable: history")]
    public void A_browser_running_an_older_bundle_is_told_to_reload(string message)
    {
        var store = NewStore();
        var panel = Flight(store, NewLibrary());

        Assert.False(store.Broke(panel.Id, message));

        var after = store.Get(panel.Id)!;
        Assert.Contains("older version", after.Error);
        // Not recorded as a fault in the panel: there is nothing to mend, and offering to mend it would be an
        // invitation to rewrite something that works.
        Assert.Null(after.RenderError);
    }

    [Fact]
    public void A_name_the_scope_does_not_have_is_a_real_fault()
    {
        // The other side of that guard. A component reaching for something the kit never offered is a genuine bug, and
        // mistaking it for a stale tab would leave it unmentionable.
        var store = NewStore();
        var panel = Flight(store, NewLibrary());

        Assert.True(store.Broke(panel.Id, "Can't find variable: Carousel"));
        Assert.True(store.Get(panel.Id)!.Ailing);
    }

    [Fact]
    public void Every_name_the_kit_offers_is_one_the_stale_tab_guard_knows()
    {
        // The guard is only as good as its list of names, and its list used to be hand-kept: eight names against the
        // kit's twenty-three. Every name missing from it is a stale tab mistaken for a broken panel.
        foreach (var name in WidgetTools.KitNames)
            Assert.Contains(name, WidgetScope.Names);
    }


    // ── Relevancy, and the page itself ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_page_is_ordered_by_what_matters_and_pinning_beats_all_of_it()
    {
        var store = NewStore();
        var library = NewLibrary();
        var kind = library.Upsert(FlightKind());

        Widget Add(string title, int priority)
        {
            var w = store.Reserve(title, WidgetSizes.Kpi, priority, null, null, proposed: false);
            store.Attach(w.Id, kind, new Dictionary<string, string> { ["flightNumber"] = title });
            store.Edit(w.Id, priority: priority);
            return w;
        }

        Add("Projects", 20);
        Add("BA2490", 85);
        var bins = Add("Bins", 5);

        Assert.Equal(new[] { "BA2490", "Projects", "Bins" }, store.All().Select(w => w.Title));
        store.Edit(bins.Id, pinned: true);
        Assert.Equal("Bins", store.All()[0].Title);
    }

    [Fact]
    public void A_build_interrupted_by_a_restart_says_so_rather_than_spinning_forever()
    {
        var path = Path.Combine(Path.GetTempPath(), $"widgets-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var first = new WidgetStore(path, json);
        var w = first.Reserve("BA2490", WidgetSizes.Kpi, 80, null, null, proposed: false);
        first.Building(w.Id, "7");   // and the process dies here — task 7 no longer exists

        var reopened = new WidgetStore(path, json).All()[0];
        Assert.Equal(WidgetStatus.Failed, reopened.Status);
        Assert.Contains("restart", reopened.Error);
    }

    [Fact]
    public void A_failed_rebuild_leaves_the_working_panel_alone()
    {
        var store = NewStore();
        var library = NewLibrary();
        var kind = library.Upsert(FlightKind());
        var panel = store.Reserve("BA2490", WidgetSizes.Kpi, 80, null, null, proposed: false);
        store.Attach(panel.Id, kind, new Dictionary<string, string> { ["flightNumber"] = "BA2490" });

        store.Failed(panel.Id, "couldn't find a better source");

        var after = store.Get(panel.Id)!;
        Assert.Equal(WidgetStatus.Live, after.Status);      // still renders
        Assert.Contains("better source", after.Error);      // and admits the problem
    }

    [Fact]
    public void Everything_survives_a_restart()
    {
        var wPath = Path.Combine(Path.GetTempPath(), $"widgets-{Guid.NewGuid():N}.json");
        var kPath = Path.Combine(Path.GetTempPath(), $"kinds-{Guid.NewGuid():N}.json");
        _paths.Add(wPath);
        _paths.Add(kPath);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var library = new WidgetLibrary(kPath, json);
        var store = new WidgetStore(wPath, json);
        var kind = library.Upsert(FlightKind());
        var panel = store.Reserve("BA2490", WidgetSizes.Kpi, 85, null, null, proposed: false);
        store.Attach(panel.Id, kind, new Dictionary<string, string> { ["flightNumber"] = "BA2490" });
        store.Loaded(panel.Id, "{\"status\":\"On time\"}", null);

        var reopenedKind = new WidgetLibrary(kPath, json).Get("flight-status")!;
        var reopenedPanel = new WidgetStore(wPath, json).All()[0];

        Assert.Equal("https://api.example.com/flights/{flightNumber}", reopenedKind.Loader.Url);
        Assert.Equal("flight.status", reopenedKind.Loader.Map["status"]);
        Assert.Equal(2, reopenedKind.Model.Count);
        Assert.Equal("BA2490", reopenedPanel.Params["flightNumber"]);
        Assert.Contains("On time", reopenedPanel.Data);
    }

    [Fact]
    public void A_first_visit_gets_real_panels_from_what_we_already_hold()
    {
        var store = NewStore();
        var library = NewLibrary();

        WidgetSeed.Ensure(store, library);

        Assert.Equal(3, store.All().Count);
        Assert.Equal(3, library.All().Count);
        Assert.All(store.All(), w =>
        {
            Assert.Equal(WidgetStatus.Live, w.Status);
            Assert.False(string.IsNullOrWhiteSpace(w.Kind));
        });
        // Seeded as ordinary kinds with internal loaders, so there is exactly one rendering path and one loading
        // path — a bug in either shows up on day one rather than only on the panels a model wrote.
        Assert.All(library.All(), k =>
        {
            Assert.Equal(LoaderModes.Internal, k.Loader.Mode);
            Assert.False(string.IsNullOrWhiteSpace(k.Code));
            Assert.NotEmpty(k.Model);
        });
    }

    [Fact]
    public void A_curated_page_is_never_re_stocked_behind_the_user()
    {
        var store = NewStore();
        var library = NewLibrary();
        var kind = library.Upsert(FlightKind());
        var w = store.Reserve("BA2490", WidgetSizes.Kpi, 80, null, null, proposed: false);
        store.Attach(w.Id, kind, new Dictionary<string, string> { ["flightNumber"] = "BA2490" });

        WidgetSeed.Ensure(store, library);

        Assert.Single(store.All());
    }

    // ---- a panel is photographed when it first SHOWS something, not when its build ends ----

    [Fact]
    public void A_panel_that_has_never_loaded_is_not_yet_worth_looking_at()
    {
        // The race that had a working panel rebuilt. Publishing is not loading: the build finishes, the photograph is
        // taken seconds later, and a panel whose source is the user's own logged-in browser is still showing its loading
        // branch — so the judge reports exactly that, and a correct panel is marked broken.
        var store = NewStore();
        var panel = store.Reserve("My listings", WidgetSizes.Tall, 80, null, "chat-1", proposed: false);

        Widget? photographed = null;
        store.FirstShowed = w => photographed = w;

        Assert.Null(store.Get(panel.Id)!.Data);
        Assert.Null(photographed);

        // Minutes later, the browser finally answers.
        store.Loaded(panel.Id, "{\"total\":3}", error: null);

        Assert.Equal(panel.Id, photographed?.Id);
        Assert.Equal("{\"total\":3}", store.Get(panel.Id)!.Data);
    }

    [Fact]
    public void A_failed_first_load_is_a_load_failure_not_something_to_photograph()
    {
        // Nothing to see, and the loader already knows the real reason. A photograph of a panel that never loaded adds
        // "it says loading" to an error that names the actual cause.
        var store = NewStore();
        var panel = store.Reserve("My listings", WidgetSizes.Tall, 80, null, "chat-1", proposed: false);

        Widget? photographed = null;
        store.FirstShowed = w => photographed = w;

        store.Loaded(panel.Id, dataJson: null, error: "the feed returned 401");

        Assert.Null(photographed);
        Assert.Equal("the feed returned 401", store.Get(panel.Id)!.Error);
    }

    [Fact]
    public void It_is_only_ever_offered_up_once()
    {
        // Every later refresh is a load too, and re-photographing on each one would spend a vision call every half hour
        // for the life of the panel.
        var store = NewStore();
        var panel = store.Reserve("My listings", WidgetSizes.Tall, 80, null, "chat-1", proposed: false);

        var times = 0;
        store.FirstShowed = _ => times++;

        store.Loaded(panel.Id, "{\"total\":3}", error: null);
        store.Looked(panel.Id, working: true, seen: "three listings");
        store.Loaded(panel.Id, "{\"total\":4}", error: null);
        store.Loaded(panel.Id, "{\"total\":5}", error: null);

        Assert.Equal(1, times);
    }

    public void Dispose()
    {
        foreach (var p in _paths) { try { File.Delete(p); } catch { } }
    }
}
