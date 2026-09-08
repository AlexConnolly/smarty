using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// Picking a task back up after the process that ran it has gone.
///
/// <para>
/// Resuming already worked, and worked well: message_task on a finished task re-opens it, the worker keeps
/// everything it loaded and produced, and the planning gate is skipped because the transcript isn't empty. All
/// of that is true for exactly as long as the API keeps running.
/// </para>
/// <para>
/// A restart ends it. <see cref="Session.Replay"/> restores the event log and the id counters and nothing else,
/// so <see cref="Session.Tasks"/> comes back empty — the chat still lists three tasks, because the UI reads
/// those from the events, but the orchestrator has no TaskInfo behind any of them. Asked to continue #3 it
/// answers "there's no task #3". Not starting over: refusing to start at all.
/// </para>
/// <para>
/// The transcript was never actually lost. Every tool call, its arguments and its result are recorded against
/// the run in the control hub, on disk, because that is what drives the run viewer. So a task the session has
/// forgotten is rebuilt from there — the brief as the opening instruction, then each call and each result in
/// order — and the worker resumes holding what it held, instead of rediscovering a page it already found.
/// </para>
/// </summary>
public sealed partial class Orchestrator
{
    /// <summary>
    /// Give a replayed session back the conversation the MODEL sees.
    ///
    /// <para>
    /// <see cref="Session.Replay"/> restores the event log, which is what the client renders, and
    /// <see cref="Session.History"/> is only ever appended during a live turn. So a conversation reopened after
    /// a restart looked complete on screen and was empty behind it: the orchestrator woke with no memory of
    /// anything discussed. Asked to carry on with a task from an hour earlier, it had no idea what the task was,
    /// and invented a different one.
    /// </para>
    /// <para>
    /// Rebuilt from the events rather than kept separately, so there is one recording rather than two that can
    /// disagree. Tool calls are not restored — those belong to the turn that made them and the ids are gone —
    /// but what was SAID is what a conversation is.
    /// </para>
    /// </summary>
    public static void RestoreHistory(Session session)
    {
        if (session.History.Count > 0) return;   // a live session is already the truth

        foreach (var (role, text) in SpokenTurns(session.Snapshot()))
            session.History.Add(role == "user" ? Message.User(text) : Message.Assistant(text));
    }

    /// <summary>Who said what, in order, from the raw event log.</summary>
    private static IEnumerable<(string Role, string Text)> SpokenTurns(IReadOnlyList<SessionEvent> events)
    {
        var roles = new Dictionary<int, string>();

        foreach (var e in events)
        {
            // Only the message events matter here. Checked first because a `working` event carries its id as a
            // STRING, and asking a string element for an int throws rather than declining — which took the
            // whole endpoint down with a 500 the first time this ran against a real conversation.
            if (e.Event is not ("msg_start" or "msg_end")) continue;

            JsonElement root;
            try { root = JsonDocument.Parse(e.Data).RootElement; }
            catch { continue; }
            if (root.ValueKind != JsonValueKind.Object) continue;
            if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
            if (!idEl.TryGetInt32(out int id)) continue;

            switch (e.Event)
            {
                case "msg_start":
                    roles[id] = root.TryGetProperty("role", out var r) && r.GetString() == "user"
                        ? "user" : "assistant";
                    break;

                case "msg_end":
                    var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(text)) break;
                    // A user turn is emitted without a msg_start, so an unheralded message is theirs.
                    yield return (roles.TryGetValue(id, out var role) ? role : "user", text);
                    break;
            }
        }
    }

    /// <summary>
    /// Whether a task can be picked back up. Everything except one that is still going.
    /// <para>
    /// Stated once, because it was stated twice and got it wrong both times: the list was waiting/done/failed,
    /// which leaves out INTERRUPTED — the state every in-flight task lands in when the process restarts, and the
    /// one that most obviously wants resuming. The transcript was faithfully rebuilt and then refused.
    /// </para>
    /// <para>
    /// A running task is excluded because it is not stopped; steering that is what its inbox is for. Cancelled
    /// is included: it was stopped on purpose, and picking it up again takes an explicit ask, so honouring that
    /// ask is the whole point.
    /// </para>
    /// </summary>
    internal static bool Resumable(string status) =>
        status is "waiting" or "done" or "failed" or "interrupted" or "cancelled";

    /// <summary>
    /// Put back every task this conversation ran, so the orchestrator knows they exist.
    ///
    /// <para>
    /// Rebuilding one on demand was not enough. A task is only looked up when the model calls a tool with its
    /// id, and the model will not name a task nothing has told it about — after a restart the running/finished
    /// notes are built from an empty <see cref="Session.Tasks"/>, so "continue task 3" met an orchestrator that
    /// had never heard of task 3 and delegated a fresh job instead, about the wrong item.
    /// </para>
    /// <para>
    /// Done once when the conversation is opened, which is also the only moment it is cheap: the runs are
    /// already in memory for the dashboard, and the alternative is a model inventing work.
    /// </para>
    /// </summary>
    public void RestoreTasks(Session session)
    {
        if (_hub is null) return;

        foreach (var run in _hub.RunsFor(session.Id))
            if (!string.IsNullOrEmpty(run.TaskId) && !session.Tasks.ContainsKey(run.TaskId))
                Recall(session, run.TaskId);
    }

    /// <summary>
    /// A task the live session knows about, or one rebuilt from what was recorded about it.
    /// Null when nothing anywhere has heard of that id.
    /// </summary>
    private TaskInfo? Recall(Session session, string id)
    {
        if (session.Tasks.TryGetValue(id, out var live)) return live;
        if (_hub is null) return null;

        var recorded = _hub.RunsFor(session.Id).FirstOrDefault(r => r.TaskId == id);
        if (recorded is null) return null;

        // A question that was asked and not answered is still outstanding. The process dying does not answer it,
        // and it gets stamped "interrupted" on the way down — which left the card on screen and the endpoint
        // refusing it: "task #6 isn't waiting for an answer (status: interrupted)". So the QUESTION decides,
        // not the stamp. Options aren't recorded, so it comes back as a plain one; the substance is the ask.
        // The hub's copy is the first place to look and not the last. It clears a question when a run reports
        // finishing and when a restart finds one mid-flight, both by design — so a build that was re-driven, or
        // killed while running, loses the ask there while the PANEL still holds it and still cannot finish
        // without it. Either source outstanding means the question is outstanding.
        var asked = string.IsNullOrWhiteSpace(recorded.PendingQuestion)
            ? _widgets?.All().FirstOrDefault(w => w.TaskId == id && !string.IsNullOrWhiteSpace(w.Asks))?.Asks
            : recorded.PendingQuestion;

        var question = string.IsNullOrWhiteSpace(asked)
            ? null
            : new PendingQuestion(asked!, Array.Empty<string>());

        var task = new TaskInfo
        {
            Id = id,
            Description = recorded.Task,
            Project = recorded.Project,
            Persona = recorded.Persona,
            UserScope = session.CurrentUserScope,
            Room = session.Room,
            // Whatever it was, it is not running now — the process that ran it is gone.
            Status = question is not null ? "waiting"
                : recorded.Status is "running" or "waiting" ? "waiting"
                : recorded.Status,
            Pending = question,
            Result = recorded.Result,
            Rebuilt = true,
            // Which panel it is building, if it is building one. Lost on rebuild until now, and everything keyed on
            // it silently stopped working after a restart — including "this panel is no longer asking anything",
            // which is why an answered question stayed on screen with the button stuck mid-send.
            BuildsWidget = _widgets?.All().FirstOrDefault(w => w.TaskId == id)?.Id,
        };
        task.Conversation.AddRange(TranscriptOf(recorded));
        task.WorkspaceDir = CreateWorkspace(session, task, null);

        session.Tasks[id] = task;
        Trace($"[resume #{id}] rebuilt from {recorded.Steps.Count} recorded step(s)");
        return task;
    }

    /// <summary>
    /// The recorded run as a conversation the model can carry on from.
    /// <para>
    /// Tool calls are replayed as narration rather than as real tool-call messages. A provider rejects an
    /// assistant tool-call that has no matching result id, and these ids died with the process — so rebuilding
    /// them faithfully would produce a transcript no provider will accept. What matters for continuing is not
    /// the call protocol but the KNOWLEDGE: which page it reached, what the page said, what worked and what
    /// refused. That survives perfectly well as text.
    /// </para>
    /// </summary>
    private static IEnumerable<Message> TranscriptOf(ControlRun run)
    {
        yield return Message.User(run.Task);

        foreach (var step in run.Steps)
        {
            switch (step.Kind)
            {
                case "tool" when !string.IsNullOrWhiteSpace(step.Tool):
                    yield return Message.Assistant($"[called {step.Tool} {Snip(step.Args ?? "", 300)}]");
                    // Truncated: a page read is twenty thousand characters and the run is over. What is needed
                    // is what it learned, not a second copy of every page it saw.
                    yield return Message.User($"[{step.Tool} returned] {Head(step.Result ?? "", 1200)}");
                    break;

                case "answer" when !string.IsNullOrWhiteSpace(step.Text):
                    yield return Message.Assistant(step.Text!);
                    break;

                // Thinking is deliberately dropped. It is the longest part of a transcript and the least useful
                // to a later attempt — deliberation about a state the page is no longer in.
            }
        }
    }

    /// <summary>
    /// What a resumed worker is told before it carries on.
    /// <para>
    /// Written to answer the question it will actually have: everything above happened, it is over, and the
    /// thing that stopped it may no longer be true. Without saying so it re-reads its own last failure and
    /// concludes it is still stuck.
    /// </para>
    /// </summary>
    internal const string ResumeNote =
        "Everything above is what you already did on this task, in an earlier session that has since ended — " +
        "the pages you reached, the calls you made and what they returned. It is history, not a live browser: " +
        "any refs are stale and the page may have moved on, so look before you act.\n\n" +
        "You are being asked to CONTINUE, not to start again. Whatever stopped you last time may well have " +
        "been resolved since. Pick up from where you got to, redo only what you must, and finish the job.";
}
