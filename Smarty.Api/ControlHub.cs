using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Smarty.Api;

/// <summary>
/// The Smarty.Control command-centre hub. Every conversation across every surface — the local web chat AND
/// Slack threads (which run in a separate process and forward their events here) — mirrors its event stream
/// into the hub. The hub keeps a live registry of conversations, a unified run history, and fans the events
/// out to connected control dashboards over SSE, so you can watch what every Smarty conversation is doing
/// right now. Read-only over the conversations themselves; it never steers them.
/// </summary>
public sealed class ControlHub
{
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, ControlConversation> _conversations = new();
    private readonly List<ControlRun> _runs = new();                 // unified, cross-surface run history (newest last)
    private readonly List<Subscriber> _subscribers = new();
    private readonly string? _path;
    private readonly JsonSerializerOptions _json;
    private long _seq;

    private const int MaxTranscriptPerConversation = 200;
    private const int MaxRuns = 500;

    public ControlHub(string? persistPath, JsonSerializerOptions json)
    {
        _path = persistPath;
        _json = json;
        Load();
    }

    // ---- ingest (the single seam every surface feeds) ----

    /// <summary>Mirror one session event into the hub. Auto-registers the conversation on first sight and
    /// enriches its metadata as events arrive. Safe to call from any thread / process boundary.</summary>
    public void Ingest(string conversationId, string surface, string @event, string data, ConversationMeta? meta = null)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return;
        var conv = _conversations.GetOrAdd(conversationId, id => new ControlConversation
        {
            Id = id,
            Surface = string.IsNullOrWhiteSpace(surface) ? InferSurface(id) : surface,
            StartedAt = DateTimeOffset.UtcNow,
        });

        lock (_lock)
        {
            conv.LastActivityAt = DateTimeOffset.UtcNow;
            if (meta is not null) ApplyMeta(conv, meta);
            Apply(conv, @event, data);
        }

        Broadcast(conv, @event, data);
        Persist();
    }

    /// <summary>Register / update a conversation's metadata without an event (e.g. when a Slack thread is first
    /// seen, or a web session is pinned to a project).</summary>
    public void Register(string conversationId, string surface, ConversationMeta meta)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return;
        var conv = _conversations.GetOrAdd(conversationId, id => new ControlConversation
        {
            Id = id,
            Surface = string.IsNullOrWhiteSpace(surface) ? InferSurface(id) : surface,
            StartedAt = DateTimeOffset.UtcNow,
        });
        lock (_lock)
        {
            ApplyMeta(conv, meta);
            conv.LastActivityAt = DateTimeOffset.UtcNow;
        }
        Persist();
    }

    // ---- event interpretation ----

    private void Apply(ControlConversation conv, string @event, string data)
    {
        switch (@event)
        {
            case "msg_start":
            {
                var (id, _) = ReadMsg(data);
                var role = ReadString(data, "role") ?? "assistant";
                conv.OpenMessages[id] = role;
                if (role == "assistant") conv.Status = "thinking";
                break;
            }
            case "msg_end":
            {
                var (id, text) = ReadMsg(data);
                string role = conv.OpenMessages.TryGetValue(id, out var r) ? r : "assistant";
                conv.OpenMessages.Remove(id);
                AddMessage(conv, role, text);
                if (role == "user")
                {
                    conv.MessageCount++;
                    if (string.IsNullOrWhiteSpace(conv.Title) && !string.IsNullOrWhiteSpace(text))
                        conv.Title = Title(text);
                }
                if (!HasActiveTask(conv)) conv.Status = "idle";
                break;
            }
            case "working":
            {
                string taskId = ReadString(data, "id") ?? "";
                string desc = ReadString(data, "task") ?? "";
                string? persona = ReadString(data, "persona");
                var run = RunFor(conv, taskId);
                if (run is null)
                {
                    run = new ControlRun
                    {
                        Id = $"{conv.Id}#{taskId}",
                        ConversationId = conv.Id,
                        Surface = conv.Surface,
                        TaskId = taskId,
                        Task = desc,
                        Project = conv.Project,
                        Persona = persona ?? conv.Persona,
                        Status = "running",
                        StartedAt = DateTimeOffset.UtcNow,
                    };
                    _runs.Add(run);
                    TrimRuns();
                }
                else
                {
                    run.Status = "running";
                    if (!string.IsNullOrWhiteSpace(desc)) run.Task = desc;
                }
                conv.Status = "working";
                break;
            }
            case "progress":
            {
                string taskId = ReadString(data, "id") ?? "";
                var run = RunFor(conv, taskId);
                if (run is not null) run.LatestNote = ReadString(data, "note");
                break;
            }
            case "tool_started":
            {
                string taskId = ReadString(data, "id") ?? "";
                var run = RunFor(conv, taskId);
                run?.Steps.Add(new RunStep
                {
                    Kind = "tool",
                    Tool = ReadString(data, "name"),
                    Args = ReadString(data, "arguments"),
                });
                conv.Status = "working";
                break;
            }
            case "tool_completed":
            {
                string taskId = ReadString(data, "id") ?? "";
                var run = RunFor(conv, taskId);
                if (run is not null)
                {
                    string name = ReadString(data, "name") ?? "";
                    // Fill the most recent unresolved step for this tool, else append a standalone result.
                    var step = run.Steps.LastOrDefault(s => s.Kind == "tool" && s.Tool == name && s.Result is null)
                               ?? run.Steps.LastOrDefault(s => s.Kind == "tool" && s.Result is null);
                    if (step is null)
                    {
                        step = new RunStep { Kind = "tool", Tool = name };
                        run.Steps.Add(step);
                    }
                    step.Result = ReadString(data, "result");
                }
                break;
            }
            case "question":
            {
                string taskId = ReadString(data, "id") ?? "";
                var run = RunFor(conv, taskId);
                if (run is not null)
                {
                    run.Status = "waiting";
                    run.PendingQuestion = ReadString(data, "question");
                }
                conv.Status = "waiting";
                break;
            }
            case "working_done":
            {
                string taskId = ReadString(data, "id") ?? "";
                string status = ReadString(data, "status") ?? "done";
                var run = RunFor(conv, taskId);
                if (run is not null)
                {
                    run.Status = status;
                    if (status != "waiting")
                    {
                        run.EndedAt = DateTimeOffset.UtcNow;
                        run.PendingQuestion = null;
                    }
                }
                if (!HasActiveTask(conv)) conv.Status = "idle";
                else if (_runs.Any(r => r.ConversationId == conv.Id && r.Status == "waiting")) conv.Status = "waiting";
                break;
            }
            case "file":
            {
                string name = ReadString(data, "name") ?? "file";
                if (!conv.Files.Contains(name)) conv.Files.Add(name);
                break;
            }
        }
    }

    private static void AddMessage(ControlConversation conv, string role, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        conv.Transcript.Add(new ConversationMessage
        {
            Role = role,
            Text = text!,
            At = DateTimeOffset.UtcNow,
        });
        if (conv.Transcript.Count > MaxTranscriptPerConversation)
            conv.Transcript.RemoveRange(0, conv.Transcript.Count - MaxTranscriptPerConversation);
    }

    private ControlRun? RunFor(ControlConversation conv, string taskId) =>
        _runs.LastOrDefault(r => r.ConversationId == conv.Id && r.TaskId == taskId);

    private bool HasActiveTask(ControlConversation conv) =>
        _runs.Any(r => r.ConversationId == conv.Id && r.Status is "running" or "waiting");

    private static void ApplyMeta(ControlConversation conv, ConversationMeta meta)
    {
        if (!string.IsNullOrWhiteSpace(meta.Title)) conv.Title = meta.Title;
        if (!string.IsNullOrWhiteSpace(meta.Subtitle)) conv.Subtitle = meta.Subtitle;
        if (!string.IsNullOrWhiteSpace(meta.Project)) conv.Project = meta.Project;
        if (!string.IsNullOrWhiteSpace(meta.Persona)) conv.Persona = meta.Persona;
        if (!string.IsNullOrWhiteSpace(meta.UserName)) conv.UserName = meta.UserName;
    }

    // ---- queries ----

    public IReadOnlyList<ControlConversation> Conversations() =>
        _conversations.Values.OrderByDescending(c => c.LastActivityAt).ToList();

    public ControlConversation? Conversation(string id) =>
        _conversations.TryGetValue(id, out var c) ? c : null;

    /// <summary>All runs, newest first. Includes live (running/waiting) and finished runs across every surface.</summary>
    public IReadOnlyList<ControlRun> Runs()
    {
        lock (_lock) return _runs.AsEnumerable().Reverse().ToList();
    }

    public IReadOnlyList<ControlRun> RunsFor(string conversationId)
    {
        lock (_lock) return _runs.Where(r => r.ConversationId == conversationId).Reverse().ToList();
    }

    // ---- live SSE fan-out ----

    public sealed class Subscriber
    {
        public required Channel<string> Channel { get; init; }
    }

    /// <summary>Subscribe to the live activity stream. Returns a subscriber whose channel yields ready-to-send
    /// SSE frame bodies (the JSON for the "data:" line). Caller must call <see cref="Unsubscribe"/> when done.</summary>
    public Subscriber Subscribe()
    {
        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        var sub = new Subscriber { Channel = ch };
        lock (_lock) _subscribers.Add(sub);
        return sub;
    }

    public void Unsubscribe(Subscriber sub)
    {
        lock (_lock) _subscribers.Remove(sub);
        sub.Channel.Writer.TryComplete();
    }

    private void Broadcast(ControlConversation conv, string @event, string data)
    {
        long seq = Interlocked.Increment(ref _seq);
        string frame = JsonSerializer.Serialize(new
        {
            seq,
            conversationId = conv.Id,
            surface = conv.Surface,
            title = conv.Title,
            status = conv.Status,
            project = conv.Project,
            @event,
            data = AsElement(data),
            ts = DateTimeOffset.UtcNow,
        }, _json);

        List<Subscriber> targets;
        lock (_lock) targets = _subscribers.ToList();
        foreach (var s in targets) s.Channel.Writer.TryWrite(frame);
    }

    // ---- persistence (recent history survives a restart; live event stream is in-memory only) ----

    private void Persist()
    {
        if (_path is null) return;
        try
        {
            Snapshot snap;
            lock (_lock)
            {
                snap = new Snapshot
                {
                    Conversations = _conversations.Values
                        .OrderByDescending(c => c.LastActivityAt).Take(200).ToList(),
                    Runs = _runs.ToList(),
                };
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(snap, _json));
        }
        catch { /* best-effort */ }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_path), _json);
            if (snap is null) return;
            foreach (var c in snap.Conversations)
            {
                // A conversation can't still be mid-flight after a restart — its live tasks are gone.
                if (c.Status is "thinking" or "working" or "waiting") c.Status = "idle";
                c.OpenMessages.Clear();
                _conversations[c.Id] = c;
            }
            foreach (var r in snap.Runs)
            {
                if (r.Status is "running" or "waiting") { r.Status = "interrupted"; r.EndedAt ??= r.StartedAt; }
                _runs.Add(r);
            }
        }
        catch { /* a corrupt control log shouldn't crash startup */ }
    }

    private void TrimRuns()
    {
        if (_runs.Count > MaxRuns) _runs.RemoveRange(0, _runs.Count - MaxRuns);
    }

    private sealed class Snapshot
    {
        public List<ControlConversation> Conversations { get; set; } = new();
        public List<ControlRun> Runs { get; set; } = new();
    }

    // ---- helpers ----

    private static string InferSurface(string id) =>
        id.StartsWith("slack:", StringComparison.OrdinalIgnoreCase) ? "slack" : "chat";

    private static string Title(string text)
    {
        text = text.Trim().ReplaceLineEndings(" ");
        return text.Length <= 60 ? text : text[..59].TrimEnd() + "…";
    }

    private static (string id, string? text) ReadMsg(string data)
        => (ReadString(data, "id") ?? "", ReadString(data, "text"));

    private static string? ReadString(string json, string prop)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(prop, out var el)) return null;
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.GetRawText(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => el.GetRawText(),
            };
        }
        catch { return null; }
    }

    // Parse a stored JSON string back into an element so it nests cleanly in a broadcast frame
    // (rather than being double-encoded as a string). Falls back to the raw string if it isn't valid JSON.
    private static object? AsElement(string data)
    {
        try { return JsonDocument.Parse(data).RootElement.Clone(); }
        catch { return data; }
    }
}

/// <summary>Optional metadata carried alongside an ingested event (or a registration), enriching a
/// conversation's display in the control centre.</summary>
public sealed record ConversationMeta(
    string? Title = null,
    string? Subtitle = null,
    string? Project = null,
    string? Persona = null,
    string? UserName = null);

/// <summary>One message in a conversation's transcript, as reconstructed from its event stream.</summary>
public sealed class ConversationMessage
{
    public string Role { get; set; } = "";   // user | assistant
    public string Text { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

/// <summary>A conversation the control centre is tracking — one web chat session or one Slack thread.</summary>
public sealed class ControlConversation
{
    public string Id { get; set; } = "";
    public string Surface { get; set; } = "chat";    // chat | slack
    public string? Title { get; set; }
    public string? Subtitle { get; set; }            // e.g. Slack channel / DM partner
    public string? Project { get; set; }
    public string? Persona { get; set; }
    public string? UserName { get; set; }
    public string Status { get; set; } = "idle";     // idle | thinking | working | waiting
    public int MessageCount { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public List<ConversationMessage> Transcript { get; set; } = new();
    public List<string> Files { get; set; } = new();

    /// <summary>Message ids whose msg_end hasn't arrived yet, mapped to their role — to tag the transcript.</summary>
    public Dictionary<string, string> OpenMessages { get; set; } = new();
}

/// <summary>A background worker run, as reconstructed from the event stream — the unified, cross-surface
/// "what a task did" record that powers the Tasks view (running and past).</summary>
public sealed class ControlRun
{
    public string Id { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string Surface { get; set; } = "chat";
    public string TaskId { get; set; } = "";
    public string Task { get; set; } = "";
    public string? Project { get; set; }
    public string? Persona { get; set; }
    public string Status { get; set; } = "running";  // running | waiting | done | cancelled | failed | interrupted
    public string? LatestNote { get; set; }
    public string? PendingQuestion { get; set; }
    public string? Result { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public List<RunStep> Steps { get; set; } = new();
}
