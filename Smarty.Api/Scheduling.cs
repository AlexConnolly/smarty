using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Smarty.Api;

/// <summary>
/// One scheduled task: a FROZEN instruction to be carried out at <see cref="FireAt"/> in the thread it was
/// created in. The instruction never changes after scheduling; what DOES change is the context it runs
/// against — when it fires, the host refreshes the live thread first, so a "check back next week and save the
/// decision" task sees whatever was decided in the meantime. Persisted, so a reminder survives a restart.
/// </summary>
public sealed class ScheduledTask
{
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";   // "slack:<channel>:<threadTs>" — the conversation it belongs to
    public string Channel { get; set; } = "";
    public string ThreadTs { get; set; } = "";
    public string TaskText { get; set; } = "";     // the frozen instruction, verbatim
    public DateTimeOffset FireAt { get; set; }      // stored UTC
    public DateTimeOffset CreatedAt { get; set; }
    public string? UserScope { get; set; }          // who set it (memory scope), so the fire acts as them
    public string? UserName { get; set; }
    public string Status { get; set; } = "pending"; // pending | firing | done | failed | cancelled
}

/// <summary>
/// A persisted list of scheduled tasks (JSON file, mirroring <see cref="MemoryStore"/>). Thread-safe.
/// A task left "firing" by a crash is reverted to "pending" on load so it still goes off.
/// </summary>
public sealed class ScheduleStore
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly JsonSerializerOptions _json;
    private readonly List<ScheduledTask> _tasks = new();
    private int _next;

    public ScheduleStore(string path, JsonSerializerOptions json)
    {
        _path = path;
        _json = json;
        Load();
    }

    public ScheduledTask Add(string sessionId, string channel, string threadTs, string taskText,
        DateTimeOffset fireAtUtc, string? userScope, string? userName)
    {
        lock (_lock)
        {
            var t = new ScheduledTask
            {
                Id = (++_next).ToString(),
                SessionId = sessionId,
                Channel = channel,
                ThreadTs = threadTs,
                TaskText = taskText,
                FireAt = fireAtUtc,
                CreatedAt = DateTimeOffset.UtcNow,
                UserScope = userScope,
                UserName = userName,
            };
            _tasks.Add(t);
            Save();
            return t;
        }
    }

    /// <summary>Pending tasks whose time has come.</summary>
    public IReadOnlyList<ScheduledTask> Due(DateTimeOffset nowUtc)
    {
        lock (_lock) return _tasks.Where(t => t.Status == "pending" && t.FireAt <= nowUtc).ToList();
    }

    /// <summary>Atomically move a pending task to "firing" so a single tick fires it exactly once.</summary>
    public bool TryClaim(string id)
    {
        lock (_lock)
        {
            var t = _tasks.FirstOrDefault(x => x.Id == id);
            if (t is null || t.Status != "pending") return false;
            t.Status = "firing";
            Save();
            return true;
        }
    }

    public void Complete(string id, bool failed = false)
    {
        lock (_lock)
        {
            var t = _tasks.FirstOrDefault(x => x.Id == id);
            if (t is null) return;
            t.Status = failed ? "failed" : "done";
            Save();
        }
    }

    public bool Cancel(string id)
    {
        lock (_lock)
        {
            var t = _tasks.FirstOrDefault(x => x.Id == id && x.Status is "pending" or "firing");
            if (t is null) return false;
            t.Status = "cancelled";
            Save();
            return true;
        }
    }

    /// <summary>Pending tasks for one conversation — for the orchestrator's context note and cancellation.</summary>
    public IReadOnlyList<ScheduledTask> PendingFor(string sessionId)
    {
        lock (_lock)
            return _tasks.Where(t => t.Status == "pending" && t.SessionId == sessionId)
                         .OrderBy(t => t.FireAt).ToList();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<List<ScheduledTask>>(File.ReadAllText(_path), _json);
            if (loaded is not null)
            {
                foreach (var t in loaded)
                    if (t.Status == "firing") t.Status = "pending"; // a crash mid-fire → let it fire again
                _tasks.AddRange(loaded);
                _next = _tasks.Select(t => int.TryParse(t.Id, out var n) ? n : 0).DefaultIfEmpty(0).Max();
            }
        }
        catch { /* a corrupt schedule file shouldn't crash startup */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_tasks, _json));
        }
        catch { /* best-effort persistence */ }
    }

    // ---- time parsing ----------------------------------------------------------------------------

    private static readonly Regex Relative = new(
        @"^in\s+(\d+)\s*(minutes?|mins?|m|hours?|hrs?|h|days?|d|weeks?|w)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Parse a `when` string DETERMINISTICALLY (the model does any "X before/after" arithmetic and
    /// hands us a concrete time): a relative "in N minutes/hours/days/weeks", or an absolute date-time
    /// (assumed local if it carries no offset). Returns the fire time in UTC.</summary>
    public static bool TryParseWhen(string when, DateTimeOffset nowLocal, out DateTimeOffset fireAtUtc)
    {
        fireAtUtc = default;
        if (string.IsNullOrWhiteSpace(when)) return false;
        when = when.Trim();

        var m = Relative.Match(when);
        if (m.Success)
        {
            int n = int.Parse(m.Groups[1].Value);
            string u = m.Groups[2].Value.ToLowerInvariant();
            TimeSpan span = u[0] switch
            {
                'm' => TimeSpan.FromMinutes(n),
                'h' => TimeSpan.FromHours(n),
                'd' => TimeSpan.FromDays(n),
                'w' => TimeSpan.FromDays(7 * n),
                _ => TimeSpan.Zero,
            };
            if (span <= TimeSpan.Zero) return false;
            fireAtUtc = nowLocal.ToUniversalTime() + span;
            return true;
        }

        if (DateTimeOffset.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
        {
            fireAtUtc = dto.ToUniversalTime();
            return true;
        }
        return false;
    }
}

/// <summary>
/// A simple in-process scheduler: on each tick, claim every due task and hand it to a host-supplied <c>fire</c>
/// delegate (which re-attaches the thread, refreshes its context, and runs the task). Survives a restart via
/// <see cref="ScheduleStore"/>; on boot, any task already past due simply fires on the first tick.
/// </summary>
public sealed class Scheduler
{
    private readonly ScheduleStore _store;
    private readonly Func<ScheduledTask, Task> _fire;
    private readonly TimeSpan _tick;

    public Scheduler(ScheduleStore store, Func<ScheduledTask, Task> fire, TimeSpan? tick = null)
    {
        _store = store;
        _fire = fire;
        _tick = tick ?? TimeSpan.FromSeconds(20);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var t in _store.Due(DateTimeOffset.UtcNow))
            {
                if (!_store.TryClaim(t.Id)) continue;
                _ = FireOneAsync(t); // fire concurrently; each completes/fails independently
            }
            try { await Task.Delay(_tick, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task FireOneAsync(ScheduledTask t)
    {
        try { await _fire(t).ConfigureAwait(false); _store.Complete(t.Id); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[schedule] fire #{t.Id} failed: {ex.Message}");
            _store.Complete(t.Id, failed: true);
        }
    }
}
