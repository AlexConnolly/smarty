using System.Text;
using System.Text.Json;

namespace Smarty.Api;

/// <summary>
/// A conversation, on disk.
/// <para>
/// Sessions have lived only in memory, which meant every restart silently took the chats with it — and I restarted
/// this project seventy-odd times in two days, three of them over work that was still running. The conversation
/// LIST already survived (the control hub keeps titles), so a reload showed a chat that existed and was empty,
/// which is worse than showing nothing.
/// </para>
/// <para>
/// One file per conversation, one JSON object per line, appended as events happen. A line-per-event file is the
/// right shape here: appending never rewrites what came before, a half-written last line costs one event rather
/// than the file, and reading back is a stream rather than a parse of the whole history.
/// </para>
/// <para>
/// Only what a reader needs to see the conversation again is kept — messages, and the cards that hang off them.
/// Not the internal chatter: thinking, tool starts, spend and heartbeats are noise a day later, and they are the
/// bulk of the volume.
/// </para>
/// </summary>
public sealed class ChatStore
{
    /// <summary>The events worth keeping. Everything else is scaffolding for a conversation happening NOW.</summary>
    private static readonly HashSet<string> Durable = new(StringComparer.Ordinal)
    {
        "msg_start", "msg_end", "file", "links", "question", "working", "working_done",
        // Whatever retires a question has to last as long as the question does. Left out, dismissing one
        // cleared it from the screen and from memory, and the next reload brought it straight back — which is
        // worse than not offering to dismiss it at all.
        "question_done",
    };

    private readonly string _root;
    private readonly object _lock = new();

    public ChatStore(string root)
    {
        _root = root;
        try { Directory.CreateDirectory(root); } catch (IOException) { /* first write will report it */ }
    }

    private string PathFor(string sessionId) =>
        Path.Combine(_root, SafeName(sessionId) + ".jsonl");

    /// <summary>Record one event, if it is one of the kinds a reader needs. Never throws: losing a line of history
    /// is a nuisance, losing the turn it belongs to is not acceptable.</summary>
    public void Append(string sessionId, string @event, string data)
    {
        if (!Durable.Contains(@event) || string.IsNullOrWhiteSpace(sessionId)) return;

        try
        {
            var line = JsonSerializer.Serialize(new { e = @event, d = data, at = DateTimeOffset.UtcNow });
            lock (_lock) File.AppendAllText(PathFor(sessionId), line + "\n", Encoding.UTF8);
        }
        catch (Exception)
        {
            // Deliberately silent: this is a mirror of what the user already has on screen.
        }
    }

    /// <summary>Replay a conversation in order. Empty when it was never written or the file is gone.</summary>
    public IReadOnlyList<(string Event, string Data)> Read(string sessionId)
    {
        var path = PathFor(sessionId);
        var events = new List<(string, string)>();
        if (!File.Exists(path)) return events;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var e = doc.RootElement.TryGetProperty("e", out var ev) ? ev.GetString() : null;
                    var d = doc.RootElement.TryGetProperty("d", out var da) ? da.GetString() : null;
                    if (e is not null && d is not null) events.Add((e, d));
                }
                catch (JsonException)
                {
                    // A torn final line from a kill mid-write. Skip it and keep the rest.
                }
            }
        }
        catch (IOException)
        {
            return events;
        }
        return events;
    }

    /// <summary>Whether anything was ever written for this conversation.</summary>
    public bool Has(string sessionId) => File.Exists(PathFor(sessionId));

    /// <summary>
    /// Every conversation on disk, most recently written first. This is the authority on what has been retained —
    /// the control hub's list includes conversations whose messages were never recorded, and offering one of those
    /// in a sidebar leads to the empty room this store exists to prevent.
    /// </summary>
    public IReadOnlyList<(string Id, DateTimeOffset LastWrite, long Bytes)> List()
    {
        try
        {
            return new DirectoryInfo(_root)
                .EnumerateFiles("*.jsonl")
                .Select(f => (Id: Path.GetFileNameWithoutExtension(f.Name),
                              LastWrite: (DateTimeOffset)f.LastWriteTimeUtc,
                              Bytes: f.Length))
                .OrderByDescending(x => x.LastWrite)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<(string, DateTimeOffset, long)>();
        }
    }

    /// <summary>
    /// The opening line of the conversation, for naming it when nothing else has. Stops at the first user message
    /// rather than reading the file — a long chat's history is megabytes and all that's wanted is its first
    /// sentence.
    /// </summary>
    public string? FirstUserMessage(string sessionId)
    {
        var path = PathFor(sessionId);
        if (!File.Exists(path)) return null;

        try
        {
            var userMessage = -1;
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
                using (doc)
                {
                    // Every read here is guarded on the VALUE KIND, not just on the property being present. A
                    // JsonElement of the wrong type throws when you ask for the wrong thing out of it — GetString on a
                    // number, TryGetInt32 on a string — and TryGetInt32 in particular reads as safe while being the
                    // opposite: its "Try" only covers a number too large to fit, never a value that was never a number.
                    var e = Text(doc.RootElement, "e");
                    var d = Text(doc.RootElement, "d");
                    if (e is null || d is null) continue;

                    JsonDocument inner;
                    try { inner = JsonDocument.Parse(d); } catch (JsonException) { continue; }
                    using (inner)
                    {
                        var id = Whole(inner.RootElement, "id");

                        // msg_start says whose message this is; msg_end carries the finished text. Match them by
                        // id so an assistant reply can never be mistaken for the user's opening line.
                        if (e == "msg_start" && Text(inner.RootElement, "role") == "user")
                        {
                            userMessage = id;
                        }
                        else if (e == "msg_end" && id == userMessage && id >= 0)
                        {
                            var text = Text(inner.RootElement, "text");
                            if (!string.IsNullOrWhiteSpace(text)) return text!.Trim();
                        }
                    }
                }
            }
        }
        catch (IOException)
        {
            /* mid-write, or gone — no title is better than a failed request */
        }
        catch (Exception ex)
        {
            // Anything at all. This is one conversation's title in a list of many, and a single unreadable line used to
            // take the whole list down with it — the chat sidebar returning 500 because one event somewhere was shaped
            // unexpectedly. A missing title is invisible; a failed request is the app.
            Console.Error.WriteLine($"[chats] couldn't read a title from {sessionId}: {ex.Message}");
        }
        return null;
    }

    /// <summary>A string property, or null if it is absent or isn't one.</summary>
    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var found)
        && found.ValueKind == JsonValueKind.String
            ? found.GetString()
            : null;

    /// <summary>An integer property, or -1 if it is absent or isn't one.</summary>
    private static int Whole(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var found)
        && found.ValueKind == JsonValueKind.Number
        && found.TryGetInt32(out var value)
            ? value
            : -1;

    /// <summary>Forget a conversation entirely. The user's history is theirs to delete.</summary>
    public bool Delete(string sessionId)
    {
        try
        {
            var path = PathFor(sessionId);
            if (!File.Exists(path)) return false;
            lock (_lock) File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string SafeName(string id) =>
        string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')).TrimStart('_');
}
