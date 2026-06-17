namespace Smarty.Api;

/// <summary>One buffered Server-Sent Event belonging to a run.</summary>
public sealed record RunEvent(string Event, string Data);

/// <summary>
/// A single agent run, executing independently of any HTTP connection. Events are buffered in an
/// append-only list so a client can (re)subscribe at any offset — disconnecting never stops the run.
/// </summary>
public sealed class AgentRunSession
{
    private readonly object _lock = new();
    private readonly List<RunEvent> _events = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile TaskCompletionSource<bool> _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Id { get; } = Guid.NewGuid().ToString("n");

    public bool Completed { get; private set; }

    public DateTimeOffset LastActivity { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>Cancelled only by an explicit stop — never by a client disconnect.</summary>
    public CancellationToken Token => _cts.Token;

    public int Count
    {
        get { lock (_lock) return _events.Count; }
    }

    public void Append(string @event, string data)
    {
        lock (_lock)
        {
            _events.Add(new RunEvent(@event, data));
            LastActivity = DateTimeOffset.UtcNow;
        }
        Signal();
    }

    public bool TryGet(int index, out RunEvent ev)
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

    public void Complete()
    {
        lock (_lock) Completed = true;
        Signal();
    }

    public void Cancel() => _cts.Cancel();

    /// <summary>Completes the next time an event is appended or the run completes.</summary>
    public Task WaitForChangeAsync() => _signal.Task;

    private void Signal()
    {
        var tcs = Interlocked.Exchange(ref _signal, new(TaskCreationOptions.RunContinuationsAsynchronously));
        tcs.TrySetResult(true);
    }
}
