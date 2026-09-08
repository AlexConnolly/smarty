using System.Reflection;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Which stopped tasks can be picked back up.
///
/// The list was waiting/done/failed, and it left out INTERRUPTED — which is what every in-flight task becomes
/// when the process restarts, and the state that most obviously wants resuming. So the whole resume path did
/// its work, rebuilt the transcript faithfully from the recorded run, and was then refused on the status:
/// "The previous task is stuck in an interrupted state and can't be resumed. Let me start it fresh."
///
/// An eBay listing that was one field from finished got started again from nothing.
/// </summary>
public class ResumableStatusTests
{
    private static string NoteFor(Session session) =>
        (string)typeof(Orchestrator)
            .GetMethod("FinishedTasksNote", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { session })!;

    private static Session With(params (string Id, string Status)[] tasks)
    {
        var session = new Session("s");
        foreach (var (id, status) in tasks)
            session.Tasks[id] = new TaskInfo { Id = id, Description = $"task {id}", Status = status };
        return session;
    }

    [Theory]
    [InlineData("interrupted")]   // the process died — the commonest case, and the one that was missing
    [InlineData("cancelled")]     // stopped on purpose; picking it up again takes an explicit ask
    [InlineData("waiting")]
    [InlineData("done")]
    [InlineData("failed")]
    public void A_stopped_task_can_be_picked_back_up(string status)
    {
        Assert.True(Orchestrator.Resumable(status), $"'{status}' should be resumable");
    }

    [Fact]
    public void A_task_still_going_is_not_resumed_but_steered()
    {
        Assert.False(Orchestrator.Resumable("running"));
    }

    [Fact]
    public void An_interrupted_task_is_listed_so_continue_that_finds_it()
    {
        // Listing only done/failed made a restarted task invisible: nothing said it existed, so "continue that"
        // had nothing to continue and started again from nothing.
        var note = NoteFor(With(("3", "interrupted")));

        Assert.Contains("#3 [interrupted]", note);
        Assert.Contains("message_task", note);
    }

    [Fact]
    public void The_note_forbids_telling_the_user_it_cannot_be_resumed()
    {
        // The exact sentence the user was given.
        Assert.Contains("Never tell the user a task can't be resumed", NoteFor(With(("3", "interrupted"))));
    }

    [Fact]
    public void A_running_task_is_not_offered_for_re_opening()
    {
        Assert.Equal("", NoteFor(With(("1", "running"))));
    }

    [Fact]
    public void A_waiting_task_belongs_to_the_question_note_not_this_one()
    {
        // It is paused on a question, which is a different thing to say about it.
        Assert.Equal("", NoteFor(With(("1", "waiting"))));
    }

    [Fact]
    public void Every_stopped_task_in_the_thread_is_offered()
    {
        var note = NoteFor(With(("3", "interrupted"), ("4", "interrupted"), ("5", "cancelled"), ("6", "done")));

        foreach (var id in new[] { "#3", "#4", "#5", "#6" })
            Assert.Contains(id, note);
    }
}
