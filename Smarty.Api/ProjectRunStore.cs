using System.Text.Json;

namespace Smarty.Api;

/// <summary>One step in a worker run, flattened for read-only display: a chunk of thinking, a tool call
/// (with its arguments and result), or the final answer. This is "what the sub-agent did".</summary>
public sealed class RunStep
{
    public string Kind { get; set; } = "";   // thinking | tool | answer
    public string? Text { get; set; }          // thinking text, or the final answer
    public string? Tool { get; set; }          // tool name (kind == tool)
    public string? Args { get; set; }          // tool arguments, as JSON text
    public string? Result { get; set; }        // tool result text
}

/// <summary>A completed (or cancelled/failed) background worker run, scoped to a project so the project
/// overview can show everything that was done for it. Only project-tagged runs are kept — work with no
/// project isn't shown anywhere.</summary>
public sealed class ProjectRun
{
    public string Id { get; set; } = "";
    public string Project { get; set; } = "";      // slug
    public string Task { get; set; } = "";
    public string? Title { get; set; }              // short human label for the run (filled in the background)
    public string Status { get; set; } = "";        // done | cancelled | failed
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public List<RunStep> Steps { get; set; } = new();
    public string? Result { get; set; }
}

/// <summary>Persisted log of project-scoped worker runs. Append-only; read back per project, newest first.</summary>
public sealed class ProjectRunStore
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly JsonSerializerOptions _json;
    private readonly List<ProjectRun> _runs = new();

    public ProjectRunStore(string path, JsonSerializerOptions json)
    {
        _path = path;
        _json = json;
        Load();
    }

    public void Add(ProjectRun run)
    {
        lock (_lock)
        {
            _runs.Add(run);
            Save();
        }
    }

    /// <summary>Set a run's short title (generated in the background after it's logged).</summary>
    public void SetTitle(string id, string title)
    {
        lock (_lock)
        {
            var run = _runs.FirstOrDefault(r => r.Id == id);
            if (run is null) return;
            run.Title = title;
            Save();
        }
    }

    /// <summary>The runs recorded for a project, newest first.</summary>
    public IReadOnlyList<ProjectRun> ForProject(string slug)
    {
        slug = slug.Trim().ToLowerInvariant();
        lock (_lock)
            return _runs.Where(r => r.Project == slug)
                .OrderByDescending(r => r.StartedAt).ToList();
    }

    public int CountFor(string slug)
    {
        slug = slug.Trim().ToLowerInvariant();
        lock (_lock) return _runs.Count(r => r.Project == slug);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<List<ProjectRun>>(File.ReadAllText(_path), _json);
            if (loaded is not null) _runs.AddRange(loaded);
        }
        catch { /* a corrupt run log shouldn't crash startup */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_runs, _json));
        }
        catch { /* best-effort */ }
    }
}
