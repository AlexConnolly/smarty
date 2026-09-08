using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// A panel whose build stopped to ask something.
///
/// <para>
/// The camera panel asked for the Tapo credentials and its model, correctly, and then had nowhere to ask it: the
/// question went into the conversation the build belonged to, the home page showed "Building…", and the answer the
/// build was waiting for could not be given from the one screen the user was looking at. What makes the panel able
/// to ask is the pair of links recorded on it — which conversation and which task — so those are what is tested.
/// </para>
/// </summary>
public class WidgetQuestionTests
{
    private static WidgetStore Store() =>
        new(Path.Combine(Path.GetTempPath(), $"widgets-{Guid.NewGuid():N}.json"),
            new System.Text.Json.JsonSerializerOptions());

    [Fact]
    public void A_building_panel_remembers_which_task_and_conversation_to_ask_through()
    {
        var store = Store();
        var w = store.Reserve("Tapo CCTV camera", "tall", 60, "see the driveway", session: "camera", proposed: false);
        store.Building(w.Id, "5");

        var panel = store.Get(w.Id)!;
        Assert.Equal("camera", panel.Session);
        Assert.Equal("5", panel.TaskId);
    }

    [Fact]
    public void A_restart_leaves_the_link_intact_so_the_question_can_still_be_found()
    {
        // The conversation link has to survive, or a question asked by this build is unreachable from the panel.
        //
        // The TASK id does not, and that changed on purpose. A task from a process that no longer exists is not a job
        // anybody is doing, and left attached it reads as one: the page offers to cancel a build that is not running and
        // refuses to start another because something apparently already is. A build that stopped to ASK is the exception
        // and keeps both — see the test below, where the question is what makes the task worth keeping.
        var path = Path.Combine(Path.GetTempPath(), $"widgets-{Guid.NewGuid():N}.json");
        var json = new System.Text.Json.JsonSerializerOptions();

        var first = new WidgetStore(path, json);
        var w = first.Reserve("Tapo CCTV camera", "tall", 60, null, session: "camera", proposed: false);
        first.Building(w.Id, "5");

        var reopened = new WidgetStore(path, json).Get(w.Id)!;

        Assert.Equal(WidgetStatus.Failed, reopened.Status);
        Assert.Equal("camera", reopened.Session);
        Assert.Null(reopened.TaskId);
    }

    [Fact]
    public void The_question_itself_survives_a_restart_on_the_panel()
    {
        // The point of keeping it here. The hub drops a question when the run is re-driven or killed mid-flight,
        // both deliberately — and the panel still cannot finish without an answer, so the panel keeps the ask.
        var path = Path.Combine(Path.GetTempPath(), $"widgets-{Guid.NewGuid():N}.json");
        var json = new System.Text.Json.JsonSerializerOptions();

        var first = new WidgetStore(path, json);
        var w = first.Reserve("Tapo CCTV camera", "tall", 60, null, session: "camera", proposed: false);
        first.Building(w.Id, "5");
        first.Asked(w.Id, "Which model is it, and what are its admin credentials?");

        var reopened = new WidgetStore(path, json).Get(w.Id)!;

        Assert.Equal("Which model is it, and what are its admin credentials?", reopened.Asks);
        Assert.Equal("5", reopened.TaskId);
        Assert.Equal("camera", reopened.Session);
    }

    [Fact]
    public void A_finished_panel_is_no_longer_asking_anything()
    {
        var store = Store();
        var w = store.Reserve("Tapo CCTV camera", "tall", 60, null, session: "camera", proposed: false);
        store.Building(w.Id, "5");
        store.Asked(w.Id, "Which model is it?");

        store.Attach(w.Id, new WidgetKind { Name = "camera-still", Code = "return <div/>" }, null);

        Assert.Null(store.Get(w.Id)!.Asks);
    }

    [Fact]
    public void A_build_that_gave_up_stops_asking_too()
    {
        var store = Store();
        var w = store.Reserve("Tapo CCTV camera", "tall", 60, null, session: "camera", proposed: false);
        store.Building(w.Id, "5");
        store.Asked(w.Id, "Which model is it?");

        store.Failed(w.Id, "no url returns image bytes");

        Assert.Null(store.Get(w.Id)!.Asks);
    }

    [Fact]
    public void A_panel_remembers_the_numbers_its_kind_asked_to_track()
    {
        // The point of memory: a series you can subtract. One reading is a fact, two are a trend.
        var one = WidgetMemory.Read("{\"views\":247,\"price\":\"£203.07\",\"title\":\"Kingfishr LP\"}",
            new[] { "views", "price", "title" });

        Assert.Equal(247, one["views"]);
        // Currency and separators taken off, because the panels most worth tracking are the scraped ones.
        Assert.Equal(203.07, one["price"], 2);
        // Not a number, not remembered — a series of titles is not something you can graph.
        Assert.False(one.ContainsKey("title"));
    }

    [Fact]
    public void Readings_closer_together_than_the_gap_replace_rather_than_pile_up()
    {
        // The refresher can load a panel every fifteen seconds. Keeping all of those would make a day's history
        // nothing but the last hour, and the graph a flat line at today's value.
        var start = new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);
        var series = new List<WidgetSample>();

        series = WidgetMemory.Append(series, new() { ["views"] = 10 }, start);
        series = WidgetMemory.Append(series, new() { ["views"] = 11 }, start.AddSeconds(30));
        series = WidgetMemory.Append(series, new() { ["views"] = 20 }, start.AddMinutes(10));

        Assert.Equal(2, series.Count);
        // The freshest value wins within the window, so the series never ends on a stale point.
        Assert.Equal(11, series[0].Values["views"]);
        Assert.Equal(20, series[1].Values["views"]);
    }

    [Fact]
    public void The_series_is_handed_over_already_differenced()
    {
        var start = new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);
        var series = new List<WidgetSample>();
        series = WidgetMemory.Append(series, new() { ["views"] = 200 }, start);
        series = WidgetMemory.Append(series, new() { ["views"] = 250 }, start.AddHours(1));

        var shaped = (Dictionary<string, object>)WidgetMemory.Shape(series);
        var views = shaped["views"];
        var change = (double)views.GetType().GetProperty("change")!.GetValue(views)!;
        var percent = (double?)views.GetType().GetProperty("percent")!.GetValue(views)!;

        Assert.Equal(50, change);
        Assert.Equal(25, percent!.Value, 1);
    }

    [Fact]
    public void An_old_size_becomes_the_nearest_of_the_three()
    {
        // The 4×1 letterbox is the one that went: a box one row high could not hold what was put in it, and the
        // quote panel stopping mid-sentence was the proof.
        Assert.Equal(WidgetSizes.Kpi, WidgetSizes.Migrate("small"));
        Assert.Equal(WidgetSizes.Wide, WidgetSizes.Migrate("large"));
        Assert.Equal(WidgetSizes.Wide, WidgetSizes.Migrate("wide"));
        Assert.Equal(WidgetSizes.Tall, WidgetSizes.Migrate("tall"));
        Assert.Equal(WidgetSizes.Kpi, WidgetSizes.Migrate("nonsense"));
    }

    [Fact]
    public void An_offer_made_of_punctuation_is_not_an_offer()
    {
        // Verbatim from the home page at 07:13: "Build ... for your home page?" with "..." underneath. The schema
        // demanded a title even when declining, so declining produced placeholders, and a whitespace check let them
        // through.
        var sayable = typeof(WidgetSuggester).GetMethod("Sayable",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        bool Ok(string? text, int min) => (bool)sayable.Invoke(null, new object?[] { text, min })!;

        Assert.False(Ok("...", 3));
        Assert.False(Ok("---", 3));
        Assert.False(Ok("   ", 3));
        Assert.False(Ok(null, 3));
        Assert.False(Ok("ok", 3));               // too short to be a title
        Assert.True(Ok("Tube status", 3));
        Assert.True(Ok("Your holiday has a real date attached.", 12));
        // A reason has to be a sentence, not a word.
        Assert.False(Ok("useful", 12));
    }

    [Fact]
    public void A_kind_in_use_cannot_be_reshaped_only_fixed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kinds-{Guid.NewGuid():N}.json");
        var library = new WidgetLibrary(path, new System.Text.Json.JsonSerializerOptions());
        library.Upsert(new WidgetKind
        {
            Name = "city-weather",
            Code = "return <div/>",
            Params = new List<WidgetParam> { new() { Name = "lat" }, new() { Name = "lon" } },
        });

        // Same parameters, new component: a repair, and it must stay allowed — one fix mends every panel.
        Assert.Empty(library.ShapeChange(new WidgetKind
        {
            Name = "city-weather",
            Params = new List<WidgetParam> { new() { Name = "lat" }, new() { Name = "lon" } },
        }));

        // Renamed parameters: a different kind wearing the same name, and every instance would be orphaned.
        var changed = library.ShapeChange(new WidgetKind
        {
            Name = "city-weather",
            Params = new List<WidgetParam> { new() { Name = "latitude" }, new() { Name = "longitude" } },
        });
        Assert.Contains("lat", changed);
        Assert.Contains("latitude", changed);

        // A parameter QUIETLY DROPPED is as breaking as one added, so both directions count.
        Assert.Contains("lon", library.ShapeChange(new WidgetKind
        {
            Name = "city-weather",
            Params = new List<WidgetParam> { new() { Name = "lat" } },
        }));
    }

    [Fact]
    public void A_kind_nobody_has_used_yet_can_be_anything()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kinds-{Guid.NewGuid():N}.json");
        var library = new WidgetLibrary(path, new System.Text.Json.JsonSerializerOptions());

        Assert.Empty(library.ShapeChange(new WidgetKind
        {
            Name = "brand-new",
            Params = new List<WidgetParam> { new() { Name = "whatever" } },
        }));
    }

    [Fact]
    public void A_republish_that_renames_parameters_says_so_instead_of_failing_forever()
    {
        // Two weather panels shared the city-weather kind. A 7-day build republished it taking latitude/longitude,
        // the older panel still held lat/lon, and its feed url kept the literal "{latitude}" in it — so every load
        // failed, the footer said "stale" in red, and nothing said why.
        var store = Store();
        var w = store.Reserve("London weather", "tall", 60, null, session: "s", proposed: false);
        var before = new WidgetKind
        {
            Name = "city-weather",
            Code = "return <div>{data}</div>",
            Params = new List<WidgetParam> { new() { Name = "lat", Description = "latitude" } },
        };
        store.Attach(w.Id, before, new Dictionary<string, string> { ["lat"] = "51.5" });
        Assert.Equal(WidgetStatus.Live, store.Get(w.Id)!.Status);

        var renamed = new WidgetKind
        {
            Name = "city-weather",
            Code = "return <div>{data}</div>",
            Params = new List<WidgetParam> { new() { Name = "latitude", Description = "latitude" } },
        };
        store.Requalify(renamed);

        var after = store.Get(w.Id)!;
        Assert.Equal(WidgetStatus.Failed, after.Status);
        Assert.Contains("latitude", after.Error);
        Assert.Contains("lat", after.Error);
        // And it stops trying, rather than failing on a timer for ever.
        Assert.Null(after.NextRefresh);
    }

    [Fact]
    public void A_republish_that_keeps_its_parameters_leaves_its_panels_alone()
    {
        var store = Store();
        var w = store.Reserve("London weather", "tall", 60, null, session: "s", proposed: false);
        var kind = new WidgetKind
        {
            Name = "city-weather",
            Code = "return <div>{data}</div>",
            Params = new List<WidgetParam> { new() { Name = "lat", Description = "latitude" } },
        };
        store.Attach(w.Id, kind, new Dictionary<string, string> { ["lat"] = "51.5" });

        store.Requalify(kind);

        Assert.Equal(WidgetStatus.Live, store.Get(w.Id)!.Status);
        Assert.Null(store.Get(w.Id)!.Error);
    }

    [Fact]
    public void A_build_that_stops_without_saying_so_gives_up_on_its_own()
    {
        // The camera panel showed "Fixing…" for forty minutes after its build timed out, because the code that
        // stops a panel spinning sat below an early return taken on cancellation. This is the backstop that does
        // not care why: past the deadline with nothing published, the panel stops pretending.
        var store = Store();
        var w = store.Reserve("Tapo CCTV camera", "tall", 60, null, session: "camera", proposed: false);
        store.Building(w.Id, "6");

        Assert.Empty(store.Stalled(DateTimeOffset.UtcNow));
        Assert.Single(store.Stalled(DateTimeOffset.UtcNow.AddMinutes(21)));
    }

    [Fact]
    public void A_build_waiting_on_the_user_is_patient_rather_than_stalled()
    {
        // It could sit there for a week and still be correct — the answer is what it is waiting for, and giving up
        // on it would throw away the question the user is about to answer.
        var store = Store();
        var w = store.Reserve("Tapo CCTV camera", "tall", 60, null, session: "camera", proposed: false);
        store.Building(w.Id, "6");
        store.Asked(w.Id, "Which model is it?");

        Assert.Empty(store.Stalled(DateTimeOffset.UtcNow.AddDays(7)));
    }

    [Fact]
    public void A_rebuild_is_told_what_the_panel_already_is()
    {
        // It asked the user for "the item id of the vinyl listing you want tracked" while rebuilding the panel that
        // carried itemId=327313414035. The brief never mentioned it, and told it not to ask anything in the same
        // breath.
        var store = Store();
        var w = store.Reserve("eBay vinyl sale", "tall", 60, "how the listing is doing", session: "s", proposed: false);
        store.Attach(w.Id, new WidgetKind { Name = "ebay-listing", Code = "return <div/>" },
            new Dictionary<string, string> { ["itemId"] = "327313414035" });

        var brief = WidgetTools.Brief(store.Get(w.Id)!, "track the listing");

        Assert.Contains("REBUILD", brief);
        Assert.Contains("ebay-listing", brief);
        Assert.Contains("itemId=327313414035", brief);
    }

    [Fact]
    public void A_first_build_is_not_described_as_a_rebuild()
    {
        var store = Store();
        var w = store.Reserve("Something new", "tall", 60, null, session: "s", proposed: false);

        var brief = WidgetTools.Brief(store.Get(w.Id)!, "show me a thing");

        Assert.DoesNotContain("REBUILD", brief);
    }

    [Fact]
    public void An_answer_arrives_with_the_question_it_answers()
    {
        // Verbatim from the run that went wrong: the build asked for the camera's credentials, the user sent them,
        // and the worker called github_list on "user/pass" as though it were an owner/repo. Two words and a slash
        // with no context around them is a repo; with the question restated it is an answer.
        var note = Orchestrator.AnsweredNote("What are the camera's admin credentials?");

        Assert.Contains("What are the camera's admin credentials?", note);
        Assert.Contains("answer TO THAT QUESTION", note);
        Assert.Contains("repository name", note);   // the exact misreading this exists to stop
    }

    [Fact]
    public void A_run_that_was_waiting_keeps_its_question_across_a_restart()
    {
        // The other half: the hub is what the panel reads the question out of, and a waiting run is not an
        // interrupted one. Flattening the two is what left the question on screen and unanswerable.
        var path = Path.Combine(Path.GetTempPath(), $"control-{Guid.NewGuid():N}.json");
        var json = new System.Text.Json.JsonSerializerOptions();

        var hub = new ControlHub(path, json);
        hub.Ingest("camera", "web", "working", "{\"id\":\"5\",\"task\":\"Build the camera panel\"}");
        hub.Ingest("camera", "web", "question", "{\"id\":\"5\",\"question\":\"Which model is it?\"}");

        var run = new ControlHub(path, json).RunsFor("camera").FirstOrDefault(r => r.TaskId == "5");

        Assert.NotNull(run);
        Assert.Equal("waiting", run!.Status);
        Assert.Equal("Which model is it?", run.PendingQuestion);
    }

    [Fact]
    public void A_run_that_was_merely_running_comes_back_with_nothing_to_ask()
    {
        var path = Path.Combine(Path.GetTempPath(), $"control-{Guid.NewGuid():N}.json");
        var json = new System.Text.Json.JsonSerializerOptions();

        var hub = new ControlHub(path, json);
        hub.Ingest("camera", "web", "working", "{\"id\":\"7\",\"task\":\"Build something\"}");

        var run = new ControlHub(path, json).RunsFor("camera").FirstOrDefault(r => r.TaskId == "7");

        Assert.NotNull(run);
        Assert.Equal("interrupted", run!.Status);
        Assert.Null(run.PendingQuestion);
    }
}
