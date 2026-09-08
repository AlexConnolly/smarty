using System.Reflection;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Answering a question the ordinary way.
///
/// A paused task was visible to the user and invisible to the orchestrator: the running note is
/// status == "running" and the finished note is done/failed, so a WAITING task fell between the two and nothing
/// told the model a question was outstanding. Replying in the chat — the obvious thing to do — went nowhere,
/// because as far as it knew nothing had been asked. The floating card was the only route in, and three of them
/// stacked up unanswered.
/// </summary>
public class WaitingQuestionTests
{
    private static string NoteFor(Session session) =>
        (string)typeof(Orchestrator)
            .GetMethod("WaitingTasksNote", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { session })!;

    private static Session WithWaiting(string id, string description, PendingQuestion? question)
    {
        var session = new Session("s");
        session.Tasks[id] = new TaskInfo
        {
            Id = id,
            Description = description,
            Status = question is null ? "running" : "waiting",
            Pending = question,
        };
        return session;
    }

    [Fact]
    public void A_paused_question_is_put_in_front_of_the_model()
    {
        // Verbatim from the listing run: it asked, the user answered in the chat, and nothing happened.
        var session = WithWaiting("6", "List the record on eBay",
            new PendingQuestion("The condition dialog only offers New or Used — should I set Used?",
                new[] { "Used", "New" }));

        var note = NoteFor(session);

        Assert.Contains("#6 asked", note);
        Assert.Contains("only offers New or Used", note);
        Assert.Contains("Used / New", note);   // the options, so a bare "used" is recognisable as an answer
    }

    [Fact]
    public void It_says_to_route_the_reply_rather_than_re_ask()
    {
        var note = NoteFor(WithWaiting("6", "d", new PendingQuestion("Which one?", new[] { "A", "B" })));

        Assert.Contains("message_task", note);
        Assert.Contains("Do NOT re-ask", note);
        Assert.Contains("particular box", note);   // answering in the chat has to work
    }

    [Fact]
    public void Several_stacked_questions_are_all_listed()
    {
        // Three in a row is what prompted this; answering one must not hide the others.
        var session = WithWaiting("4", "one", new PendingQuestion("First?", Array.Empty<string>()));
        session.Tasks["5"] = new TaskInfo { Id = "5", Description = "two", Status = "waiting",
            Pending = new PendingQuestion("Second?", Array.Empty<string>()) };
        session.Tasks["6"] = new TaskInfo { Id = "6", Description = "three", Status = "waiting",
            Pending = new PendingQuestion("Third?", Array.Empty<string>()) };

        var note = NoteFor(session);

        Assert.Contains("#4", note);
        Assert.Contains("#5", note);
        Assert.Contains("#6", note);
    }

    [Fact]
    public void A_running_task_is_not_described_as_asking_anything()
    {
        Assert.Equal("", NoteFor(WithWaiting("1", "still going", null)));
    }

    [Fact]
    public void Nothing_waiting_means_nothing_said()
    {
        Assert.Equal("", NoteFor(new Session("empty")));
    }

    [Fact]
    public void A_plan_step_does_not_ask_on_its_own_behalf()
    {
        // Only the top-level task speaks to the user; a child's question reaches them through its coordinator.
        var session = new Session("s");
        session.Tasks["2"] = new TaskInfo
        {
            Id = "2", Description = "a step", Status = "waiting", ParentTaskId = "1",
            Pending = new PendingQuestion("Which font?", Array.Empty<string>()),
        };

        Assert.Equal("", NoteFor(session));
    }

    [Fact]
    public void A_long_question_is_trimmed_rather_than_pasted_whole()
    {
        var session = WithWaiting("6", "d", new PendingQuestion(new string('x', 900), Array.Empty<string>()));

        Assert.True(NoteFor(session).Length < 600, "the note is a glance, not a second copy of the question");
    }
}
