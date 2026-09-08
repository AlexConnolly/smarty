using System.Text.Json;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Which day a list item is for, and what that makes possible.
///
/// <para>
/// The complaint that produced this: a meal plan was asked for and produced, seven dinners went into a list, and
/// the home page said nothing whatsoever about dinner. The data had been there the whole time. Nothing could ask
/// it a question about today, because an ordered list of seven things does not say which one is Tuesday — and
/// ordinal position can't say it either, since one reorder or one insertion shifts the whole week by a day.
/// </para>
/// <para>
/// A DAY, not a time. Something wanted at a moment is a scheduled task, which goes off and does something. This
/// only answers "is this one today".
/// </para>
/// </summary>
public class AgendaTests : IDisposable
{
    private readonly List<string> _paths = new();

    private ProjectListStore NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lists-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        return new ProjectListStore(path, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static readonly DateOnly Monday = new(2026, 8, 17);

    private static readonly string[] Week =
    {
        "Chicken traybake", "Chilli", "Salmon and greens", "Pasta puttanesca", "Fish and chips",
    };

    private static Dictionary<string, DateOnly?> On(params (string Item, DateOnly? Day)[] pairs) =>
        pairs.ToDictionary(p => p.Item, p => p.Day, StringComparer.OrdinalIgnoreCase);

    // ── Putting things on days ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_item_carries_the_day_it_is_for()
    {
        var store = NewStore();
        var list = store.Create("dinners", "This week's dinners", Week, checklist: true);

        var result = store.Schedule(list.Id, On(("Chilli", Monday.AddDays(1))));

        Assert.Equal(1, result!.Value.Changed);
        Assert.Equal(Monday.AddDays(1), result.Value.List.DateOf("Chilli"));
        Assert.Null(result.Value.List.DateOf("Chicken traybake"));   // undated is the default and stays so
    }

    [Fact]
    public void Items_are_named_the_loose_way_everything_else_here_is_named()
    {
        // The one matching rule: a worker that wrote "Chicken traybake with lemon" should be able to date
        // "chicken traybake" without the two agreeing character for character.
        var store = NewStore();
        var list = store.Create("dinners", "Dinners", new[] { "Chicken traybake with lemon" });

        store.Schedule(list.Id, On(("chicken traybake", Monday)));

        // Stored against the item's OWN text, so the date still lines up when the list is read back.
        Assert.Equal(Monday, store.Get(list.Id)!.DateOf("Chicken traybake with lemon"));
    }

    [Fact]
    public void A_null_day_clears_it()
    {
        var store = NewStore();
        var list = store.Create("dinners", "Dinners", Week);
        store.Schedule(list.Id, On(("Chilli", Monday)));

        var result = store.Schedule(list.Id, On(("Chilli", null)));

        Assert.Equal(1, result!.Value.Changed);
        Assert.Null(result.Value.List.DateOf("Chilli"));
    }

    [Fact]
    public void Setting_the_day_it_already_has_changes_nothing()
    {
        var store = NewStore();
        var list = store.Create("dinners", "Dinners", Week);
        store.Schedule(list.Id, On(("Chilli", Monday)));

        Assert.Equal(0, store.Schedule(list.Id, On(("Chilli", Monday)))!.Value.Changed);
    }

    [Fact]
    public void Words_that_match_nothing_report_it_rather_than_passing_quietly()
    {
        // Silence here would have a worker believe it had planned the week.
        var store = NewStore();
        var list = store.Create("dinners", "Dinners", Week);

        Assert.Equal(0, store.Schedule(list.Id, On(("Beef wellington", Monday)))!.Value.Changed);
    }

    [Fact]
    public void There_is_no_such_list_is_said_rather_than_thrown()
    {
        Assert.Null(NewStore().Schedule("nope", On(("Chilli", Monday))));
    }

    [Fact]
    public void Removing_an_item_takes_its_day_with_it()
    {
        // Otherwise it keeps turning up in the day's view with nothing left to tick and no way to tell where it
        // came from — the same ghost a stale tick used to leave in the count.
        var store = NewStore();
        var list = store.Create("dinners", "Dinners", Week);
        store.Schedule(list.Id, On(("Chilli", Monday)));

        store.Update(list.Id, remove: new[] { "Chilli" });

        Assert.Empty(store.Get(list.Id)!.Dates);
        Assert.False(store.Get(list.Id)!.Dated);
    }

    [Fact]
    public void Dates_survive_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lists-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var first = new ProjectListStore(path, json);
        var list = first.Create("dinners", "Dinners", Week);
        first.Schedule(list.Id, On(("Chilli", Monday)));

        Assert.Equal(Monday, new ProjectListStore(path, json).Get(list.Id)!.DateOf("Chilli"));
    }

    [Fact]
    public void A_list_written_before_dates_existed_still_loads()
    {
        // The parallel dictionary earns its keep here: the stored JSON has no Dates at all.
        var path = Path.Combine(Path.GetTempPath(), $"lists-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        File.WriteAllText(path,
            "[{\"id\":\"abc123\",\"project\":\"dinners\",\"title\":\"Dinners\"," +
            "\"items\":[\"Chilli\"],\"done\":[],\"checklist\":true}]");

        var store = new ProjectListStore(path, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var list = store.Get("abc123");

        Assert.NotNull(list);
        Assert.Empty(list!.Dates);
        Assert.Null(list.DateOf("Chilli"));
        Assert.Equal(1, store.Schedule("abc123", On(("Chilli", Monday)))!.Value.Changed);
    }

    // ── Asking what's on ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Todays_dinner_is_answerable()
    {
        // The whole point. This is the question the meal plan could not answer.
        var store = NewStore();
        var list = store.Create("dinners", "This week's dinners", Week, checklist: true);
        store.Schedule(list.Id, On(
            ("Chicken traybake", Monday),
            ("Chilli", Monday.AddDays(1)),
            ("Salmon and greens", Monday.AddDays(2))));

        var today = store.Agenda(Monday);

        var entry = Assert.Single(today);
        Assert.Equal("Chicken traybake", entry.Item);
        Assert.Equal("This week's dinners", entry.ListTitle);
        Assert.Equal("dinners", entry.Project);
        Assert.False(entry.Done);
    }

    [Fact]
    public void The_window_holds_the_days_asked_for_and_no_others()
    {
        var store = NewStore();
        var list = store.Create("dinners", "Dinners", Week);
        store.Schedule(list.Id, On(
            ("Chicken traybake", Monday.AddDays(-1)),   // yesterday
            ("Chilli", Monday),
            ("Salmon and greens", Monday.AddDays(1)),
            ("Pasta puttanesca", Monday.AddDays(5))));  // beyond it

        var twoDays = store.Agenda(Monday, 2);

        Assert.Equal(new[] { "Chilli", "Salmon and greens" }, twoDays.Select(e => e.Item));
    }

    [Fact]
    public void The_day_comes_out_in_order_across_every_list_and_project()
    {
        // "What's on today" isn't a question about a project. The meal plan, the jobs before the trip and the bins
        // all land on the same Tuesday, and making you pick a project first is the same wasted step as making you
        // go and find it.
        var store = NewStore();
        var dinners = store.Create("dinners", "Dinners", new[] { "Chilli" });
        var trip = store.Create("holiday", "Before we go", new[] { "Renew the passport", "Order euros" });

        store.Schedule(dinners.Id, On(("Chilli", Monday.AddDays(1))));
        store.Schedule(trip.Id, On(("Renew the passport", Monday), ("Order euros", Monday.AddDays(1))));

        var window = store.Agenda(Monday, 2);

        Assert.Equal(3, window.Count);
        Assert.Equal("Renew the passport", window[0].Item);        // today first
        Assert.Equal(Monday, window[0].On);
        Assert.All(window.Skip(1), e => Assert.Equal(Monday.AddDays(1), e.On));
        Assert.Contains(window, e => e.Project == "dinners");
        Assert.Contains(window, e => e.Project == "holiday");
    }

    [Fact]
    public void A_ticked_item_still_shows_up_ticked_rather_than_vanishing()
    {
        // Tonight's dinner doesn't stop being tonight's dinner once you've eaten it — and a row that disappears on
        // being ticked leaves nothing to un-tick when the click was a mistake.
        var store = NewStore();
        var list = store.Create("dinners", "Dinners", Week, checklist: true);
        store.Schedule(list.Id, On(("Chicken traybake", Monday)));
        store.Check(list.Id, new[] { "Chicken traybake" }, null);

        var entry = Assert.Single(store.Agenda(Monday));
        Assert.True(entry.Done);
    }

    [Fact]
    public void Nothing_dated_means_nothing_to_show()
    {
        var store = NewStore();
        store.Create("dinners", "Dinners", Week, checklist: true);

        Assert.Empty(store.Agenda(Monday, 7));
    }

    // ── Reading it back ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_list_reads_back_with_its_days_on_it()
    {
        // Without this, a model that had already planned the week would read the list, see seven undated dinners,
        // and set them all again over the top of the ones it had just written.
        var store = NewStore();
        var list = store.Create("dinners", "Dinners", Week);
        store.Schedule(list.Id, On(("Chilli", Monday)));

        var described = store.Describe("dinners");

        Assert.Contains("Mon 17 Aug — Chilli", described);
        Assert.Contains("Chicken traybake", described);          // the undated ones read plainly
        Assert.DoesNotContain("— Chicken traybake", described);
    }

    // ── The tools the orchestrator can actually reach ───────────────────────────────────────────────────

    [Fact]
    public void Every_list_tool_is_reachable_by_the_prefix_the_orchestrator_dispatches_on()
    {
        // The orchestrator OFFERS whatever ProjectListTools.All returns, and dispatches on the "list_" prefix.
        // Naming the tools one by one is what broke before: list_check was added, the dispatch case still said
        // "list_write or list_update or list_delete", and the model could see the tool, call it, and be told the
        // tool doesn't exist — with nothing left to try, because it had already done the right thing. This is the
        // guard that keeps the next tool added here from landing in the same hole.
        var names = ProjectListTools.All(NewStore(), () => "dinners").Select(t => t.Name).ToList();

        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.StartsWith("list_", n));
        // Two names the orchestrator handles itself and which must never be shadowed by a list tool.
        Assert.DoesNotContain("list_projects", names);
        Assert.DoesNotContain("list_tasks", names);
        Assert.Contains("list_schedule", names);
        Assert.Contains("list_check", names);
    }

    public void Dispose()
    {
        foreach (var p in _paths) { try { File.Delete(p); } catch { } }
    }
}
