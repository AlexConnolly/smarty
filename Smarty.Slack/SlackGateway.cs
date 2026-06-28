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
    private readonly string _uploadsDir;
    private readonly Regex _mentionRegex;

    private readonly Dictionary<string, SlackThread> _threads = new();
    private readonly object _threadsLock = new();

    // Slack redelivers events on missed acks â€” dedupe by event_id so we never double-process.
    private readonly HashSet<string> _seen = new();
    private readonly Queue<string> _seenOrder = new();
    private readonly object _seenLock = new();

    public SlackGateway(SlackApiClient api, Orchestrator orchestrator, EngagementQualifier qualifier, string botUserId, string dataDir)
    {
        _api = api;
        _orchestrator = orchestrator;
        _qualifier = qualifier;
        _botUserId = botUserId;
        _uploadsDir = Path.Combine(dataDir, "uploads");
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

        /// <summary>Distinct human (non-bot) user ids seen in this thread. When it's just one person + Smarty,
        /// the thread is effectively a 1:1 chat, so we engage with everything they say (banter included);
        /// the classifier is only needed once other humans are present (to avoid barging into cross-talk).</summary>
        public HashSet<string> Humans { get; } = new();

        /// <summary>Messages we've decided to act on, waiting to be turned into a reply. A single drain loop
        /// per thread batches whatever has piled up into ONE turn — so rapid-fire or buffered-then-flushed
        /// messages produce one reply, not several. <see cref="Draining"/> guards that exactly one loop runs.</summary>
        public Queue<(string Line, string UserId, string UserName, IReadOnlyList<Attachment>? Attachments)> Pending { get; } = new();
        public bool Draining { get; set; }
        public object DrainLock { get; } = new();
    }

    // A brief settle before a turn, so messages arriving in a burst (e.g. Slack flushing events buffered while
    // we were restarting, or someone double-tapping) get batched into one reply instead of racing into two.
    private static readonly TimeSpan CoalesceDelay = TimeSpan.FromMilliseconds(500);

    // Set SMARTY_TRACE=1 to log the full intake decision path to stderr (same switch the orchestrator uses).
    private static readonly bool TraceOn = Environment.GetEnvironmentVariable("SMARTY_TRACE") == "1";
    private static void Trace(string msg) { if (TraceOn) Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [gw] {msg}"); }
    private static string Snip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Handle one Events API payload or interactive payload (already acked by the socket layer).</summary>
    public async Task HandlePayloadAsync(JsonElement payload)
    {
        if (payload.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "block_actions")
        {
            HandleInteraction(payload);
            return;
        }

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
        if (type == "message" && ev.GetPropertyOrNull("subtype") is { } st && st != "file_share") { Trace($"ignore: subtype={st}"); return; }

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

        // Files dropped into the message (a doc to tldr, a file to act on). Parsed cheaply now; only actually
        // downloaded if we decide to engage (so we don't pull files from cross-talk that isn't for us).
        var fileRefs = ParseFiles(ev);

        var thread = GetThread(channel, threadTs);
        await thread.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            thread.Humans.Add(user); // this speaker counts toward 1:1-vs-group

            if (isMention)
            {
                await BackfillIfNeededAsync(thread, ts).ConfigureAwait(false);
                Trace($"engage (mention); history now {thread.Session.History.Count} msgs");
                await EngageAsync(thread, user, text, fileRefs).ConfigureAwait(false);
                return;
            }

            // Untagged message in a thread we've never been tagged in â†’ not our conversation, ignore.
            if (!thread.Backfilled) { Trace("ignore: untagged msg in a thread we're not listening to"); return; }

            string author = await _api.GetUserNameAsync(user).ConfigureAwait(false);
            string clean = CleanText(text);

            // 1:1 (just this person + Smarty) → it's all addressed to Smarty, so engage with everything,
            // banter and asides included. Only when other humans are in the thread do we classify, to avoid
            // jumping into people talking to each other. Keep the message in context either way.
            bool respond;
            if (thread.Humans.Count <= 1)
            {
                respond = true;
                Trace("1:1 thread — engaging without classifier");
            }
            else
            {
                var recent = RecentLines(thread.Session);
                respond = await _qualifier.ShouldRespondAsync(recent, author, clean).ConfigureAwait(false);
                Trace($"qualifier respond={respond} (humans={thread.Humans.Count}) for \"{Snip(clean, 60)}\"");
            }

            if (respond)
                await EngageAsync(thread, user, text, fileRefs).ConfigureAwait(false);
            else
                await AppendQuietlyAsync(thread.Session, $"{author}: {clean}").ConfigureAwait(false);
        }
        finally
        {
            thread.Gate.Release();
        }
    }

    /// <summary>Fire a scheduled task into its thread (called by the <see cref="Scheduler"/>). Attaches or
    /// reuses the thread's session and makes sure it has context — a cold session (e.g. after a restart) is
    /// backfilled from the whole Slack thread; a live one already has current history — then runs the frozen
    /// instruction, whose result/file posts back into the thread as a proactive nudge.</summary>
    public async Task FireScheduledAsync(ScheduledTask t)
    {
        var thread = GetThread(t.Channel, t.ThreadTs);
        await thread.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await BackfillIfNeededAsync(thread, "").ConfigureAwait(false);
        }
        finally { thread.Gate.Release(); }
        await _orchestrator.RunScheduledAsync(thread.Session, t.TaskText, t.UserScope, t.UserName).ConfigureAwait(false);
    }

    // Run a turn for a message we've decided is for us. If a task in this thread is waiting on a question,
    // the message is its answer â€” route it back so the worker resumes (mirrors the web app's answer path).
    private async Task EngageAsync(SlackThread thread, string user, string rawText, IReadOnlyList<SlackFileRef> fileRefs)
    {
        string author = await _api.GetUserNameAsync(user).ConfigureAwait(false);
        string clean = CleanText(rawText);

        // Now that we've decided this turn is for us, pull any attached files down to disk.
        var attachments = await DownloadAttachmentsAsync(thread, fileRefs).ConfigureAwait(false);

        if (clean.Length == 0 && attachments is null)
            // A bare "@smarty" with no message. Don't hand the model an empty prompt — that's what made it
            // flail/spiral. Give it a clear directive to engage with the THREAD it was tagged in, so it picks
            // up the actual context instead of replying "huh?".
            clean = "(tagged you with no message — read the thread above and jump in on what's being discussed: " +
                    "answer the open question, offer to help, or do the obvious next thing. Only ask what they " +
                    "want if there's genuinely nothing to go on.)";
        else if (clean.Length == 0 && attachments is not null)
            // A file dropped with no words. Don't sit blank — take a look and offer the obvious next thing.
            clean = "(shared this file with no message — take a look at what it is and offer to help: summarise " +
                    "it, pull out the key points, or ask what they'd like done with it.)";
        string line = $"{author}: {clean}";

        var waiting = thread.Session.Tasks.Values
            .Where(t => t.Status == "waiting")
            .OrderByDescending(t => t.StartedAt)
            .FirstOrDefault();

        if (waiting is not null)
        {
            Trace($"route to waiting task #{waiting.Id} as its answer: \"{Snip(line, 60)}\"");
            // Acknowledge immediately so answering a question doesn't drop into silence while the worker
            // resumes and grinds away — otherwise it feels like the answer went nowhere.
            _ = _api.PostMessageAsync(thread.Channel, thread.ThreadTs, "Got it, thanks — picking that back up now 👍");
            _ = Task.Run(async () =>
            {
                try { await _orchestrator.AnswerTaskAsync(thread.Session, waiting, line, $"user:{user}", author, attachments); }
                catch (Exception ex) { Console.Error.WriteLine($"[slack] answer-task: {ex.Message}"); }
            });
            return;
        }

        // Normal path: queue the line and make sure the thread's single drain loop is running. Batching
        // happens there, so two messages in quick succession become one turn (one reply), not two.
        bool startDrain;
        lock (thread.DrainLock)
        {
            thread.Pending.Enqueue((line, user, author, attachments));
            startDrain = !thread.Draining;
            if (startDrain) thread.Draining = true;
        }
        Trace($"queued for turn: \"{Snip(line, 60)}\"{(startDrain ? " (starting drain)" : " (drain running)")}");
        if (startDrain)
            _ = Task.Run(() => DrainThreadAsync(thread));
    }

    // One drain loop per thread. Each pass waits a beat (to let a burst settle), then takes EVERYTHING
    // pending and runs it as a single turn — so piled-up messages yield one reply. Anything that arrives
    // while a turn is running is picked up by the next pass, never a parallel reply. Exits when the queue
    // is empty, atomically clearing Draining so the next message restarts it.
    private async Task DrainThreadAsync(SlackThread thread)
    {
        while (true)
        {
            await Task.Delay(CoalesceDelay).ConfigureAwait(false);

            List<(string Line, string UserId, string UserName, IReadOnlyList<Attachment>? Attachments)> batch;
            lock (thread.DrainLock)
            {
                batch = new(thread.Pending);
                thread.Pending.Clear();
                if (batch.Count == 0) { thread.Draining = false; return; }
            }

            // Join the batch into one user turn. Near-duplicate re-tags (the buffered + the manual retry)
            // collapse here, so Smarty answers the gist once instead of echoing each. The most recent speaker
            // is whose memory this turn reads/writes (in the common 1:1 case the whole batch is one person).
            string combined = string.Join("\n", batch.Select(b => b.Line));
            var (_, lastUser, lastName, _) = batch[^1];
            // Gather every file across the batch into this one turn (a burst could include several uploads).
            var attachments = batch.Where(b => b.Attachments is not null).SelectMany(b => b.Attachments!).ToList();
            Trace($"turn for {batch.Count} message(s){(attachments.Count > 0 ? $", {attachments.Count} file(s)" : "")}: \"{Snip(combined, 80)}\"");
            try { await _orchestrator.HandleMessageAsync(thread.Session, combined, CancellationToken.None, $"user:{lastUser}", lastName, attachments.Count > 0 ? attachments : null).ConfigureAwait(false); }
            catch (Exception ex) { Console.Error.WriteLine($"[slack] handle-message: {ex.Message}"); }
        }
    }

    // The first time we engage a thread, pull in what was already said so Smarty isn't blind to the
    // conversation it was dropped into. Skips the bot's own posts and the triggering message itself.
    private async Task BackfillIfNeededAsync(SlackThread thread, string triggeringTs)
    {
        if (thread.Backfilled) return;
        thread.Backfilled = true;

        // Tell Smarty which channel it's in, so its replies fit the room (#food vs #engineering). Seeded as
        // the first context line of the thread; stable for the thread's life. Best-effort — null if the
        // channels:read scope isn't granted.
        var channelName = await _api.GetChannelNameAsync(thread.Channel).ConfigureAwait(false);
        if (channelName is not null)
        {
            await SeedSystemAsync(thread.Session, $"This conversation is happening in the #{channelName} channel.").ConfigureAwait(false);
            Trace($"channel resolved: #{channelName}");
        }

        var replies = await _api.GetThreadRepliesAsync(thread.Channel, thread.ThreadTs).ConfigureAwait(false);
        Trace($"backfill: thread {thread.ThreadTs} has {replies.Count} message(s) in it (via conversations.replies)");
        int added = 0;
        foreach (var m in replies)
        {
            if (m.User == _botUserId)
            {
                if (m.Ts == triggeringTs) continue;
                string cleanBot = CleanText(m.Text);
                if (cleanBot.Length == 0) continue;
                await AppendAssistantQuietlyAsync(thread.Session, cleanBot).ConfigureAwait(false);
                Trace($"backfill + assistant: \"{Snip(cleanBot, 60)}\"");
                added++;
                continue;
            }
            if (m.BotId is not null) continue; // skip other bots
            if (m.User is not null) thread.Humans.Add(m.User);         // count everyone who spoke before us
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

    // Add a human line to the session history WITHOUT triggering a turn — used for backfill and for messages
    // the qualifier judged not-for-us, so the thread context stays complete. Guarded by the turn lock so it
    // can't race a running turn's own history writes.
    private static async Task AppendQuietlyAsync(Session session, string line)
    {
        await session.TurnLock.WaitAsync().ConfigureAwait(false);
        try { session.History.Add(Message.User(line)); }
        finally { session.TurnLock.Release(); }
    }

    // Add an assistant line to the session history WITHOUT triggering a turn — used for backfill.
    // Guarded by the turn lock so it can't race a running turn's own history writes.
    private static async Task AppendAssistantQuietlyAsync(Session session, string text)
    {
        await session.TurnLock.WaitAsync().ConfigureAwait(false);
        try { session.History.Add(Message.Assistant(text)); }
        finally { session.TurnLock.Release(); }
    }

    // Seed a System context line into the thread's history (e.g. which channel we're in). The orchestrator
    // includes history every turn, so this rides along like part of the prompt. Guarded by the turn lock.
    private static async Task SeedSystemAsync(Session session, string text)
    {
        await session.TurnLock.WaitAsync().ConfigureAwait(false);
        try { session.History.Add(Message.System(text)); }
        finally { session.TurnLock.Release(); }
    }

    private SlackThread GetThread(string channel, string threadTs)
    {
        string key = $"{channel}:{threadTs}";
        lock (_threadsLock)
        {
            if (_threads.TryGetValue(key, out var existing)) return existing;
            var session = new Session($"slack:{key}");
            bool isDm = channel.StartsWith("D", StringComparison.OrdinalIgnoreCase);
            session.PersonalMemoryEnabled = isDm;
            Trace($"session created for thread {key}; DM={isDm}, personal memory {(isDm ? "enabled" : "disabled")}");
            session.Sink = new SlackThreadSink(_api, channel, threadTs, session); // events -> this thread
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

    /// <summary>A file Slack reported on a message, before we've downloaded it.</summary>
    private sealed record SlackFileRef(string Id, string Name, string? Mime, long Size, string UrlDownload);

    // Pull the file metadata off an event's "files" array (cheap — no network). url_private_download is the
    // authenticated download link; fall back to url_private if it's absent.
    private static IReadOnlyList<SlackFileRef> ParseFiles(JsonElement ev)
    {
        if (!ev.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return Array.Empty<SlackFileRef>();
        var list = new List<SlackFileRef>();
        foreach (var f in files.EnumerateArray())
        {
            string? url = f.GetPropertyOrNull("url_private_download") ?? f.GetPropertyOrNull("url_private");
            if (url is null) continue;
            string id = f.GetPropertyOrNull("id") ?? Guid.NewGuid().ToString("N")[..8];
            string name = f.GetPropertyOrNull("name") ?? f.GetPropertyOrNull("title") ?? $"file-{id}";
            string? mime = f.GetPropertyOrNull("mimetype");
            long size = f.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
            list.Add(new SlackFileRef(id, name, mime, size, url));
        }
        return list;
    }

    // Download a turn's files to the uploads dir (one folder per thread) and return them as Attachments.
    // Best-effort: a file that won't download (e.g. missing files:read scope) is skipped, not fatal. Returns
    // null when there's nothing to hand on.
    private async Task<IReadOnlyList<Attachment>?> DownloadAttachmentsAsync(SlackThread thread, IReadOnlyList<SlackFileRef> fileRefs)
    {
        if (fileRefs.Count == 0) return null;
        string dir = Path.Combine(_uploadsDir, SafeName($"{thread.Channel}-{thread.ThreadTs}"));
        var result = new List<Attachment>();
        foreach (var f in fileRefs)
        {
            string dest = Path.Combine(dir, $"{f.Id}-{SafeName(f.Name)}");
            if (await _api.DownloadFileAsync(f.UrlDownload, dest).ConfigureAwait(false))
            {
                Trace($"downloaded \"{f.Name}\" ({f.Size}B) -> {dest}");
                result.Add(new Attachment(f.Name, dest, f.Mime, f.Size));
            }
            else Trace($"download failed for \"{f.Name}\"");
        }
        return result.Count > 0 ? result : null;
    }

    private static string SafeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

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

    private void HandleInteraction(JsonElement payload)
    {
        try
        {
            if (!payload.TryGetProperty("actions", out var actionsEl) || actionsEl.ValueKind != JsonValueKind.Array)
                return;

            foreach (var actionEl in actionsEl.EnumerateArray())
            {
                var actionId = actionEl.GetPropertyOrNull("action_id") ?? "";
                var blockId = actionEl.GetPropertyOrNull("block_id") ?? "";

                if (actionId is "gate_approve_task" or "gate_approve" or "gate_deny")
                {
                    var parts = blockId.Split(':');
                    if (parts.Length < 3 || parts[0] != "gate_actions") continue;

                    string taskId = parts[1];
                    string gateRequestId = parts[2];

                    // channel is an object { "id": "C...", "name": "..." } in interactive payloads
                    string channel = "";
                    if (payload.TryGetProperty("channel", out var chanEl))
                    {
                        channel = chanEl.ValueKind == JsonValueKind.Object
                            ? (chanEl.GetPropertyOrNull("id") ?? "")
                            : (chanEl.GetPropertyOrNull("id") ?? chanEl.GetString() ?? "");
                    }

                    // container has the thread_ts that roots this thread
                    string threadTs = "";
                    if (payload.TryGetProperty("container", out var containerEl))
                    {
                        threadTs = containerEl.GetPropertyOrNull("thread_ts") ?? "";
                    }
                    // fallback: dig into message.thread_ts
                    if (string.IsNullOrEmpty(threadTs) && payload.TryGetProperty("message", out var msgEl))
                    {
                        threadTs = msgEl.GetPropertyOrNull("thread_ts") ?? msgEl.GetPropertyOrNull("ts") ?? "";
                    }

                    Trace($"gate interaction: action={actionId} task={taskId} gate={gateRequestId} ch={channel} thread={threadTs}");

                    if (string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(threadTs)) continue;

                    string key = $"{channel}:{threadTs}";
                    SlackThread? thread;
                    lock (_threadsLock)
                    {
                        _threads.TryGetValue(key, out thread);
                    }

                    if (thread == null)
                    {
                        Trace($"gate interaction: no thread found for {key}");
                        continue;
                    }

                    var session = thread.Session;
                    bool approved = actionId is "gate_approve_task" or "gate_approve";
                    bool rememberForTask = actionId == "gate_approve_task";

                    if (session.Tasks.TryGetValue(taskId, out var task) && task.PendingGates.TryGetValue(gateRequestId, out var pendingGate))
                    {
                        pendingGate.CompletionSource.TrySetResult(new GateResolution(approved, rememberForTask));
                        Trace($"gate resolved: task={taskId} approved={approved} rememberForTask={rememberForTask}");

                        // Delete the interactive message and post a brief confirmation
                        string actionDetail = $"Using tool *{pendingGate.Action}* ({pendingGate.Description})";
                        string confirmation = approved
                            ? (rememberForTask
                                ? $"✅ Approved: {actionDetail}"
                                : $"✅ Approved once: {actionDetail}")
                            : $"❌ Denied: {actionDetail}";
                        if (pendingGate.SlackMessageTs is { } msgTs)
                        {
                            _ = _api.DeleteMessageAsync(channel, msgTs);
                        }
                        _ = _api.PostMessageAsync(channel, threadTs, confirmation);
                    }
                    else
                    {
                        Trace($"gate interaction: task {taskId} not found or gate mismatch");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[slack] handle-interaction error: {ex.Message}");
        }
    }
}
