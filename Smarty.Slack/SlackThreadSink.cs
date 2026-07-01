using System.Text.Json;
using System.Text.RegularExpressions;
using Smarty.Api;

namespace Smarty.Slack;

/// <summary>
/// Renders one session's event stream into a Slack thread. The orchestrator emits the SAME logical events
/// it sends to the web SSE stream — we just turn them into thread messages instead:
///
/// - assistant <c>msg_end</c>  → post the finished message (one clean message, not token-streamed; Slack
///   chat isn't a streaming surface and short, whole messages read as snappy, not chunky).
/// - <c>question</c>           → post the worker's question and its suggested answers; a human just replies
///   in-thread and the gateway routes that back as the answer.
/// - <c>working</c> / <c>progress</c> / <c>working_done</c> → a single LIVE STATUS message per job. When a job
///   starts we post one "⏳ {job} — …" line and remember its ts; each progress heartbeat EDITS that same message
///   (chat.update) with the latest state rather than posting a new one — so a long task shows one message that
///   ticks along instead of a growing pile of "still on it" lines. On finish the spinner flips to ✅ / ❌ (or ⏸
///   when it paused to ask, 🚫 cancelled). A re-run/resume emits a fresh <c>working</c>, so it gets its OWN new
///   status message rather than reviving the finished one. The actual RESULT arrives separately as an assistant
///   <c>msg_end</c>, i.e. a new message — the status line only ever tracks progress, never the answer.
/// - user echoes / working pills / deltas → ignored (the user already sees their own message; the ack and
///   the later result arrive as their own assistant messages).
/// </summary>
public sealed class SlackThreadSink : IEventSink
{
    private readonly ISlackThreadApi _api;
    private readonly string _channel;
    private readonly string _threadTs;

    // msg id -> role, captured at msg_start so we only post the assistant's messages (never echo the human's).
    // Keyed by the id AS A STRING: message ids arrive as JSON numbers but task ids as JSON strings, and we key
    // both here uniformly (see EventId) so the two shapes never clash.
    private readonly Dictionary<string, string> _roles = new();

    // task id -> its live status message. Present only while a job is in flight; removed the moment it finishes
    // so a later resume (which re-emits `working`) starts a brand-new message instead of editing the old one.
    private readonly Dictionary<string, JobStatus> _jobs = new();
    private readonly object _lock = new();

    // The in-progress marker. Slack has no built-in animated spinner; ⏳ always renders (a workspace can swap
    // this for a custom :spinner: emoji if it has one). It flips to a tick/cross when the job finishes.
    private const string Spinner = "⏳";

    // The pending chain of Slack operations for one job's status message. Each edit is chained onto the previous
    // so they apply in order and always see the ts the initial post returned — the chain's result IS that ts.
    private sealed class JobStatus
    {
        public string Head = "";
        public Task<string?> Chain = Task.FromResult<string?>(null);
    }

    public SlackThreadSink(ISlackThreadApi api, string channel, string threadTs)
    {
        _api = api;
        _channel = channel;
        _threadTs = threadTs;
    }

    // The event's "id" as a string, whatever JSON shape it arrived in. Message ids are emitted as numbers, but
    // TASK ids are strings (Session.NextTaskId is an incrementing int rendered ToString()). Reading a task
    // event's string id with GetInt32() throws ("requires Number, has String"), which silently killed every
    // job status card. Normalise here so both shapes just work.
    private static string? EventId(JsonElement root) =>
        root.TryGetProperty("id", out var e)
            ? e.ValueKind switch
            {
                JsonValueKind.String => e.GetString(),
                JsonValueKind.Number => e.GetRawText(),
                _ => null,
            }
            : null;

    public void OnEvent(string @event, string data)
    {
        switch (@event)
        {
            case "msg_start":
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (EventId(root) is { } id && root.TryGetProperty("role", out var role))
                    lock (_lock) _roles[id] = role.GetString() ?? "";
                break;
            }

            case "msg_end":
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (EventId(root) is not { } id) break;
                string role;
                lock (_lock) { _roles.TryGetValue(id, out role!); _roles.Remove(id); }
                if (role != "assistant") break; // never repost the user's own turn

                string text = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                text = StripThinking(text);
                // "[ack]" is the model's "I acknowledge but have nothing to say" — post nothing (a turn that
                // realises mid-flight the message wasn't for it can bail this way instead of forcing a reply).
                if (text.Trim().Trim('`').Equals("[ack]", StringComparison.OrdinalIgnoreCase)) break;
                if (!string.IsNullOrWhiteSpace(text)) Post(ToSlackMrkdwn(text));
                break;
            }

            case "question":
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                string q = root.TryGetProperty("question", out var qe) ? qe.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(q)) break;

                var options = new List<string>();
                if (root.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                    options = opts.EnumerateArray().Select(o => o.GetString())
                        .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();

                // With options, render them as real clickable buttons (Block Kit); the gateway routes a click
                // back as the answer to the waiting task. Slack caps button text ~75 chars and an actions block
                // at 5 elements — keep within that, falling back to a plain message if there's nothing to click.
                if (options.Count > 0)
                {
                    var elements = options.Take(5).Select((o, i) => new
                    {
                        type = "button",
                        text = new { type = "plain_text", text = o.Length > 72 ? o[..72] : o },
                        value = o.Length > 140 ? o[..140] : o,
                        action_id = $"smarty_opt_{i}",
                    }).ToArray();
                    var blocks = new object[]
                    {
                        new { type = "section", text = new { type = "mrkdwn", text = ToSlackMrkdwn(q) } },
                        new { type = "actions", elements },
                    };
                    string blocksJson = JsonSerializer.Serialize(blocks);
                    string fallback = q + "\n\n" + string.Join("  ·  ", options.Select(o => $"_{o}_"));
                    _ = _api.PostMessageBlocksAsync(_channel, _threadTs, fallback, blocksJson);
                }
                else
                {
                    Post(ToSlackMrkdwn(q));
                }
                break;
            }

            case "working":
            {
                // A job has started (a fresh delegation) or resumed (an answer/refine re-drives it). Post ONE
                // new status message and remember its ts; a resume deliberately gets its own new message.
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (EventId(root) is not { } id) break;
                string task = root.TryGetProperty("task", out var te) ? (te.GetString() ?? "") : "";
                string head = Head(task);
                string line = ToSlackMrkdwn($"{Spinner} *{head}* — starting…");
                lock (_lock)
                    _jobs[id] = new JobStatus { Head = head, Chain = _api.PostMessageAsync(_channel, _threadTs, line) };
                break;
            }

            case "progress":
            {
                // A periodic "still on it" heartbeat for a long-running task (backs off exponentially in the
                // orchestrator). Edit the job's existing status message in place with the latest step/thought —
                // no new message — so a long task never spams the thread.
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (EventId(root) is not { } id) break;
                string note = root.TryGetProperty("note", out var n) ? (n.GetString() ?? "") : "";
                string task = root.TryGetProperty("task", out var te) ? (te.GetString() ?? "") : "";
                UpdateJob(id, task, note);
                break;
            }

            case "working_done":
            {
                // The job finished — flip the spinner to its outcome and stop tracking it.
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (EventId(root) is not { } id) break;
                string status = root.TryGetProperty("status", out var se) ? (se.GetString() ?? "") : "";
                FinishJob(id, status);
                break;
            }

            case "file":
            {
                // A worker is sending a file back into the thread. The path is a thread-scoped file the
                // orchestrator vouched for. Upload it, but DON'T swallow a failure: a silent upload error left
                // the user told "here's your file" with nothing attached. Report it in the thread instead.
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                string? path = root.TryGetProperty("path", out var pe) ? pe.GetString() : null;
                if (string.IsNullOrWhiteSpace(path)) break;
                string? name = root.TryGetProperty("name", out var ne) ? ne.GetString() : null;
                string? caption = root.TryGetProperty("caption", out var ce) ? ce.GetString() : null;
                _ = UploadAndReportAsync(path!, name, caption);
                break;
            }
        }
    }

    // Edit a job's status message with the latest progress note. If the job isn't tracked (e.g. the sink
    // attached mid-task), fall back to posting a fresh status message so the progress isn't lost.
    private void UpdateJob(string id, string task, string note)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out var job))
            {
                string head0 = Head(task);
                _jobs[id] = new JobStatus
                {
                    Head = head0,
                    Chain = _api.PostMessageAsync(_channel, _threadTs, ToSlackMrkdwn(ProgressLine(head0, note))),
                };
                return;
            }
            if (string.IsNullOrEmpty(job.Head)) job.Head = Head(task);
            job.Chain = ChainUpdate(job.Chain, ToSlackMrkdwn(ProgressLine(job.Head, note)));
        }
    }

    // Finalise a job's status message (⏳ → ✅ / ❌ / ⏸ / 🚫) and stop tracking it, so a later resume posts anew.
    private void FinishJob(string id, string status)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out var job)) return;
            _jobs.Remove(id);
            job.Chain = ChainUpdate(job.Chain, ToSlackMrkdwn(FinalLine(status, job.Head)));
        }
    }

    // Chain an edit onto a job's status message. Awaiting the previous step means we always have the ts the
    // initial post returned (and updates apply in order) without blocking OnEvent, which the orchestrator calls
    // synchronously inside its turn. Returns the ts so the next edit in the chain can reuse it.
    private async Task<string?> ChainUpdate(Task<string?> prev, string text)
    {
        string? ts;
        try { ts = await prev.ConfigureAwait(false); } catch { return null; }
        if (string.IsNullOrEmpty(ts)) return null; // the initial post failed — nothing to edit
        try { await _api.UpdateMessageAsync(_channel, ts, text).ConfigureAwait(false); } catch { /* best-effort */ }
        return ts;
    }

    // The live status line while a job runs: the spinner, the job name, and (once there's one) its latest step.
    internal static string ProgressLine(string head, string note) =>
        string.IsNullOrWhiteSpace(note) ? $"{Spinner} *{head}*" : $"{Spinner} *{head}* — {note}";

    // The finished status line: the spinner becomes a tick / cross, ⏸ when it paused to ask you something,
    // 🚫 when cancelled. An unknown status is treated as done rather than left spinning forever.
    internal static string FinalLine(string status, string head) => status switch
    {
        "failed" => $"❌ *{head}* — couldn't finish",
        "cancelled" => $"🚫 *{head}* — cancelled",
        "waiting" => $"⏸️ *{head}* — waiting on your reply",
        _ => $"✅ *{head}* — done",
    };

    // A short, single-line job name for the status message (a task description can run to a paragraph).
    internal static string Head(string task)
    {
        task = (task ?? "").Trim().Replace('\n', ' ').Replace('\r', ' ');
        return task.Length > 60 ? task[..60].TrimEnd() + "…" : task;
    }

    // qwen3 is a thinking model; normally Ollama routes its chain-of-thought to a separate field, but for
    // some turns it leaks the raw <think>…</think> into the CONTENT channel instead. Strip it so Slack only
    // ever sees the clean answer — complete blocks, an unclosed trailing block, and any orphan tags.
    private static readonly Regex ThinkBlock = new(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ThinkUnclosed = new(@"<think>.*$", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ThinkTag = new(@"</?think>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string StripThinking(string text)
    {
        text = ThinkBlock.Replace(text, "");
        text = ThinkUnclosed.Replace(text, "");
        text = ThinkTag.Replace(text, "");
        return text.Trim();
    }

    private static readonly Regex MarkdownLinkRegex = new(@"\[([^\]]+)\]\(([a-zA-Z]+://[^\s)]+)\)", RegexOptions.Compiled);
    private static readonly Regex MarkdownBoldRegex = new(@"\*\*([^*]+)\*\*", RegexOptions.Compiled);
    private static readonly Regex MarkdownHeaderRegex = new(@"^#+\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    internal static string ToSlackMrkdwn(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        // 1. Convert bold: **text** -> *text*
        text = MarkdownBoldRegex.Replace(text, "*$1*");
        // 2. Convert links: [text](url) -> <url|text>
        text = MarkdownLinkRegex.Replace(text, "<$2|$1>");
        // 3. Convert headers: ## Header -> *Header*
        text = MarkdownHeaderRegex.Replace(text, "*$1*");
        return text;
    }

    // Fire-and-forget the post (Append is called synchronously inside the orchestrator turn; we mustn't block it).
    private void Post(string text) =>
        _ = _api.PostMessageAsync(_channel, _threadTs, text);

    // Upload a file and surface a failure rather than dropping it silently. UploadFileAsync already logs the
    // specific Slack error to stderr (missing_scope, not_in_channel, …); here we also tell the user in-thread so
    // they're never told a file arrived when it didn't.
    private async Task UploadAndReportAsync(string path, string? name, string? caption)
    {
        string display = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(path) : name!;
        bool ok;
        try
        {
            ok = await _api.UploadFileAsync(_channel, _threadTs, path, name,
                string.IsNullOrWhiteSpace(caption) ? null : ToSlackMrkdwn(caption!)).ConfigureAwait(false);
        }
        catch { ok = false; }
        if (!ok)
            Post($"⚠️ I couldn't upload `{display}` to this thread — the file is ready, but Slack rejected the " +
                 "upload. That's usually a permissions issue: I may need the `files:write` scope or to be added to " +
                 "this channel.");
    }
}
