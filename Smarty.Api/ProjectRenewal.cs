using System.Globalization;

namespace Smarty.Api;

/// <summary>
/// A project that comes round again.
///
/// <para>
/// Some projects are a period rather than a goal. "Week of dinners" covers Mon–Sun and then it is over — but the NEED
/// is not over, it repeats, and the version of it that mattered last week is still the record of last week. So the
/// project ends and the same project has to start again, which is a thing the model had no way to express: it could
/// only carry a window, and once that window closed the project was history and anything built on it went stale.
/// </para>
/// <para>
/// That staleness is the part worth fixing, because it is what makes a panel over your own data worth having. A panel
/// filtered by date stays correct forever provided something keeps adding items — and this is the something. Renewal
/// advances the window and books a task to fill the new one, so the panel that answers "what is for dinner tonight"
/// keeps answering it in a month without anybody touching it.
/// </para>
/// <para>
/// The old items are not deleted. They are dated, so they fall out of "this week" on their own, and the list remains
/// the record of what was planned when — which is exactly the behaviour the dated items were added for.
/// </para>
/// </summary>
public static class ProjectRenewal
{
    /// <summary>
    /// The next window for a project whose own has closed.
    ///
    /// <para>
    /// Advanced by the project's OWN length rather than by a calendar rule: a Mon–Sun project becomes the next Mon–Sun,
    /// a fortnight becomes the next fortnight, and nobody has to say which. A named cadence overrides it where the
    /// length would be ambiguous — a month is not 30 days if you started on the 31st.
    /// </para>
    /// </summary>
    public static (DateOnly Starts, DateOnly Ends)? Next(Project project, DateOnly today)
    {
        if (project.StartsOn is not { } start || project.EndsOn is not { } end) return null;
        if (end >= today) return null;   // still current: nothing to do

        var repeat = (project.Repeat ?? "").Trim().ToLowerInvariant();

        // Named cadences, where "the same length again" is the wrong answer.
        if (repeat is "monthly" or "every month")
        {
            var nextStart = start.AddMonths(1);
            var nextEnd = end.AddMonths(1);
            return Catch(nextStart, nextEnd, today, months: true);
        }

        // Length-based, which covers weekly, fortnightly and anything else somebody has actually set up. The +1 is
        // because a Mon–Sun window is seven days inclusive, so the next one starts the day after it ends.
        var span = end.DayNumber - start.DayNumber + 1;
        if (span <= 0) return null;

        return Catch(start.AddDays(span), end.AddDays(span), today, months: false);
    }

    /// <summary>
    /// Roll forward until the window actually contains today.
    ///
    /// <para>
    /// A project left alone over a holiday is three weeks stale, and advancing it by one week would produce a window
    /// that is still in the past — renewed, still wrong, and renewed again twenty minutes later. It catches up in one
    /// go instead, which also means the task that fills it is booked once rather than three times.
    /// </para>
    /// </summary>
    private static (DateOnly, DateOnly)? Catch(DateOnly starts, DateOnly ends, DateOnly today, bool months)
    {
        // A generous ceiling: a project abandoned for two years should stop renewing rather than spin here.
        for (var i = 0; i < 120 && ends < today; i++)
        {
            if (months)
            {
                starts = starts.AddMonths(1);
                ends = ends.AddMonths(1);
            }
            else
            {
                var span = ends.DayNumber - starts.DayNumber + 1;
                starts = starts.AddDays(span);
                ends = ends.AddDays(span);
            }
        }

        return ends < today ? null : (starts, ends);
    }

    /// <summary>
    /// What to tell a worker to do about the new window.
    ///
    /// <para>
    /// The project's own renewal note is the whole value — it is the standing instruction ("no pork; each night a
    /// vegetarian base plus a protein") that would otherwise have to be re-typed every week. Without one there is
    /// nothing useful to say, so nothing is booked and the window simply moves.
    /// </para>
    /// </summary>
    public static string? Brief(Project project, DateOnly starts, DateOnly ends)
    {
        if (string.IsNullOrWhiteSpace(project.RenewPrompt)) return null;

        var window = starts == ends
            ? starts.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)
            : $"{starts:ddd d MMM} – {ends:ddd d MMM yyyy}";

        return $"The \"{project.Title}\" project has rolled over to a new period: {window}.\n\n" +
               $"What it is: {project.Description}\n\n" +
               $"STANDING INSTRUCTION for each new period:\n{project.RenewPrompt}\n\n" +
               "Fill in the new period NOW, yourself, with list_write — one entry per day, each carrying the DATE it " +
               "falls on. The dates are the whole point: they are what makes it answerable day by day and what keeps " +
               "a panel built on this list current. Do not write it to a file, do not hand it back as a plan, and do " +
               "not simply acknowledge this — a previous renewal replied \"On the case\" and did nothing, and another " +
               "wrote a perfectly good week into a markdown file that nothing reads.\n" +
               "If the list does not exist yet, list_write creates it — use a title that says what it is.\n" +
               "Do not remove what is already there: the previous period's items are the record of it, and they fall " +
               "out of view on their own because they are dated.\n" +
               "If something genuinely needs deciding that only the user can answer, ask them.";
    }
}
