using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// A conversation folded once, on the server, instead of rebuilt by every client on every load.
///
/// Reopening a chat used to re-push its whole event history down the live stream, so the client re-derived the
/// conversation each time — and every "the job says it's still running", "0:00", "the card is on the wrong
/// message" bug lived in that re-derivation. This is where that derivation now happens, once, where it can be
/// tested.
/// </summary>
public class ConversationSnapshotTests
{
    private static ConversationSnapshot Of(params (string, string)[] events) =>
        ConversationSnapshot.Of(events);

    [Fact]
    public void A_message_is_its_final_text_not_the_sum_of_its_deltas()
    {
        // msg_end carries the whole thing and is authoritative, so a stream that dropped a delta still ends up
        // correct rather than subtly truncated.
        var snap = Of(
            ("msg_start", "{\"id\":0,\"role\":\"user\"}"),
            ("content", "{\"id\":0,\"text\":\"how much\"}"),
            ("msg_end", "{\"id\":0,\"text\":\"how much is it\"}"));

        var message = Assert.Single(snap.Messages);
        Assert.Equal("how much is it", message.Text);
        Assert.Equal("user", message.Role);
    }

    [Fact]
    public void An_announcement_and_the_answer_it_promised_are_two_messages()
    {
        // "Let me check that for you" → tool call → the answer. It used to be one bubble whose text was replaced
        // when the answer arrived, so the user watched the assistant un-say something it had just said. Two
        // messages is what a person does, and it has to survive a reload as two.
        var snap = Of(
            ("msg_start", "{\"id\":0,\"role\":\"user\"}"),
            ("msg_end", "{\"id\":0,\"text\":\"what's on my calendar?\"}"),
            ("msg_start", "{\"id\":1,\"role\":\"assistant\"}"),
            ("content", "{\"id\":1,\"text\":\"Let me check that for you.\"}"),
            ("msg_end", "{\"id\":1,\"text\":\"Let me check that for you.\"}"),
            ("msg_start", "{\"id\":2,\"role\":\"assistant\"}"),
            ("msg_end", "{\"id\":2,\"text\":\"Two things: the dentist at 10, and dinner at 8.\"}"));

        Assert.Equal(3, snap.Messages.Count);
        Assert.Equal("Let me check that for you.", snap.Messages[1].Text);
        Assert.Equal("assistant", snap.Messages[1].Role);
        Assert.Equal("Two things: the dentist at 10, and dinner at 8.", snap.Messages[2].Text);
        Assert.Equal("assistant", snap.Messages[2].Role);
    }

    [Fact]
    public void Text_the_model_withdrew_does_not_survive_into_the_history()
    {
        // content_cleared means it was taken back off the user's screen mid-turn; keeping it in the history would
        // show them something they were never shown.
        var snap = Of(
            ("msg_start", "{\"id\":0,\"role\":\"assistant\"}"),
            ("content", "{\"id\":0,\"text\":\"half a thought that got abandoned\"}"),
            ("content_cleared", "{\"id\":0}"),
            ("content", "{\"id\":0,\"text\":\"On it.\"}"),
            ("msg_end", "{\"id\":0,\"text\":\"On it.\"}"));

        Assert.Equal("On it.", Assert.Single(snap.Messages).Text);
    }

    [Fact]
    public void A_file_lands_on_the_message_that_announced_it()
    {
        var snap = Of(
            ("msg_start", "{\"id\":0,\"role\":\"assistant\"}"),
            ("msg_end", "{\"id\":0,\"text\":\"working on it\"}"),
            ("msg_start", "{\"id\":1,\"role\":\"assistant\"}"),
            ("msg_end", "{\"id\":1,\"text\":\"here's your deck\"}"),
            ("file", "{\"name\":\"deck.html\",\"msgId\":1}"));

        Assert.Empty(snap.Messages[0].Files);
        Assert.Equal("deck.html", Assert.Single(snap.Messages[1].Files).Name);
    }

    [Fact]
    public void A_file_with_no_owner_goes_to_the_latest_message_rather_than_nowhere()
    {
        var snap = Of(
            ("msg_start", "{\"id\":0,\"role\":\"assistant\"}"),
            ("msg_end", "{\"id\":0,\"text\":\"done\"}"),
            ("file", "{\"name\":\"orphan.html\"}"));

        Assert.Equal("orphan.html", Assert.Single(snap.Messages[0].Files).Name);
    }

    [Fact]
    public void A_finished_task_carries_the_outcome_it_reached_and_how_long_it_took()
    {
        var snap = Of(
            ("working", "{\"id\":\"1\",\"task\":\"research\",\"msgId\":1,\"at\":\"2026-08-14T09:00:00+00:00\"}"),
            ("working_done", "{\"id\":\"1\",\"status\":\"done\",\"at\":\"2026-08-14T09:04:30+00:00\"}"));

        var task = Assert.Single(snap.Tasks);
        Assert.Equal("done", task.Status);
        Assert.Equal(1, task.MsgId);
        Assert.Equal(TimeSpan.FromMinutes(4.5), task.EndedAt - task.StartedAt);
    }

    [Fact]
    public void A_task_with_no_outcome_yet_says_so_rather_than_guessing_one()
    {
        // Null status is the only honest answer for something still going — and it is what tells the UI to keep
        // spinning. Inventing "done" here is how a live job would look finished.
        var snap = Of(("working", "{\"id\":\"1\",\"task\":\"still going\",\"at\":\"2026-08-14T09:00:00+00:00\"}"));

        Assert.Null(Assert.Single(snap.Tasks).Status);
    }

    [Fact]
    public void A_task_that_starts_again_is_running_again()
    {
        // What a resume after a question looks like. Left as "waiting", the row would sit there for ever claiming
        // to need an answer it has already had.
        var snap = Of(
            ("working", "{\"id\":\"1\",\"task\":\"book it\",\"msgId\":1,\"at\":\"2026-08-14T09:00:00+00:00\"}"),
            ("question", "{\"id\":\"1\",\"question\":\"which room?\",\"options\":[\"sea view\"]}"),
            ("working_done", "{\"id\":\"1\",\"status\":\"waiting\",\"at\":\"2026-08-14T09:01:00+00:00\"}"),
            ("working", "{\"id\":\"1\",\"task\":\"book it\",\"msgId\":3,\"at\":\"2026-08-14T09:05:00+00:00\"}"));

        var task = Assert.Single(snap.Tasks);
        Assert.Null(task.Status);
        Assert.Null(task.EndedAt);
        Assert.Equal(3, task.MsgId);            // it belongs to the message that resumed it
        Assert.Empty(snap.Questions);           // and it is no longer waiting on the user
    }

    [Fact]
    public void An_unanswered_question_is_still_waiting()
    {
        var snap = Of(
            ("working", "{\"id\":\"1\",\"task\":\"book it\",\"at\":\"2026-08-14T09:00:00+00:00\"}"),
            ("question", "{\"id\":\"1\",\"question\":\"which room?\",\"options\":[\"sea view\",\"garden\"]}"),
            ("working_done", "{\"id\":\"1\",\"status\":\"waiting\"}"));

        var q = Assert.Single(snap.Questions);
        Assert.Equal("which room?", q.Question);
        Assert.Equal(2, q.Options.Count);
    }

    [Fact]
    public void Messages_that_never_said_anything_are_left_out()
    {
        // An assistant turn that only delegated has a msg_start and nothing else; rendering it gives an empty
        // bubble with a copy button under it.
        var snap = Of(
            ("msg_start", "{\"id\":0,\"role\":\"user\"}"),
            ("msg_end", "{\"id\":0,\"text\":\"do the thing\"}"),
            ("msg_start", "{\"id\":1,\"role\":\"assistant\"}"));

        Assert.Single(snap.Messages);
    }

    [Fact]
    public void Next_accounts_for_every_event_so_the_stream_resumes_exactly_after_it()
    {
        // Off by one either way and the client either misses a message or shows one twice.
        var events = new (string, string)[]
        {
            ("msg_start", "{\"id\":0,\"role\":\"user\"}"),
            ("reasoning", "{\"id\":1,\"text\":\"thinking\"}"),   // dropped from the history, still counted
            ("msg_end", "{\"id\":0,\"text\":\"hello\"}"),
        };

        Assert.Equal(events.Length, ConversationSnapshot.Of(events).Next);
    }

    [Fact]
    public void A_torn_or_unreadable_event_is_skipped_rather_than_losing_the_conversation()
    {
        var snap = Of(
            ("msg_start", "{\"id\":0,\"role\":\"user\"}"),
            ("content", "not json at all"),
            ("msg_end", "{\"id\":0,\"text\":\"still here\"}"));

        Assert.Equal("still here", Assert.Single(snap.Messages).Text);
    }
}
