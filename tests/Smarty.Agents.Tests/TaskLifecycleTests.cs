using System.Text.Json;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Closing tasks that were still going when the recording stopped.
///
/// <para>
/// Written because this was wrong twice, and both times the symptom was a stray job showing a moving progress bar
/// with no way to put it down. The second time it was the ORDER of events: a task that pauses to ask the user
/// something records a done long before it is done.
/// </para>
/// </summary>
public class TaskLifecycleTests
{
    private static (string, string) Working(string id) =>
        ("working", JsonSerializer.Serialize(new { id, task = "a job", at = "2026-08-17T19:00:00Z" }));

    private static (string, string) Done(string id, string status) =>
        ("working_done", JsonSerializer.Serialize(new { id, status, at = "2026-08-17T19:05:00Z" }));

    private static (string, string) Question(string id) =>
        ("question", JsonSerializer.Serialize(new { id, question = "which one?" }));

    private static string? StatusOf(IEnumerable<(string Event, string Data)> events, string id)
    {
        string? status = null;
        foreach (var (e, d) in events)
        {
            if (e != "working_done") continue;
            using var doc = JsonDocument.Parse(d);
            if (doc.RootElement.TryGetProperty("id", out var got) && got.ToString() == id)
                status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
        }
        return status;
    }

    [Fact]
    public void A_task_that_paused_then_resumed_then_died_is_still_closed()
    {
        // Verbatim from the run that exposed it: the eBay rebuild asked for an eBay sign-in, was answered, resumed,
        // and the process was killed. Its history contains a done — from the pause — so an existence check called it
        // finished and the pill turned for ever.
        var recorded = new List<(string, string)>
        {
            Working("5"), Done("5", "waiting"), Question("5"), Working("5"),
        };

        var closed = TaskLifecycle.CloseDangling(recorded);

        Assert.Equal("interrupted", StatusOf(closed, "5"));
    }

    [Fact]
    public void A_task_that_genuinely_finished_is_left_alone()
    {
        var recorded = new List<(string, string)> { Working("1"), Done("1", "done") };

        var closed = TaskLifecycle.CloseDangling(recorded);

        Assert.Equal("done", StatusOf(closed, "1"));
        Assert.Equal(recorded.Count, closed.Count);
    }

    [Fact]
    public void A_task_still_waiting_on_a_question_is_not_declared_interrupted()
    {
        // It stopped on purpose and the question is still outstanding — that is not the same as having been killed,
        // and flattening the two is what made real questions unanswerable.
        var recorded = new List<(string, string)> { Working("6"), Done("6", "waiting"), Question("6") };

        var closed = TaskLifecycle.CloseDangling(recorded);

        Assert.Equal("waiting", StatusOf(closed, "6"));
        Assert.Equal(recorded.Count, closed.Count);
    }

    [Fact]
    public void A_task_that_started_and_never_reported_again_is_closed()
    {
        var closed = TaskLifecycle.CloseDangling(new List<(string, string)> { Working("2") });

        Assert.Equal("interrupted", StatusOf(closed, "2"));
    }

    [Fact]
    public void An_interrupted_task_is_stamped_when_it_was_last_alive_not_now()
    {
        // Otherwise every reopened chat restarts the clock and an old dead task reports running for seconds.
        var recorded = new List<(string, string)> { Working("3"), ("content", JsonSerializer.Serialize(new { id = 9, at = "2026-08-17T19:30:00Z" })) };

        var closed = TaskLifecycle.CloseDangling(recorded);
        var added = closed[^1];

        using var doc = JsonDocument.Parse(added.Item2);
        Assert.Contains("2026-08-17T19:30", doc.RootElement.GetProperty("at").GetString());
    }

    [Fact]
    public void Several_tasks_are_judged_independently()
    {
        var recorded = new List<(string, string)>
        {
            Working("1"), Done("1", "done"),
            Working("2"),
            Working("3"), Done("3", "waiting"), Question("3"), Working("3"),
        };

        var closed = TaskLifecycle.CloseDangling(recorded);

        Assert.Equal("done", StatusOf(closed, "1"));
        Assert.Equal("interrupted", StatusOf(closed, "2"));
        Assert.Equal("interrupted", StatusOf(closed, "3"));
    }
}
