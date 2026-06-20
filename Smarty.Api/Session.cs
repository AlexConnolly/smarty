using System.Collections.Concurrent;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>One buffered Server-Sent Event on a session's stream.</summary>
public sealed record SessionEvent(string Event, string Data);

/// <summary>A delegated background task the orchestrator can track, peek at, steer, and cancel.</summary>
public sealed class TaskInfo
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public string? Project { get; init; }            // slug of the project this task runs within, if any
    public string Status { get; set; } = "running"; // running | done | cancelled | failed
    public string? LatestThought { get; set; } // the worker's recent reasoning, for status peeks
    public string? Result { get; set; } // the final answer once finished
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public CancellationTokenSource Cts { get; } = new();

    /// <summary>Out-of-band messages handed to the running worker between iterations (steer / interrupt).</summary>
    public ConcurrentQueue<string> Inbox { get; } = new();

    public bool IsRunning => Status == "running";
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

    public DateTimeOffset LastActivity { get; private set; } = DateTimeOffset.UtcNow;

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
