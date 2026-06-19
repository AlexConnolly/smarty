using System.Text;
using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>One remembered fact about the user (an edge: type/key → value), with its own metadata.</summary>
public sealed class MemoryFact
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";          // category: location, food, person, ...
    public string Key { get; set; } = "";           // the slot/attribute: home, diet, wife — what supersedes
    public string Value { get; set; } = "";         // the value: London, vegetarian, Sarah
    public string? Context { get; set; }            // free note / explanation
    public DateTimeOffset Asserted { get; set; }    // when it became true / was last reaffirmed
    public string Status { get; set; } = "active";  // active | superseded | retired
    public string? SupersededBy { get; set; }
}

/// <summary>
/// Smarty's long-term memory. Structured, deterministic facts keyed by (type, key) — a new value for an
/// existing single slot supersedes the old one (kept, never deleted). Persisted to a JSON file. See
/// MEMORY_SPEC.md for the full design; this is Phase 1 (store + the two access tools + supersede/soft-retire).
/// </summary>
public sealed class MemoryStore
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly JsonSerializerOptions _json;
    private readonly List<MemoryFact> _facts = new();
    private int _next;

    public MemoryStore(string path, JsonSerializerOptions json)
    {
        _path = path;
        _json = json;
        Load();
    }

    /// <summary>Remember a durable fact. Same (type,key) with a new value supersedes the old; same value
    /// reaffirms; otherwise it's inserted. Returns a short confirmation for the model to relay.</summary>
    public string Set(string type, string key, string value, string? context)
    {
        type = type.Trim().ToLowerInvariant();
        key = key.Trim().ToLowerInvariant();
        value = value.Trim();
        if (type.Length == 0 || key.Length == 0 || value.Length == 0)
            return "Couldn't save that — type, key and value are all required.";

        lock (_lock)
        {
            var existing = _facts.FirstOrDefault(f =>
                f.Status == "active" && f.Type == type && f.Key == key);

            if (existing is not null && string.Equals(existing.Value, value, StringComparison.OrdinalIgnoreCase))
            {
                existing.Asserted = DateTimeOffset.UtcNow;
                if (!string.IsNullOrWhiteSpace(context)) existing.Context = context;
                Save();
                return $"Already knew that — {key} is {value}. (refreshed)";
            }

            var fact = new MemoryFact
            {
                Id = "m" + (++_next),
                Type = type,
                Key = key,
                Value = value,
                Context = string.IsNullOrWhiteSpace(context) ? null : context.Trim(),
                Asserted = DateTimeOffset.UtcNow,
                Status = "active",
            };
            _facts.Add(fact);

            if (existing is not null)
            {
                existing.Status = "superseded";
                existing.SupersededBy = fact.Id;
                Save();
                return $"Updated {key}: was {existing.Value} ({Age(existing.Asserted)}), now {value}.";
            }

            Save();
            return $"Noted — {key}: {value}.";
        }
    }

    /// <summary>Keyword search across type/key/value/context of active facts. Pass keywords, not sentences.</summary>
    public string Search(string query)
    {
        var terms = query.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2)
            .Distinct()
            .ToList();
        if (terms.Count == 0) return "Give me a keyword or two to look up.";

        lock (_lock)
        {
            var hits = _facts.Where(f => f.Status == "active")
                .Select(f => (f, score: Score(f, terms)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .ThenByDescending(x => x.f.Asserted)
                .Take(10)
                .ToList();

            if (hits.Count == 0) return $"Nothing in memory for \"{query}\".";

            var sb = new StringBuilder();
            foreach (var (f, _) in hits)
            {
                sb.Append($"- {f.Key}: {f.Value}");
                if (!string.IsNullOrWhiteSpace(f.Context)) sb.Append($" ({f.Context})");
                sb.Append($"  [{f.Type}, noted {Age(f.Asserted)}]\n");
            }
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>Everything currently known, for injecting a profile into a prompt (Phase 1.5).</summary>
    public IReadOnlyList<MemoryFact> Active()
    {
        lock (_lock) return _facts.Where(f => f.Status == "active").ToList();
    }

    private static int Score(MemoryFact f, IEnumerable<string> terms)
    {
        string hay = $"{f.Type} {f.Key} {f.Value} {f.Context}".ToLowerInvariant();
        return terms.Count(t => hay.Contains(t, StringComparison.Ordinal));
    }

    private static string Age(DateTimeOffset when)
    {
        var d = DateTimeOffset.UtcNow - when;
        if (d.TotalHours < 20) return "today";
        int days = (int)Math.Round(d.TotalDays);
        if (days < 30) return $"{days}d ago";
        int months = (int)Math.Round(d.TotalDays / 30);
        return months < 12 ? $"{months}mo ago" : $"{(int)Math.Round(d.TotalDays / 365)}y ago";
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<List<MemoryFact>>(File.ReadAllText(_path), _json);
            if (loaded is not null)
            {
                _facts.AddRange(loaded);
                _next = _facts.Select(f => int.TryParse(f.Id.TrimStart('m'), out var n) ? n : 0).DefaultIfEmpty(0).Max();
            }
        }
        catch { /* a corrupt memory file shouldn't crash startup */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_facts, _json));
        }
        catch { /* best-effort persistence */ }
    }
}

/// <summary>The two standard memory-access tools, given to the orchestrator and the workers.</summary>
public static class MemoryTools
{
    public static AgentTool SearchTool(MemoryStore m) => new(
        "search_memory",
        "Search your long-term memory of the user. Search by KEYWORDS (e.g. \"home London\" or \"diet\"), " +
        "not full sentences.",
        new[] { ToolParameter.String("query", "Keywords to look up.", required: true) },
        (args, _) => Task.FromResult(ToolOutput.Ok(m.Search(args.GetString("query")))));

    public static AgentTool SetTool(MemoryStore m) => new(
        "set_memory",
        "Remember a durable fact about the user. `key` is the attribute/slot (e.g. home, favourite-city, " +
        "diet, wife); a new value for an existing key replaces the old one.",
        new[]
        {
            ToolParameter.String("type", "Category — e.g. location, food, person, work.", required: true),
            ToolParameter.String("key", "The attribute/slot — e.g. home, favourite-city, diet, wife.", required: true),
            ToolParameter.String("value", "The value — e.g. London, vegetarian, Sarah.", required: true),
            ToolParameter.String("context", "Optional short note/explanation.", required: false),
        },
        (args, _) => Task.FromResult(ToolOutput.Ok(
            m.Set(args.GetString("type"), args.GetString("key"), args.GetString("value"), args.GetStringOrNull("context")))));
}
