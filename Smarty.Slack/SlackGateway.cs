using System.Text.Json;
using System.Text.RegularExpressions;
using Smarty.Agents;
using Smarty.Api;

namespace Smarty.Slack;

/// <summary>
/// The intake brain. Turns raw Slack events into orchestrator turns, deciding â€” per the two-stage gate â€”
/// WHEN Smarty should speak:
///
///   Stage A (deterministic): an @mention always engages and puts the thread into "listening".
///   Stage B (cheap classifier): for later untagged messages in a listening thread, ask the qualifier
///     whether the message is actually for Smarty before replying.
///
/// A Slack thread maps onto one <see cref="Session"/> (the same long-lived session the web app uses), keyed
/// by channel+thread. The first time we engage a thread we backfill what was already said so Smarty has the
/// full picture. There are no projects here â€” a thread is the only "entry point".
/// </summary>
public sealed class SlackGateway
{
    private readonly SlackApiClient _api;
    private readonly Orchestrator _orchestrator;
    private readonly EngagementQualifier _qualifier;
    private readonly string _botUserId;
    private readonly Regex _mentionRegex;

    private readonly Dictionary<string, SlackThread> _threads = new();
    private readonly object _threadsLock = new();

    // Slack redelivers events on missed acks â€” dedupe by event_id so we never double-process.
    private readonly HashSet<string> _seen = new();
    private readonly Queue<string> _seenOrder = new();
    private readonly object _seenLock = new();

    public SlackGateway(SlackApiClient api, Orchestrator orchestrator, EngagementQualifier qualifier, string botUserId)
    {
        _api = api;
        _orchestrator = orchestrator;
        _qualifier = qualifier;
        _botUserId = botUserId;
        _mentionRegex = new Regex(@"<@[A-Z0-9]+>", RegexOptions.Compiled);
    }

    /// <summary>One thread's state: its session plus whether we're engaged and have backfilled history.</summary>
    private sealed class SlackThread
    {
        public required Session Session { get; init; }
        public required string Channel { get; init; }
        public required string ThreadTs { get; init; }
        public bool Backfilled { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1); // serialises this thread's intake decisions
    }

    // Set SMARTY_TRACE=1 to log the full intake decision path to stderr (same switch the orchestrator uses).
    private static readonly bool TraceOn = Environment.GetEnvironmentVariable("SMARTY_TRACE") == "1";
    private static void Trace(string msg) { if (TraceOn) Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [gw] {msg}"); }
    private static string Snip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Handle one Events API payload (already acked by the socket layer).</summary>
    public async Task HandlePayloadAsync(JsonElement payload)
    {
        if (payload.GetPropertyOrNull("event_id") is { } eventId && !FirstTime(eventId))
        { Trace($"dup event_id {eventId} — skipped"); return; }
        if (!payload.TryGetProperty("event", out var ev)) return;

        string? type = ev.GetPropertyOrNull("type");
        if (type is not ("app_mention" or "message")) { Trace($"ignore: type={type}"); return; }

        // Ignore anything from a bot (including ourselves) and message subtypes (edits, joins, our own
        // thread broadcasts) â€” only real human messages drive a turn.
        if (ev.GetPropertyOrNull("bot_id") is not null) { Trace("ignore: from a bot"); return; }
        string? user = ev.GetPropertyOrNull("user");
        if (user is null || user == _botUserId) { Trace($"ignore: self/no-user ({user})"); return; }
        if (type == "message" && ev.GetPropertyOrNull("subtype") is { } st) { Trace($"ignore: subtype={st}"); return; }

        string text = ev.GetPropertyOrNull("text") ?? "";
        string ts = ev.GetPropertyOrNull("ts") ?? "";
        string channel = ev.GetPropertyOrNull("channel") ?? "";
        string? rawThreadTs = ev.GetPropertyOrNull("thread_ts");
        string threadTs = rawThreadTs ?? ts; // top-level mention roots a new thread
        if (channel.Length == 0 || ts.Length == 0) return;

        bool isMention = type == "app_mention" || text.Contains($"<@{_botUserId}>", StringComparison.Ordinal);
        Trace($"event type={type} ch={channel} ts={ts} thread_ts={rawThreadTs ?? "(none→roots new)"} " +
              $"user={user} mention={isMention} text=\"{Snip(text, 80)}\"");

        // A mention also arrives as a separate "message" event â€” let the app_mention handle it so we don't
        // process it twice.
        if (type == "message" && isMention) { Trace("ignore: mention will arrive as app_mention"); return; }

        var thread = GetThread(channel, threadTs);
        await thread.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (isMention)
            {
                await BackfillIfNeededAsync(thread, ts).ConfigureAwait(false);
                Trace($"engage (mention); history now {thread.Session.History.Count} msgs");
                await EngageAsync(thread, user, text).ConfigureAwait(false);
                return;
            }

            // Untagged message in a thread we've never been tagged in â†’ not our conversation, ignore.
            if (!thread.Backfilled) { Trace("ignore: untagged msg in a thread we're not listening to"); return; }

            // Listening thread: classify before speaking. Keep the message in context either way, so when we
            // do reply we have the full thread.
            var recent = RecentLines(thread.Session);
            string author = await _api.GetUserNameAsync(user).ConfigureAwait(false);
            string clean = CleanText(text);

            bool respond = await _qualifier.ShouldRespondAsync(recent, author, clean).ConfigureAwait(false);
            Trace($"qualifier respond={respond} for \"{Snip(clean, 60)}\"");
            if (respond)
                await EngageAsync(thread, user, text).ConfigureAwait(false);
            else
                await AppendQuietlyAsync(thread.Session, $"{author}: {clean}").ConfigureAwait(false);
        }
        finally
        {
            thread.Gate.Release();
        }
    }

    // Run a turn for a message we've decided is for us. If a task in this thread is waiting on a question,
    // the message is its answer â€” route it back so the worker resumes (mirrors the web app's answer path).
    private async Task EngageAsync(SlackThread thread, string user, string rawText)
    {
        string author = await _api.GetUserNameAsync(user).ConfigureAwait(false);
        string clean = CleanText(rawText);
        if (clean.Length == 0) clean = "(no text)";
        string line = $"{author}: {clean}";

        var waiting = thread.Session.Tasks.Values
            .Where(t => t.Status == "waiting")
            .OrderByDescending(t => t.StartedAt)
            .FirstOrDefault();

        if (waiting is not null)
        {
            Trace($"route to waiting task #{waiting.Id} as its answer: \"{Snip(line, 60)}\"");
            _ = Task.Run(async () =>
            {
                try { await _orchestrator.AnswerTaskAsync(thread.Session, waiting, line); }
                catch (Exception ex) { Console.Error.WriteLine($"[slack] answer-task: {ex.Message}"); }
            });
            return;
        }

        Trace($"orchestrator turn for: \"{Snip(line, 60)}\"");
        _ = Task.Run(async () =>
        {
            try { await _orchestrator.HandleMessageAsync(thread.Session, line, CancellationToken.None); }
            catch (Exception ex) { Console.Error.WriteLine($"[slack] handle-message: {ex.Message}"); }
        });
    }

    // The first time we engage a thread, pull in what was already said so Smarty isn't blind to the
    // conversation it was dropped into. Skips the bot's own posts and the triggering message itself.
    private async Task BackfillIfNeededAsync(SlackThread thread, string triggeringTs)
    {
        if (thread.Backfilled) return;
        thread.Backfilled = true;

        var replies = await _api.GetThreadRepliesAsync(thread.Channel, thread.ThreadTs).ConfigureAwait(false);
        Trace($"backfill: thread {thread.ThreadTs} has {replies.Count} message(s) in it (via conversations.replies)");
        int added = 0;
        foreach (var m in replies)
        {
            if (m.BotId is not null || m.User == _botUserId) continue; // skip bots / our own messages
            if (m.Ts == triggeringTs) continue;                        // the live mention is passed separately
            if (m.User is null) continue;
            string clean = CleanText(m.Text);
            if (clean.Length == 0) continue;
            string author = await _api.GetUserNameAsync(m.User).ConfigureAwait(false);
            await AppendQuietlyAsync(thread.Session, $"{author}: {clean}").ConfigureAwait(false);
            Trace($"backfill + \"{author}: {Snip(clean, 60)}\"");
            added++;
        }
        Trace($"backfill: {added} line(s) added to context");
    }

    // Add a human line to the session history WITHOUT triggering a turn â€” used for backfill and for messages
    // the qualifier judged not-for-us, so the thread context stays complete. Guarded by the turn lock so it
    // can't race a running turn's own history writes.
    private static async Task AppendQuietlyAsync(Session session, string line)
    {
        await session.TurnLock.WaitAsync().ConfigureAwait(false);
        try { session.History.Add(Message.User(line)); }
        finally { session.TurnLock.Release(); }
    }

    private SlackThread GetThread(string channel, string threadTs)
    {
        string key = $"{channel}:{threadTs}";
        lock (_threadsLock)
        {
            if (_threads.TryGetValue(key, out var existing)) return existing;
            var session = new Session($"slack:{key}");
            session.Sink = new SlackThreadSink(_api, channel, threadTs); // events â†’ this thread
            var thread = new SlackThread { Session = session, Channel = channel, ThreadTs = threadTs };
            _threads[key] = thread;
            return thread;
        }
    }

    // The last few human/assistant lines, for the qualifier's context.
    private static IReadOnlyList<string> RecentLines(Session session)
    {
        var lines = new List<string>();
        foreach (var m in session.History)
        {
            if (m.Role == Role.User && !string.IsNullOrWhiteSpace(m.Content)) lines.Add(m.Content!);
            else if (m.Role == Role.Assistant && !string.IsNullOrWhiteSpace(m.Content)) lines.Add($"Smarty: {m.Content}");
        }
        return lines;
    }

    // Strip Slack mention tokens (<@U123>) and tidy whitespace, so the model sees plain text.
    private string CleanText(string text) =>
        Regex.Replace(_mentionRegex.Replace(text, ""), @"\s+", " ").Trim();

    private bool FirstTime(string eventId)
    {
        lock (_seenLock)
        {
            if (!_seen.Add(eventId)) return false;
            _seenOrder.Enqueue(eventId);
            if (_seenOrder.Count > 1000) _seen.Remove(_seenOrder.Dequeue()); // bound the memory
            return true;
        }
    }
}
