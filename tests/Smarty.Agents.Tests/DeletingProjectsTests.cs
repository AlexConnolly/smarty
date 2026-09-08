using System.Text.Json;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Deleting a project, and everything that has to go with it.
///
/// <para>
/// Nothing could be deleted at all before this, which is worse than it sounds: most containers arrive by mistake, so
/// the list only ever grew and every one of them stayed in front of the model. The interesting part is the cascade —
/// the record alone leaves lists nothing can name, runs keyed to a slug that no longer resolves, and a brain still
/// answering with a thing the owner has just deleted.
/// </para>
/// </summary>
public class DeletingProjectsTests : IDisposable
{
    private readonly List<string> _paths = new();
    private static readonly DateOnly Today = new(2026, 8, 20);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string Temp(string what)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{what}-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        return path;
    }

    private ProjectStore NewStore() => new(Temp("projects"), Json)
    {
        Now = () => new DateTimeOffset(Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
    };

    [Fact]
    public void The_record_goes_and_is_handed_back_so_the_caller_knows_what_it_was()
    {
        var store = NewStore();
        var (_, slug) = store.Create("Selling the car", "", null, null,
            goal: "it is sold and the money is in the account", filing: false);

        var gone = store.Delete(slug!);

        Assert.NotNull(gone);
        Assert.Equal("Selling the car", gone!.Title);
        Assert.Equal("project", gone.Sort);
        Assert.Null(store.Get(slug!));
        Assert.Empty(store.ActiveProjects());
    }

    [Fact]
    public void A_topic_deletes_the_same_way_a_project_does()
    {
        var store = NewStore();
        var (_, slug) = store.Create("Coffee", "how I like it", null, null, goal: null, filing: true);

        var gone = store.Delete(slug!);

        Assert.Equal("topic", gone!.Sort);
        Assert.Null(store.Get(slug!));
    }

    [Fact]
    public void Deleting_something_that_is_not_there_says_so_rather_than_throwing()
    {
        Assert.Null(NewStore().Delete("nothing-like-this"));
    }

    [Fact]
    public void A_finished_project_can_still_be_deleted()
    {
        // Finishing and deleting are different things: one is a fact worth keeping, the other is tidying away a
        // container that should not have existed. A finished one must not become undeletable.
        var store = NewStore();
        var (_, slug) = store.Create("Selling the car", "", null, null,
            goal: "it is sold and the money is in the account", filing: false);
        store.Complete(slug!);

        Assert.NotNull(store.Delete(slug!));
        Assert.Equal("Nothing on the go yet.", store.List());
    }

    [Fact]
    public void Its_lists_go_with_it_because_nothing_else_can_ever_name_them()
    {
        var lists = new ProjectListStore(Temp("lists"), Json);
        lists.Create("selling-the-car", "Things to fix first", new[] { "wing mirror", "service" });
        lists.Create("selling-the-car", "Where to advertise", new[] { "Autotrader" });
        lists.Create("coffee", "Beans to try", new[] { "Ethiopian" });

        var went = lists.DeleteFor("selling-the-car");

        Assert.Equal(2, went);
        Assert.Empty(lists.ForProject("selling-the-car"));

        // And nothing else was touched.
        Assert.Single(lists.ForProject("coffee"));
    }

    [Fact]
    public void Its_runs_go_with_it_because_a_run_has_no_identity_apart_from_its_project()
    {
        var runs = new ProjectRunStore(Temp("runs"), Json);
        runs.Add(new ProjectRun { Id = "r1", Project = "selling-the-car", Task = "t1", Status = "done" });
        runs.Add(new ProjectRun { Id = "r2", Project = "selling-the-car", Task = "t2", Status = "done" });
        runs.Add(new ProjectRun { Id = "r3", Project = "coffee", Task = "t3", Status = "done" });

        var went = runs.DeleteFor("selling-the-car");

        Assert.Equal(2, went);
        Assert.Equal(0, runs.CountFor("selling-the-car"));
        Assert.Equal(1, runs.CountFor("coffee"));
    }

    [Fact]
    public void Deleting_for_something_with_nothing_against_it_removes_nothing()
    {
        Assert.Equal(0, new ProjectListStore(Temp("lists"), Json).DeleteFor("never-existed"));
        Assert.Equal(0, new ProjectRunStore(Temp("runs"), Json).DeleteFor("never-existed"));
    }

    [Fact]
    public void A_pinned_conversation_lets_go_of_it()
    {
        // Left pinned, a chat stays scoped to something that no longer exists — narrower toolset, told to stay on the
        // subject, and refusing to talk about anything else.
        var sessions = new SessionStore(_ => { });
        var pinned = sessions.GetOrCreate("chat-a");
        var soft = sessions.GetOrCreate("chat-b");
        var other = sessions.GetOrCreate("chat-c");

        pinned.PinnedProject = "selling-the-car";
        soft.CurrentProject = "selling-the-car";
        other.PinnedProject = "coffee";

        var let = sessions.Unpin("selling-the-car");

        Assert.Equal(1, let);
        Assert.Null(pinned.PinnedProject);
        Assert.Null(soft.CurrentProject);
        Assert.Equal("coffee", other.PinnedProject);
    }

    [Fact]
    public void Unpinning_something_nothing_is_pinned_to_changes_nothing()
    {
        var sessions = new SessionStore(_ => { });
        sessions.GetOrCreate("chat-a").PinnedProject = "coffee";

        Assert.Equal(0, sessions.Unpin("selling-the-car"));
        Assert.Equal(0, sessions.Unpin(""));
        Assert.Equal("coffee", sessions.Get("chat-a")!.PinnedProject);
    }

    public void Dispose()
    {
        foreach (var path in _paths)
            try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
