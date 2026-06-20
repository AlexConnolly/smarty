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
    public string? Project { get; set; }            // null = global "who you are"; slug = project-scoped
    public float[]? Embedding { get; set; }         // semantic vector for relevance retrieval (lazy-filled)
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
    private readonly Func<string, CancellationToken, Task<float[]?>>? _embed;
    private readonly List<MemoryFact> _facts = new();
    private int _next;

    public MemoryStore(string path, JsonSerializerOptions json, Func<string, CancellationToken, Task<float[]?>>? embed = null)
    {
        _path = path;
        _json = json;
        _embed = embed;
        Load();
    }

    // The text we embed for a fact — its meaning, not its plumbing.
    private static string FactText(MemoryFact f) =>
        string.Join(' ', new[] { f.Key, f.Value, f.Context }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>Remember a durable fact. Same (type,key) with a new value supersedes the old; same value
    /// reaffirms; otherwise it's inserted. Returns a short confirmation for the model to relay.</summary>
    public string Set(string type, string key, string value, string? context, string? project = null)
    {
        type = type.Trim().ToLowerInvariant();
        key = key.Trim().ToLowerInvariant();
        value = value.Trim();
        project = string.IsNullOrWhiteSpace(project) ? null : project.Trim().ToLowerInvariant();
        if (type.Length == 0 || key.Length == 0 || value.Length == 0)
            return "Couldn't save that — type, key and value are all required.";

        lock (_lock)
        {
            // Supersession is scoped to the project — a project's "destination" is independent of the
            // global profile (and of other projects').
            var existing = _facts.FirstOrDefault(f =>
                f.Status == "active" && f.Type == type && f.Key == key && f.Project == project);

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
                Project = project,
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

    /// <summary>Keyword search across type/key/value/context of active facts in a scope (null = global
    /// user facts; a slug = that project's facts). Pass keywords, not sentences.</summary>
    public string Search(string query, string? project = null)
    {
        project = string.IsNullOrWhiteSpace(project) ? null : project.Trim().ToLowerInvariant();
        var terms = query.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2)
            .Distinct()
            .ToList();
        if (terms.Count == 0) return "Give me a keyword or two to look up.";

        lock (_lock)
        {
            var hits = _facts.Where(f => f.Status == "active" && f.Project == project)
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

    /// <summary>Active facts in a scope (null = global user facts; a slug = that project's facts).</summary>
    public IReadOnlyList<MemoryFact> Active(string? project = null)
    {
        project = string.IsNullOrWhiteSpace(project) ? null : project.Trim().ToLowerInvariant();
        lock (_lock) return _facts.Where(f => f.Status == "active" && f.Project == project).ToList();
    }

    /// <summary>The active facts most relevant to a message — for auto-surfacing context per turn (the
    /// system retrieves; the model doesn't have to decide to search). Keyword-scored for now; this is the
    /// seam where embeddings/vectors slot in to make "relevant" semantic.</summary>
    public async Task<IReadOnlyList<MemoryFact>> RelevantTo(string message, int k, string? project = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return Array.Empty<MemoryFact>();
        project = string.IsNullOrWhiteSpace(project) ? null : project.Trim().ToLowerInvariant();
        List<MemoryFact> active;
        lock (_lock) active = _facts.Where(f => f.Status == "active" && f.Project == project).ToList();
        if (active.Count == 0) return Array.Empty<MemoryFact>();

        // Semantic path: embed the query + any facts missing a vector (nomic prefixes), rank by cosine.
        if (_embed is not null)
        {
            var qv = await _embed("search_query: " + message, ct).ConfigureAwait(false);
            if (qv is not null)
            {
                bool changed = false;
                foreach (var f in active.Where(f => f.Embedding is null))
                {
                    f.Embedding = await _embed("search_document: " + FactText(f), ct).ConfigureAwait(false);
                    changed |= f.Embedding is not null;
                }
                if (changed) lock (_lock) Save();

                var scored = active.Where(f => f.Embedding is not null)
                    .Select(f => (f, sim: Cosine(qv, f.Embedding!)))
                    .OrderByDescending(x => x.sim)
                    .ToList();
                if (Environment.GetEnvironmentVariable("SMARTY_TRACE") == "1")
                    Console.Error.WriteLine("[mem] sims: " + string.Join(", ", scored.Select(x => $"{x.f.Key}={x.sim:F2}")));
                var ranked = scored.Where(x => x.sim >= RelevanceThreshold).Take(k).Select(x => x.f).ToList();
                if (ranked.Count > 0) return ranked;
            }
        }

        // Keyword fallback — no embedder, embedding failed, or nothing cleared the semantic bar.
        var terms = Terms(message);
        if (terms.Count == 0) return Array.Empty<MemoryFact>();
        return active.Select(f => (f, s: Score(f, terms)))
            .Where(x => x.s > 0)
            .OrderByDescending(x => x.s)
            .ThenByDescending(x => x.f.Asserted)
            .Take(k)
            .Select(x => x.f)
            .ToList();
    }

    /// <summary>How well a statement matches a project — its strongest hit across the project's title,
    /// description and accumulated facts (a single strong fact, e.g. destination=Lisbon, is enough).</summary>
    public readonly record struct ProjectMatch(string Slug, string Title, double Score, string Matched);

    /// <summary>Rank projects by how well a free-text statement resolves to each — searching not just their
    /// titles/descriptions but the facts recorded inside them. This is how "the flights next week" finds the
    /// Lisbon holiday even though neither word is in its title. Embeddings-backed; empty without an embedder.</summary>
    public async Task<IReadOnlyList<ProjectMatch>> RankProjects(
        string statement, IReadOnlyList<(string Slug, string Title, string Description)> projects, CancellationToken ct = default)
    {
        var results = new List<ProjectMatch>();
        if (_embed is null || projects.Count == 0 || string.IsNullOrWhiteSpace(statement)) return results;

        var qv = await _embed("search_query: " + statement, ct).ConfigureAwait(false);
        if (qv is null) return results;

        bool changed = false;
        foreach (var (slug, title, description) in projects)
        {
            double best = 0; string matched = "";

            var head = (title + " " + description).Trim();
            var hv = await _embed("search_document: " + head, ct).ConfigureAwait(false);
            if (hv is not null) { var s = Cosine(qv, hv); if (s > best) { best = s; matched = title; } }

            List<MemoryFact> facts;
            lock (_lock) facts = _facts.Where(f => f.Status == "active" && f.Project == slug).ToList();
            foreach (var f in facts)
            {
                if (f.Embedding is null)
                {
                    f.Embedding = await _embed("search_document: " + FactText(f), ct).ConfigureAwait(false);
                    changed |= f.Embedding is not null;
                }
                if (f.Embedding is null) continue;
                var s = Cosine(qv, f.Embedding);
                if (s > best) { best = s; matched = $"{f.Key}: {f.Value}"; }
            }

            results.Add(new ProjectMatch(slug, title, best, matched));
        }
        if (changed) lock (_lock) Save();

        if (Environment.GetEnvironmentVariable("SMARTY_TRACE") == "1")
            Console.Error.WriteLine("[proj] resolve: " + string.Join(", ", results.OrderByDescending(r => r.Score).Select(r => $"{r.Slug}={r.Score:F2}")));

        return results.OrderByDescending(r => r.Score).ToList();
    }

    private const double RelevanceThreshold = 0.5; // cosine; tune against real queries

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return (na == 0 || nb == 0) ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private static List<string> Terms(string s) =>
        s.ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim('.', ',', '?', '!', ';', ':', '"', '\'', '(', ')'))
            .Where(t => t.Length >= 3)
            .Distinct()
            .ToList();

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

/// <summary>The two standard memory-access tools. When <paramref name="project"/> is set (a worker
/// running inside a project), reads and writes are scoped/auto-tagged to that project and the wording
/// reframes from "the user" to "this project".</summary>
public static class MemoryTools
{
    public static AgentTool SearchTool(MemoryStore m, string? project = null) => new(
        "search_memory",
        project is null
            ? "Search your long-term memory of the user. Search by KEYWORDS (e.g. \"home London\"), not sentences."
            : "Search what you've recorded about THIS project. Search by KEYWORDS, not sentences.",
        new[] { ToolParameter.String("query", "Keywords to look up.", required: true) },
        (args, _) => Task.FromResult(ToolOutput.Ok(m.Search(args.GetString("query"), project))));

    public static AgentTool SetTool(MemoryStore m, string? project = null) => new(
        "set_memory",
        project is null
            ? "Remember a durable fact about the user. `key` is the attribute/slot (e.g. home, diet, wife); a " +
              "new value for an existing key replaces the old one."
            : "Record a detail about THIS PROJECT (a decision, date, finding) — not a fact about the user. " +
              "`key` is the slot (e.g. destination, dates, budget); a new value for an existing key updates it.",
        new[]
        {
            ToolParameter.String("type", "Category — e.g. location, food, person, work.", required: true),
            ToolParameter.String("key", "The attribute/slot — e.g. home, diet, destination, dates.", required: true),
            ToolParameter.String("value", "The value — e.g. London, vegetarian, Lisbon.", required: true),
            ToolParameter.String("context", "Optional short note/explanation.", required: false),
        },
        (args, _) => Task.FromResult(ToolOutput.Ok(
            m.Set(args.GetString("type"), args.GetString("key"), args.GetString("value"), args.GetStringOrNull("context"), project))));
}
