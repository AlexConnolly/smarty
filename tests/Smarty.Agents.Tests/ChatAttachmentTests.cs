using System.Text.Json;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Attaching a file to a web chat, where the two halves pull in opposite directions: the MODEL must be told what
/// arrived and where it is, and the USER must not see any of that plumbing in their own message.
///
/// The files reach the session before the message does — the browser uploads, then sends — so these also pin the
/// handover between the two requests. Getting that wrong loses the attachment silently, which is the worst
/// possible failure: the message goes, the assistant is told nothing, and nobody can see why.
/// </summary>
public class ChatAttachmentTests : IDisposable
{
    private readonly List<string> _paths = new();

    private string Temp(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}");
        _paths.Add(path);
        return path;
    }

    /// <summary>
    /// An orchestrator whose model is unreachable on purpose. Everything under test happens before the first
    /// model call — the echo and the history entry — so the turn is free to fail immediately afterwards, and a
    /// closed port fails far faster than a stub would take to write.
    /// </summary>
    private Orchestrator NewOrchestrator(string workspaceRoot)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return new Orchestrator(
            "test-model",
            "http://127.0.0.1:1",
            () => "worker system",
            json,
            new TrainingLog(Temp("training"), json),
            SilentBrain.Over(Temp("brain")),
            new ProjectStore(Temp("projects") + ".json", json),
            new ProjectRunStore(Temp("runs") + ".json", json),
            new OrchestratorOptions
            {
                WorkspaceRoot = workspaceRoot,
                TurnTimeout = TimeSpan.FromSeconds(5),
            });
    }

    /// <summary>The turn is expected to fail at the model; the state it wrote on the way there is the subject.</summary>
    private static async Task RunTurn(Orchestrator orchestrator, Session session, string text)
    {
        try
        {
            await orchestrator.HandleMessageAsync(session, text, CancellationToken.None);
        }
        catch
        {
            /* no model on the other end — expected, and irrelevant to what's asserted below */
        }
    }

    private static List<(string Event, string Data)> EventsOf(Session session)
    {
        var events = new List<(string, string)>();
        for (var i = 0; session.TryGet(i, out var ev); i++) events.Add((ev.Event, ev.Data));
        return events;
    }

    private static string UserHistory(Session session) =>
        session.History.Where(m => m.Role == Role.User).Select(m => m.Content ?? "").LastOrDefault() ?? "";

    private Session UploadedThen(string userText, out List<(string Event, string Data)> events)
    {
        var workspace = Temp("workspace");
        var session = new Session("upload-test");
        var file = Path.Combine(workspace, "files", "quarterly.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "%PDF-1.4 pretend");

        // Exactly what POST /api/session/{id}/upload leaves behind: the file saved, and held against the
        // session so the next message carries it.
        session.PendingAttachments = new[] { new Attachment("quarterly.pdf", file, "application/pdf", 16) };

        RunTurn(NewOrchestrator(workspace), session, userText).GetAwaiter().GetResult();
        events = EventsOf(session);
        return session;
    }

    [Fact]
    public void An_uploaded_file_survives_the_message_that_carries_none_of_its_own()
    {
        // The regression this guards: the web app uploads in one request and sends in the next, so by the time
        // the message arrives its own attachment list is empty. Clearing the session's pending attachments on
        // that basis threw away the upload one line before it was read — every browser attachment lost.
        var session = UploadedThen("Have a look at this please", out _);

        var history = UserHistory(session);
        Assert.Contains("quarterly.pdf", history);
    }

    [Fact]
    public void The_user_sees_only_their_own_words()
    {
        var session = UploadedThen("Have a look at this please", out var events);

        // Every message event the UI receives, exactly as it was emitted.
        var shown = events
            .Where(e => e.Event is "msg_start" or "content" or "msg_end")
            .Select(e => e.Data)
            .ToList();
        Assert.NotEmpty(shown);

        var userText = shown.Where(d => d.Contains("\"text\"")).ToList();
        Assert.NotEmpty(userText);

        foreach (var data in userText)
        {
            // The filename is fine to show — it's the plumbing that isn't. None of the note's phrasing, and
            // above all no local path, may appear in anything the user is shown.
            Assert.DoesNotContain("attached", data, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("saved", data, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetTempPath().Replace("\\", ""), data.Replace("\\\\", "").Replace("\\", ""),
                StringComparison.OrdinalIgnoreCase);
        }

        // And the first thing said is the user's sentence and nothing else.
        Assert.Contains("\"text\":\"Have a look at this please\"", userText[0]);
    }

    [Fact]
    public void The_model_is_told_where_the_file_actually_is()
    {
        // The note exists to let a worker open the thing. A name with no location is a dead end, and the model
        // cannot see the file card the user gets.
        var session = UploadedThen("Summarise it", out _);

        var history = UserHistory(session);
        Assert.StartsWith("Summarise it", history);
        Assert.Contains("quarterly.pdf", history);
        Assert.True(history.Length > "Summarise it".Length, "the model got nothing beyond the user's own words");
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
