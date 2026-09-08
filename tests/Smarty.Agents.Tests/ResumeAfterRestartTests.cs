using System.Reflection;
using Smarty.Agents;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Picking a task back up after the process that ran it has gone.
///
/// Resuming a finished task already worked — message_task re-opens it and the worker keeps everything it
/// loaded — but only while the API keeps running. Session.Replay restores the event log and the id counters and
/// nothing else, so Session.Tasks comes back empty after a restart. The chat still lists the tasks, because the
/// UI reads those from the events; the orchestrator has no TaskInfo behind any of them and answers "there's no
/// task #3". Not starting over: refusing to start.
///
/// The transcript was never lost. Every call, its arguments and its result are recorded against the run because
/// that is what drives the run viewer, so a forgotten task is rebuilt from there.
/// </summary>
public class ResumeAfterRestartTests
{
    private static IReadOnlyList<Message> Rebuild(ControlRun run) =>
        ((IEnumerable<Message>)typeof(Orchestrator)
            .GetMethod("TranscriptOf", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { run })!).ToList();

    /// <summary>The eBay listing run, as it was actually recorded.</summary>
    private static ControlRun EbayRun() => new()
    {
        Id = "7a059625#3",
        ConversationId = "7a059625",
        TaskId = "3",
        Task = "List an item for sale on the user's eBay account using the browser.",
        Status = "done",
        Steps =
        {
            new RunStep { Kind = "tool", Tool = "chrome_navigate", Args = """{"url":"https://www.ebay.co.uk/sell"}""",
                Result = """{"navigated":true,"url":"https://www.ebay.co.uk/sl/sell","status":200}""" },
            new RunStep { Kind = "thinking", Text = "The page has loaded. I should look for the sell button." },
            new RunStep { Kind = "tool", Tool = "chrome_click", Args = """{"ref":"79"}""",
                Result = """{"clicked":"79","changed":{"url":"https://www.ebay.co.uk/sl/prelist/suggest"}}""" },
            new RunStep { Kind = "answer", Text = "I've reached the listing form and entered the item name." },
        },
    };

    [Fact]
    public void The_brief_leads_so_the_worker_knows_what_it_was_doing()
    {
        var messages = Rebuild(EbayRun());

        Assert.Equal(Role.User, messages[0].Role);
        Assert.Contains("List an item for sale", messages[0].Content);
    }

    [Fact]
    public void Every_tool_call_and_its_result_come_back()
    {
        // The point of resuming rather than restarting: it already knows which page it reached and what the
        // page said.
        var text = string.Join("\n", Rebuild(EbayRun()).Select(m => m.Content));

        Assert.Contains("chrome_navigate", text);
        Assert.Contains("chrome_click", text);
        Assert.Contains("ebay.co.uk/sl/prelist/suggest", text);   // where it actually got to
    }

    [Fact]
    public void Its_own_answers_come_back_too()
    {
        var text = string.Join("\n", Rebuild(EbayRun()).Select(m => m.Content));

        Assert.Contains("reached the listing form", text);
    }

    [Fact]
    public void Thinking_is_left_behind()
    {
        // The longest part of a transcript and the least useful to a later attempt: deliberation about a state
        // the page is no longer in.
        var text = string.Join("\n", Rebuild(EbayRun()).Select(m => m.Content));

        Assert.DoesNotContain("I should look for the sell button", text);
    }

    [Fact]
    public void No_message_claims_to_be_a_tool_call_with_no_matching_result()
    {
        // A provider rejects an assistant tool-call whose result id is missing, and those ids died with the
        // process. Replayed as narration instead, which carries the knowledge without the protocol.
        var messages = Rebuild(EbayRun());

        Assert.All(messages, m => Assert.Null(m.ToolCalls));
        Assert.DoesNotContain(messages, m => m.Role == Role.Tool);
    }

    [Fact]
    public void A_huge_result_is_trimmed_rather_than_replayed_whole()
    {
        // A page read is twenty thousand characters and the run is over. Rebuilding the context must not cost
        // more than the work did.
        var run = new ControlRun
        {
            TaskId = "1",
            Task = "read a page",
            Steps = { new RunStep { Kind = "tool", Tool = "chrome_read_page", Args = "{}",
                Result = new string('x', 20_000) } },
        };

        var longest = Rebuild(run).Max(m => m.Content?.Length ?? 0);

        Assert.True(longest < 2_000, $"a replayed result should be trimmed; the longest message was {longest}");
    }

    [Fact]
    public void A_run_with_nothing_recorded_still_yields_its_brief()
    {
        var run = new ControlRun { TaskId = "1", Task = "do the thing" };

        var messages = Rebuild(run);

        Assert.Single(messages);
        Assert.Contains("do the thing", messages[0].Content);
    }

    private static Session Replayed(params (string Event, string Data)[] events)
    {
        var session = new Session("s");
        session.Replay(events);
        Orchestrator.RestoreHistory(session);
        return session;
    }

    [Fact]
    public void A_reopened_conversation_gives_the_model_back_what_was_said()
    {
        // The event log is what the CLIENT renders; History is what the MODEL sees, and nothing rebuilt it.
        // A chat reopened after a restart looked complete on screen and was empty behind it.
        var session = Replayed(
            ("msg_end", """{"id":0,"text":"list my record on eBay"}"""),
            ("msg_start", """{"id":1,"role":"assistant"}"""),
            ("msg_end", """{"id":1,"text":"I'll get a worker on it."}"""));

        Assert.Equal(2, session.History.Count);
        Assert.Equal(Role.User, session.History[0].Role);
        Assert.Contains("list my record", session.History[0].Content);
        Assert.Equal(Role.Assistant, session.History[1].Role);
    }

    [Fact]
    public void A_working_event_does_not_take_the_endpoint_down()
    {
        // A `working` event carries its id as a STRING, and asking a string element for an int THROWS rather
        // than declining — which 500'd the message endpoint the first time this met a real conversation.
        var session = Replayed(
            ("msg_end", """{"id":0,"text":"do the thing"}"""),
            ("working", """{"id":"3","task":"doing the thing"}"""),
            ("working_done", """{"id":"3","status":"done"}"""));

        Assert.Single(session.History);
    }

    [Fact]
    public void A_live_session_is_never_overwritten_by_its_own_history()
    {
        var session = new Session("s");
        session.History.Add(Message.User("said live"));
        session.Replay(new[] { ("msg_end", """{"id":0,"text":"from the log"}""") });

        Orchestrator.RestoreHistory(session);

        Assert.Single(session.History);
        Assert.Contains("said live", session.History[0].Content);
    }

    [Fact]
    public void The_resumed_worker_is_told_the_transcript_is_history()
    {
        // Without this it reads its own last failure as the current state and concludes it is still stuck on
        // something the user has since dealt with — which is the whole reason for resuming.
        Assert.Contains("history, not a live browser", Orchestrator.ResumeNote);
        Assert.Contains("CONTINUE, not to start again", Orchestrator.ResumeNote);
        Assert.Contains("may well have been resolved", Orchestrator.ResumeNote);
    }
}
