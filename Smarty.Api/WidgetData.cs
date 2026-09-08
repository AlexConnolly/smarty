using System.Text.Json;
using System.Text.Json.Nodes;

namespace Smarty.Api;

/// <summary>What a load produced, or why it didn't.</summary>
public sealed record LoadResult(string? Json, string? Error)
{
    public static LoadResult Ok(JsonObject data) => new(data.ToJsonString(), null);
    public static LoadResult Ok(string json) => new(json, null);
    public static LoadResult Failed(string why) => new(null, why);

    /// <summary>Whether the load produced something usable. Named around the factory that already owns "Ok".</summary>
    public bool Worked => Error is null;
}

/// <summary>
/// The data loader: the one place a panel's data is produced, in the shape the component was written against.
///
/// <para>
/// Separating this from the component is what stops a source's shape being a rendering bug. The component renders a
/// declared model and cannot be broken by a feed; a source that moves its fields breaks the loader instead, which is
/// server-side, testable without a browser, and fixable without touching the rendering. It also means the same panel
/// can move from an API to a browser scrape without a character of the component changing.
/// </para>
/// <para>
/// Three modes, in order of what they cost: <b>internal</b> reads what we already hold; <b>http</b> is a server-side
/// GET; <b>browser</b> loads the page in the user's own Chrome — which is both the way past a site that refuses a
/// server (eBay answers 403 to any plain client) and the only way to see data that is only visible when signed in
/// as them. No model in any of them: the url, the expression and the mapping were all settled when the kind was
/// built.
/// </para>
/// </summary>
public sealed class WidgetDataLoader
{
    private readonly HttpClient _http;
    private readonly Func<string, CancellationToken, Task<string?>> _internal;
    private readonly Func<string, string, CancellationToken, Task<string?>>? _browser;
    private readonly Action<string>? _trace;

    /// <summary>A response bigger than this is a loader pointed at the wrong thing.</summary>
    private const int MaxBody = 512 * 1024;

    public WidgetDataLoader(HttpClient http, Func<string, CancellationToken, Task<string?>> internalFeed,
        Func<string, string, CancellationToken, Task<string?>>? browser = null, Action<string>? trace = null)
    {
        _http = http;
        _internal = internalFeed;
        _browser = browser;
        _trace = trace;
    }

    public async Task<LoadResult> LoadAsync(WidgetKind kind, IReadOnlyDictionary<string, string> values,
        CancellationToken ct)
    {
        var loader = kind.Loader.For(values);
        try
        {
            // One feed is the overwhelmingly common case and goes down exactly the path it always did: fetched,
            // shaped and contract-checked in one step, with the errors it has always produced.
            if (kind.More.Count == 0) return await OneAsync(kind, loader, ct).ConfigureAwait(false);

            return await SeveralAsync(kind, values, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return LoadResult.Failed("The source timed out.");
        }
        catch (Exception ex)
        {
            return LoadResult.Failed(Head(ex.Message, 200));
        }
    }

    private Task<LoadResult> OneAsync(WidgetKind kind, WidgetLoader loader, CancellationToken ct) =>
        loader.Mode switch
        {
            LoaderModes.Internal => FromInternalAsync(kind, loader, ct),
            LoaderModes.Browser => FromBrowserAsync(kind, loader, ct),
            _ => FromHttpAsync(kind, loader, ct),
        };

    // ---- several feeds -----------------------------------------------------------------------------

    /// <summary>
    /// Read every feed, let each supply the fields it maps, and judge the result once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order of those three things is the whole design. Shaping per feed and checking the contract per feed
    /// would fail every time, because no single feed carries the whole model — that is why there are several. So
    /// each contributes what it declares, the pieces merge, and the model is judged at the end against what
    /// actually arrived.
    /// </para>
    /// <para>
    /// Which also decides what a broken feed costs. A feed that fails is noted and skipped rather than failing the
    /// load, so a panel survives losing a source that was only carrying optional fields — and still fails
    /// honestly, naming the feed, when something required went missing with it. Two feeds must not mean twice the
    /// fragility, or nobody should ever use a second one.
    /// </para>
    /// </remarks>
    private async Task<LoadResult> SeveralAsync(WidgetKind kind, IReadOnlyDictionary<string, string> values,
        CancellationToken ct)
    {
        var merged = new JsonObject();
        var trouble = new List<string>();

        foreach (var feed in kind.Feeds)
        {
            var loader = feed.For(values);
            if (loader.Map.Count == 0)
            {
                trouble.Add($"{Where(loader)} declares no mapping, so there is no telling which fields are its");
                continue;
            }

            var (raw, error) = await FetchAsync(loader, ct).ConfigureAwait(false);
            if (error is not null || raw is null)
            {
                trouble.Add($"{Where(loader)}: {error ?? "nothing came back"}");
                continue;
            }

            var fetched = raw;
            if (FeedXml.Looks(fetched) && FeedXml.ToJson(fetched) is { } converted) fetched = converted;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(fetched); }
            catch { trouble.Add($"{Where(loader)} didn't return JSON or a readable XML feed"); continue; }

            using (doc)
            {
                // Only the fields this feed claims. Handing it the whole model would report every field the OTHER
                // feeds carry as missing from this one.
                var mine = kind.Model.Where(f => loader.Map.ContainsKey(f.Name)).ToList();
                var (shaped, missing) = WidgetShape.Apply(doc.RootElement, mine, loader.Map);
                if (missing.Count > 0)
                    trouble.Add($"{Where(loader)} didn't carry {string.Join(", ", missing)}");

                foreach (var pair in shaped)
                {
                    // First feed to supply a field owns it. The primary loader is the one the kind was built
                    // around, and a later feed quietly overwriting it would be the hardest kind of thing to see.
                    if (merged.ContainsKey(pair.Key)) continue;
                    // Re-parsed rather than assigned: a JsonNode belongs to one parent, and moving it straight
                    // across would detach it from the object still being read.
                    merged[pair.Key] = pair.Value is null ? null : JsonNode.Parse(pair.Value.ToJsonString());
                }
            }
        }

        if (trouble.Count > 0) _trace?.Invoke($"[widget] {kind.Name}: {string.Join("; ", trouble)}");

        if (merged.Count == 0)
            return LoadResult.Failed(trouble.Count > 0
                ? $"None of this panel's {kind.Feeds.Count} feeds answered. {string.Join(". ", trouble)}."
                : "None of this panel's feeds returned anything.");

        // Judged once, on everything that arrived. A feed that failed only matters here if it was carrying
        // something the component actually needs.
        if (WidgetContract.Broken(merged.Deserialize<JsonElement>(), kind.Model, kind.Code) is { } wrong)
            return LoadResult.Failed(trouble.Count > 0 ? $"{wrong} ({string.Join("; ", trouble)})" : wrong);

        _trace?.Invoke($"[widget] shaped {kind.Name} from {kind.Feeds.Count} feeds: " +
                       $"{string.Join(", ", merged.Select(kv => kv.Key))}");
        return LoadResult.Ok(merged);
    }

    /// <summary>Fetch one feed's raw response, whatever kind of feed it is.</summary>
    private async Task<(string? Raw, string? Error)> FetchAsync(WidgetLoader loader, CancellationToken ct)
    {
        try
        {
            switch (loader.Mode)
            {
                case LoaderModes.Internal:
                    if (loader.Internal is not { Length: > 0 } name) return (null, "no internal feed named");
                    var held = await _internal(name, ct).ConfigureAwait(false);
                    return held is null ? (null, $"the {name} feed couldn't be read") : (held, null);

                case LoaderModes.Browser:
                    if (_browser is null) return (null, "there's no browser available on this instance");
                    if (loader.Url is not { Length: > 0 } page) return (null, "no url to load");
                    if (loader.Expression is not { Length: > 0 } expression)
                        return (null, "a browser feed needs an expression to read the page with");
                    var read = await _browser(page, expression, ct).ConfigureAwait(false);
                    return string.IsNullOrWhiteSpace(read) ? (null, "the page returned nothing") : (read, null);

                default:
                    if (loader.Url is not { Length: > 0 } url) return (null, "no url to load");
                    if (loader.Holes().ToList() is { Count: > 0 } unfilled)
                        return (null, $"the url still has {{{string.Join("}, {", unfilled)}}} in it");

                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        BrowserLike.Wear(request);
                        foreach (var (key, value) in loader.Headers)
                            request.Headers.TryAddWithoutValidation(key, value);

                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(TimeSpan.FromSeconds(20));
                        using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
                        var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode) return (null, $"answered {(int)response.StatusCode}");
                        return (body.Length > MaxBody ? body[..MaxBody] : body, null);
                    }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, "timed out"); }
        catch (Exception ex) { return (null, Head(ex.Message, 120)); }
    }

    /// <summary>A feed, named the way an error should name it.</summary>
    private static string Where(WidgetLoader loader) =>
        loader.Url is { Length: > 0 } url
            ? (Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host + uri.AbsolutePath : url)
            : loader.Internal is { Length: > 0 } named ? $"the {named} feed" : loader.Mode;

    // ---- internal ----------------------------------------------------------------------------------

    private async Task<LoadResult> FromInternalAsync(WidgetKind kind, WidgetLoader loader, CancellationToken ct)
    {
        if (loader.Internal is not { Length: > 0 } name) return LoadResult.Failed("No internal feed named.");
        var raw = await _internal(name, ct).ConfigureAwait(false);
        if (raw is null) return LoadResult.Failed($"The {name} feed couldn't be read.");
        return Shape(kind, loader, raw, _trace);
    }

    // ---- http -------------------------------------------------------------------------------------

    private async Task<LoadResult> FromHttpAsync(WidgetKind kind, WidgetLoader loader, CancellationToken ct)
    {
        if (loader.Url is not { Length: > 0 } url) return LoadResult.Failed("No url to load.");

        // A url still carrying {holes} is a url whose parameters were never filled in, and requesting it gets a 400
        // that reads like the source's fault. Named, so the answer is obvious: the panel's parameter names don't
        // match the ones the kind declares.
        if (loader.Holes().ToList() is { Count: > 0 } unfilled)
            return LoadResult.Failed(
                $"The url still has {{{string.Join("}, {", unfilled)}}} in it — this panel has no value for " +
                $"{(unfilled.Count == 1 ? "that parameter" : "those parameters")}.");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // The user's own browser, minus the tab. A panel is one page they asked to be kept informed about, fetched
        // on a cadence they chose — so it makes the request their browser would. The polite custom agent this
        // replaced was refused outright by anything with a WAF in front of it (ESPN answered 403 to it and 200 to
        // curl), and a panel whose source refuses it looks identical, from the page, to a panel that is broken.
        BrowserLike.Wear(request);
        // After the defaults, so a source that needs its own key, Accept or referer still wins.
        foreach (var (key, value) in loader.Headers)
            request.Headers.TryAddWithoutValidation(key, value);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return LoadResult.Failed(
                $"The source answered {(int)response.StatusCode}." +
                // Said explicitly, because it is the specific failure that has a specific answer: the browser is
                // signed in and is not refused, and moving the loader there is the fix.
                (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized
                    ? " A site that refuses a server request needs a browser loader."
                    : ""));

        if (body.Length > MaxBody) body = body[..MaxBody];
        return Shape(kind, loader, body, _trace);
    }

    // ---- browser ----------------------------------------------------------------------------------

    private async Task<LoadResult> FromBrowserAsync(WidgetKind kind, WidgetLoader loader, CancellationToken ct)
    {
        if (_browser is null) return LoadResult.Failed("There's no browser available on this instance.");
        if (loader.Url is not { Length: > 0 } url) return LoadResult.Failed("No url to load.");
        if (loader.Expression is not { Length: > 0 } expression)
            return LoadResult.Failed("A browser loader needs an expression to read the page with.");

        var raw = await _browser(url, expression, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw)) return LoadResult.Failed("The page returned nothing.");

        // The expression is expected to return the shaped object outright — the browser is a JavaScript engine, so
        // there is no reason to shape twice. A mapping is still honoured for a loader that returns raw and maps.
        return Shape(kind, loader, raw!, _trace);
    }

    // ---- shaping ----------------------------------------------------------------------------------

    /// <summary>
    /// Turn a source response into the kind's declared model.
    ///
    /// <para>
    /// With no mapping the response is taken to be the shape already — which is the normal case for a browser
    /// expression and for an internal feed built to match. With a mapping, only declared fields come through, so
    /// the component's contract holds whatever else the source starts returning.
    /// </para>
    /// </summary>
    public static LoadResult Shape(WidgetKind kind, WidgetLoader loader, string raw, Action<string>? trace = null)
    {
        // A feed is XML, and every path in this system walks JSON — so it is converted here, once, and everything
        // downstream is unchanged. RSS and Atom are how most of the world publishes a list of things that changed,
        // they need no key, and they survive rate limiting that kills the JSON api beside them. Without this a news
        // panel whose json endpoint started answering 403 had no route left, when the feed was serving perfectly.
        if (FeedXml.Looks(raw) && FeedXml.ToJson(raw) is { } converted) raw = converted;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch { return LoadResult.Failed("The source didn't return JSON or a readable XML feed."); }

        using (doc)
        {
            if (loader.Map.Count == 0)
            {
                // Reported rather than passed on: a panel rendering a shape that is missing half its fields is a
                // panel showing dashes, and the reason belongs with the loader that produced it.
                if (WidgetContract.Broken(doc.RootElement, kind.Model, kind.Code) is { } wrong)
                    return LoadResult.Failed(wrong);

                return LoadResult.Ok(raw);
            }

            var (shaped, missing) = WidgetShape.Apply(doc.RootElement, kind.Model, loader.Map);
            if (missing.Count > 0)
                return LoadResult.Failed(
                    $"Couldn't find {string.Join(", ", missing)} in the source. Its shape has probably changed — " +
                    "the mapping needs updating.");

            // The mapping succeeded, which says the paths RESOLVED — not that they resolved to anything. A wildcard
            // over a list of rows returns a value per row either way, so this is the same check again on the other
            // side of the shaping, and the only one that sees inside the list it just built.
            if (WidgetContract.Broken(shaped.Deserialize<JsonElement>(), kind.Model, kind.Code) is { } hollow)
                return LoadResult.Failed(hollow);

            trace?.Invoke($"[widget] shaped {kind.Name}: {string.Join(", ", shaped.Select(kv => kv.Key))}");
            return LoadResult.Ok(shaped);
        }
    }

    private static string Head(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>
/// The loop that keeps panels current.
///
/// <para>
/// Its own loop rather than the task scheduler's, because a scheduled task exists to speak into a conversation and a
/// panel refreshing every ten minutes must not say a word. Same recurrence vocabulary, different destination.
/// </para>
/// </summary>
public sealed class WidgetRefresher
{
    private readonly WidgetStore _store;
    private readonly WidgetLibrary _library;
    private readonly WidgetDataLoader _loader;
    private readonly TimeSpan _tick;
    private readonly Action<string>? _trace;

    public WidgetRefresher(WidgetStore store, WidgetLibrary library, WidgetDataLoader loader,
        TimeSpan? tick = null, Action<string>? trace = null)
    {
        _store = store;
        _library = library;
        _loader = loader;
        _tick = tick ?? TimeSpan.FromSeconds(15);
        _trace = trace;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // A build that stopped without saying so. Checked here because this loop is already awake, and the
            // alternative is a panel showing a progress bar with nothing behind it until someone restarts the app.
            foreach (var stuck in _store.Stalled(DateTimeOffset.UtcNow))
            {
                _store.Failed(stuck.Id, "The build stopped without finishing. Ask again to have another go.");
                _trace?.Invoke($"[widget] {stuck.Id} \"{stuck.Title}\" was still building with nothing behind it");
            }

            foreach (var w in _store.Due(DateTimeOffset.UtcNow))
            {
                // Booked forward before loading, so a slow source isn't picked up again by the next four ticks and
                // run five times over.
                _store.Loaded(w.Id, null, w.Error);
                _ = LoadOneAsync(w, ct);
            }
            try { await Task.Delay(_tick, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Load one panel now. Used by the tick and by the data endpoint when what it has is stale.</summary>
    public async Task<LoadResult> LoadOneAsync(Widget w, CancellationToken ct)
    {
        if (_library.Get(w.Kind) is not { } kind)
        {
            var gone = LoadResult.Failed("This panel's kind has gone from the library.");
            _store.Loaded(w.Id, null, gone.Error);
            return gone;
        }

        var result = await _loader.LoadAsync(kind, w.Params, ct).ConfigureAwait(false);
        // The kind says which numbers are worth keeping, so the panel accumulates a series simply by being refreshed
        // in the background — which is the point of refreshing it in the background.
        _store.Loaded(w.Id, result.Json, result.Error, kind.Track);
        _trace?.Invoke(result.Error is null
            ? $"[widget] {w.Id} \"{w.Title}\" loaded {result.Json?.Length ?? 0}B via {kind.Loader.Mode}"
            : $"[widget] {w.Id} \"{w.Title}\" load failed: {result.Error}");

        // Failing the same way twice is the loader being wrong rather than the network being bad. Said out loud, once,
        // and left there: the panel shows its age and its reason, and mending it is offered in its menu rather than
        // taken out of the user's hands. A rewrite triggered by the second identical failure replaced panels that were
        // working minutes later, because "twice" is not the same as "for good".
        if (result.Error is { Length: > 0 } && _store.LoaderStuck(w.Id))
            _trace?.Invoke($"[widget] {w.Id} \"{w.Title}\" keeps failing to load the same way — its menu offers a fix");

        return result;
    }
}
