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
/// - user echoes / working pills / deltas → ignored (the user already sees their own message; the ack and
///   the later result arrive as their own assistant messages).
/// </summary>
public sealed class SlackThreadSink : IEventSink
{
    private readonly SlackApiClient _api;
    private readonly string _channel;
    private readonly string _threadTs;

    // msg id -> role, captured at msg_start so we only post the assistant's messages (never echo the human's).
    private readonly Dictionary<int, string> _roles = new();
    private readonly object _lock = new();

    public SlackThreadSink(SlackApiClient api, string channel, string threadTs)
    {
        _api = api;
        _channel = channel;
        _threadTs = threadTs;
    }

    public void OnEvent(string @event, string data)
    {
        switch (@event)
        {
            case "msg_start":
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var id) && root.TryGetProperty("role", out var role))
                    lock (_lock) _roles[id.GetInt32()] = role.GetString() ?? "";
                break;
            }

            case "msg_end":
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var idEl)) break;
                int id = idEl.GetInt32();
                string role;
                lock (_lock) { _roles.TryGetValue(id, out role!); _roles.Remove(id); }
                if (role != "assistant") break; // never repost the user's own turn

                string text = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                text = StripThinking(text);
                if (!string.IsNullOrWhiteSpace(text)) Post(ToSlackMrkdwn(text));
                break;
            }

            case "question":
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                string q = root.TryGetProperty("question", out var qe) ? qe.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(q)) break;

                var sb = new System.Text.StringBuilder(q);
                if (root.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                {
                    var list = opts.EnumerateArray().Select(o => o.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                    if (list.Count > 0)
                        sb.Append("\n\n").Append(string.Join("  ·  ", list.Select(o => $"_{o}_")));
                }
                Post(ToSlackMrkdwn(sb.ToString()));
                break;
            }

            case "file":
            {
                // A worker is sending a file back into the thread. The path is a thread-scoped file the
                // orchestrator vouched for; we just upload it (fire-and-forget, like Post).
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                string? path = root.TryGetProperty("path", out var pe) ? pe.GetString() : null;
                if (string.IsNullOrWhiteSpace(path)) break;
                string? name = root.TryGetProperty("name", out var ne) ? ne.GetString() : null;
                string? caption = root.TryGetProperty("caption", out var ce) ? ce.GetString() : null;
                _ = _api.UploadFileAsync(_channel, _threadTs, path!, name,
                    string.IsNullOrWhiteSpace(caption) ? null : ToSlackMrkdwn(caption!));
                break;
            }
        }
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
}
