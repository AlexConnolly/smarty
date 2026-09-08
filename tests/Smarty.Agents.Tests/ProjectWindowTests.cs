using System.Text.Json;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Projects that are ABOUT a period, and what happens when that period passes.
///
/// The case this exists for: meal prep for next week. Ask for it, and a week's plan gets made. Ask again a
/// fortnight later and the old plan must not come back as the answer — but it must still be there as the record
/// of what was planned then. A project with no window keeps working exactly as it always did; the dates are
/// optional because most things genuinely have none, and an invented date is worse than no date.
/// </summary>
public class ProjectWindowTests : IDisposable
{
    private readonly List<string> _paths = new();
    private static readonly DateOnly Today = new(2026, 8, 20);

    private ProjectStore NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"projects-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        var store = new ProjectStore(path, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        {
            Now = () => new DateTimeOffset(Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        };
        return store;
    }

    private static Project Week(DateOnly from, DateOnly to) =>
        new() { Slug = "meal-prep", Title = "Meal prep", StartsOn = from, EndsOn = to };

    [Fact]
    public void A_window_that_has_passed_says_so_in_words_that_cannot_be_misread()
    {
        // "ran 10–16 Aug" invites the model to treat a finished week as current. This does not.
        var project = Week(new(2026, 8, 10), new(2026, 8, 16));

        var note = project.WindowNote(Today);
        Assert.Contains("ENDED", note);
        Assert.Contains("NEW period", note);
        Assert.True(project.HasEnded(Today));
    }

    [Fact]
    public void A_window_still_open_reads_as_running()
    {
        var project = Week(new(2026, 8, 17), new(2026, 8, 23));

        Assert.Contains("running now", project.WindowNote(Today));
        Assert.False(project.HasEnded(Today));
        Assert.True(project.Covers(Today));
    }

    [Fact]
    public void A_window_still_ahead_reads_as_not_yet_under_way()
    {
        var project = Week(new(2026, 8, 24), new(2026, 8, 30));

        Assert.Contains("not yet under way", project.WindowNote(Today));
        Assert.True(project.NotYetStarted(Today));
        Assert.False(project.Covers(Today));
    }

    [Fact]
    public void A_project_with_no_dates_is_unchanged_and_never_ends()
    {
        var project = new Project { Slug = "learn-spanish", Title = "Learn Spanish" };

        Assert.Equal("", project.WindowNote(Today));
        Assert.False(project.HasEnded(Today));
        Assert.True(project.Covers(Today)); // an unbounded project covers every day
    }

    [Fact]
    public void A_finished_project_drops_out_of_what_is_on_the_go()
    {
        // ActiveProjects is what gets put in front of the model every single turn, so a closed window must not
        // be in it — that list is the main way an old project keeps steering new conversations.
        var store = NewStore();
        store.Create("Meal prep last week", "", new(2026, 8, 10), new(2026, 8, 16));
        store.Create("House move", "", null, null);

        var live = store.ActiveProjects();
        Assert.Equal("house-move", Assert.Single(live).Slug);
    }

    [Fact]
    public void But_it_is_still_readable_rather_than_gone()
    {
        var store = NewStore();
        var (_, slug) = store.Create("Meal prep last week", "", new(2026, 8, 10), new(2026, 8, 16));

        Assert.NotNull(store.Get(slug!));                       // still there to be looked at
        Assert.Contains("ENDED", store.List());                 // and listed, under its own heading
        Assert.Contains("do NOT treat as current", store.List());
    }

    [Fact]
    public void The_same_subject_for_a_new_week_is_not_a_duplicate()
    {
        // The anti-sprawl check would otherwise refuse this — "meal prep" twice — and send the model off to edit
        // last week's plan, which is precisely the failure the dates exist to prevent.
        var store = NewStore();
        store.Create("Meal prep", "w/c 10 Aug", new(2026, 8, 10), new(2026, 8, 16));

        var (message, slug) = store.Create("Meal prep", "w/c 24 Aug", new(2026, 8, 24), new(2026, 8, 30));

        Assert.NotNull(slug);
        Assert.DoesNotContain("already a similar project", message);
    }

    [Fact]
    public void A_still_running_project_of_the_same_name_IS_a_duplicate()
    {
        // The sprawl guard has to keep working for the case it was built for. Stated as a project with its goal, which
        // is what meal prep for a week actually is — the guard applies to both sorts either way.
        var store = NewStore();
        store.Create("Meal prep", "this week", new(2026, 8, 17), new(2026, 8, 23),
            goal: "every dinner this week is planned and shopped for", filing: false);

        var (message, slug) = store.Create("Meal prep", "also this week", new(2026, 8, 17), new(2026, 8, 23),
            goal: "every dinner this week is planned and shopped for", filing: false);

        Assert.Null(slug);
        Assert.Contains("already a similar project", message);
    }

    [Fact]
    public void Dates_can_arrive_after_the_project_does_and_can_be_taken_away_again()
    {
        var store = NewStore();
        var (_, slug) = store.Create("Trip", "");

        Assert.Contains("running now", store.SetDates(slug!, new(2026, 8, 18), new(2026, 8, 25)));
        Assert.Equal(new DateOnly(2026, 8, 25), store.Get(slug!)!.EndsOn);

        Assert.Contains("no longer tied", store.SetDates(slug!, null, null));
        Assert.Null(store.Get(slug!)!.EndsOn);
    }

    [Fact]
    public void A_window_that_ends_before_it_starts_is_refused_rather_than_stored()
    {
        var store = NewStore();

        var (message, slug) = store.Create("Backwards", "", new(2026, 8, 30), new(2026, 8, 1));
        Assert.Null(slug);
        Assert.Contains("before it starts", message);

        var (_, real) = store.Create("Fine", "");
        Assert.Contains("before it starts", store.SetDates(real!, new(2026, 8, 30), new(2026, 8, 1)));
    }

    public void Dispose()
    {
        foreach (var path in _paths) try { File.Delete(path); } catch { }
    }
}
