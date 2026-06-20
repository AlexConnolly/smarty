using System.Text.RegularExpressions;

namespace Smarty.Api;

/// <summary>Each project's living README — a plain-language markdown summary the user reads to catch up,
/// regenerated in the background whenever the project is touched. Stored as one markdown file per project
/// so it literally "sits there" next to the data (data/readmes/&lt;slug&gt;.md).</summary>
public sealed class ProjectReadmeStore
{
    private readonly object _lock = new();
    private readonly string _dir;

    public ProjectReadmeStore(string dir)
    {
        _dir = dir;
        try { Directory.CreateDirectory(_dir); } catch { /* best-effort */ }
    }

    public string? Get(string slug)
    {
        try
        {
            var path = PathFor(slug);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch { return null; }
    }

    public void Set(string slug, string markdown)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(_dir);
                File.WriteAllText(PathFor(slug), markdown);
            }
        }
        catch { /* best-effort */ }
    }

    private string PathFor(string slug) =>
        Path.Combine(_dir, Regex.Replace(slug.ToLowerInvariant(), "[^a-z0-9-]", "") + ".md");
}
