using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>A long-running endeavour the chat can route to and scope context/jobs by (see PROJECTS_SPEC.md).</summary>
public sealed class Project
{
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "active";   // active | done | archived
    public DateTimeOffset Created { get; set; }
}

/// <summary>
/// The handful of projects on the go. Deliberately small — projects are created explicitly (never as a
/// side-effect of delegating), so this stays a short, intentional list.
/// </summary>
public sealed class ProjectStore
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly JsonSerializerOptions _json;
    private readonly List<Project> _projects = new();

    public ProjectStore(string path, JsonSerializerOptions json)
    {
        _path = path;
        _json = json;
        Load();
    }

    /// <summary>Create a project (deliberate, confirmed act). Returns a confirmation incl. the slug.</summary>
    public string Create(string title, string description)
    {
        title = title.Trim();
        if (title.Length == 0) return "A project needs a title.";

        lock (_lock)
        {
            // Anti-sprawl: refuse a near-duplicate of an existing project (share ≥2 meaningful words).
            var words = Words(title);
            var dup = _projects.FirstOrDefault(p =>
                p.Status == "active" && Words(p.Title).Intersect(words).Count() >= 2);
            if (dup is not null)
                return $"There's already a similar project: \"{dup.Title}\" (slug: {dup.Slug}). Use that one " +
                       "instead of creating a duplicate.";

            var slug = Slugify(title);
            string baseSlug = slug;
            for (int n = 2; _projects.Any(p => p.Slug == slug); n++) slug = $"{baseSlug}-{n}";

            _projects.Add(new Project
            {
                Slug = slug,
                Title = title,
                Description = description?.Trim() ?? "",
                Status = "active",
                Created = DateTimeOffset.UtcNow,
            });
            Save();
            return $"Project created: \"{title}\" (slug: {slug}). Tag work to it with that slug.";
        }
    }

    public Project? Get(string slug)
    {
        slug = slug.Trim().ToLowerInvariant();
        lock (_lock) return _projects.FirstOrDefault(p => p.Slug == slug);
    }

    public bool Exists(string slug) => Get(slug) is not null;

    /// <summary>The active projects (slug/title/description), for resolving a statement against them.</summary>
    public IReadOnlyList<(string Slug, string Title, string Description)> ActiveProjects()
    {
        lock (_lock)
            return _projects.Where(p => p.Status == "active")
                .Select(p => (p.Slug, p.Title, p.Description)).ToList();
    }

    public string List()
    {
        lock (_lock)
        {
            var active = _projects.Where(p => p.Status == "active").ToList();
            if (active.Count == 0) return "No projects yet.";
            var sb = new StringBuilder("Projects:\n");
            foreach (var p in active)
            {
                sb.Append($"- {p.Slug}: {p.Title}");
                if (!string.IsNullOrWhiteSpace(p.Description)) sb.Append($" — {p.Description}");
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd();
        }
    }

    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
        { "the", "and", "with", "for", "my", "our", "plan", "planning", "project", "trip", "get", "getting", "sort", "sorting" };

    private static HashSet<string> Words(string s) =>
        Regex.Matches(s.ToLowerInvariant(), @"[a-z0-9]{3,}")
            .Select(m => m.Value).Where(w => !Common.Contains(w))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string Slugify(string title)
    {
        var s = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return s.Length == 0 ? "project" : (s.Length > 40 ? s[..40].Trim('-') : s);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<List<Project>>(File.ReadAllText(_path), _json);
            if (loaded is not null) _projects.AddRange(loaded);
        }
        catch { /* a corrupt file shouldn't crash startup */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_projects, _json));
        }
        catch { /* best-effort */ }
    }
}

/// <summary>Tools for creating and listing projects. Creation is its own deliberate step.</summary>
public static class ProjectTools
{
    public static AgentTool CreateTool(ProjectStore store) => new(
        "create_project",
        "Start a project for anything that involves planning or organising something with several parts — an " +
        "event or party, a trip or holiday, a house move, any multi-step undertaking you'll return to. Use it " +
        "whenever a request is more than a quick question or a couple of back-and-forths (e.g. \"help me plan " +
        "an evening for 20 people\"). Propose it and confirm with the user first; don't duplicate an existing one.",
        new[]
        {
            ToolParameter.String("title", "Short title, e.g. \"Holiday with my sister\".", required: true),
            ToolParameter.String("description", "One line on what it's about.", required: false),
        },
        (args, _) => Task.FromResult(ToolOutput.Ok(store.Create(args.GetString("title"), args.GetStringOrNull("description") ?? ""))));

    public static AgentTool ListTool(ProjectStore store) => new(
        "list_projects",
        "List the projects currently on the go (with their slugs).",
        Array.Empty<ToolParameter>(),
        (_, _) => Task.FromResult(ToolOutput.Ok(store.List())));
}
