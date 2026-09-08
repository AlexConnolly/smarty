using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Brain;

/// <summary>
/// One piece of context held against a node: a file, or a note.
/// </summary>
/// <remarks>
/// The graph says what things ARE to each other and nothing about what they contain, which is the right division and also
/// a gap — the connection between a person and their flight ticket is not the ticket. This is the ticket.
/// </remarks>
public sealed class Context
{
    public string Id { get; set; } = "";

    /// <summary>The node this belongs to. The only key — context is found by asking the graph first.</summary>
    public string NodeId { get; set; } = "";

    /// <summary>What it is: a file, or a note.</summary>
    public string Sort { get; set; } = Sorts.Note;

    /// <summary>What to call it. A filename for a file; the first line for a note.</summary>
    public string Name { get; set; } = "";

    /// <summary>The file, relative to the store's own folder. Null for a note.</summary>
    public string? File { get; set; }

    /// <summary>A note's words. Null for a file — its words live beside it, because they can be enormous.</summary>
    public string? Value { get; set; }

    public string? MediaType { get; set; }
    public long Bytes { get; set; }

    /// <summary>
    /// Whether readable text was got out of it.
    /// </summary>
    /// <remarks>
    /// The difference between handing a file over and answering from it. Asked what time something starts when all that
    /// exists is a ticket, the answer is inside the PDF — so it is extracted once, when the file arrives, and kept beside
    /// it. An image or a spreadsheet may yield nothing, and that is worth knowing before promising an answer from it.
    /// </remarks>
    public bool Readable { get; set; }

    public DateTimeOffset Added { get; set; }
    public string? Source { get; set; }

    /// <summary>Who was party to it, carried from the sentence that filed it. Same opaque key as an edge's.</summary>
    public string? Audience { get; set; }
}

public static class Sorts
{
    public const string File = "file";
    public const string Note = "note";
}

/// <summary>
/// The context store: what nodes actually contain, kept out of the graph.
/// </summary>
/// <remarks>
/// <para>
/// Separate on purpose, and keyed only by node id. The graph is read whole — every question and every write is shown the
/// full list of what exists — so it has to stay small and uniform. Files and long text are neither, and putting them in
/// would make the one structure that must be cheap to read the most expensive thing in the system.
/// </para>
/// <para>
/// So the order is: walk the graph, settle on which nodes matter, THEN load context for those and only those. That is the
/// same discipline as loading a finding's properties only when the answer is narrow, applied to the expensive end — and
/// it means a hundred stored files cost nothing to a question that turns out to be about none of them.
/// </para>
/// <para>
/// Files are copied in rather than referenced. What arrives in a conversation lives in a turn-scoped folder that gets
/// cleaned, so a reference to one is a promise that expires; a copy is the only version that is still there in a month.
/// </para>
/// </remarks>
public sealed class Contexts
{
    /// <summary>How much of a file's text an answer carries before it stops being an answer and becomes the file.</summary>
    public const int Excerpt = 1_500;

    private readonly string? _dir;
    private readonly JsonSerializerOptions _json;
    private readonly List<Context> _held = new();
    private readonly object _lock = new();
    private int _next;

    public Contexts(string? dir = null, JsonSerializerOptions? json = null)
    {
        _dir = dir;
        _json = json ?? new JsonSerializerOptions { WriteIndented = true };
        Load();
    }

    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public Action<string>? Trace { get; set; }

    public IReadOnlyList<Context> All
    {
        get { lock (_lock) return _held.ToList(); }
    }

    /// <summary>Everything held against one node.</summary>
    public IReadOnlyList<Context> For(string? nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) return Array.Empty<Context>();
        lock (_lock)
            return _held.Where(c => c.NodeId.Equals(nodeId, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public bool Has(string? nodeId) => For(nodeId).Count > 0;

    public Context? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_lock) return _held.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Where a file actually lives, or null if it has gone.</summary>
    public string? PathOf(Context context)
    {
        if (_dir is null || context.File is not { Length: > 0 }) return null;
        var path = Path.Combine(_dir, context.File);
        return System.IO.File.Exists(path) ? path : null;
    }

    /// <summary>
    /// The readable text of a file, up to a limit.
    /// </summary>
    /// <remarks>
    /// Read from beside the file rather than from the index. A long document's text is hundreds of kilobytes, and an index
    /// that carries it has to be loaded in full to answer "what nodes have context" — a question that needs none of it.
    /// </remarks>
    public string? TextOf(Context context, int most = Excerpt)
    {
        if (context.Sort == Sorts.Note) return context.Value;
        if (!context.Readable || _dir is null || context.File is not { Length: > 0 }) return null;

        try
        {
            var beside = Path.Combine(_dir, context.File + ".txt");
            if (!System.IO.File.Exists(beside)) return null;

            var text = System.IO.File.ReadAllText(beside);
            return text.Length <= most ? text : text[..most] + $"\n… ({text.Length - most:N0} more characters)";
        }
        catch (Exception ex)
        {
            Trace?.Invoke($"[context] couldn't read the text of {context.Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Keep a note against a node.</summary>
    public Context Note(string nodeId, string text, string? source = null, string? audience = null)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) throw new ArgumentException("A note needs words.", nameof(text));

        return Add(new Context
        {
            NodeId = nodeId,
            Sort = Sorts.Note,
            Name = trimmed.Length <= 60 ? trimmed : trimmed[..57] + "…",
            Value = trimmed,
            Readable = true,
            Bytes = trimmed.Length,
            Source = source,
            Audience = audience,
        });
    }

    /// <summary>
    /// Keep a file against a node: copied in, and its text pulled out once.
    /// </summary>
    /// <param name="text">
    /// Already-extracted text, when the caller has it. Whatever filed the document has usually read it in order to work out
    /// what it was about, and reading it twice is the slow part done twice for the same answer.
    /// </param>
    public Context? Keep(string nodeId, string path, string? name = null, string? source = null,
        string? audience = null, string? text = null)
    {
        if (_dir is null)
        {
            Trace?.Invoke("[context] nowhere to keep files — this store has no folder");
            return null;
        }

        if (!System.IO.File.Exists(path))
        {
            Trace?.Invoke($"[context] {path} isn't there any more");
            return null;
        }

        try
        {
            var called = Safe(name is { Length: > 0 } ? name : Path.GetFileName(path));
            var folder = Path.Combine(_dir, Graph.Slug(nodeId));
            Directory.CreateDirectory(folder);

            var landed = Path.Combine(folder, called);
            for (var n = 2; System.IO.File.Exists(landed); n++)
                landed = Path.Combine(folder,
                    $"{Path.GetFileNameWithoutExtension(called)}-{n}{Path.GetExtension(called)}");

            System.IO.File.Copy(path, landed);

            // Once, here, rather than every time somebody asks. Extraction is the slow part and the answer never changes.
            var words = text;
            string? kind = null;

            if (words is null)
            {
                var pulled = FileText.Extract(landed);
                if (pulled.Ok && pulled.Text.Trim().Length > 0) (words, kind) = (pulled.Text, pulled.Kind);
                else Trace?.Invoke($"[context] no text out of {called}: {pulled.Reason ?? "nothing readable"}");
            }

            if (words is { Length: > 0 }) System.IO.File.WriteAllText(landed + ".txt", words);

            return Add(new Context
            {
                NodeId = nodeId,
                Sort = Sorts.File,
                Name = Path.GetFileName(landed),
                File = Path.GetRelativePath(_dir, landed),
                MediaType = kind,
                Bytes = new FileInfo(landed).Length,
                Readable = words is { Length: > 0 },
                Source = source,
                Audience = audience,
            });
        }
        catch (Exception ex)
        {
            Trace?.Invoke($"[context] couldn't keep {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Drop one piece of context, and the file with it.</summary>
    /// <summary>
    /// Throw away everything held, and the copies on disk with it.
    /// </summary>
    /// <remarks>
    /// The files are the part that matters here. These are copies of real documents somebody handed over, so a wipe that
    /// cleared the index and left the folder full would be the worst of both — nothing can find them, and they are still
    /// sitting there. Returns how many went, since that is the one thing worth reporting back.
    /// </remarks>
    public int Wipe()
    {
        lock (_lock)
        {
            var counted = _held.Count;

            foreach (var held in _held.ToList())
                if (_dir is not null && held.File is { Length: > 0 })
                    foreach (var path in new[] { Path.Combine(_dir, held.File), Path.Combine(_dir, held.File + ".txt") })
                        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }

            _held.Clear();
            Save();
            return counted;
        }
    }

    public bool Forget(string id)
    {
        lock (_lock)
        {
            var held = _held.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (held is null) return false;

            if (_dir is not null && held.File is { Length: > 0 })
                foreach (var path in new[] { Path.Combine(_dir, held.File), Path.Combine(_dir, held.File + ".txt") })
                    try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }

            _held.Remove(held);
            Save();
            return true;
        }
    }

    /// <summary>Move context to the node a merge kept, so nothing is orphaned by two things turning out to be one.</summary>
    public int Moved(string fromNodeId, string toNodeId)
    {
        lock (_lock)
        {
            var moving = _held.Where(c => c.NodeId.Equals(fromNodeId, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var held in moving) held.NodeId = toNodeId;
            if (moving.Count > 0) Save();
            return moving.Count;
        }
    }

    private Context Add(Context held)
    {
        lock (_lock)
        {
            held.Id = $"c{++_next}";
            held.Added = Now();
            _held.Add(held);
            Save();
            return held;
        }
    }

    /// <summary>A filename that cannot climb out of the folder it belongs in.</summary>
    private static string Safe(string name)
    {
        var cleaned = new string(name.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)).ToArray()).Trim();
        cleaned = cleaned.Replace("..", "");
        return cleaned.Length == 0 ? "file" : cleaned;
    }

    private string IndexPath => Path.Combine(_dir!, "index.json");

    private void Load()
    {
        if (_dir is null || !System.IO.File.Exists(IndexPath)) return;

        try
        {
            _held.AddRange(JsonSerializer.Deserialize<List<Context>>(
                System.IO.File.ReadAllText(IndexPath), _json) ?? new());

            _next = _held.Select(c => int.TryParse(c.Id.TrimStart('c'), out var n) ? n : 0)
                .DefaultIfEmpty(0).Max();
        }
        catch
        {
            _held.Clear();
        }
    }

    private void Save()
    {
        if (_dir is null) return;

        try
        {
            Directory.CreateDirectory(_dir);
            System.IO.File.WriteAllText(IndexPath, JsonSerializer.Serialize(_held, _json));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[context] couldn't save: {ex.Message}");
        }
    }
}
