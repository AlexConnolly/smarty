using System.Text.Json;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// How long a project stays in the front of the model's mind.
///
/// Once a project had been resolved, every later message in that conversation arrived wrapped in "you're
/// currently focused on X" plus that project's details — for the rest of the conversation, with nothing able to
/// let go of it. So an unrelated question got answered as though it were about the project: ask which of two
/// models scored better, in a chat that had been about a holiday, and you were told about the holiday.
///
/// The focus now fades on its own after a couple of quiet turns, and the details — the strongest steer in the
/// note — only ride along while it's fresh.
/// </summary>
public class ProjectFocusTests : IDisposable
{
    private readonly List<string> _paths = new();

    private string Temp(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}");
        _paths.Add(path);
        return path;
    }

    private (Orchestrator Orchestrator, ProjectStore Projects) Build()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var projects = new ProjectStore(Temp("projects") + ".json", json);
        // Unreachable model: the focus note is built while assembling context, before the first model call, so
        // the turn is free to fail straight afterwards.
        var orchestrator = new Orchestrator(
            "test-model",
            "http://127.0.0.1:1",
            () => "worker system",
            json,
            new TrainingLog(Temp("training"), json),
            SilentBrain.Over(Temp("brain")),
            projects,
            new ProjectRunStore(Temp("runs") + ".json", json),
            new OrchestratorOptions { TurnTimeout = TimeSpan.FromSeconds(5) });
        return (orchestrator, projects);
    }

    private static void Turn(Orchestrator orchestrator, Session session, string text)
    {
        try
        {
            orchestrator.HandleMessageAsync(session, text, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            /* no model on the other end — expected */
        }
    }

    /// <summary>
    /// The project part of the context this turn would put in front of the model. Read from the orchestrator
    /// directly: the per-turn context is assembled into a copy of the history and never persisted, so it can't be
    /// recovered from the session after the fact.
    /// </summary>
    private static string NoteFor(Orchestrator orchestrator, Session session, string message = "anything") =>
        orchestrator.ProjectFocusNote(session, message, CancellationToken.None).GetAwaiter().GetResult();

    private (Orchestrator, Session, string) FocusedSession()
    {
        var (orchestrator, projects) = Build();
        var (_, slug) = projects.Create("Holiday with my wife", "Hot, beachy, all-inclusive next year");
        Assert.NotNull(slug);

        var session = new Session("focus-test");
        session.CurrentProject = slug;          // as find_project would leave it
        session.TurnsSinceProjectTouched = 0;
        return (orchestrator, session, slug!);
    }

    [Fact]
    public void The_project_is_in_context_while_the_focus_holds()
    {
        var (orchestrator, session, slug) = FocusedSession();
        session.TurnsSinceProjectTouched = 1; // the turn straight after it was resolved

        var note = NoteFor(orchestrator, session);
        Assert.Contains("Holiday with my wife", note);
        Assert.Contains(slug, note);
    }

    [Fact]
    public void It_is_offered_as_context_rather_than_as_the_subject()
    {
        // The old wording asserted what the conversation was about ("you're currently focused on") and told the
        // model to stay on it. What it must do instead is leave the choice open.
        var (orchestrator, session, _) = FocusedSession();
        session.TurnsSinceProjectTouched = 1;

        var note = NoteFor(orchestrator, session, "which of these two models scored better?");
        Assert.DoesNotContain("currently focused on", note);
        Assert.Contains("if this message relates to it", note);
        Assert.Contains("just answer what was actually asked", note);
    }

    [Fact]
    public void The_note_goes_quiet_once_the_focus_has_expired()
    {
        var (orchestrator, session, _) = FocusedSession();
        session.TurnsSinceProjectTouched = 3; // past the grace

        Assert.Equal("", NoteFor(orchestrator, session));
        Assert.Null(session.CurrentProject); // and the focus itself is dropped, not just hidden
    }

    [Fact]
    public void The_focus_lets_go_after_a_few_turns_of_talking_about_something_else()
    {
        var (orchestrator, session, _) = FocusedSession();

        Turn(orchestrator, session, "which of these two models scored better?");
        Assert.Equal(1, session.TurnsSinceProjectTouched);
        Assert.NotNull(session.CurrentProject); // one aside doesn't mean the subject changed

        Turn(orchestrator, session, "and what's the difference between them?");
        Assert.NotNull(session.CurrentProject); // still in grace: a follow-up may yet come back to it

        Turn(orchestrator, session, "how much disk space have I got?");
        Assert.Null(session.CurrentProject);    // three turns elsewhere — the conversation has moved on
        Assert.Equal("", NoteFor(orchestrator, session));
    }

    [Fact]
    public void Touching_the_project_again_renews_it()
    {
        var (orchestrator, session, _) = FocusedSession();
        Turn(orchestrator, session, "something else");
        Turn(orchestrator, session, "something else again");
        Assert.NotNull(session.CurrentProject);

        // What find_project/project_summary/create_project do when they resolve one.
        session.TurnsSinceProjectTouched = 0;

        Turn(orchestrator, session, "back to the holiday — what did we decide?");
        Turn(orchestrator, session, "and the flights?");
        Assert.NotNull(session.CurrentProject); // the clock restarted, so it's still here
        Assert.Contains("Holiday with my wife", NoteFor(orchestrator, session));
    }

    public void Dispose()
    {
        foreach (var path in _paths)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
            try { File.Delete(path); } catch { }
            try { File.Delete(path + ".json"); } catch { }
        }
    }
}
