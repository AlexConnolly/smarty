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

    /// <summary>The conversation this ran in — how its files are addressed, since they live under it.</summary>
    public string? Session { get; set; }

    /// <summary>
    /// The file(s) this run handed over, by name.
    /// <para>
    /// Recorded because a deliverable was otherwise reachable only through the conversation that produced it: a
    /// deck built for a project lived under a session id, and once that conversation was gone the file sat on disk
    /// with nothing pointing at it. A project should be able to show its own work.
    /// </para>
    /// </summary>
    public List<string> Files { get; set; } = new();
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

    /// <summary>
    /// Forget every run recorded against something, and say how many went.
    /// </summary>
    /// <remarks>
    /// For when the project itself is deleted. A run's whole identity is the project it belongs to, so runs left behind
    /// are rows keyed to a slug nothing resolves — they would still be counted, still be searched, and still turn up in
    /// an answer naming a project that no longer exists.
    /// </remarks>
    public int DeleteFor(string slug)
    {
        slug = slug.Trim().ToLowerInvariant();

        lock (_lock)
        {
            var went = _runs.RemoveAll(r => r.Project == slug);
            if (went > 0) Save();
            return went;
        }
    }

    /// <summary>
    /// Drop a file from a project's list of what it produced. Returns how many runs mentioned it.
    /// <para>
    /// Only the project's claim on the file goes: the conversation that produced it still shows it in its own
    /// transcript, and that card still works. Removing a name from a project shelf should not reach back and
    /// silently rewrite an old conversation.
    /// </para>
    /// </summary>
    public int ForgetFile(string slug, string name)
    {
        slug = slug.Trim().ToLowerInvariant();
        name = Path.GetFileName(name);

        lock (_lock)
        {
            int touched = 0;
            foreach (var run in _runs.Where(r => r.Project == slug))
                if (run.Files.RemoveAll(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)) > 0)
                    touched++;

            if (touched > 0) Save();
            return touched;
        }
    }

    /// <summary>One run by id, or null. For reading back what a past run produced.</summary>
    public ProjectRun? Get(string id)
    {
        id = id.Trim();
        lock (_lock) return _runs.FirstOrDefault(r => r.Id == id);
    }

    public int CountFor(string slug)
    {
        slug = slug.Trim().ToLowerInvariant();
        lock (_lock) return _runs.Count(r => r.Project == slug);
    }

    /// <summary>
    /// When work last happened on a project, or null if none has. Derived from the runs rather than stamped on
    /// the project, so it cannot drift out of step with reality — every path that does work on a project records
    /// a run, and there is no second place to remember to update.
    /// <para>
    /// A run in flight has no end yet, so its start is what counts: work happening right now is the most recent
    /// interaction there is, not the least.
    /// </para>
    /// </summary>
    public DateTimeOffset? LastActivityFor(string slug)
    {
        slug = slug.Trim().ToLowerInvariant();
        lock (_lock)
        {
            DateTimeOffset? latest = null;
            foreach (var run in _runs)
            {
                if (run.Project != slug) continue;
                var at = run.EndedAt > run.StartedAt ? run.EndedAt : run.StartedAt;
                if (latest is null || at > latest) latest = at;
            }
            return latest;
        }
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
