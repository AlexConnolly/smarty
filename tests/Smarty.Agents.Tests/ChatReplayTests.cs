using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Reopening a conversation the process never saw. The events go back into the session's own buffer so that
/// streaming, <c>?from=N</c> and the client's reconnect all work on history exactly as they do on a live chat —
/// which means the numbering has to survive the handover too.
/// </summary>
public class ChatReplayTests
{
    private static (string, string) Msg(string @event, int id, string? role = null, string? text = null) =>
        (@event, role is not null
            ? $"{{\"id\":{id},\"role\":\"{role}\"}}"
            : $"{{\"id\":{id},\"text\":\"{text}\"}}");

    private static List<(string, string)> TwoMessages() => new()
    {
        Msg("msg_start", 0, role: "user"),
        Msg("msg_end", 0, text: "first"),
        Msg("msg_start", 1, role: "assistant"),
        Msg("msg_end", 1, text: "second"),
    };

    [Fact]
    public void Numbering_continues_past_what_was_replayed()
    {
        // The regression: message ids restarted at 0 after a replay, so the first thing said in a reopened
        // conversation was emitted as id 0 — which the client already holds as the opening line. The new message
        // was merged into the old one instead of appended after it.
        var session = new Session("reopened");
        session.Replay(TwoMessages());

        Assert.Equal(2, session.NextMessageId());
    }

    [Fact]
    public void Task_numbering_continues_past_what_was_replayed()
    {
        // The regression, and it was a nasty one: task ids restarted at 1 after a reopen, so the next job took an
        // id a finished job already had. Everything downstream then treated the two as one task — the new work
        // inherited the old one's "done", so it never showed as running, never got a spinner, sat under the wrong
        // message, and its clock restarted on every reload.
        var session = new Session("reopened");
        session.Replay(new List<(string, string)>
        {
            Msg("msg_start", 0, role: "user"),
            ("working", "{\"id\":\"1\",\"task\":\"describe the screenshot\",\"msgId\":1}"),
            ("working_done", "{\"id\":\"1\",\"status\":\"done\"}"),
            ("working", "{\"id\":\"2\",\"task\":\"build the brochure\",\"msgId\":4}"),
            ("working_done", "{\"id\":\"2\",\"status\":\"done\"}"),
        });

        Assert.Equal("3", session.NextTaskId());
    }

    [Fact]
    public void A_conversation_with_no_tasks_still_numbers_from_one()
    {
        var session = new Session("reopened");
        session.Replay(TwoMessages());

        Assert.Equal("1", session.NextTaskId());
    }

    [Fact]
    public void Replayed_events_are_readable_in_order()
    {
        var session = new Session("reopened");
        session.Replay(TwoMessages());

        Assert.Equal(4, session.Count);
        Assert.True(session.TryGet(0, out var first));
        Assert.Equal("msg_start", first.Event);
        Assert.Contains("\"role\":\"user\"", first.Data);
    }

    [Fact]
    public void A_live_conversation_is_never_overwritten_by_history()
    {
        // Two clients can open the same chat at once; the second must not splice a stale transcript in front of
        // what is being said right now.
        var session = new Session("live");
        session.Append("msg_start", "{\"id\":0,\"role\":\"user\"}");
        session.Replay(TwoMessages());

        Assert.Equal(1, session.Count);
    }

    [Fact]
    public void Events_without_a_message_id_do_not_disturb_numbering()
    {
        // Not every durable event carries an id — a delivered file doesn't. One of those must not be read as
        // message -1 and must not reset the counter.
        var session = new Session("reopened");
        session.Replay(new List<(string, string)>
        {
            Msg("msg_start", 0, role: "user"),
            Msg("msg_end", 0, text: "hello"),
            ("file", "{\"name\":\"deck.html\"}"),
            ("links", "not even json"),
        });

        Assert.Equal(1, session.NextMessageId());
        Assert.Equal(4, session.Count);
    }
}
