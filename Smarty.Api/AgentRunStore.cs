using System.Collections.Concurrent;

namespace Smarty.Api;

/// <summary>
/// Holds the in-flight (and recently completed) agent runs, keyed by id, so clients can subscribe
/// and reconnect. In-memory: runs do not survive an API restart — that's the seam where a durable
/// store (Hangfire / a queue / a DB) would slot in if cross-restart persistence is ever needed.
/// </summary>
public sealed class AgentRunStore
{
    private static readonly TimeSpan KeepCompletedFor = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, AgentRunSession> _runs = new();

    public AgentRunSession Create()
    {
        EvictStale();
        var session = new AgentRunSession();
        _runs[session.Id] = session;
        return session;
    }

    public AgentRunSession? Get(string id) => _runs.TryGetValue(id, out var s) ? s : null;

    private void EvictStale()
    {
        var cutoff = DateTimeOffset.UtcNow - KeepCompletedFor;
        foreach (var kv in _runs)
            if (kv.Value.Completed && kv.Value.LastActivity < cutoff)
                _runs.TryRemove(kv.Key, out _);
    }
}
