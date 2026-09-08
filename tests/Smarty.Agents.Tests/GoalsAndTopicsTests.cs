using System.Text.Json;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// The two containers, and the one field that separates them.
///
/// <para>
/// Written from a real failure: the user mentioned a favourite restaurant and a PROJECT was created for it. Nothing
/// about that was a bad judgement — a project was the only container on offer, so a preference became work in
/// progress, and every question about what was on and what was outstanding had a restaurant in the answer. The fix is
/// not to instruct against it. It is to require a goal for the container that means "being driven at an outcome" and
/// to offer a second one for the container that means "somewhere to keep things".
/// </para>
/// <para>
/// Dates play no part in this. A project never requires a window and most have none; the goal is the whole test.
/// </para>
/// </summary>
public class GoalsAndTopicsTests : IDisposable
{
    private readonly List<string> _paths = new();
    private static readonly DateOnly Today = new(2026, 8, 20);

    private ProjectStore NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"projects-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        return new ProjectStore(path, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        {
            Now = () => new DateTimeOffset(Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        };
    }

    [Fact]
    public void A_project_without_a_goal_is_refused_and_offered_as_a_topic()
    {
        var store = NewStore();

        var (message, slug) = store.Create("Favourite restaurant", "", null, null, goal: null, filing: false);

        Assert.Null(slug);
        Assert.Contains("goal", message);
        Assert.Contains("topic", message);
        Assert.Empty(store.ActiveProjects());
    }

    [Fact]
    public void A_goal_that_only_repeats_the_title_is_not_a_goal()
    {
        // The way round the requirement that a model would actually find: satisfy the field by restating the title.
        // Catchable by comparing two supplied fields, which is not the same as reading either as prose.
        var store = NewStore();

        var (message, slug) = store.Create("Favourite restaurant", "", null, null,
            goal: "favourite restaurant", filing: false);

        Assert.Null(slug);
        Assert.Contains("restates the title", message);
        Assert.Contains("topic", message);
    }

    [Fact]
    public void A_near_restatement_gets_through_and_that_is_the_honest_limit()
    {
        // Recorded rather than hidden. The check compares the words of two supplied fields, so it catches a goal that
        // IS the title and not one that is the title plus a verb. Going further means judging whether prose describes
        // a finishable outcome, which is reading intent out of words — the thing that has been wrong every time it has
        // been tried here. What actually removes the pressure to fake a goal is having somewhere else to put it.
        var store = NewStore();

        var (_, slug) = store.Create("Favourite restaurant", "", null, null,
            goal: "find a favourite restaurant", filing: false);

        Assert.NotNull(slug);
    }

    [Fact]
    public void A_project_with_a_real_goal_is_created_and_says_what_finishing_means()
    {
        var store = NewStore();

        var (message, slug) = store.Create("The car", "", null, null,
            goal: "it is sold and the money is in the account", filing: false);

        Assert.NotNull(slug);
        Assert.Contains("done when it is sold", message);

        var made = store.Get(slug!)!;
        Assert.False(made.IsTopic);
        Assert.Equal("project", made.Sort);
    }

    [Fact]
    public void A_topic_needs_no_goal_and_has_nothing_to_finish()
    {
        var store = NewStore();

        var (message, slug) = store.Create("Favourite restaurant", "where we like eating", null, null,
            goal: null, filing: true);

        Assert.NotNull(slug);
        Assert.Contains("nothing to finish", message);

        var made = store.Get(slug!)!;
        Assert.True(made.IsTopic);
        Assert.Equal("topic", made.Sort);
        Assert.Null(made.Goal);
    }

    [Fact]
    public void A_project_never_requires_dates()
    {
        var store = NewStore();

        var (_, slug) = store.Create("Learn Spanish", "", null, null,
            goal: "able to hold a conversation without switching to English", filing: false);

        var made = store.Get(slug!)!;
        Assert.Null(made.StartsOn);
        Assert.Null(made.EndsOn);
        Assert.False(made.HasEnded(Today));
        Assert.False(made.IsTopic);
        Assert.Contains(made.Slug, store.ActiveProjects().Select(p => p.Slug));
    }

    [Fact]
    public void The_two_are_listed_apart_so_a_preference_does_not_read_as_outstanding_work()
    {
        var store = NewStore();
        store.Create("The car", "", null, null, goal: "it is sold and the money is in the account", filing: false);
        store.Create("Coffee", "how I like it", null, null, goal: null, filing: true);

        var listed = store.List();

        Assert.Contains("Projects", listed);
        Assert.Contains("Topics", listed);
        Assert.Contains("done when it is sold", listed);

        // The topic is below its own heading rather than mixed in with the work.
        Assert.True(listed.IndexOf("Topics", StringComparison.Ordinal)
                    < listed.IndexOf("Coffee", StringComparison.Ordinal));
        Assert.True(listed.IndexOf("The car", StringComparison.Ordinal)
                    < listed.IndexOf("Topics", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_on_the_go_reads_as_nothing_rather_than_as_no_projects()
    {
        Assert.Equal("Nothing on the go yet.", NewStore().List());
    }

    [Fact]
    public void A_topic_and_a_project_share_one_namespace_so_neither_can_shadow_the_other()
    {
        // The reason both live in one store. Two stores would allow a project and a topic of the same name, and every
        // slug reference — a list, a task, a widget — would then have to say which sort it meant.
        var store = NewStore();
        store.Create("Selling the car", "", null, null, goal: "it is sold and the money is in the account",
            filing: false);

        var (message, slug) = store.Create("Selling the car", "how I keep it", null, null, goal: null, filing: true);

        Assert.Null(slug);
        Assert.Contains("already a similar project", message);
    }

    [Fact]
    public void A_title_with_one_meaningful_word_is_still_caught_as_a_duplicate()
    {
        // The hole this had all along: the guard wanted two meaningful words in common, and "The car" has one once the
        // article is dropped, so a second one was created beside the first. Short titles are the common case for a
        // topic, which is where sprawl hurts most.
        var store = NewStore();
        store.Create("The car", "", null, null, goal: "it is sold and the money is in the account", filing: false);

        var (message, second) = store.Create("The car", "how I keep it", null, null, goal: null, filing: true);

        Assert.Null(second);
        Assert.Contains("already a similar project", message);
    }

    [Fact]
    public void Two_things_that_merely_share_a_word_are_not_duplicates()
    {
        // The other half of that rule. Tightening it for short titles must not start refusing genuinely different
        // things — each of these keeps a meaningful word the other does not.
        var store = NewStore();
        store.Create("Selling the car", "", null, null, goal: "it is sold and the money is in the account",
            filing: false);

        var (_, second) = store.Create("Washing the car", "", null, null, goal: "it is clean before the wedding",
            filing: false);

        Assert.NotNull(second);
    }

    [Fact]
    public void Two_titles_whose_only_meaningful_word_is_shared_are_treated_as_the_same_thing()
    {
        // The deliberate consequence of the tightening, recorded rather than discovered later. "plan" is on the ignored
        // word list, so "Meal plan" carries the single word "meal" — which the existing "Meal prep" already has. By
        // this rule's own vocabulary they are indistinguishable, so the second is refused and pointed at the first.
        // That is the intended behaviour for a sprawl guard, not an accident.
        var store = NewStore();
        store.Create("Meal prep", "", null, null, goal: "every dinner is planned and shopped for", filing: false);

        var (message, second) = store.Create("Meal plan", "", null, null, goal: "a month of menus written down",
            filing: false);

        Assert.Null(second);
        Assert.Contains("Meal prep", message);
    }

    [Fact]
    public void A_project_can_be_finished_against_its_goal_and_stops_counting_as_current()
    {
        // What a goal is FOR. Status was declared active|done|archived and never once set anywhere in the codebase, so
        // a project could state an outcome and never be measured against it.
        var store = NewStore();
        var (_, slug) = store.Create("Selling the car", "", null, null,
            goal: "it is sold and the money is in the account", filing: false);

        var message = store.Complete(slug!);

        Assert.Contains("is done", message);
        Assert.Contains("money is in the account", message);

        var made = store.Get(slug!)!;
        Assert.True(made.Finished);
        Assert.Equal(Today, made.CompletedOn);
        Assert.DoesNotContain(slug, store.ActiveProjects().Select(p => p.Slug));

        // Listed rather than vanished, or the model starts it again next week.
        Assert.Contains("Done (their goal was reached", store.List());
    }

    [Fact]
    public void A_topic_cannot_be_finished_because_there_is_nothing_to_finish()
    {
        var store = NewStore();
        var (_, slug) = store.Create("Coffee", "how I like it", null, null, goal: null, filing: true);

        var message = store.Complete(slug!);

        Assert.Contains("is a topic", message);
        Assert.False(store.Get(slug!)!.Finished);
        Assert.Contains(slug, store.ActiveProjects().Select(p => p.Slug));
    }

    [Fact]
    public void Finishing_something_twice_says_so_rather_than_pretending()
    {
        var store = NewStore();
        var (_, slug) = store.Create("Selling the car", "", null, null,
            goal: "it is sold and the money is in the account", filing: false);

        store.Complete(slug!);
        Assert.Contains("already marked done", store.Complete(slug!));
    }

    [Fact]
    public void A_finished_project_can_be_reopened_when_it_turns_out_not_to_have_been()
    {
        var store = NewStore();
        var (_, slug) = store.Create("Selling the car", "", null, null,
            goal: "it is sold and the money is in the account", filing: false);

        store.Complete(slug!);
        var message = store.Reopen(slug!);

        Assert.Contains("open again", message);
        Assert.False(store.Get(slug!)!.Finished);
        Assert.Null(store.Get(slug!)!.CompletedOn);
        Assert.Contains(slug, store.ActiveProjects().Select(p => p.Slug));
    }

    [Fact]
    public void Finishing_is_offered_as_a_tool_and_takes_a_slug()
    {
        var tool = ProjectTools.CompleteTool(NewStore());

        Assert.Equal("finish_project", tool.Name);
        Assert.Contains("project", tool.Parameters.Select(p => p.Name));
        Assert.True(tool.Parameters.Single(p => p.Name == "project").Required);
    }

    [Fact]
    public void Both_tools_exist_and_only_one_of_them_asks_for_a_goal()
    {
        var store = NewStore();

        var project = ProjectTools.CreateTool(store);
        var topic = ProjectTools.TopicTool(store);

        Assert.Contains("goal", project.Parameters.Select(p => p.Name));
        Assert.True(project.Parameters.Single(p => p.Name == "goal").Required);
        Assert.DoesNotContain("goal", topic.Parameters.Select(p => p.Name));

        // And a topic is never asked for dates, because a topic is not about a period.
        Assert.DoesNotContain("starts_on", topic.Parameters.Select(p => p.Name));
    }

    public void Dispose()
    {
        foreach (var path in _paths)
            try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
