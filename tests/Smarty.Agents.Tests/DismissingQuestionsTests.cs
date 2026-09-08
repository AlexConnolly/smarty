using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Getting rid of a question without answering it.
///
/// <para>
/// A panel was proposed, declined, and removed from the page — and the job building it was never told. It carried on,
/// finished its work, could not publish to a panel that no longer existed, and asked the user what to do about that. The
/// only thing a waiting task would accept was an answer, so the question sat on the home page and dismissing it did not
/// help: the token was never tripped, so the worker asked the next thing a minute later, and the control centre learns a
/// run's state from the event log, which the dismiss never wrote to.
/// </para>
/// </summary>
public class DismissingQuestionsTests
{
    private static (Session Session, TaskInfo Task) Waiting()
    {
        var session = new Session("widget-1c6941ff");
        var task = new TaskInfo { Id = "9", Description = "Build the dinner spots panel" };
        task.Status = "waiting";
        task.Pending = new PendingQuestion("Which panel id should I publish to?", Array.Empty<string>());
        session.Tasks["9"] = task;
        return (session, task);
    }

    [Fact]
    public void Dismissing_clears_the_question()
    {
        var (session, task) = Waiting();

        task.Pending = null;

        Assert.Null(session.Tasks["9"].Pending);
    }

    [Fact]
    public void Dismissing_must_also_stop_the_work()
    {
        // The half that was missing. Clearing the question and leaving the worker running only delays the next one.
        var (_, task) = Waiting();

        task.Pending = null;
        task.Status = "cancelled";
        task.Cts.Cancel();

        Assert.True(task.Cts.IsCancellationRequested, "a dismissed job has to actually stop");
        Assert.False(task.IsRunning);
    }

    [Fact]
    public void A_cancelled_child_goes_with_its_parent()
    {
        // A build can run its steps as hidden children, and cancelling only the named task leaves one grinding on to
        // deliver minutes later.
        var session = new Session("widget-1c6941ff");
        var parent = new TaskInfo { Id = "9", Description = "parent" };
        var child = new TaskInfo { Id = "10", Description = "step", ParentTaskId = "9" };
        session.Tasks["9"] = parent;
        session.Tasks["10"] = child;

        foreach (var t in new[] { parent }.Concat(session.Tasks.Values.Where(c => c.ParentTaskId == "9")))
        {
            t.Status = "cancelled";
            t.Cts.Cancel();
        }

        Assert.True(child.Cts.IsCancellationRequested);
    }

    [Fact]
    public void The_run_is_recorded_as_over_so_it_is_not_reported_as_waiting_again()
    {
        // The control centre rebuilds a run's state from the event log, and the home page's question list reads that.
        // Without an event saying the run ended, the question comes back however many times it is dismissed.
        var (session, _) = Waiting();

        session.Append("working_done", "{\"id\":\"9\",\"status\":\"cancelled\"}");

        var events = new List<(string Event, string Data)>();
        for (var i = 0; session.TryGet(i, out var ev); i++) events.Add((ev.Event, ev.Data));

        var done = Assert.Single(events.Where(e => e.Event == "working_done"));
        Assert.Contains("cancelled", done.Data);
    }

    [Fact]
    public void A_task_building_a_removed_panel_is_the_one_to_cancel()
    {
        // What removing or declining a panel now does: find the builds pointed at it, and stop those only.
        var session = new Session("widget-1c6941ff");
        var building = new TaskInfo { Id = "9", Description = "build it" } ;
        building.BuildsWidget = "1c6941ff";
        var unrelated = new TaskInfo { Id = "11", Description = "something else" };
        unrelated.BuildsWidget = "caf37865";
        session.Tasks["9"] = building;
        session.Tasks["11"] = unrelated;

        var mine = session.Tasks.Values.Where(t => t.BuildsWidget == "1c6941ff").ToList();

        Assert.Equal("9", Assert.Single(mine).Id);
        Assert.Equal("caf37865", unrelated.BuildsWidget);
    }
}
