using System.Text.Json;

namespace Smarty.Agents;

/// <summary>
/// A small time-boxed cache for the web tools, so the same search query or page fetch within a short window
/// reuses the earlier result instead of hitting the network again — which is not just cheaper but kinder to
/// the search engines / sites that bot-block repeated automated requests. Keyed by an opaque string; values
/// are plain text (a formatted search result, or a page's extracted text).
/// </summary>
public interface IResearchCache
{
    bool TryGet(string key, out string value);
    void Set(string key, string value, TimeSpan ttl);
}

/// <summary>The default: no caching. Keeps the library's behaviour unchanged unless a host opts in.</summary>
public sealed class NullResearchCache : IResearchCache
{
    public static readonly NullResearchCache Instance = new();
    public bool TryGet(string key, out string value) { value = ""; return false; }
    public void Set(string key, string value, TimeSpan ttl) { }
}

/// <summary>
/// A JSON-on-disk cache with per-entry TTLs — same persist-to-a-file pattern as the memory/project stores.
/// It survives restarts (handy in dev), needs no server, and self-purges: expired entries are dropped on
/// load and on every write, so the file can't grow without bound. Oversized values (huge pages) are skipped
/// rather than bloating the file. Best-effort throughout — a cache fault must never break a tool call.
/// </summary>
public sealed class FileResearchCache : IResearchCache
{
    private sealed class Entry
    {
        public string Value { get; set; } = "";
        public DateTimeOffset Expires { get; set; }
    }

    private readonly object _lock = new();
    private readonly string _path;
    private readonly int _maxValueLength;
    private readonly Dictionary<string, Entry> _entries = new();
    private static readonly JsonSerializerOptions Json = new();

    public FileResearchCache(string path, int maxValueLength = 500_000)
    {
        _path = path;
        _maxValueLength = maxValueLength;
        Load();
    }

    public bool TryGet(string key, out string value)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var e))
            {
                if (e.Expires > DateTimeOffset.UtcNow) { value = e.Value; return true; }
                _entries.Remove(key); // expired — let the caller refetch
            }
        }
        value = "";
        return false;
    }

    public void Set(string key, string value, TimeSpan ttl)
    {
        if (value.Length > _maxValueLength) return; // don't persist an enormous page into the cache file
        lock (_lock)
        {
            _entries[key] = new Entry { Value = value, Expires = DateTimeOffset.UtcNow + ttl };
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path), Json);
            if (loaded is null) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var (k, e) in loaded)
                if (e.Expires > now) _entries[k] = e; // skip anything already expired
        }
        catch { /* a corrupt cache file shouldn't crash startup */ }
    }

    // Caller holds _lock. Purge expired entries before writing so the file stays bounded.
    private void Save()
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var k in _entries.Where(kv => kv.Value.Expires <= now).Select(kv => kv.Key).ToList())
                _entries.Remove(k);
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, Json));
        }
        catch { /* best-effort persistence */ }
    }
}
