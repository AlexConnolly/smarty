using System.Reflection;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// What the model is told about work that has already finished.
///
/// The status alone says the worker stopped running, not that the job happened. A run that came back "I don't
/// have a browser, I can't do this" was listed as [done], indistinguishable from a success — so "try again" found
/// nothing here to retry and went looking elsewhere, and retried something from a different subject entirely. A
/// few words of the actual result make an empty outcome visible where the decision gets made.
/// </summary>
public class FinishedTaskNoteTests
{
    /// <summary>The note is assembled privately per turn and never persisted, so it's read back by reflection.</summary>
    private static string NoteFor(Session session) =>
        (string)typeof(Orchestrator)
            .GetMethod("FinishedTasksNote", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { session })!;

    private static Session WithTask(string id, string description, string status, string? result)
    {
        var session = new Session("notes");
        session.Tasks[id] = new TaskInfo { Id = id, Description = description, Status = status, Result = result };
        return session;
    }

    [Fact]
    public void A_task_that_achieved_nothing_shows_what_it_actually_said()
    {
        // Verbatim from the run that caused this: reported as done, having done nothing.
        var session = WithTask("1", "Add the shopping list to the user's Ocado basket", "done",
            "I have to be straight with you: I don't have a browser or any web-access tool available in this " +
            "session, so I can't open ocado.com or add anything to a basket.");

        var note = NoteFor(session);

        Assert.Contains("#1 [done]", note);
        Assert.Contains("don't have a browser", note);   // the outcome is visible, not just the status
    }

    [Fact]
    public void The_result_is_trimmed_to_a_line_not_pasted_in_whole()
    {
        // The point is a glance, not a second copy of the answer in every prompt from here on. Measured against
        // the RESULT's contribution rather than the note's total length — most of the note is its fixed preamble.
        var session = WithTask("1", "Research something", "done", new string('x', 4000));

        var note = NoteFor(session);
        // Anchored on the result marker: the note's closing paragraph quotes example phrases of its own.
        var start = note.IndexOf("→ \"", StringComparison.Ordinal) + 3;
        var quoted = note[start..note.IndexOf('"', start)];

        Assert.True(quoted.Length <= 61, $"the result should be trimmed to a line; it was {quoted.Length}");
        Assert.EndsWith("…", quoted);
    }

    [Fact]
    public void A_multi_line_result_stays_on_one_line()
    {
        // One line per task, or the list stops being scannable.
        var session = WithTask("1", "Build a deck", "done", "Done.\n\n- slide one\n- slide two\n\nAll good.");

        var note = NoteFor(session);
        var taskLines = note.Split('\n').Count(l => l.StartsWith("- #"));

        Assert.Equal(1, taskLines);
    }

    [Fact]
    public void A_task_with_no_result_yet_reads_as_it_always_did()
    {
        var session = WithTask("1", "Do a thing", "done", null);

        var note = NoteFor(session);

        Assert.Contains("#1 [done]: Do a thing", note);
        Assert.DoesNotContain("→", note); // no empty quotes dangling off the end
    }

    [Fact]
    public void Nothing_finished_means_nothing_said()
    {
        Assert.Equal("", NoteFor(new Session("empty")));
    }
}
