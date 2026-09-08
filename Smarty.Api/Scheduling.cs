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

    /// <summary>The conversation it belongs to and fires into. A Slack thread ("slack:&lt;channel&gt;:&lt;threadTs&gt;")
    /// or a web conversation id — the scheduler doesn't care which, and neither does the fire.</summary>
    public string SessionId { get; set; } = "";

    // Slack's own addressing, so a Slack fire can re-attach the thread. Empty for everything else.
    public string Channel { get; set; } = "";
    public string ThreadTs { get; set; } = "";

    public string TaskText { get; set; } = "";     // the frozen instruction, verbatim
    public DateTimeOffset FireAt { get; set; }      // when it next goes off, stored UTC
    public DateTimeOffset CreatedAt { get; set; }
    public string? UserScope { get; set; }          // who set it (memory scope), so the fire acts as them
    public string? UserName { get; set; }
    public string Status { get; set; } = "pending"; // pending | firing | done | failed | cancelled

    /// <summary>
    /// How often it comes back, as a phrase — "daily at 08:00", "every 2 hours", "weekly on monday at 09:00".
    /// Empty for a one-shot.
    ///
    /// <para>
    /// A phrase rather than a cron expression on purpose. This is set by saying "every morning check the
    /// overnight orders", by a model filling in a tool argument, or by typing in a box on a page — three places
    /// where five space-separated asterisks are a worse answer than the words. It is parsed deterministically
    /// either way (see <see cref="ScheduleStore.TryParseRepeat"/>); nothing about it is guessed at fire time.
    /// </para>
    /// </summary>
    public string Repeat { get; set; } = "";

    /// <summary>How many times it has gone off. A recurring task's history in one number.</summary>
    public int Runs { get; set; }

    public DateTimeOffset? LastFiredAt { get; set; }

    /// <summary>
    /// What it said last time, trimmed.
    ///
    /// <para>
    /// Kept because a recurring task's results otherwise live only in the conversation it fires into, which is
    /// the right place to READ them — that thread is what gives the next answer its context — and the wrong
    /// place to check whether the thing has been working. Six weeks of a daily job is six weeks of scrollback.
    /// </para>
    /// </summary>
    public string? LastResult { get; set; }

    /// <summary>Set when a recurring task is paused: it keeps its place in the list and stops going off.</summary>
    public bool Paused { get; set; }

    /// <summary>Does it come back after firing?</summary>
    public bool Recurring => Repeat.Trim().Length > 0;
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
        DateTimeOffset fireAtUtc, string? userScope, string? userName, string repeat = "")
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
                Repeat = (repeat ?? "").Trim(),
            };
            _tasks.Add(t);
            Save();
            return t;
        }
    }

    /// <summary>Pending tasks whose time has come. A paused one keeps its place and its time, and stays put.</summary>
    public IReadOnlyList<ScheduledTask> Due(DateTimeOffset nowUtc)
    {
        lock (_lock)
            return _tasks.Where(t => t.Status == "pending" && !t.Paused && t.FireAt <= nowUtc).ToList();
    }

    /// <summary>Everything, newest-due first — what the tasks page lists.</summary>
    public IReadOnlyList<ScheduledTask> All()
    {
        lock (_lock) return _tasks.OrderBy(t => t.Status == "pending" ? 0 : 1).ThenBy(t => t.FireAt).ToList();
    }

    public ScheduledTask? Get(string id)
    {
        lock (_lock) return _tasks.FirstOrDefault(t => t.Id == id);
    }

    /// <summary>
    /// Change when it next goes off, how often it comes back, what it does, or whether it is paused. Any
    /// argument left null is left alone. False if there is no such task, or the new recurrence is unreadable.
    /// </summary>
    public bool Edit(string id, DateTimeOffset? fireAtUtc = null, string? repeat = null,
        string? taskText = null, bool? paused = null)
    {
        lock (_lock)
        {
            var t = _tasks.FirstOrDefault(x => x.Id == id);
            if (t is null) return false;

            if (repeat is not null)
            {
                var trimmed = repeat.Trim();
                var fromNew = default(DateTimeOffset);
                // Refused rather than stored, so a typed recurrence that cannot be read is rejected at the point
                // someone can still fix it — not silently kept and then quietly never applied at fire time.
                if (trimmed.Length > 0 && !TryParseRepeat(trimmed, DateTimeOffset.Now, out fromNew)) return false;
                bool changed = !string.Equals(t.Repeat, trimmed, StringComparison.OrdinalIgnoreCase);
                t.Repeat = trimmed;

                // A new rhythm takes effect now, not after one more run of the old one. Moving something from
                // "every 2 minutes" to "daily at 07:30" and having it go off in ninety seconds regardless is the
                // change appearing not to have worked — the list says daily and the thing fires immediately.
                if (changed && trimmed.Length > 0 && fireAtUtc is null) t.FireAt = fromNew;
            }
            if (fireAtUtc is not null) t.FireAt = fireAtUtc.Value;
            if (taskText is { Length: > 0 }) t.TaskText = taskText.Trim();
            if (paused is not null) t.Paused = paused.Value;

            // Editing a finished one-shot puts it back in the queue: giving it a new time is the only thing
            // anyone means by that, and the alternative is a row you can edit that then does nothing.
            if (t.Status is "done" or "failed" or "cancelled" && (fireAtUtc is not null || repeat is not null))
                t.Status = "pending";

            Save();
            return true;
        }
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

    /// <summary>
    /// It has been. Records what happened, and — if it recurs — books the next one.
    ///
    /// <para>
    /// A failure does NOT stop a recurring task. A daily job that fell over because a site was down at 07:00 is
    /// not a job that should never run again; retiring it on the first bad morning is how a schedule silently
    /// becomes a schedule of one. The failure is recorded against it and it comes back tomorrow.
    /// </para>
    /// </summary>
    public void Complete(string id, bool failed = false, string? result = null)
    {
        lock (_lock)
        {
            var t = _tasks.FirstOrDefault(x => x.Id == id);
            if (t is null) return;

            t.Runs++;
            t.LastFiredAt = DateTimeOffset.UtcNow;
            if (result is { Length: > 0 })
                t.LastResult = result.Length <= 600 ? result : result[..600] + "…";

            if (t.Recurring && TryParseRepeat(t.Repeat, DateTimeOffset.Now, out var next))
            {
                t.FireAt = next;
                t.Status = "pending";
            }
            else t.Status = failed ? "failed" : "done";

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

    /// <summary>
    /// Split a Slack session id ("slack:&lt;channel&gt;:&lt;threadTs&gt;") into its parts. False for anything else,
    /// which is not a failure — a web conversation's id IS its whole address and needs no parts.
    /// </summary>
    public static bool TryParseSlackSessionId(string sessionId, out string channel, out string threadTs)
    {
        channel = ""; threadTs = "";
        const string prefix = "slack:";
        if (string.IsNullOrEmpty(sessionId) || !sessionId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = sessionId[prefix.Length..];
        int i = rest.IndexOf(':');
        if (i <= 0 || i >= rest.Length - 1) return false;
        channel = rest[..i];
        threadTs = rest[(i + 1)..];
        return true;
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

    // ---- recurrence ------------------------------------------------------------------------------
    //
    // Every one of these resolves to the NEXT concrete moment after `nowLocal`, computed here and stored as a
    // single UTC instant. Nothing about a recurrence is re-interpreted at fire time, which is what keeps a
    // scheduled task the same kind of thing whether it was set once or comes back daily — the tick only ever
    // asks "is FireAt in the past".

    private static readonly Regex EveryN = new(
        @"^every\s+(\d+)\s*(minutes?|mins?|m|hours?|hrs?|h|days?|d|weeks?|w)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Named = new(
        @"^(hourly|daily|weekly|every\s+day|every\s+hour|every\s+week|every\s+weekday)" +
        @"(?:\s+on\s+(?<day>monday|tuesday|wednesday|thursday|friday|saturday|sunday|mon|tue|tues|wed|thu|thur|thurs|fri|sat|sun))?" +
        @"(?:\s+at\s+(?<time>\d{1,2}(?::\d{2})?\s*(?:am|pm)?))?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The next time a recurrence phrase comes round, in UTC. False if the phrase isn't one we read.
    /// <para>
    /// Accepts "hourly", "daily", "weekly", "every weekday", "every N minutes/hours/days/weeks", any of the named
    /// ones with "at 08:00" / "at 7am", and "weekly on monday at 09:00". Deliberately a closed list: a phrase
    /// this can't read is refused when it is typed, which is better than accepting anything and then having a
    /// task that never goes off for reasons nobody can see.
    /// </para>
    /// </summary>
    public static bool TryParseRepeat(string repeat, DateTimeOffset nowLocal, out DateTimeOffset nextUtc)
    {
        nextUtc = default;
        if (string.IsNullOrWhiteSpace(repeat)) return false;
        repeat = repeat.Trim();

        var every = EveryN.Match(repeat);
        if (every.Success)
        {
            int n = int.Parse(every.Groups[1].Value);
            if (n <= 0) return false;
            var span = every.Groups[2].Value.ToLowerInvariant()[0] switch
            {
                'm' => TimeSpan.FromMinutes(n),
                'h' => TimeSpan.FromHours(n),
                'd' => TimeSpan.FromDays(n),
                'w' => TimeSpan.FromDays(7 * n),
                _ => TimeSpan.Zero,
            };
            if (span <= TimeSpan.Zero) return false;
            nextUtc = nowLocal.ToUniversalTime() + span;
            return true;
        }

        var named = Named.Match(repeat);
        if (!named.Success) return false;

        var kind = Regex.Replace(named.Groups[1].Value.ToLowerInvariant(), @"^every\s+", "");
        var at = named.Groups["time"].Value;
        var onDay = named.Groups["day"].Value;

        // No time of day given: come round one period from now, keeping whatever o'clock it is.
        if (at.Length == 0 && onDay.Length == 0)
        {
            nextUtc = kind switch
            {
                "hour" or "hourly" => nowLocal.AddHours(1).ToUniversalTime(),
                "day" or "daily" => nowLocal.AddDays(1).ToUniversalTime(),
                "week" or "weekly" => nowLocal.AddDays(7).ToUniversalTime(),
                "weekday" => NextWeekday(nowLocal.AddDays(1)).ToUniversalTime(),
                _ => default,
            };
            return nextUtc != default;
        }

        if (kind is "hour" or "hourly") return false; // "hourly at 09:00" is a daily job wearing the wrong word

        if (!TryParseTimeOfDay(at, out var hour, out var minute)) return false;

        // The next time that clock time comes round — today if it hasn't yet, otherwise tomorrow.
        var candidate = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, hour, minute, 0, nowLocal.Offset);
        if (candidate <= nowLocal) candidate = candidate.AddDays(1);

        if (onDay.Length > 0 || kind is "week" or "weekly")
        {
            // "weekly at 09:00" with no day means the same weekday it was set on.
            var want = onDay.Length > 0 ? Weekday(onDay) : nowLocal.DayOfWeek;
            if (want is null) return false;
            while (candidate.DayOfWeek != want.Value) candidate = candidate.AddDays(1);
        }
        else if (kind == "weekday") candidate = NextWeekday(candidate);

        nextUtc = candidate.ToUniversalTime();
        return true;
    }

    private static DateTimeOffset NextWeekday(DateTimeOffset from)
    {
        while (from.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) from = from.AddDays(1);
        return from;
    }

    private static DayOfWeek? Weekday(string name) => name.ToLowerInvariant() switch
    {
        "monday" or "mon" => DayOfWeek.Monday,
        "tuesday" or "tue" or "tues" => DayOfWeek.Tuesday,
        "wednesday" or "wed" => DayOfWeek.Wednesday,
        "thursday" or "thu" or "thur" or "thurs" => DayOfWeek.Thursday,
        "friday" or "fri" => DayOfWeek.Friday,
        "saturday" or "sat" => DayOfWeek.Saturday,
        "sunday" or "sun" => DayOfWeek.Sunday,
        _ => null,
    };

    private static bool TryParseTimeOfDay(string text, out int hour, out int minute)
    {
        hour = minute = 0;
        var m = Regex.Match(text.Trim(), @"^(\d{1,2})(?::(\d{2}))?\s*(am|pm)?$", RegexOptions.IgnoreCase);
        if (!m.Success) return false;

        hour = int.Parse(m.Groups[1].Value);
        minute = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        var suffix = m.Groups[3].Value.ToLowerInvariant();

        if (suffix == "pm" && hour < 12) hour += 12;
        else if (suffix == "am" && hour == 12) hour = 0;

        return hour is >= 0 and <= 23 && minute is >= 0 and <= 59;
    }

    /// <summary>How a recurrence reads back to a person. Empty for a one-shot.</summary>
    public static string DescribeRepeat(string repeat) =>
        string.IsNullOrWhiteSpace(repeat) ? "" : repeat.Trim().ToLowerInvariant();
}

/// <summary>
/// A simple in-process scheduler: on each tick, claim every due task and hand it to a host-supplied <c>fire</c>
/// delegate (which re-attaches the thread, refreshes its context, and runs the task). Survives a restart via
/// <see cref="ScheduleStore"/>; on boot, any task already past due simply fires on the first tick.
/// </summary>
public sealed class Scheduler
{
    private readonly ScheduleStore _store;
    private readonly Func<ScheduledTask, Task<string?>> _fire;
    private readonly TimeSpan _tick;

    /// <summary>The fire delegate returns what the task produced, or null. It is recorded against the task so a
    /// recurring one can be checked on without reading a month of the conversation it fires into.</summary>
    public Scheduler(ScheduleStore store, Func<ScheduledTask, Task<string?>> fire, TimeSpan? tick = null)
    {
        _store = store;
        _fire = fire;
        _tick = tick ?? TimeSpan.FromSeconds(20);
    }

    /// <summary>For a host whose fire has nothing to report back.</summary>
    public Scheduler(ScheduleStore store, Func<ScheduledTask, Task> fire, TimeSpan? tick = null)
        : this(store, async t => { await fire(t).ConfigureAwait(false); return null; }, tick)
    {
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
        try
        {
            var said = await _fire(t).ConfigureAwait(false);
            _store.Complete(t.Id, result: said);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[schedule] fire #{t.Id} failed: {ex.Message}");
            // The reason is kept where the failure is visible. A recurring task comes back regardless.
            _store.Complete(t.Id, failed: true, result: $"Failed: {ex.Message}");
        }
    }
}
