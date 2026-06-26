using System.Collections.Concurrent;
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

/// <summary>A structured question a worker has paused to ask: the question plus a few precomputed answers
/// (the user can always type their own instead).</summary>
public sealed record PendingQuestion(string Question, IReadOnlyList<string> Options);

/// <summary>A delegated background task the orchestrator can track, peek at, steer, answer, and cancel.</summary>
public sealed class TaskInfo
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public string? Project { get; init; }            // slug of the project this task runs within, if any
    public string Status { get; set; } = "running"; // running | waiting | done | cancelled | failed
    public string? LatestThought { get; set; } // the worker's recent reasoning, for status peeks
    public string? Result { get; set; } // the final answer once finished
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public CancellationTokenSource Cts { get; } = new();

    /// <summary>Out-of-band messages handed to the running worker between iterations (steer / interrupt).</summary>
    public ConcurrentQueue<string> Inbox { get; } = new();

    /// <summary>Set while the worker is paused on a question (status == waiting).</summary>
    public PendingQuestion? Pending { get; set; }

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

    /// <summary>The project the conversation is currently focused on (slug), set when find_project resolves
    /// a reference. Surfaces that project's context in chat and routes project-detail writes to it — until
    /// the talk clearly moves on. Null = no project in focus (the default).</summary>
    public string? CurrentProject { get; set; }

    /// <summary>When set, this whole session is the DEDICATED chat for one project (its own conversation,
    /// opened from the project page). The orchestrator stays scoped to it — project-only topic, tools, and
    /// memory — and won't wander. Distinct from <see cref="CurrentProject"/>, which is a soft, shifting
    /// focus on the general chat.</summary>
    public string? PinnedProject { get; set; }

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

    public void Append(string @event, string data)
    {
        lock (_lock)
        {
            _events.Add(new SessionEvent(@event, data));
            LastActivity = DateTimeOffset.UtcNow;
        }
        // Mirror to an attached sink (e.g. Slack). Best-effort: a sink fault must never break the turn or
        // the SSE buffer above. Null in the web app, so this is a no-op there.
        try { Sink?.OnEvent(@event, data); }
        catch (Exception ex) { Console.Error.WriteLine($"[sink] {ex.Message}"); }
        Signal();
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

    public Session GetOrCreate(string id) => _sessions.GetOrAdd(id, key => new Session(key));

    public Session? Get(string id) => _sessions.TryGetValue(id, out var s) ? s : null;
}
