using System.Text.Json;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Lists you work THROUGH.
///
/// Two of the three lists in the real store are criteria — "preferred amenities: all-inclusive, pool" — where a
/// tick would mean nothing. The third is a 35-item shopping list, which is a job, and the only interesting
/// question about it is which ones are done. So the purpose is stated rather than guessed at.
///
/// The reason this is worth more than a checkbox: a shopping run that stops halfway now leaves the list showing
/// exactly what landed, so the next attempt starts from the remainder instead of doing all thirty-five again.
/// </summary>
public class ChecklistTests : IDisposable
{
    private readonly List<string> _paths = new();

    private ProjectListStore NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lists-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        return new ProjectListStore(path, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static readonly string[] Shop = { "Spinach", "Garlic", "Cherry tomatoes", "Halloumi" };

    [Fact]
    public void An_ordinary_list_has_nothing_to_tick()
    {
        var store = NewStore();
        var list = store.Create("holiday", "preferred amenities", new[] { "all inclusive", "pool" });

        Assert.False(list.Checklist);
        Assert.Equal(0, list.DoneCount);
    }

    [Fact]
    public void A_checklist_records_what_is_done()
    {
        var store = NewStore();
        var list = store.Create("shop", "Shopping list", Shop, checklist: true);

        var after = store.Check(list.Id, new[] { "Spinach", "Garlic" }, null);

        Assert.NotNull(after);
        Assert.Equal(2, after!.Value.Changed);
        Assert.Equal(2, after.Value.List.DoneCount);
        Assert.True(after.Value.List.IsDone("Spinach"));
        Assert.False(after.Value.List.IsDone("Halloumi"));
    }

    [Fact]
    public void An_item_can_be_named_the_way_a_person_would_say_it()
    {
        // A worker that just put "Cherry tomatoes" in a basket shouldn't have to reproduce the list's exact
        // wording to record it. Same loose matching that removal already uses.
        var store = NewStore();
        var list = store.Create("shop", "Shopping list", Shop, checklist: true);

        store.Check(list.Id, new[] { "cherry tomatoes" }, null);

        Assert.True(store.Get(list.Id)!.IsDone("Cherry tomatoes"));
    }

    [Fact]
    public void A_tick_is_stored_as_the_lists_own_wording()
    {
        // Otherwise the ticks and the items drift apart and the list stops adding up when read back.
        var store = NewStore();
        var list = store.Create("shop", "Shopping list", Shop, checklist: true);

        store.Check(list.Id, new[] { "GARLIC" }, null);

        Assert.Contains("Garlic", store.Get(list.Id)!.Done);
    }

    [Fact]
    public void Ticking_something_makes_it_a_checklist()
    {
        // The 35-item shopping list already in the store was written before any of this existed. Work starting
        // on it is a better signal of what it is than the flag it was never given.
        var store = NewStore();
        var list = store.Create("shop", "Shopping list", Shop);   // no flag
        Assert.False(list.Checklist);

        store.Check(list.Id, new[] { "Spinach" }, null);

        Assert.True(store.Get(list.Id)!.Checklist);
    }

    [Fact]
    public void A_mistake_can_be_unticked()
    {
        var store = NewStore();
        var list = store.Create("shop", "Shopping list", Shop, checklist: true);
        store.Check(list.Id, new[] { "Spinach", "Garlic" }, null);

        var after = store.Check(list.Id, null, new[] { "Garlic" });

        Assert.Equal(1, after!.Value.List.DoneCount);
        Assert.True(after.Value.List.IsDone("Spinach"));
    }

    [Fact]
    public void Ticking_the_same_thing_twice_changes_nothing()
    {
        var store = NewStore();
        var list = store.Create("shop", "Shopping list", Shop, checklist: true);
        store.Check(list.Id, new[] { "Spinach" }, null);

        var again = store.Check(list.Id, new[] { "Spinach" }, null);

        Assert.Equal(0, again!.Value.Changed);
        Assert.Equal(1, again.Value.List.DoneCount);
    }

    [Fact]
    public void Words_that_match_nothing_are_reported_as_no_change()
    {
        // Silently succeeding would have a worker believe it had recorded progress it hadn't.
        var store = NewStore();
        var list = store.Create("shop", "Shopping list", Shop, checklist: true);

        Assert.Equal(0, store.Check(list.Id, new[] { "kumquats" }, null)!.Value.Changed);
    }

    [Fact]
    public void Removing_an_item_takes_its_tick_with_it()
    {
        // A tick for something no longer on the list is a ghost that keeps counting towards the total.
        var store = NewStore();
        var list = store.Create("shop", "Shopping list", Shop, checklist: true);
        store.Check(list.Id, new[] { "Spinach" }, null);

        store.Update(list.Id, add: null, remove: new[] { "Spinach" });

        var after = store.Get(list.Id)!;
        Assert.Empty(after.Done);
        Assert.Equal(0, after.DoneCount);
    }

    [Fact]
    public void Progress_survives_a_restart_so_a_halted_run_can_be_resumed()
    {
        // The whole point. A shopping run that stops at item 12 leaves a list that says so.
        var path = Path.Combine(Path.GetTempPath(), $"lists-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var first = new ProjectListStore(path, json);
        var list = first.Create("shop", "Shopping list", Shop, checklist: true);
        first.Check(list.Id, new[] { "Spinach", "Garlic" }, null);

        var reopened = new ProjectListStore(path, json).Get(list.Id)!;

        Assert.True(reopened.Checklist);
        Assert.Equal(2, reopened.DoneCount);
        Assert.Equal(new[] { "Cherry tomatoes", "Halloumi" }, reopened.Items.Where(i => !reopened.IsDone(i)));
    }

    [Fact]
    public void A_list_written_before_checklists_existed_still_loads()
    {
        // The stored JSON has no "checklist" or "done" at all — the shape on disk didn't change.
        var path = Path.Combine(Path.GetTempPath(), $"lists-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        File.WriteAllText(path, """
        [{"id":"abc12345","project":"holiday","title":"preferred amenities",
          "items":["all inclusive","pool"],
          "created":"2026-08-01T00:00:00+00:00","updated":"2026-08-01T00:00:00+00:00"}]
        """);

        var list = new ProjectListStore(path, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Get("abc12345");

        Assert.NotNull(list);
        Assert.Equal(2, list!.Items.Count);
        Assert.False(list.Checklist);
        Assert.Empty(list.Done);
    }

    [Fact]
    public void There_is_no_such_list_is_said_rather_than_thrown()
    {
        Assert.Null(NewStore().Check("nope", new[] { "Spinach" }, null));
    }

    public void Dispose()
    {
        foreach (var p in _paths)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
    }
}
