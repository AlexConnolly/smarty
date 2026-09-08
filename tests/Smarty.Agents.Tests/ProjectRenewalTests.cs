using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// A project whose period comes round again.
///
/// <para>
/// A period project ended on its last day and everything built on it went stale — a panel whose whole job was
/// answering "what is for dinner tonight" was correct for six days and wrong for ever after. The arithmetic of the
/// next window is what makes it keep working, so the arithmetic is what gets tested.
/// </para>
/// </summary>
public class ProjectRenewalTests
{
    private static Project Weekly(string starts, string ends, string repeat = "weekly") => new()
    {
        Slug = "week-of-dinners",
        Title = "Week of dinners",
        Description = "Dinner plan for me and my wife.",
        StartsOn = DateOnly.Parse(starts),
        EndsOn = DateOnly.Parse(ends),
        Repeat = repeat,
        RenewPrompt = "No pork; each night a vegetarian base plus a protein.",
    };

    [Fact]
    public void A_week_becomes_the_next_week()
    {
        // Mon 17 – Sun 23, asked on the Monday after. The next window starts the day after the last one ended, which
        // is what makes "the same length again" line up with the calendar rather than drifting a day each time.
        var next = ProjectRenewal.Next(Weekly("2026-08-17", "2026-08-23"), DateOnly.Parse("2026-08-24"));

        Assert.NotNull(next);
        Assert.Equal(DateOnly.Parse("2026-08-24"), next!.Value.Starts);
        Assert.Equal(DateOnly.Parse("2026-08-30"), next.Value.Ends);
    }

    [Fact]
    public void A_project_still_inside_its_window_is_left_alone()
    {
        Assert.Null(ProjectRenewal.Next(Weekly("2026-08-17", "2026-08-23"), DateOnly.Parse("2026-08-20")));
    }

    [Fact]
    public void A_project_left_for_three_weeks_catches_up_in_one_go()
    {
        // Otherwise a machine that was off over a holiday renews to a window that is STILL in the past, and books a
        // planning task for a week that has already happened — then does it again an hour later.
        var next = ProjectRenewal.Next(Weekly("2026-08-17", "2026-08-23"), DateOnly.Parse("2026-09-10"));

        Assert.NotNull(next);
        Assert.True(next!.Value.Starts <= DateOnly.Parse("2026-09-10"));
        Assert.True(next.Value.Ends >= DateOnly.Parse("2026-09-10"));
        Assert.Equal(DateOnly.Parse("2026-09-07"), next.Value.Starts);
        Assert.Equal(DateOnly.Parse("2026-09-13"), next.Value.Ends);
    }

    [Fact]
    public void A_fortnight_repeats_as_a_fortnight_without_being_told()
    {
        // The length is taken from the window itself, so nobody has to name the cadence for anything but months.
        var next = ProjectRenewal.Next(Weekly("2026-08-03", "2026-08-16", "fortnightly"), DateOnly.Parse("2026-08-17"));

        Assert.NotNull(next);
        Assert.Equal(DateOnly.Parse("2026-08-17"), next!.Value.Starts);
        Assert.Equal(DateOnly.Parse("2026-08-30"), next.Value.Ends);
    }

    [Fact]
    public void A_month_moves_by_the_calendar_rather_than_by_days()
    {
        // 31 days is not a month, and adding the window's length to a January project would land it in early March.
        var next = ProjectRenewal.Next(Weekly("2026-01-01", "2026-01-31", "monthly"), DateOnly.Parse("2026-02-01"));

        Assert.NotNull(next);
        Assert.Equal(DateOnly.Parse("2026-02-01"), next!.Value.Starts);
        Assert.Equal(DateOnly.Parse("2026-02-28"), next.Value.Ends);
    }

    [Fact]
    public void A_project_with_no_window_cannot_repeat()
    {
        var unbounded = new Project { Slug = "learn-spanish", Title = "Learn Spanish", Repeat = "weekly" };

        Assert.False(unbounded.Recurring);
        Assert.Null(ProjectRenewal.Next(unbounded, DateOnly.Parse("2026-08-24")));
    }

    [Fact]
    public void The_new_period_is_briefed_with_the_standing_instruction()
    {
        // The whole value: the thing that would otherwise be re-typed every Sunday.
        var brief = ProjectRenewal.Brief(Weekly("2026-08-17", "2026-08-23"),
            DateOnly.Parse("2026-08-24"), DateOnly.Parse("2026-08-30"));

        Assert.NotNull(brief);
        Assert.Contains("No pork", brief);
        Assert.Contains("24 Aug", brief);
        Assert.Contains("30 Aug", brief);
        // The dates are the point — they are what keeps a panel over this list answering day by day.
        Assert.Contains("DATE", brief);
        // And it must name the tool, because the worker path cannot reach a list at all.
        Assert.Contains("list_write", brief);
        // And last period's items stay, because they are the record of last period.
        Assert.Contains("Do not remove", brief);
    }

    [Fact]
    public void Without_a_standing_instruction_nothing_is_booked()
    {
        // The window still moves — a recurring project should never read as finished for ever — but there is nothing
        // useful to tell a worker, so it is not asked to do anything.
        var project = Weekly("2026-08-17", "2026-08-23");
        project.RenewPrompt = null;

        Assert.Null(ProjectRenewal.Brief(project, DateOnly.Parse("2026-08-24"), DateOnly.Parse("2026-08-30")));
    }

    [Fact]
    public void A_period_is_never_renewed_twice_on_the_same_day()
    {
        var path = Path.Combine(Path.GetTempPath(), $"projects-{Guid.NewGuid():N}.json");
        var store = new ProjectStore(path, new System.Text.Json.JsonSerializerOptions());
        var (_, slug) = store.Create("Week of dinners", "Dinner plan",
            DateOnly.Parse("2026-08-10"), DateOnly.Parse("2026-08-16"));
        store.SetRepeat(slug!, "weekly", "No pork.");

        var today = DateOnly.Parse("2026-08-17");
        Assert.Single(store.DueForRenewal(today));

        Assert.True(store.Renew(slug!, DateOnly.Parse("2026-08-17"), DateOnly.Parse("2026-08-23"), today));
        // A second tick an hour later must not plan the same week again.
        Assert.False(store.Renew(slug!, DateOnly.Parse("2026-08-24"), DateOnly.Parse("2026-08-30"), today));
        Assert.Empty(store.DueForRenewal(today));
    }

    [Fact]
    public void A_project_cannot_be_made_to_repeat_without_dates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"projects-{Guid.NewGuid():N}.json");
        var store = new ProjectStore(path, new System.Text.Json.JsonSerializerOptions());
        var (_, slug) = store.Create("Learn Spanish", "Ongoing");

        var message = store.SetRepeat(slug!, "weekly", "Practise daily.");

        Assert.Contains("no dates", message);
        Assert.False(store.Get(slug!)!.Recurring);
    }
}
