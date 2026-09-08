namespace Smarty.Api;

/// <summary>
/// The loop that makes the assistant able to start something by itself.
///
/// <para>
/// Every feed that is due gets read, and everything new is offered to every watcher. Nothing else in the system runs
/// on this shape: a panel refresh ends in a value on a screen, and this ends in the assistant deciding to do
/// something. That is the whole difference between a dashboard and an assistant.
/// </para>
/// <para>
/// The order of the decision is the design. Free first: the right feed, the right topic, the field tests. Only what
/// survives all of that is read by a model, and only if the watcher asked for judgement at all. A watcher whose
/// filters are precise never costs anything until the thing it is waiting for happens.
/// </para>
/// </summary>
public sealed class FeedTick
{
    private readonly FeedStore _feeds;
    private readonly WatcherStore _watchers;
    private readonly FeedReader _reader;
    private readonly Func<Watcher, Feed, FeedItem, Task> _fire;
    private readonly Func<Watcher, FeedItem, CancellationToken, Task<bool>>? _judge;
    private readonly TimeSpan _tick;
    private readonly Action<string>? _trace;

    /// <param name="fire">Starts the conversation. Given the watcher, the feed, and what arrived.</param>
    /// <param name="judge">
    /// Reads an item and says whether it is what the watcher meant — only called for a watcher that set
    /// <see cref="Watcher.About"/>, and only after the free filters passed.
    /// </param>
    public FeedTick(FeedStore feeds, WatcherStore watchers, FeedReader reader,
        Func<Watcher, Feed, FeedItem, Task> fire,
        Func<Watcher, FeedItem, CancellationToken, Task<bool>>? judge = null,
        TimeSpan? tick = null, Action<string>? trace = null)
    {
        _feeds = feeds;
        _watchers = watchers;
        _reader = reader;
        _fire = fire;
        _judge = judge;
        _tick = tick ?? TimeSpan.FromSeconds(20);
        _trace = trace;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var feed in _feeds.Due(DateTimeOffset.UtcNow))
                _ = PollOneAsync(feed, ct);

            try { await Task.Delay(_tick, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Read one feed now, and act on anything new. Used by the tick and by "look now" on the page.
    /// </summary>
    public async Task<FeedRead> PollOneAsync(Feed feed, CancellationToken ct)
    {
        var read = await _reader.ReadAsync(feed, ct).ConfigureAwait(false);
        var fresh = _feeds.Polled(feed.Id, read.Items, read.Error, DateTimeOffset.UtcNow);

        _trace?.Invoke(read.Error is { Length: > 0 }
            ? $"[feeds] {feed.Name} failed: {read.Error}"
            : $"[feeds] {feed.Name} — {read.Items.Count} item(s), {fresh.Count} new");

        foreach (var item in fresh)
            await OfferAsync(feed, item, ct).ConfigureAwait(false);

        return read;
    }

    /// <summary>Offer one new item to every watcher, cheapest test first.</summary>
    private async Task OfferAsync(Feed feed, FeedItem item, CancellationToken ct)
    {
        foreach (var watcher in _watchers.All())
        {
            if (!Watching.Matches(watcher, feed, item)) continue;

            // The expensive half, and only ever reached by an item that already passed the free half.
            if (watcher.About is { Length: > 0 } && _judge is not null)
            {
                bool wanted;
                try { wanted = await _judge(watcher, item, ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    // A judgement that could not be made is not a match. Firing on a failed call would turn one bad
                    // minute on the network into a conversation about nothing.
                    _trace?.Invoke($"[watch] {watcher.Name} couldn't judge \"{item.Title}\": {ex.Message}");
                    continue;
                }
                if (!wanted) continue;
            }

            if (!_watchers.Claim(watcher.Id, DateTimeOffset.UtcNow))
            {
                _trace?.Invoke($"[watch] {watcher.Name} matched \"{item.Title}\" but has hit its limit for this hour");
                continue;
            }

            _trace?.Invoke($"[watch] {watcher.Name} fired on \"{item.Title}\" ({item.Topic})");
            try { await _fire(watcher, feed, item).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _watchers.Fired(watcher.Id, "", feed.Id, item, $"Couldn't start the conversation: {ex.Message}");
                _trace?.Invoke($"[watch] {watcher.Name} couldn't act on \"{item.Title}\": {ex.Message}");
            }
        }
    }
}
