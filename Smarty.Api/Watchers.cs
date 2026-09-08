using System.Text.Json;

namespace Smarty.Api;

/// <summary>
/// Something to watch for, and what to do about it.
///
/// <para>
/// A feed brings things in; a watcher is the reason anybody wanted them. The pair is deliberately split, because they
/// fail differently and cost differently: a feed is an address on a timer and must stay free, while a watcher is a
/// decision and is allowed to be expensive — but only when something has actually arrived that concerns it.
/// </para>
/// <para>
/// So a watcher is two halves, exactly as it is said out loud: WHAT TO WATCH FOR, and WHAT TO DO WHEN IT HAPPENS. The
/// first half is a filter over items — a topic, a test on a field — and the second is an instruction in plain words.
/// When it matches, the instruction and the item become the opening of a NEW conversation that the assistant starts
/// by itself, which is the point of the whole thing: the assistant getting on with something without being asked
/// first.
/// </para>
/// <para>
/// Free where it can be. A topic comparison and a field test cost nothing, and that is what topics are for — "the
/// thread about the invoice" is a string match, where "an email about an invoice" would be a model call per message
/// for ever. <see cref="About"/> exists for what genuinely needs reading, and it costs one call per item that already
/// passed the free tests.
/// </para>
/// </summary>
public sealed class Watcher
{
    public string Id { get; set; } = "";

    /// <summary>What it is watching for, in a few words. Shown wherever it is listed.</summary>
    public string Name { get; set; } = "";

    /// <summary>Which feed. Null watches every feed, which is occasionally what somebody means.</summary>
    public string? Feed { get; set; }

    /// <summary>Only items filed under this topic. The cheapest and most useful narrowing there is.</summary>
    public string? Topic { get; set; }

    /// <summary>
    /// Tests on the item, all of which must hold. Free.
    /// </summary>
    public List<WatchTest> When { get; set; } = new();

    /// <summary>
    /// A judgement, for what a test cannot express: "anything that reads like a delivery going wrong".
    /// </summary>
    /// <remarks>
    /// Applied only to items that already passed the topic and the tests, so the bill is bounded by how well the free
    /// half was written. Left unset, a watcher never calls a model at all.
    /// </remarks>
    public string? About { get; set; }

    /// <summary>What to do when it fires, in plain words. This becomes the opening of the conversation.</summary>
    public string Do { get; set; } = "";

    /// <summary>Stopped without being forgotten.</summary>
    public bool Paused { get; set; }

    public int Fired { get; set; }
    public DateTimeOffset? LastFiredAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>The conversation it was set up in.</summary>
    public string? Session { get; set; }

    public DateTimeOffset Created { get; set; }

    /// <summary>
    /// How many times this may fire in an hour.
    /// </summary>
    /// <remarks>
    /// A watcher on a busy feed with a loose filter is a machine for starting fifty conversations, and the first thing
    /// anybody would want is for it to stop. Capped rather than trusted: the cap is per watcher and per hour, and
    /// hitting it is recorded so it reads as "this is watching too much" rather than as a watcher that stopped working.
    /// </remarks>
    public int Limit { get; set; } = 6;

    /// <summary>When it fired, within the last hour — for the cap.</summary>
    public List<DateTimeOffset> Recent { get; set; } = new();
}

/// <summary>One free test on an item.</summary>
/// <remarks>
/// A tiny set of operators on purpose. Anything richer becomes a query language nobody can debug from a list on a
/// page, and the interesting narrowing is nearly always the topic anyway.
/// </remarks>
public sealed class WatchTest
{
    /// <summary>A part of the item — title, body, topic, url — or any field the source carried.</summary>
    public string Field { get; set; } = "";

    /// <summary>has | is | not | above | below</summary>
    public string Op { get; set; } = WatchOps.Has;

    public string Value { get; set; } = "";
}

public static class WatchOps
{
    /// <summary>The field contains these words.</summary>
    public const string Has = "has";

    /// <summary>The field is exactly this.</summary>
    public const string Is = "is";

    /// <summary>The field is anything but this.</summary>
    public const string Not = "not";

    /// <summary>The field is a number greater than this.</summary>
    public const string Above = "above";

    /// <summary>The field is a number less than this.</summary>
    public const string Below = "below";

    public static readonly string[] All = { Has, Is, Not, Above, Below };

    public static bool Known(string? op) => All.Contains((op ?? "").Trim().ToLowerInvariant());
}

/// <summary>
/// Does this item concern this watcher? The free half of the decision.
/// </summary>
public static class Watching
{
    /// <summary>
    /// Everything except <see cref="Watcher.About"/>, which needs a model and is asked separately.
    /// </summary>
    public static bool Matches(Watcher watcher, Feed feed, FeedItem item)
    {
        if (watcher.Paused) return false;

        if (watcher.Feed is { Length: > 0 } wanted
            && !wanted.Equals(feed.Id, StringComparison.OrdinalIgnoreCase)
            && !wanted.Equals(feed.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        if (watcher.Topic is { Length: > 0 } topic
            && !topic.Equals(item.Topic, StringComparison.OrdinalIgnoreCase))
            return false;

        return watcher.When.All(test => Holds(test, item));
    }

    private static bool Holds(WatchTest test, FeedItem item)
    {
        var actual = Part(item, test.Field);
        var op = (test.Op ?? "").Trim().ToLowerInvariant();
        var wanted = (test.Value ?? "").Trim();

        return op switch
        {
            WatchOps.Is => string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase),
            WatchOps.Not => !string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase),
            WatchOps.Above => Numbers(actual, wanted, (a, b) => a > b),
            WatchOps.Below => Numbers(actual, wanted, (a, b) => a < b),
            // The default, because "has" is what people mean nearly every time: the word appears in it.
            _ => actual.Contains(wanted, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static bool Numbers(string actual, string wanted, Func<double, double, bool> compare)
    {
        // Only the digits: a price arrives as "£28.16" and a comparison against 30 has to work anyway.
        var cleaned = new string(actual.Where(c => char.IsDigit(c) || c is '.' or '-').ToArray());
        return double.TryParse(cleaned, out var left) && double.TryParse(wanted, out var right)
               && compare(left, right);
    }

    /// <summary>
    /// A named part of an item: one of the standard ones, or anything the source carried.
    /// </summary>
    /// <remarks>
    /// The fallback into the raw data is what stops the fixed field names being a ceiling. A source with a
    /// "priority" or a "watchers" field can be tested on it without the feed having to have mapped it in advance,
    /// which it never would have, because nobody knows what they will want to watch until they want it.
    /// </remarks>
    public static string Part(FeedItem item, string? field)
    {
        switch ((field ?? "").Trim().ToLowerInvariant())
        {
            case FeedFields.Title: return item.Title ?? "";
            case FeedFields.Body: return item.Body ?? "";
            case FeedFields.Topic: return item.Topic ?? "";
            case FeedFields.Thread: return item.Thread ?? "";
            case FeedFields.Url: return item.Url ?? "";
            case FeedFields.Key: return item.Key ?? "";
            case "": return $"{item.Title} {item.Body}";
        }

        if (item.Data is not { Length: > 0 } data) return "";
        try
        {
            using var doc = JsonDocument.Parse(data);
            return WidgetShape.Walk(doc.RootElement, field!)?.ToString() ?? "";
        }
        catch { return ""; }
    }
}

/// <summary>The watchers, on disk.</summary>
public sealed class WatcherStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json;
    private readonly List<Watcher> _watchers = new();
    private readonly List<WatchFire> _fires = new();
    private readonly object _lock = new();

    public WatcherStore(string path, JsonSerializerOptions json)
    {
        _path = path;
        _json = json;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Kept>(File.ReadAllText(path), json) is { } kept)
            {
                _watchers = kept.Watchers ?? new();
                _fires = kept.Fires ?? new();
            }
        }
        catch { /* a corrupt file starts empty rather than stopping the app */ }
    }

    public IReadOnlyList<Watcher> All()
    {
        lock (_lock) return _watchers.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Watcher? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_lock) return _watchers.FirstOrDefault(w => w.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public Watcher Add(Watcher watcher)
    {
        lock (_lock)
        {
            watcher.Id = string.IsNullOrWhiteSpace(watcher.Id) ? Guid.NewGuid().ToString("N")[..8] : watcher.Id;
            watcher.Created = DateTimeOffset.UtcNow;
            _watchers.RemoveAll(w => w.Id.Equals(watcher.Id, StringComparison.OrdinalIgnoreCase));
            _watchers.Add(watcher);
            Save();
            return watcher;
        }
    }

    public bool Edit(string id, string? name = null, string? doThis = null, bool? paused = null, int? limit = null)
    {
        lock (_lock)
        {
            var watcher = _watchers.FirstOrDefault(w => w.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (watcher is null) return false;

            if (name is { Length: > 0 }) watcher.Name = name;
            if (doThis is { Length: > 0 }) watcher.Do = doThis;
            if (paused is { } stop) watcher.Paused = stop;
            if (limit is { } cap && cap > 0) watcher.Limit = cap;
            Save();
            return true;
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var gone = _watchers.RemoveAll(w => w.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;
            // Its fires go with it. A watcher nobody has any more cannot leave an unread nudge behind pointing at a
            // conversation about something nobody is watching for.
            if (gone) _fires.RemoveAll(f => f.Watcher.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (gone) Save();
            return gone;
        }
    }

    /// <summary>
    /// May this one fire right now?
    /// </summary>
    /// <remarks>
    /// Claimed rather than asked, so two items arriving in the same poll cannot both pass a cap of one. The count is
    /// the last hour only — a cap that never forgets is a watcher that works for a day and then never again.
    /// </remarks>
    public bool Claim(string id, DateTimeOffset nowUtc)
    {
        lock (_lock)
        {
            var watcher = _watchers.FirstOrDefault(w => w.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (watcher is null || watcher.Paused) return false;

            watcher.Recent.RemoveAll(at => at < nowUtc.AddHours(-1));
            if (watcher.Recent.Count >= Math.Max(1, watcher.Limit))
            {
                watcher.LastError = $"It matched again but has already fired {watcher.Recent.Count} times this hour.";
                Save();
                return false;
            }

            watcher.Recent.Add(nowUtc);
            Save();
            return true;
        }
    }

    /// <summary>It fired, and this is the conversation it started.</summary>
    public void Fired(string id, string sessionId, string feedId, FeedItem item, string? error = null)
    {
        lock (_lock)
        {
            var watcher = _watchers.FirstOrDefault(w => w.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (watcher is null) return;

            watcher.Fired++;
            watcher.LastFiredAt = DateTimeOffset.UtcNow;
            watcher.LastError = error;

            _fires.Insert(0, new WatchFire
            {
                Watcher = watcher.Id,
                WatcherName = watcher.Name,
                Feed = feedId,
                Session = sessionId,
                Title = item.Title,
                Topic = item.Topic,
                Url = item.Url,
                At = DateTimeOffset.UtcNow,
            });
            if (_fires.Count > FiresKept) _fires.RemoveRange(FiresKept, _fires.Count - FiresKept);
            Save();
        }
    }

    /// <summary>Everything that has fired, newest first.</summary>
    public IReadOnlyList<WatchFire> Fires()
    {
        lock (_lock) return _fires.ToList();
    }

    /// <summary>
    /// The conversations the assistant started by itself that nobody has looked at yet.
    /// </summary>
    /// <remarks>
    /// The other half of a watcher actually working. A conversation started in the background that nothing points at
    /// is a conversation nobody reads — so the fire is what carries the "unread", and opening the chat is what clears
    /// it.
    /// </remarks>
    public IReadOnlyList<WatchFire> Unopened()
    {
        lock (_lock) return _fires.Where(f => !f.Opened).ToList();
    }

    /// <summary>Somebody looked at the conversation a fire started.</summary>
    public bool Opened(string sessionId)
    {
        lock (_lock)
        {
            var touched = false;
            foreach (var fire in _fires.Where(f => !f.Opened
                && f.Session.Equals(sessionId, StringComparison.OrdinalIgnoreCase)))
            {
                fire.Opened = true;
                touched = true;
            }
            if (touched) Save();
            return touched;
        }
    }

    /// <summary>Which watcher started this conversation, if one did.</summary>
    public WatchFire? Started(string sessionId)
    {
        lock (_lock)
            return _fires.FirstOrDefault(f => f.Session.Equals(sessionId, StringComparison.OrdinalIgnoreCase));
    }

    private const int FiresKept = 100;

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new Kept { Watchers = _watchers, Fires = _fires }, _json));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[watch] couldn't save: {ex.Message}");
        }
    }

    private sealed class Kept
    {
        public List<Watcher>? Watchers { get; set; }
        public List<WatchFire>? Fires { get; set; }
    }
}

/// <summary>One time a watcher went off.</summary>
public sealed class WatchFire
{
    public string Watcher { get; set; } = "";
    public string WatcherName { get; set; } = "";
    public string Feed { get; set; } = "";

    /// <summary>The conversation it started.</summary>
    public string Session { get; set; } = "";

    public string Title { get; set; } = "";
    public string Topic { get; set; } = "";
    public string? Url { get; set; }
    public DateTimeOffset At { get; set; }

    /// <summary>Has anybody looked at the conversation yet?</summary>
    public bool Opened { get; set; }
}
