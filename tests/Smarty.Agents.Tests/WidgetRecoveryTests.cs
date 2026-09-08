using System.Text.Json;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Why a panel appeared to keep failing, and the two things that were actually wrong.
///
/// <para>
/// One panel spent an afternoon showing an error. Neither cause was the panel: its build kept dying with the process,
/// and it was told to ask its owner for a rebuild that needed no decision from anybody — so every restart cost a manual
/// nudge. Underneath, an empty list was being reported as a source that carried no lists at all, which turned "nothing
/// on your list" into a plain failure.
/// </para>
/// </summary>
public class WidgetRecoveryTests : IDisposable
{
    private readonly List<string> _paths = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private WidgetStore NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"widgets-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        return new WidgetStore(path, Json);
    }

    /// <summary>A panel left mid-build, which is the state a restart interrupts.</summary>
    private static Widget Building(WidgetStore store, string title, int already = 0)
    {
        var w = store.Reserve(title, "wide", 60, null, null, proposed: false);
        if (already > 0) w.RestartRetries = already;
        store.Building(w.Id, "task-1");
        return w;
    }

    /// <summary>A working kind, so a panel can be live rather than merely reserved.</summary>
    private static WidgetKind WeatherKind() => new()
    {
        Name = "city-weather",
        Title = "City weather",
        Description = "the temperature in a city",
        Model = { new WidgetField { Name = "temp", Type = "number", Description = "degrees" } },
        Loader = new WidgetLoader { Mode = LoaderModes.Http, Url = "https://api.example.com/weather" },
        Code = "return <Stat label=\"Now\" value={data.temp} />",
        Refresh = "every 2 hours",
    };

    private WidgetStore Reopened(WidgetStore from)
    {
        // Reopening is what a restart IS, as far as the store is concerned.
        var path = _paths[^1];
        _ = from;
        return new WidgetStore(path, Json);
    }

    [Fact]
    public void A_panel_that_already_works_survives_a_restart_mid_change()
    {
        // What actually broke somebody's home page. An hourly weather panel that had worked for days was being adjusted
        // when the process went, and came back as "the build was interrupted" — then got rebuilt from its original
        // one-line description, losing everything it had been adjusted into. A panel being CHANGED is not a panel being
        // built: it already has a component and a feed that work, and the honest outcome is the panel it already was.
        var store = NewStore();
        var w = store.Reserve("Hourly weather", "wide", 60, null, null, proposed: false);
        store.Attach(w.Id, WeatherKind(), new Dictionary<string, string>());
        store.Building(w.Id, "task-9");

        var after = Reopened(store);
        var same = after.Get(w.Id)!;

        Assert.Equal(WidgetStatus.Live, same.Status);
        Assert.Equal("city-weather", same.Kind);
        Assert.Null(same.Error);
        Assert.False(same.Interrupted);
        // And nothing is claiming to be working on it.
        Assert.Null(same.TaskId);
        Assert.Empty(after.ClaimInterrupted());
    }

    [Fact]
    public void A_dead_task_stops_being_attached_to_the_panel()
    {
        // The other half of the same complaint: the panel kept pointing at a task from a process that no longer exists,
        // so the page showed a job attached to it, offered to cancel a build nobody was doing, and refused to start
        // anything because something was apparently already running.
        var store = NewStore();
        var building = Building(store, "Dinner spots near Fitzrovia");

        var after = Reopened(store);

        Assert.Null(after.Get(building.Id)!.TaskId);
    }

    [Fact]
    public void A_build_waiting_on_an_answer_keeps_its_question_and_its_task()
    {
        // Not interrupted by anything — waiting on somebody. The question is the proof, and the answer is what restarts
        // it, so throwing the task away would strand the one thing standing between the panel and working.
        var store = NewStore();
        var asking = Building(store, "Front door camera");
        store.Asked(asking.Id, "What's the camera's password?");

        var after = Reopened(store);
        var same = after.Get(asking.Id)!;

        Assert.Equal(WidgetStatus.Building, same.Status);
        Assert.Equal("What's the camera's password?", same.Asks);
        Assert.Equal("task-1", same.TaskId);
    }

    [Fact]
    public void A_build_killed_by_a_restart_is_owed_another_go_rather_than_asking()
    {
        var store = NewStore();
        var w = Building(store, "Dinner spots near Fitzrovia");

        var after = Reopened(store);
        var owed = after.Get(w.Id)!;

        Assert.Equal(WidgetStatus.Failed, owed.Status);
        Assert.True(owed.Interrupted);
        Assert.Contains("Picking it up again", owed.Error);
    }

    [Fact]
    public void Claiming_it_takes_the_flag_off_so_two_callers_cannot_both_rebuild_it()
    {
        var store = NewStore();
        var w = Building(store, "Dinner spots");
        var after = Reopened(store);

        var first = after.ClaimInterrupted();
        var second = after.ClaimInterrupted();

        Assert.Equal(w.Id, Assert.Single(first).Id);
        Assert.Empty(second);
        Assert.Equal(1, after.Get(w.Id)!.RestartRetries);
    }

    [Fact]
    public void It_stops_making_restarts_good_once_they_stop_looking_like_restarts()
    {
        // The guard that keeps a crash loop from becoming a build loop. Past the cap it goes back to asking, which is the
        // honest signal that something other than a restart is wrong.
        var store = NewStore();
        var w = Building(store, "Dinner spots", already: WidgetStore.RestartRetryLimit);

        var after = Reopened(store);
        var stuck = after.Get(w.Id)!;

        Assert.Equal(WidgetStatus.Failed, stuck.Status);
        Assert.False(stuck.Interrupted);
        Assert.Contains("Ask again", stuck.Error);
        Assert.Empty(after.ClaimInterrupted());
    }

    [Fact]
    public void A_panel_that_was_not_mid_build_is_left_alone()
    {
        var store = NewStore();
        var live = store.Reserve("Today's eBay listings", "wide", 60, null, null, proposed: false);
        // Attaching to a kind is the moment it becomes real and goes live — the state a restart must not disturb.
        store.Attach(live.Id, new WidgetKind { Name = "ebay-listings" }, null);

        var after = Reopened(store);

        Assert.Equal(WidgetStatus.Live, after.Get(live.Id)!.Status);
        Assert.False(after.Get(live.Id)!.Interrupted);
        Assert.Empty(after.ClaimInterrupted());
    }

    [Fact]
    public void An_empty_list_is_a_real_answer_and_not_a_missing_field()
    {
        // The bug that turned an emptied list into "the source didn't carry lists". Panels already have a line for
        // nothing to show; the loader is checking the field is THERE, not that it is interesting.
        var kind = new WidgetKind
        {
            Name = "places-list",
            Model = new List<WidgetField>
            {
                new() { Name = "lists", Type = "list", Description = "The lists.", Required = true },
            },
        };

        var loaded = WidgetDataLoader.Shape(kind, new WidgetLoader { Internal = "lists" }, "{\"lists\":[]}");

        Assert.True(loaded.Worked, loaded.Error);
    }

    [Fact]
    public void A_field_that_genuinely_is_not_there_still_fails()
    {
        var kind = new WidgetKind
        {
            Name = "places-list",
            Model = new List<WidgetField>
            {
                new() { Name = "lists", Type = "list", Description = "The lists.", Required = true },
            },
        };

        var loaded = WidgetDataLoader.Shape(kind, new WidgetLoader { Internal = "lists" }, "{\"other\":[1]}");

        Assert.False(loaded.Worked);
        Assert.Contains("didn't carry lists", loaded.Error);
    }

    [Fact]
    public void A_null_field_is_still_an_absence()
    {
        var kind = new WidgetKind
        {
            Name = "places-list",
            Model = new List<WidgetField>
            {
                new() { Name = "lists", Type = "list", Description = "The lists.", Required = true },
            },
        };

        Assert.False(WidgetDataLoader
            .Shape(kind, new WidgetLoader { Internal = "lists" }, "{\"lists\":null}").Worked);
    }

    public void Dispose()
    {
        foreach (var path in _paths)
            try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
