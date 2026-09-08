using System.Collections.Concurrent;
using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>One buffered Server-Sent Event on a session's stream.</summary>
public sealed record SessionEvent(string Event, string Data);

/// <summary>
/// An observer of a session's event stream — the seam that lets a non-browser host (e.g. Smarty.Slack)
/// receive the same logical events the SSE stream carries (msg_start / content / msg_end / working /
/// working_done / question) and render them its own way, WITHOUT the orchestrator knowing the difference.
/// The default web app leaves <see cref="Session.Sink"/> null, so its behaviour is completely unchanged.
/// </summary>
public interface IEventSink
{
    void OnEvent(string @event, string data);
}

/// <summary>
/// A structured question a worker has paused to ask, and what SHAPE of answer it wants.
/// <para>
/// Everything used to arrive as prose with a few suggested strings, so "which pickup point?" and "how many
/// people?" and "is this the right spot?" were all the same widget: a sentence and some chips. The kind lets the
/// asking side say what it actually needs, so the answering side can offer the right control — a map to confirm a
/// place, a number field for a count, a straight yes/no — rather than making someone type a sentence to answer a
/// question that has one obvious form.
/// </para>
/// <para>
/// Text is the default and the fallback: an unrecognised kind, or a payload that doesn't make sense, degrades to
/// a question with a text box, which is what it did before any of this existed.
/// </para>
/// </summary>
public sealed record PendingQuestion(
    string Question,
    IReadOnlyList<string> Options,
    string Kind = QuestionKind.Text,
    QuestionNumber? Number = null,
    QuestionPlace? Place = null);

/// <summary>The shapes of answer a question can ask for.</summary>
public static class QuestionKind
{
    /// <summary>Free text. The default, and what anything unrecognised falls back to.</summary>
    public const string Text = "text";

    /// <summary>One of the supplied options (still typeable, since the options are guesses).</summary>
    public const string Choice = "choice";

    /// <summary>A quantity — how many people, how long, what budget.</summary>
    public const string Number = "number";

    /// <summary>A place, shown on a map to be confirmed or corrected.</summary>
    public const string Place = "place";

    /// <summary>Yes or no, where the question really is binary.</summary>
    public const string Confirm = "confirm";

    /// <summary>Whether we know how to render this kind; anything else becomes plain text.</summary>
    public static bool Known(string? kind) =>
        kind is Text or Choice or Number or Place or Confirm;
}

/// <summary>What a number question is asking for, so the control can be labelled and bounded.</summary>
public sealed record QuestionNumber(string? Unit, double? Min, double? Max, double? Suggested);

/// <summary>The place a question is asking about — shown on a map so it can be confirmed at a glance.</summary>
public sealed record QuestionPlace(double Latitude, double Longitude, string? Label);

/// <summary>A file the user attached to a turn, already downloaded to local disk. Surfaced to the
/// orchestrator in context and copied into a delegated task's workspace so the worker can read/use it.</summary>
public sealed record Attachment(string Name, string LocalPath, string? MimeType, long Size);

/// <summary>A delegated background task the orchestrator can track, peek at, steer, answer, and cancel.</summary>
public sealed class TaskInfo
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public string? Project { get; init; }            // slug of the project this task runs within, if any
    public string? Persona { get; set; }             // id of the specialist persona handling it (set by triage, if any)
    public string? Brand { get; set; }               // for branding work: which brand kit (slug) this task uses; "house" = the agency's own
    public string? ParentTaskId { get; init; }       // set on a plan's child STEP tasks; null for top-level tasks

    /// <summary>Set on the per-item children of a <c>fan_out</c>. They don't get the fan_out tool themselves —
    /// one level of "do this for each of these" is the useful shape; a worker fanning out from inside a fan-out
    /// is a runaway, not a plan. A plan STEP is still allowed to fan out, which is why this is its own flag and
    /// not just "has a parent".</summary>
    public bool IsFanOutChild { get; init; }

    /// <summary>Set on a task reconstructed from a previous process's recorded run. It gets one explanatory
    /// note on its next leg — that the transcript above is history, not a live browser — and the flag is
    /// cleared, so a long resumed task isn't told the same thing on every turn.</summary>
    public bool Rebuilt { get; set; }

    /// <summary>
    /// The home-page panel this task is building, if it is building one.
    ///
    /// <para>
    /// Two things hang off it. The worker gets <c>widget_publish</c>, which nothing else should have — a panel is
    /// published by the job that built it and by nobody else. And a build that fails has to mark the PANEL failed,
    /// otherwise it sits on the page saying "being built" by a task that stopped an hour ago.
    /// </para>
    /// </summary>
    public string? BuildsWidget { get; set; }

    /// <summary>
    /// This task drives a panel build as a sequence of states rather than as one long brief.
    /// </summary>
    /// <remarks>
    /// On the COORDINATOR — the task the user's request created. It runs no model of its own: it walks the states,
    /// and each state is a child task with its own brief, its own tools and its own budget.
    /// </remarks>
    public bool PanelStates { get; set; }

    /// <summary>What the panel must show, as the user asked for it. The coordinator's input to every state.</summary>
    public string? PanelShows { get; set; }

    /// <summary>
    /// Which state of a panel build this task IS.
    /// </summary>
    /// <remarks>
    /// Set on the child, and it decides the brief and the toolset. Null on everything else, including the
    /// coordinator — which is how the tool trim tells "a panel build" from "one state of one".
    /// </remarks>
    public PanelStep? PanelStep { get; set; }

    /// <summary>This task is CHANGING an existing panel rather than building one. Its own flag rather than a
    /// step, because an adjust is not one of the build's states: it keeps every panel tool (it has to publish
    /// what it changed) and, like the design state, has no use for a browser.</summary>
    public bool AdjustsWidget { get; set; }

    /// <summary>
    /// This task is only finding a missing PARAMETER for that panel, not building it.
    ///
    /// <para>
    /// It changes which tool the worker gets: widget_fill supplies values to a panel whose component and feed
    /// already exist, where widget_publish would have it write a whole new kind. Handing a filler the publish tool
    /// invites exactly that, and the result would be a second library entry for a shape that already had one.
    /// </para>
    /// </summary>
    public bool FillsWidget { get; set; }

    /// <summary>
    /// This task is setting up something to WATCH: finding a source that keeps producing, and saying what to do when
    /// it does.
    /// </summary>
    /// <remarks>
    /// Same argument as the panel flag above, and the same economics. Finding a feed is a job — an address, whether it
    /// answers, how its items are shaped, what a topic is here — and it happens once; after that, watching costs an
    /// HTTP GET on a timer. So the tools that create a feed and a watcher belong to the job that did the finding and
    /// to nothing else, or every worker in the system can quietly arrange to be woken up later.
    /// </remarks>
    public bool SetsUpWatch { get; set; }

    /// <summary>
    /// This task is Proact acting on its own initiative — <c>attend</c> or <c>discover</c>.
    /// </summary>
    /// <remarks>
    /// The flag that makes the one rule enforceable. Nobody asked for this work, so nobody is watching it, so the
    /// worker must not be able to send, buy, book or cancel anything — and the only reliable way to guarantee that is
    /// to take the tools away rather than to ask it nicely. See <see cref="Proact"/>.
    ///
    /// <para>
    /// A persona cannot do this job, which is worth stating because it is the obvious place to look. After a
    /// persona's capabilities are gathered, every tool from the default worker set that is missing gets added back —
    /// on purpose, so that one bad routing decision costs some specialism rather than the ability to work at all.
    /// Right for a task somebody asked for; wrong for one nobody did.
    /// </para>
    /// </remarks>
    public string? Proact { get; set; }

    /// <summary>This task is carrying out a proposal the user ticked.</summary>
    /// <remarks>
    /// Not <see cref="Proact"/>, deliberately — a tick runs as an ORDINARY task with an ordinary toolset, which is
    /// the whole architecture of the feature. But it is not an ordinary REQUEST: the user has approved one specific
    /// written plan and expects that plan, so the executor has to be able to carry it out to the letter. Ordinary
    /// tasks get their reach from what they were asked to do; this one gets it from what was promised.
    /// </remarks>
    public bool Ticked { get; set; }

    /// <summary>This task was handed the browser, so its tabs are cleaned up when it ends.</summary>
    public bool UsedBrowser { get; set; }
    public WorkPlan? Plan { get; set; }              // set on a COORDINATOR task that runs a multi-discipline plan
    public string? UserScope { get; init; }          // memory scope of the person who asked, e.g. "user:U123"

    /// <summary>The assistant message that kicked this task off, so its progress can be shown under it. -1 when
    /// unknown (a scheduled firing has no originating message).</summary>
    public int OriginMessageId { get; set; } = -1;

    /// <summary>What the worker is told before its first leg and never again: where its workspace is, and what
    /// was learned the last time a job like this was done. Carried here rather than seeded into the transcript,
    /// because both are volatile per task, and volatile content at the HEAD of a prompt costs the provider's
    /// prefix cache for everything behind it. Cleared once it has ridden a drive message.</summary>
    public string? OpeningNote { get; set; }

    /// <summary>Guides this task was handed, by name — so the outcome can be credited or charged to them.</summary>
    public List<string> Guides { get; } = new();

    /// <summary>What this task has spent, per model — accumulated across every leg, plan step and gate call it
    /// makes, so the control centre can attribute cost to the work that caused it.</summary>
    public ModelSpend Spend { get; } = new();

    /// <summary>The room this task inherited from the conversation that started it — so a worker recalls exactly
    /// what the people in that conversation were party to, and nothing wider.</summary>
    public BrainContext Room { get; set; } = BrainContext.Unknown;
    public string? UserName { get; init; }            // their display name, for context
    public string Status { get; set; } = "running"; // running | waiting | done | cancelled | failed
    public bool Assessed { get; set; }               // has the routing/complexity gate already run? (survives a clarify pause/resume that flips firstLeg)
    public bool Seeded { get; set; }                 // have the clock/workspace context seeds been added? (survives a clarify pause/resume)
    public string? LatestThought { get; set; } // the worker's recent reasoning, for status peeks
    public string? Result { get; set; } // the final answer once finished
    // File names ACTUALLY delivered to the user for this task (recorded at the real upload points — send_file and
    // the end-of-leg deliver pass). The re-voice reads this so it never claims it "sent a file" when none went
    // out — a recurring false claim. Recorded on the root task so a plan's step deliveries roll up to it.
    public HashSet<string> DeliveredFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Files delivered but not yet announced: (path, name), waiting for the message that tells the user about them.
    /// <para>
    /// A file is produced at the end of the work, and the message announcing it is written after that — so a card
    /// pinned to the message that STARTED the task appears above its own announcement, under the "I'm on it" line.
    /// Holding them here for the length of one re-voice puts the card under the words that describe it.
    /// </para>
    /// </summary>
    public List<(string Path, string Name)> UnannouncedFiles { get; } = new();

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public CancellationTokenSource Cts { get; } = new();

    /// <summary>Out-of-band messages handed to the running worker between iterations (steer / interrupt).</summary>
    public ConcurrentQueue<string> Inbox { get; } = new();

    /// <summary>Set while the worker is paused on a question (status == waiting).</summary>
    public PendingQuestion? Pending { get; set; }

    /// <summary>The task's own working directory (when the orchestrator is configured with a workspace root).
    /// Holds task.md (the brief) and a files/ subfolder with any attachments — handed to the worker so it
    /// reads the brief and the provided files from one place.</summary>
    public string? WorkspaceDir { get; set; }

    /// <summary>The worker's accumulated transcript. When the user answers a question, the worker is re-run
    /// seeded with this, so it continues with its full prior context (findings + the Q&amp;A) — a clean,
    /// stateless resume rather than a live suspended process.</summary>
    public List<Message> Conversation { get; set; } = new();

    public bool IsRunning => Status == "running";
    public bool IsActive => Status is "running" or "waiting";
}

/// <summary>
/// A long-lived conversation. Unlike a one-shot run, a session has a persistent, append-only event
/// stream that the client stays subscribed to — so the orchestrator's replies AND results pushed
/// back from background workers can all arrive asynchronously over the same channel. Events carry a
/// message id so deltas route to the right assistant message in the UI.
/// </summary>
public sealed class Session
{
    private readonly object _lock = new();
    private readonly List<SessionEvent> _events = new();
    private volatile TaskCompletionSource<bool> _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextMessageId;
    private int _nextTaskId;

    public Session(string id) => Id = id;

    public string Id { get; }

    /// <summary>The orchestrator's conversation memory.</summary>
    public List<Message> History { get; } = new();

    /// <summary>Serialises orchestrator turns (a user reply and a worker re-voice can't interleave).</summary>
    public SemaphoreSlim TurnLock { get; } = new(1, 1);

    /// <summary>Background tasks the orchestrator has delegated, keyed by short id, alive for the session.</summary>
    public ConcurrentDictionary<string, TaskInfo> Tasks { get; } = new();

    /// <summary>When the user last spoke — so proactive nudges stay quiet while a conversation is active.</summary>
    public DateTimeOffset LastUserMessageAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// What the user actually typed this turn, without the notes appended for the model's benefit.
    /// <para>
    /// The stored user message is their words PLUS the attachment note and the "projects on the go" note — and
    /// that second one carries project titles, descriptions and their lists. Anything that searches memory with
    /// the stored message is therefore searching for the projects, not for what was asked: "Try again" in a chat
    /// about a shopping list came back with the hotel, the destination and the holiday budget, because the query
    /// contained all of them.
    /// </para>
    /// </summary>
    public string? LastUserText { get; set; }

    /// <summary>
    /// The queue id of what was overheard this turn, so a deliberate filing replaces it instead of doubling it.
    /// </summary>
    /// <remarks>
    /// Turn-scoped and set on every user message. Held as an id rather than as the words, because the point is to avoid
    /// deciding whether two wordings of one sentence are the same sentence.
    /// </remarks>
    public string? OverheardThisTurn { get; set; }

    /// <summary>The project the conversation is currently focused on (slug), set when find_project resolves
    /// a reference. Surfaces that project's context in chat and routes project-detail writes to it — until
    /// the talk clearly moves on. Null = no project in focus (the default).</summary>
    public string? CurrentProject { get; set; }

    /// <summary>
    /// Consecutive turns since anything actually touched <see cref="CurrentProject"/>.
    /// <para>
    /// The focus is meant to be soft, but nothing used to let go of it: once a project was resolved, every later
    /// message arrived wrapped in "you're currently focused on X" plus that project's details — so a question
    /// about something else entirely got answered as though it were about the project, and the model was told to
    /// drop the focus while having no means to. Counting quiet turns is the means: touch it and this resets,
    /// ignore it and the focus fades on its own.
    /// </para>
    /// </summary>
    public int TurnsSinceProjectTouched { get; set; }

    /// <summary>When set, this whole session is the DEDICATED chat for one project (its own conversation,
    /// opened from the project page). The orchestrator stays scoped to it — project-only topic, tools, and
    /// memory — and won't wander. Distinct from <see cref="CurrentProject"/>, which is a soft, shifting
    /// focus on the general chat.</summary>
    public string? PinnedProject { get; set; }

    /// <summary>The current turn's speaker as a memory scope (e.g. "user:U123") and display name — set per
    /// turn by the host that knows who's talking (Slack). Drives per-user memory: writes default to this
    /// person, reads span this person + the shared scope. Null in the single-user web app.</summary>
    public string? CurrentUserScope { get; set; }
    public string? CurrentUserName { get; set; }

    /// <summary>
    /// Who can hear this conversation, as surface identities (<c>slack:U123</c>, an email address, <c>web:local</c>)
    /// — the raw material the brain's audience is built from. Set per turn by the host that knows the room:
    /// Slack from the channel's membership, the web app from its single local user.
    /// </summary>
    /// <remarks>
    /// Re-supplied each turn rather than captured once, because membership changes: someone joining a private
    /// channel has to narrow what can be recalled there on the very next message, with nothing to invalidate.
    /// </remarks>
    public IReadOnlyList<string> ParticipantAliases { get; set; } = Array.Empty<string>();

    /// <summary>True when the room is open to the whole organisation (a public channel). Such a room both writes
    /// and reads public knowledge only — it is not a place to surface what a few people said in private.</summary>
    public bool IsPublicRoom { get; set; }

    /// <summary>The room as the brain sees it, once the host's aliases have been resolved to people. Set per turn
    /// alongside <see cref="ParticipantAliases"/>; <see cref="BrainContext.Unknown"/> until then, which recalls
    /// public knowledge only and refuses writes.</summary>
    public BrainContext Room { get; set; } = BrainContext.Unknown;

    /// <summary>The assistant message id the turn currently being handled is writing into. Turn-scoped (only
    /// meaningful while a turn holds <see cref="TurnLock"/>), and carried on the "working" event so the UI can
    /// show a task's progress under the message that actually kicked it off, instead of in a detached list.</summary>
    public int CurrentMessageId { get; set; } = -1;

    /// <summary>
    /// Pictures found mid-task, waiting for the message they belong under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Held rather than shown the moment they arrive, because the message being composed while a task runs is the one
    /// that said it had started — so nine photographs landed under "let me re-open that task" and the answer describing
    /// them arrived fourteen seconds later, underneath. Read in order, the pictures came before anything explained what
    /// they were.
    /// </para>
    /// <para>
    /// So they wait for the next thing said and go out attached to it. A queue rather than a single slot: two tasks can
    /// be finding pictures at once, and both sets belong under whatever is said next.
    /// </para>
    /// </remarks>
    public List<object> WaitingPictures { get; } = new();

    /// <summary>Whether pictures were shown at any point in this leg. Stops the same images being sent as files too.</summary>
    public bool ShowedPictures { get; set; }

    /// <summary>Files attached to the turn currently being handled — set at the top of a turn by the host
    /// (Slack), read by delegate so the files are copied into the task's workspace, then cleared. Turn-scoped:
    /// only valid while a turn holds <see cref="TurnLock"/>.</summary>
    public IReadOnlyList<Attachment>? PendingAttachments { get; set; }

    public DateTimeOffset LastActivity { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>Optional observer of this session's events. Null in the web app (pure SSE). A non-browser
    /// host sets this to mirror events to its own channel (Slack, etc.) as they are appended.</summary>
    public IEventSink? Sink { get; set; }

    public int NextMessageId()
    {
        lock (_lock) return _nextMessageId++;
    }

    public string NextTaskId() => Interlocked.Increment(ref _nextTaskId).ToString();

    public int Count
    {
        get { lock (_lock) return _events.Count; }
    }

    /// <summary>Where this conversation is written down, so it survives a restart. Null keeps the old behaviour
    /// of remembering nothing. Not to be confused with History above, which is the model's transcript.</summary>
    public ChatStore? Recorder { get; set; }

    /// <summary>
    /// Load previously recorded events into this session's buffer, without recording them again.
    /// <para>
    /// Used when a conversation is reopened in a process that never saw it. They go in as ordinary events so
    /// everything downstream — the SSE replay, ?from=N, the client's reconnect — behaves exactly as it does for a
    /// live chat, and nothing needs to know the difference.
    /// </para>
    /// </summary>
    public void Replay(IReadOnlyList<(string Event, string Data)> recorded)
    {
        lock (_lock)
        {
            if (_events.Count > 0) return; // a live session is the truth; never merge history into one
            foreach (var (@event, data) in recorded)
            {
                _events.Add(new SessionEvent(@event, data));

                // Numbering has to continue past what was replayed. Left at zero, the first thing said in a
                // reopened conversation is emitted as message 0 — which the client already holds as the first
                // line of the history, so the new message lands inside the old one instead of after it.
                var id = MessageIdOf(data);
                if (id >= _nextMessageId) _nextMessageId = id + 1;

                // The same applies to TASK numbering, and getting it wrong is worse. A recycled task id is read
                // as the task that already had it: the new job silently inherits the old one's finished status,
                // so it never shows as running, never gets a spinner, and its clock restarts from whenever you
                // last reloaded — because every reload re-adopts the first event's start time.
                if (@event is "working" or "working_done" && TaskIdOf(data) is { } taskId && taskId >= _nextTaskId)
                    _nextTaskId = taskId;
            }
        }
    }

    /// <summary>A task event's id. They're stringified counters ("1", "2"), so anything else is not one of ours.</summary>
    private static int? TaskIdOf(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("id", out var id)) return null;
            var text = id.ValueKind == JsonValueKind.String ? id.GetString() : id.ToString();
            return int.TryParse(text, out var n) ? n : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int MessageIdOf(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            return doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
                   && id.TryGetInt32(out var n)
                ? n
                : -1;
        }
        catch (JsonException)
        {
            return -1;
        }
    }

    public void Append(string @event, string data)
    {
        lock (_lock)
        {
            _events.Add(new SessionEvent(@event, data));
            LastActivity = DateTimeOffset.UtcNow;
        }
        // Mirrored to disk before the sink, so a conversation is recorded even if a sink throws.
        Recorder?.Append(Id, @event, data);
        // Mirror to an attached sink (e.g. Slack). Best-effort: a sink fault must never break the turn or
        // the SSE buffer above. Null in the web app, so this is a no-op there.
        try { Sink?.OnEvent(@event, data); }
        catch (Exception ex) { Console.Error.WriteLine($"[sink] {ex.Message}"); }
        Signal();
    }

    /// <summary>Everything recorded on this session so far. Used to rebuild the model's own view of the
    /// conversation after a restart, from the same log the client renders.</summary>
    public IReadOnlyList<SessionEvent> Snapshot()
    {
        lock (_lock) return _events.ToList();
    }

    public bool TryGet(int index, out SessionEvent ev)
    {
        lock (_lock)
        {
            if (index >= 0 && index < _events.Count)
            {
                ev = _events[index];
                return true;
            }
        }
        ev = default!;
        return false;
    }

    public Task WaitForChangeAsync() => _signal.Task;

    private void Signal()
    {
        var tcs = Interlocked.Exchange(ref _signal, new(TaskCreationOptions.RunContinuationsAsynchronously));
        tcs.TrySetResult(true);
    }
}

/// <summary>Holds the active sessions, keyed by a client-chosen id (so a reload reattaches).</summary>
public sealed class SessionStore
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly Action<Session>? _onCreate;

    /// <param name="onCreate">Optional hook run once when a session is first created — used to attach an
    /// observer sink (e.g. the control hub) so every new conversation is tracked from its first event.</param>
    public SessionStore(Action<Session>? onCreate = null) => _onCreate = onCreate;

    public Session GetOrCreate(string id) => _sessions.GetOrAdd(id, key =>
    {
        var s = new Session(key);
        _onCreate?.Invoke(s);
        return s;
    });

    public Session? Get(string id) => _sessions.TryGetValue(id, out var s) ? s : null;

    /// <summary>
    /// Drop a session from memory. Used when a conversation is deleted: without this the live session survives
    /// its own history and re-recording starts the moment anything appends to it again.
    /// </summary>
    public bool Remove(string id) => _sessions.TryRemove(id, out _);

    /// <summary>
    /// Let go of a project every conversation was holding, and say how many were.
    /// </summary>
    /// <remarks>
    /// For when the project is deleted. A pinned chat stays scoped to whatever it was pinned to — narrower toolset, its
    /// context surfaced, told to stay on the subject — so one left pinned to something that no longer exists is a chat
    /// that offers project tools for a project nothing resolves and declines to talk about anything else.
    /// </remarks>
    public int Unpin(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return 0;

        var let = 0;
        foreach (var session in _sessions.Values)
        {
            if (string.Equals(session.PinnedProject, slug, StringComparison.OrdinalIgnoreCase))
            {
                session.PinnedProject = null;
                let++;
            }

            if (string.Equals(session.CurrentProject, slug, StringComparison.OrdinalIgnoreCase))
                session.CurrentProject = null;
        }

        return let;
    }

    /// <summary>All sessions currently held in memory.</summary>
    public IReadOnlyList<Session> All => _sessions.Values.ToList();
}
