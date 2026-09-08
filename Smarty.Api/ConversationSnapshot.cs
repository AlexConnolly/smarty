using System.Text.Json;

namespace Smarty.Api;

/// <summary>
/// A conversation as a THING, rather than as the events that produced it.
/// <para>
/// Reopening a chat used to mean pushing its whole event history back down the live stream — four hundred events
/// through the same pipeline that carries what is happening right now. It worked, and it kept one code path, but
/// the client then had to rebuild the conversation from event soup on every single load: text arrived through the
/// typewriter drip, task rows re-announced themselves, and the final state of each job had to be re-derived from
/// a sequence rather than simply read. Every "it says the job is still running" bug lived in that derivation.
/// </para>
/// <para>
/// So the fold happens here, once, on the server: messages with their files and link cards already attached, and
/// each task with the outcome it actually reached. <see cref="Next"/> is how many events the snapshot accounts
/// for, so the live stream resumes from exactly there — no gap, nothing delivered twice.
/// </para>
/// </summary>
public sealed record ConversationSnapshot(
    int Next,
    IReadOnlyList<SnapshotMessage> Messages,
    IReadOnlyList<SnapshotTask> Tasks,
    IReadOnlyList<SnapshotQuestion> Questions)
{
    /// <summary>
    /// Fold a session's events into the conversation they describe.
    /// <para>
    /// Only what a reader needs survives: the live-only chatter — reasoning, per-tool progress, spend, heartbeats —
    /// is the bulk of the volume and is noise once the turn is over.
    /// </para>
    /// </summary>
    public static ConversationSnapshot Of(IReadOnlyList<(string Event, string Data)> events)
    {
        var messages = new List<SnapshotMessage>();
        var byId = new Dictionary<int, SnapshotMessage>();
        var tasks = new List<SnapshotTask>();
        var tasksById = new Dictionary<string, SnapshotTask>(StringComparer.Ordinal);
        var questions = new Dictionary<string, SnapshotQuestion>(StringComparer.Ordinal);

        foreach (var (@event, data) in events)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); } catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                switch (@event)
                {
                    case "msg_start":
                    {
                        if (MessageId(root) is not { } id) break;
                        if (byId.ContainsKey(id)) break; // a re-open of the same id keeps the first
                        var role = Str(root, "role") == "user" ? "user" : "assistant";
                        var message = new SnapshotMessage { Id = id, Role = role };
                        byId[id] = message;
                        messages.Add(message);
                        break;
                    }

                    case "content":
                    {
                        if (MessageId(root) is { } id && byId.TryGetValue(id, out var m))
                            m.Text += Str(root, "text") ?? "";
                        break;
                    }

                    // The model rewrote its answer mid-turn: what was streamed was withdrawn from the user's view,
                    // so it must not survive into the history either.
                    case "content_cleared":
                    {
                        if (MessageId(root) is { } id && byId.TryGetValue(id, out var m)) m.Text = "";
                        break;
                    }

                    case "msg_end":
                    {
                        // The end carries the whole text and is authoritative — a stream that dropped a delta
                        // still ends up correct here.
                        if (MessageId(root) is { } id && byId.TryGetValue(id, out var m)
                            && Str(root, "text") is { Length: > 0 } text)
                            m.Text = text;
                        break;
                    }

                    case "file":
                    {
                        var name = Str(root, "name") ?? Path.GetFileName(Str(root, "path") ?? "");
                        if (string.IsNullOrWhiteSpace(name)) break;
                        var owner = MessageId(root, "msgId");
                        var file = new SnapshotFile(name, Str(root, "caption"));
                        if (owner is { } mid && byId.TryGetValue(mid, out var m))
                        {
                            if (!m.Files.Any(f => f.Name == name)) m.Files.Add(file);
                        }
                        else if (messages.Count > 0 && !messages[^1].Files.Any(f => f.Name == name))
                        {
                            messages[^1].Files.Add(file); // no owner we know: the latest message is the honest home
                        }
                        break;
                    }

                    case "links":
                    {
                        if (MessageId(root) is not { } id || !byId.TryGetValue(id, out var m)) break;
                        if (!root.TryGetProperty("links", out var list) || list.ValueKind != JsonValueKind.Array) break;
                        foreach (var link in list.EnumerateArray())
                        {
                            var url = Str(link, "url");
                            if (string.IsNullOrWhiteSpace(url)) continue;
                            m.Links.Add(new SnapshotLink(url!, Str(link, "title"), Str(link, "subtitle"), Str(link, "image"), Str(link, "site")));
                        }
                        break;
                    }

                    case "working":
                    {
                        var taskId = Str(root, "id");
                        if (string.IsNullOrWhiteSpace(taskId)) break;

                        // A `working` event means it is running NOW: a task resuming after a question is no longer
                        // waiting, and any outcome recorded against this id belongs to a previous run of it.
                        if (tasksById.TryGetValue(taskId!, out var existing))
                        {
                            existing.Task = Str(root, "task") ?? existing.Task;
                            existing.Status = null;
                            existing.EndedAt = null;
                            existing.StartedAt = Stamp(root) ?? existing.StartedAt;
                            existing.MsgId = MessageId(root, "msgId") ?? existing.MsgId;
                        }
                        else
                        {
                            var task = new SnapshotTask
                            {
                                Id = taskId!,
                                Task = Str(root, "task") ?? "",
                                MsgId = MessageId(root, "msgId"),
                                StartedAt = Stamp(root),
                            };
                            tasksById[taskId!] = task;
                            tasks.Add(task);
                        }
                        questions.Remove(taskId!); // it's moving again, so it isn't waiting on an answer
                        break;
                    }

                    case "working_done":
                    {
                        var taskId = Str(root, "id");
                        if (string.IsNullOrWhiteSpace(taskId)) break;
                        if (!tasksById.TryGetValue(taskId!, out var task)) break;
                        task.Status = Str(root, "status") ?? "done";
                        task.EndedAt = Stamp(root);
                        break;
                    }

                    case "question":
                    {
                        var taskId = Str(root, "id");
                        var text = Str(root, "question");
                        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(text)) break;
                        var options = new List<string>();
                        if (root.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                            foreach (var o in opts.EnumerateArray())
                                if (o.GetString() is { Length: > 0 } option) options.Add(option);

                        // The answer's shape travels with it, so reopening a chat that's waiting on you offers the
                        // same map or number field it did a moment ago rather than falling back to a text box.
                        var kind = Str(root, "kind");
                        JsonElement? number = root.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Object
                            ? n.Clone()
                            : null;
                        JsonElement? place = root.TryGetProperty("place", out var pl) && pl.ValueKind == JsonValueKind.Object
                            ? pl.Clone()
                            : null;

                        questions[taskId!] = new SnapshotQuestion(
                            taskId!, text!, options, Str(root, "project"), kind, number, place);
                        break;
                    }

                    // Put down, without being answered.
                    //
                    // Answering is not the only way a question stops mattering. Three sat on screen for hours
                    // here — how to upload the photos, whether to paste the description by hand — long after
                    // later tasks had done that work another way. Nothing retired them, so they piled up, and
                    // the only way to clear one was to answer a question that no longer had a point.
                    case "question_done":
                    {
                        var taskId = Str(root, "id");
                        if (!string.IsNullOrWhiteSpace(taskId)) questions.Remove(taskId!);
                        break;
                    }
                }
            }
        }

        // A message that never said anything is scaffolding, not content — an assistant turn that only delegated,
        // whose ack the client would render as an empty bubble.
        var said = messages.Where(m => m.Text.Length > 0 || m.Files.Count > 0).ToList();

        return new ConversationSnapshot(events.Count, said, tasks, questions.Values.ToList());
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? MessageId(JsonElement e, string name = "id") =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static DateTimeOffset? Stamp(JsonElement e) =>
        Str(e, "at") is { Length: > 0 } at && DateTimeOffset.TryParse(at, out var when) ? when : null;
}

public sealed class SnapshotMessage
{
    public int Id { get; set; }
    public string Role { get; set; } = "assistant";
    public string Text { get; set; } = "";
    public List<SnapshotFile> Files { get; } = new();
    public List<SnapshotLink> Links { get; } = new();
}

public sealed record SnapshotFile(string Name, string? Caption);

/// <summary>
/// An image card as a reopened conversation gets it back.
/// </summary>
/// <remarks>
/// The subtitle has to survive the replay or a set of pictures loses its captions the moment the page is reloaded — the
/// images would still be there and nothing would say what any of them was.
/// </remarks>
public sealed record SnapshotLink(string Url, string? Title, string? Subtitle, string? Image, string? Site);

public sealed class SnapshotTask
{
    public string Id { get; set; } = "";
    public string Task { get; set; } = "";
    public int? MsgId { get; set; }

    /// <summary>Null while it is still running — the one honest way to say "no outcome yet".</summary>
    public string? Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}

public sealed record SnapshotQuestion(
    string Id,
    string Question,
    IReadOnlyList<string> Options,
    string? Project,
    string? Kind = null,
    JsonElement? Number = null,
    JsonElement? Place = null);

/// <summary>
/// Closing the tasks that were still going when the recording stopped.
///
/// <para>
/// Lives here, next to the fold it feeds, because it has now been wrong twice and both times the symptom was the
/// same: a job that had plainly stopped still showing a moving progress bar, with no way to put it down.
/// </para>
/// </summary>
public static class TaskLifecycle
{
    /// <summary>
    /// Append a terminal event for every task that started and never finished.
    ///
    /// <para>
    /// The subtlety is ORDER, and getting it wrong is what left a stray task turning for ever. A task that pauses to
    /// ask the user something emits <c>working_done(waiting)</c>, and emits <c>working</c> again when the answer
    /// arrives — so its history contains a "done" long before it is done. Asking merely WHETHER a done exists marked
    /// that task finished, and the one task most likely to be interrupted (the one that stopped to ask, waited, then
    /// resumed) was the one task this could never close.
    /// </para>
    /// <para>
    /// So what matters is the LAST lifecycle event a task recorded. Ending on <c>working</c> means it was going when
    /// the recording stopped, whatever it did earlier.
    /// </para>
    /// </summary>
    public static List<(string Event, string Data)> CloseDangling(IReadOnlyList<(string Event, string Data)> recorded)
    {
        // taskId -> the last lifecycle event seen for it, and when it last started.
        var latest = new Dictionary<string, string>(StringComparer.Ordinal);
        var startedAt = new Dictionary<string, string>(StringComparer.Ordinal);
        DateTimeOffset? lastKnownAlive = null;

        foreach (var (@event, data) in recorded)
        {
            try
            {
                using var doc = JsonDocument.Parse(data);

                // The latest moment anything was recorded at all. Nobody records when a process is killed, so this is
                // the best evidence there is for when an unfinished task stopped — and far better than its own start
                // time, which would report every interrupted task as having run for no time at all.
                if (doc.RootElement.TryGetProperty("at", out var stamp)
                    && DateTimeOffset.TryParse(stamp.ToString(), out var when)
                    && (lastKnownAlive is null || when > lastKnownAlive))
                {
                    lastKnownAlive = when;
                }

                if (@event is not ("working" or "working_done")) continue;
                if (!doc.RootElement.TryGetProperty("id", out var idProp)) continue;
                var taskId = idProp.ToString();
                if (string.IsNullOrEmpty(taskId)) continue;

                latest[taskId] = @event;
                if (@event == "working")
                    startedAt[taskId] = doc.RootElement.TryGetProperty("at", out var at) ? at.ToString() : "";
            }
            catch (JsonException)
            {
                /* a line we can't read can't be reconciled either */
            }
        }

        var dangling = latest.Where(kv => kv.Value == "working").Select(kv => kv.Key).ToList();
        if (dangling.Count == 0) return recorded.ToList();

        var closed = recorded.ToList();
        foreach (var taskId in dangling)
        {
            closed.Add(("working_done", JsonSerializer.Serialize(new
            {
                id = taskId,
                status = "interrupted",
                // When it was last known to be alive, not when you reopened the chat — so the pill reports roughly
                // how long it ran before it died, instead of counting up from now for ever.
                at = lastKnownAlive?.ToString("o")
                     ?? (startedAt.TryGetValue(taskId, out var began) && began.Length > 0 ? began : null),
            })));
        }

        return closed;
    }
}
