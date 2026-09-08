using System.Text.Json;
using System.Text.Json.Nodes;

namespace Smarty.Api;

/// <summary>
/// Something that keeps arriving: a feed, and what has arrived from it.
///
/// <para>
/// The half of the system that was missing. Everything until now was PULLED — a panel asks its source every few
/// minutes, a question asks the browser, a task asks and stops. Nothing arrives on its own, so nothing can be
/// reacted to, and "tell me when the price drops" had no shape at all: the only implementation was a job that ran
/// every hour and asked a model to look, which costs a model call per hour per thing anybody cares about.
/// </para>
/// <para>
/// So a feed is deliberately dumb and free: an address, how to read items out of what comes back, and how often. No
/// model, no browser (unless the source genuinely needs one), no judgement. It produces ITEMS with stable keys, and
/// the only interesting thing it does is decide what each one is ABOUT.
/// </para>
/// <para>
/// That is the part worth the design. A feed of one shape — an inbox, a forum, a repository — carries a dozen
/// unrelated conversations, and "watch my inbox" is never what anybody means; they mean watch the thread about the
/// invoice. So every item lands with a TOPIC, and where items come in threads the topic is decided ONCE per thread
/// and inherited by everything after it. A watcher scoped to a topic is then a free string comparison rather than a
/// model reading every message that arrives.
/// </para>
/// <para>
/// It is also what makes two-way possible later: an item keeps the handle it would need to be acted on — the thread
/// id, the url — so replying into a feed is a matter of writing the other direction, not of re-finding the item.
/// </para>
/// </summary>
public sealed class Feed
{
    public string Id { get; set; } = "";

    /// <summary>What it is, in a few words: "BBC tech news", "my Gmail", "eBay watchers".</summary>
    public string Name { get; set; } = "";

    /// <summary>rss | json | browser — see <see cref="FeedSources"/>.</summary>
    public string Source { get; set; } = FeedSources.Rss;

    /// <summary>The address. For a browser feed, the page to open.</summary>
    public string? Url { get; set; }

    /// <summary>
    /// For a browser feed: JavaScript evaluated IN THE PAGE that returns the items.
    /// </summary>
    /// <remarks>
    /// The custom case, and the same trick the panels use: a browser is already a JavaScript engine and is already
    /// signed in as the user, so reading a page that has no feed is a script rather than a scraping library. It
    /// returns the array of items directly, in the shape the map below describes.
    /// </remarks>
    public string? Expression { get; set; }

    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The dotted path to the ARRAY of items in whatever comes back. RSS and Atom are filled in automatically.
    /// </summary>
    public string Items { get; set; } = "";

    /// <summary>
    /// Where each of an item's parts lives inside one element, as dotted paths.
    /// </summary>
    /// <remarks>
    /// The names are fixed (see <see cref="FeedFields"/>) because everything downstream reads them: a watcher tests
    /// them, a fire quotes them, and a future reply uses <c>ref</c>. Anything else the source carries goes along in
    /// the item's data and can still be tested — the map is what gives the common parts one name each.
    /// </remarks>
    public Dictionary<string, string> Map { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How often to look: "every 5 minutes", "hourly", "daily at 07:00" — the same words a panel uses.</summary>
    public string Every { get; set; } = "every 15 minutes";

    /// <summary>field | thread | one — how an item's topic is decided. See <see cref="FeedSplits"/>.</summary>
    public string Split { get; set; } = FeedSplits.Field;

    /// <summary>For <see cref="FeedSplits.One"/>: the single topic everything from here belongs to.</summary>
    public string? Topic { get; set; }

    /// <summary>
    /// Which topic each thread belongs to, decided when the thread was first seen.
    /// </summary>
    /// <remarks>
    /// The whole point of the thread split. An inbox names its threads once — by the subject the conversation
    /// started with — and every later message in it is filed under the same topic without another decision. So a
    /// watcher on "the invoice from the printers" keeps working when the fourth reply arrives with a subject line of
    /// "Re: Re: FW:".
    /// </remarks>
    public Dictionary<string, string> Threads { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every topic this feed has produced, so a watcher can be scoped without guessing at names.</summary>
    public List<string> Topics { get; set; } = new();

    /// <summary>Stopped, without being forgotten.</summary>
    public bool Paused { get; set; }

    public DateTimeOffset? PolledAt { get; set; }
    public DateTimeOffset? NextPoll { get; set; }

    /// <summary>What went wrong last time it looked. Kept, because a feed that quietly stopped is the worst case.</summary>
    public string? Error { get; set; }

    /// <summary>How many items it has ever produced — the honest measure of whether it works.</summary>
    public int Seen { get; set; }

    /// <summary>
    /// How many times it has been read SUCCESSFULLY.
    /// </summary>
    /// <remarks>
    /// The only honest way to answer "is this the first look?", and the first live run of this proved why. The test had
    /// been "does it have any keys yet", which is also true of a feed whose first read found nothing — so the first
    /// real item to arrive at a quiet feed was treated as back catalogue and woke nobody. A count of reads separates
    /// the two, and it must not count failures either: a feed whose first poll errored would otherwise treat its whole
    /// page as news the moment the source came back.
    /// </remarks>
    public int Reads { get; set; }

    /// <summary>
    /// The keys of items already seen, so the same item is never new twice.
    /// </summary>
    /// <remarks>
    /// A window rather than the lot. Feeds re-serve their whole page every time, so dedup is the difference between a
    /// watcher that fires once and one that fires on every poll for ever; and a feed that has run for a year does not
    /// need a year of keys to know that what is on the page right now is old.
    /// </remarks>
    public List<string> Keys { get; set; } = new();

    /// <summary>The most recent items, newest first — for looking at, and for a watcher created after the fact.</summary>
    public List<FeedItem> Recent { get; set; } = new();

    public DateTimeOffset Created { get; set; }

    /// <summary>The conversation it was set up in, so a feed can be traced back to the ask.</summary>
    public string? Session { get; set; }

    /// <summary>How many keys and items are kept. Enough to dedup a busy feed, small enough to read whole.</summary>
    public const int KeysKept = 500;

    public const int ItemsKept = 40;
}

/// <summary>One thing that arrived.</summary>
public sealed class FeedItem
{
    /// <summary>
    /// Stable per item: a guid, a link, a message id. What makes "already seen" answerable.
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>What it is about, as this feed splits things. Never empty — an unsplittable feed has one topic.</summary>
    public string Topic { get; set; } = "";

    /// <summary>The thread it belongs to, where the feed has threads.</summary>
    public string? Thread { get; set; }

    public string Title { get; set; } = "";

    /// <summary>The body, trimmed. Long enough to judge by, short enough to keep.</summary>
    public string Body { get; set; } = "";

    /// <summary>Where to look at it.</summary>
    public string? Url { get; set; }

    /// <summary>
    /// What would be needed to act BACK on it — a thread id, a message id, an item number.
    /// </summary>
    /// <remarks>
    /// Kept from the start although nothing writes yet. Two-way is the direction this is going, and the difference
    /// between a feed that can be replied to and one that cannot is whether the handle was kept at the moment the
    /// item was read, or has to be found again afterwards.
    /// </remarks>
    public string? Ref { get; set; }

    /// <summary>The item's own moment, if it carries one.</summary>
    public DateTimeOffset? At { get; set; }

    /// <summary>When it arrived here.</summary>
    public DateTimeOffset Arrived { get; set; }

    /// <summary>Everything the source said about it, as JSON — what a watcher's field tests read.</summary>
    public string? Data { get; set; }

    /// <summary>The most this keeps of a body. A watcher judges by a paragraph, not by a page.</summary>
    public const int BodyKept = 600;
}

public static class FeedSources
{
    /// <summary>An RSS or Atom document. Converted to JSON and read with the usual paths.</summary>
    public const string Rss = "rss";

    /// <summary>A JSON endpoint.</summary>
    public const string Json = "json";

    /// <summary>A page, read in the user's own Chrome by a script. The custom case.</summary>
    public const string Browser = "browser";

    public static readonly string[] All = { Rss, Json, Browser };

    public static bool Known(string? source) =>
        All.Contains((source ?? "").Trim().ToLowerInvariant());
}

/// <summary>
/// How a feed decides what an item is about.
/// </summary>
public static class FeedSplits
{
    /// <summary>The source says so: a category, a label, a list name.</summary>
    public const string Field = "field";

    /// <summary>
    /// Items come in threads, and a thread is named once by the first thing in it.
    /// </summary>
    /// <remarks>
    /// The email case, and the reason topics exist at all. Naming a thread from every message would rename it every
    /// time somebody replies; naming it once keeps a watcher pointed at the same conversation for its whole life.
    /// </remarks>
    public const string Thread = "thread";

    /// <summary>Everything here is the same subject — a feed about exactly one thing.</summary>
    public const string One = "one";

    public static readonly string[] All = { Field, Thread, One };

    public static bool Known(string? split) =>
        All.Contains((split ?? "").Trim().ToLowerInvariant());
}

/// <summary>The parts of an item that have one name each, whatever the source calls them.</summary>
public static class FeedFields
{
    public const string Key = "key";
    public const string Topic = "topic";
    public const string Thread = "thread";
    public const string Title = "title";
    public const string Body = "body";
    public const string Url = "url";
    public const string Ref = "ref";
    public const string At = "at";

    public static readonly string[] All = { Key, Topic, Thread, Title, Body, Url, Ref, At };
}

/// <summary>The feeds, on disk.</summary>
public sealed class FeedStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json;
    private readonly List<Feed> _feeds = new();
    private readonly object _lock = new();

    public FeedStore(string path, JsonSerializerOptions json)
    {
        _path = path;
        _json = json;
        try
        {
            if (File.Exists(path))
                _feeds = JsonSerializer.Deserialize<List<Feed>>(File.ReadAllText(path), json) ?? new();
        }
        catch { _feeds = new(); }
    }

    public IReadOnlyList<Feed> All()
    {
        lock (_lock) return _feeds.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Feed? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_lock)
            return _feeds.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?? _feeds.FirstOrDefault(f => f.Name.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public Feed Add(Feed feed)
    {
        lock (_lock)
        {
            feed.Id = string.IsNullOrWhiteSpace(feed.Id) ? Guid.NewGuid().ToString("N")[..8] : feed.Id;
            feed.Created = DateTimeOffset.UtcNow;
            // Due immediately: a feed nobody has ever read is a feed nobody knows works, and the first poll is what
            // says whether the address and the map are right.
            feed.NextPoll = DateTimeOffset.UtcNow;
            _feeds.RemoveAll(f => f.Id.Equals(feed.Id, StringComparison.OrdinalIgnoreCase));
            _feeds.Add(feed);
            Save();
            return feed;
        }
    }

    public bool Edit(string id, string? name = null, string? every = null, bool? paused = null)
    {
        lock (_lock)
        {
            var feed = _feeds.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (feed is null) return false;

            if (name is { Length: > 0 }) feed.Name = name;
            if (every is { Length: > 0 })
            {
                feed.Every = every;
                feed.NextPoll = DateTimeOffset.UtcNow;
            }
            if (paused is { } stop)
            {
                feed.Paused = stop;
                // Coming back on, it looks straight away rather than at the far end of a cadence.
                if (!stop) feed.NextPoll = DateTimeOffset.UtcNow;
            }
            Save();
            return true;
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var gone = _feeds.RemoveAll(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (gone) Save();
            return gone;
        }
    }

    /// <summary>Feeds due a look.</summary>
    public IReadOnlyList<Feed> Due(DateTimeOffset nowUtc)
    {
        lock (_lock)
            return _feeds.Where(f => !f.Paused && f.NextPoll is { } next && next <= nowUtc).ToList();
    }

    /// <summary>Look at this one now, whatever its cadence says.</summary>
    public bool Wake(string id)
    {
        lock (_lock)
        {
            var feed = _feeds.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (feed is null) return false;
            feed.NextPoll = DateTimeOffset.UtcNow;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Record what a poll produced, and return only what is NEW.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one place an item becomes real, because it is the only place that can tell new from seen. Topics are settled
    /// here too — a thread that has been named keeps its name, and a thread nobody has seen before takes the name of
    /// the thing that started it.
    /// </para>
    /// <para>
    /// A poll that FAILED must never look like a poll that found nothing: the error is recorded and no item is
    /// forgotten, so the next successful poll does not report a page of old items as news.
    /// </para>
    /// </remarks>
    public IReadOnlyList<FeedItem> Polled(string id, IReadOnlyList<FeedItem> items, string? error,
        DateTimeOffset nowUtc)
    {
        lock (_lock)
        {
            var feed = _feeds.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (feed is null) return Array.Empty<FeedItem>();

            feed.PolledAt = nowUtc;
            feed.Error = error;
            feed.NextPoll = ScheduleStore.TryParseRepeat(feed.Every, DateTimeOffset.Now, out var next)
                ? next
                : nowUtc.AddMinutes(15);

            if (error is { Length: > 0 })
            {
                Save();
                return Array.Empty<FeedItem>();
            }

            var known = new HashSet<string>(feed.Keys, StringComparer.OrdinalIgnoreCase);
            // The first successful read of a feed is its whole back catalogue, and firing a watcher for every item on
            // it would be a hundred conversations about last week. So the first read only ever learns what is there.
            var first = feed.Reads == 0;
            feed.Reads++;
            var fresh = new List<FeedItem>();

            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.Key)) continue;
                if (!known.Add(item.Key)) continue;

                item.Arrived = nowUtc;
                item.Topic = Settle(feed, item);
                if (item.Topic is { Length: > 0 } topic
                    && !feed.Topics.Contains(topic, StringComparer.OrdinalIgnoreCase))
                    feed.Topics.Add(topic);

                feed.Keys.Add(item.Key);
                feed.Recent.Insert(0, item);
                feed.Seen++;
                if (!first) fresh.Add(item);
            }

            if (feed.Keys.Count > Feed.KeysKept) feed.Keys.RemoveRange(0, feed.Keys.Count - Feed.KeysKept);
            if (feed.Recent.Count > Feed.ItemsKept) feed.Recent.RemoveRange(Feed.ItemsKept, feed.Recent.Count - Feed.ItemsKept);

            Save();
            return fresh;
        }
    }

    /// <summary>What an item is about — decided once per thread, and remembered.</summary>
    private static string Settle(Feed feed, FeedItem item)
    {
        var split = (feed.Split ?? "").Trim().ToLowerInvariant();

        if (split == FeedSplits.One)
            return feed.Topic is { Length: > 0 } only ? only : feed.Name;

        if (split == FeedSplits.Thread && item.Thread is { Length: > 0 } thread)
        {
            if (feed.Threads.TryGetValue(thread, out var already) && already is { Length: > 0 }) return already;

            // The thread's name comes from what started it — the topic the source gave, or failing that the subject
            // the conversation opened with, which is exactly how a person refers to it.
            var named = item.Topic is { Length: > 0 } stated ? stated
                : item.Title is { Length: > 0 } title ? title
                : thread;
            feed.Threads[thread] = named;
            return named;
        }

        if (item.Topic is { Length: > 0 } given) return given;

        // A feed with no split and no stated topic still has one, because a watcher has to be able to name what it is
        // watching. The feed itself is the subject.
        return feed.Name;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_feeds, _json));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[feeds] couldn't save: {ex.Message}");
        }
    }
}

/// <summary>What a poll produced.</summary>
public sealed record FeedRead(IReadOnlyList<FeedItem> Items, string? Error)
{
    public static FeedRead Failed(string why) => new(Array.Empty<FeedItem>(), why);
}

/// <summary>
/// Reading a feed: one HTTP GET, or one page and a script.
/// </summary>
/// <remarks>
/// No model anywhere in here, on purpose. Ingest happens on a timer for ever, so anything it costs is paid for ever —
/// the same argument that keeps a panel's refresh down to a GET. The judgement all sits at the two ends: setting the
/// feed up (once) and deciding what to do about an item (only when a watcher actually matches).
/// </remarks>
public sealed class FeedReader
{
    private readonly HttpClient _http;
    private readonly Func<string, string, CancellationToken, Task<string?>>? _browser;
    private readonly Action<string>? _trace;

    private const int MaxBody = 1024 * 1024;

    public FeedReader(HttpClient http, Func<string, string, CancellationToken, Task<string?>>? browser = null,
        Action<string>? trace = null)
    {
        _http = http;
        _browser = browser;
        _trace = trace;
    }

    public async Task<FeedRead> ReadAsync(Feed feed, CancellationToken ct)
    {
        try
        {
            var raw = (feed.Source ?? "").Trim().ToLowerInvariant() == FeedSources.Browser
                ? await FromBrowserAsync(feed, ct).ConfigureAwait(false)
                : await FromHttpAsync(feed, ct).ConfigureAwait(false);

            if (raw.Error is { Length: > 0 }) return FeedRead.Failed(raw.Error);
            return Split(feed, raw.Body!, _trace);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return FeedRead.Failed("The source timed out.");
        }
        catch (Exception ex)
        {
            return FeedRead.Failed(Head(ex.Message, 200));
        }
    }

    private async Task<(string? Body, string? Error)> FromHttpAsync(Feed feed, CancellationToken ct)
    {
        if (feed.Url is not { Length: > 0 } url) return (null, "This feed has no address.");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Same bargain as a panel's loader: this is the user's own reading, on a timer, not a crawl. See
        // BrowserLike for what the honest custom agent it replaced actually cost.
        BrowserLike.Wear(request);
        foreach (var (key, value) in feed.Headers)
            request.Headers.TryAddWithoutValidation(key, value);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return (null, $"The source answered {(int)response.StatusCode}." +
                          (response.StatusCode is System.Net.HttpStatusCode.Forbidden
                              or System.Net.HttpStatusCode.Unauthorized
                              ? " A source that refuses a server request needs a browser feed."
                              : ""));

        return (body.Length > MaxBody ? body[..MaxBody] : body, null);
    }

    private async Task<(string? Body, string? Error)> FromBrowserAsync(Feed feed, CancellationToken ct)
    {
        if (_browser is null) return (null, "There's no browser available on this instance.");
        if (feed.Url is not { Length: > 0 } url) return (null, "This feed has no page to open.");
        if (feed.Expression is not { Length: > 0 } script)
            return (null, "A browser feed needs a script to read the page with.");

        var raw = await _browser(url, script, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(raw) ? (null, "The page returned nothing.") : (raw, null);
    }

    /// <summary>
    /// Turn a response into items.
    /// </summary>
    /// <remarks>
    /// Public and static so a feed can be PROVED before it is saved: whoever sets one up runs the real address through
    /// this and looks at what comes out, which is the same bargain the panels make — a source you have not read is a
    /// guess, and a guess on a timer is a feed that silently produces nothing for a week.
    /// </remarks>
    public static FeedRead Split(Feed feed, string raw, Action<string>? trace = null)
    {
        if (FeedXml.Looks(raw) && FeedXml.ToJson(raw) is { } converted) raw = converted;

        JsonDocument document;
        try { document = JsonDocument.Parse(raw); }
        catch (JsonException bad) { return FeedRead.Failed($"What came back isn't JSON or a feed: {Head(bad.Message, 120)}"); }

        using (document)
        {
            var root = document.RootElement;

            // An RSS document reads rss.channel.item and an Atom one feed.entry. Filled in rather than asked for,
            // because there are exactly two answers and getting it wrong produces an empty feed with no error.
            var path = feed.Items is { Length: > 0 } stated ? stated : Guess(root);
            var array = path is { Length: > 0 } ? WidgetShape.Walk(root, path) : JsonNode.Parse(root.GetRawText());

            // A source with ONE item serves an object where it usually serves an array, and every feed that does this
            // looks broken exactly once a day — when there is a single entry.
            var elements = array switch
            {
                JsonArray many => many.ToList(),
                JsonObject one => new List<JsonNode?> { one },
                _ => new List<JsonNode?>(),
            };

            // An empty list is an ANSWER, not a fault: an inbox with nothing new in it, a schedule with nothing on it.
            // The same mistake cost a panel a day of looking broken — reporting "nothing at that path" for a source
            // that answered correctly and had nothing to say. A missing path is the fault; an empty array is Tuesday.
            if (array is JsonArray { Count: 0 }) return new FeedRead(Array.Empty<FeedItem>(), null);

            if (elements.Count == 0)
                return FeedRead.Failed(path is { Length: > 0 }
                    ? $"Nothing at \"{path}\" in what came back."
                    : "Couldn't find a list of items in what came back.");

            var items = new List<FeedItem>();
            foreach (var element in elements)
            {
                if (element is null) continue;
                using var doc = JsonDocument.Parse(element.ToJsonString());
                var item = Read(feed, doc.RootElement);
                if (item is not null) items.Add(item);
            }

            trace?.Invoke($"[feeds] {feed.Name}: {items.Count} item(s) from {path ?? "the response"}");
            return new FeedRead(items,
                items.Count == 0 && elements.Count > 0
                    ? "Items were found but none of them had a key — without one, the same item is new on every " +
                      "single poll. Map the key to something stable and unique."
                    : null);
        }
    }

    /// <summary>The two paths worth guessing, both of them feeds.</summary>
    private static string? Guess(JsonElement root) =>
        WidgetShape.Walk(root, "rss.channel.item") is not null ? "rss.channel.item"
        : WidgetShape.Walk(root, "feed.entry") is not null ? "feed.entry"
        : null;

    private static FeedItem? Read(Feed feed, JsonElement element)
    {
        string? Field(string name) =>
            feed.Map.TryGetValue(name, out var path) && path is { Length: > 0 }
                ? Text(WidgetShape.Walk(element, path))
                : null;

        // The key is what makes an item an item. Mapped if the feed says where it is, and otherwise the first of the
        // usual suspects — a feed without one would report its whole page as new on every single poll.
        var key = Field(FeedFields.Key)
                  ?? Text(WidgetShape.Walk(element, "guid"))
                  ?? Text(WidgetShape.Walk(element, "id"))
                  ?? Text(WidgetShape.Walk(element, "link.href"))
                  ?? Text(WidgetShape.Walk(element, "link"))
                  ?? Text(WidgetShape.Walk(element, "url"));

        if (string.IsNullOrWhiteSpace(key)) return null;

        var body = Field(FeedFields.Body)
                   ?? Text(WidgetShape.Walk(element, "description"))
                   ?? Text(WidgetShape.Walk(element, "summary"))
                   ?? Text(WidgetShape.Walk(element, "content"))
                   ?? "";

        var title = Field(FeedFields.Title) ?? Text(WidgetShape.Walk(element, "title")) ?? "";

        return new FeedItem
        {
            Key = key!.Trim(),
            Title = Head(title, 200),
            Body = Head(body, FeedItem.BodyKept),
            Topic = Field(FeedFields.Topic) ?? "",
            Thread = Field(FeedFields.Thread),
            Url = Field(FeedFields.Url) ?? Text(WidgetShape.Walk(element, "link.href")) ?? Text(WidgetShape.Walk(element, "link")),
            Ref = Field(FeedFields.Ref),
            At = When(Field(FeedFields.At)
                      ?? Text(WidgetShape.Walk(element, "pubDate"))
                      ?? Text(WidgetShape.Walk(element, "updated"))
                      ?? Text(WidgetShape.Walk(element, "published"))),
            Data = element.GetRawText() is { Length: > 0 } json && json.Length < 8000 ? json : null,
        };
    }

    private static DateTimeOffset? When(string? text) =>
        DateTimeOffset.TryParse(text, out var at) ? at : null;

    /// <summary>A node as the words in it — a number, a string, or the text of a wrapped XML element.</summary>
    private static string? Text(JsonNode? node)
    {
        switch (node)
        {
            case null: return null;
            case JsonValue value:
                return value.ToString() is { Length: > 0 } text ? text : null;
            case JsonObject obj:
                // XML converted to JSON wraps text in a property; an Atom link puts it in an attribute.
                foreach (var name in new[] { "#text", "text", "value", "href" })
                    if (obj.TryGetPropertyValue(name, out var inner) && Text(inner) is { Length: > 0 } found)
                        return found;
                return null;
            case JsonArray list:
                return list.Count > 0 ? Text(list[0]) : null;
            default: return null;
        }
    }

    private static string Head(string? s, int max) =>
        s is null ? "" : s.Length <= max ? s : s[..max] + "…";
}
