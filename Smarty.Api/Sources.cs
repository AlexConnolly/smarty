using System.Text.Json;

namespace Smarty.Api;

/// <summary>
/// A folder or file on this machine that the system has been GIVEN access to.
///
/// <para>
/// The answer to a whole class of thing the panels could not reach. A page cannot read a filesystem — quite rightly —
/// and so a request as ordinary as "show me my photos" had nowhere to go: not because it is hard, but because nothing
/// here served a folder. A panel asking for local data was told it was impossible, when what was missing was forty
/// lines and a decision about which folder.
/// </para>
/// <para>
/// The decision is the important half, and it is why this is a REGISTRY rather than a path in a config file. A source
/// is a grant: naming a folder here makes every file under it readable by anything that can reach the API, including
/// through the tunnel. So the boundary is explicit, it is per-folder, it is read-only, and it is contained — a request
/// that walks upwards out of the root is refused rather than resolved.
/// </para>
/// <para>
/// What it deliberately is NOT: a plugin system. There is no code here to compile or load. A source is a name, a path,
/// and a list of extensions, which is enough for photos, documents, exports and logs — the cases that actually come
/// up — and cannot become an arbitrary program.
/// </para>
/// </summary>
public sealed class LocalSource
{
    /// <summary>Url-safe name. What a panel refers to: <c>internal: source:photos</c>.</summary>
    public string Id { get; set; } = "";

    /// <summary>What the user calls it.</summary>
    public string Name { get; set; } = "";

    /// <summary>The absolute path this source is rooted at. Nothing outside it is reachable.</summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// Extensions that may be served, lower-case with the dot.
    ///
    /// <para>
    /// A second boundary inside the first. Pointing a source at a documents folder to show a photo panel should not
    /// also expose every spreadsheet in it, and the extension list is the cheapest way to say so. Empty means the
    /// default set, which is deliberately narrow rather than everything.
    /// </para>
    /// </summary>
    public List<string> Include { get; set; } = new();

    /// <summary>Whether to look in subfolders. Off by default: a photos folder with an archive under it is common.</summary>
    public bool Recursive { get; set; }

    public DateTimeOffset AddedAt { get; set; }

    /// <summary>
    /// The extensions served when a source doesn't name any.
    ///
    /// <para>
    /// Images, plus the document types a panel might legitimately link to. No executables, no scripts, no archives:
    /// this is a read-only view for a page to render, and a default that serves anything is a default nobody audits.
    /// </para>
    /// </summary>
    public static readonly string[] DefaultInclude =
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif", ".bmp", ".heic",
        ".pdf", ".txt", ".md", ".csv", ".json",
        ".mp4", ".webm", ".mov",
    };

    public IReadOnlyList<string> Extensions => Include.Count > 0 ? Include : DefaultInclude;
}

/// <summary>One file in a source, as a panel sees it.</summary>
public sealed record SourceFile(string Name, string Rel, string Url, long Bytes, DateTimeOffset ModifiedAt);

public sealed class SourceStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json;
    private readonly List<LocalSource> _sources = new();
    private readonly object _lock = new();

    /// <summary>
    /// How many files a listing returns.
    ///
    /// <para>
    /// A photos folder is routinely tens of thousands of files, and a panel showing a rotating picture needs a
    /// handful. Newest first, so a cap is "the recent ones" rather than an arbitrary slice.
    /// </para>
    /// </summary>
    public const int MaxListed = 500;

    public SourceStore(string path, JsonSerializerOptions json)
    {
        _path = path;
        _json = json;
        try
        {
            if (File.Exists(path))
                _sources = JsonSerializer.Deserialize<List<LocalSource>>(File.ReadAllText(path), json) ?? new();
        }
        catch { _sources = new(); }
    }

    public IReadOnlyList<LocalSource> All()
    {
        lock (_lock) return _sources.ToList();
    }

    public LocalSource? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_lock)
            return _sources.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Grant access to a folder.
    /// </summary>
    /// <returns>The source, or the reason it was refused.</returns>
    public (LocalSource? Source, string? Error) Add(string name, string path, IEnumerable<string>? include = null,
        bool recursive = false)
    {
        if (string.IsNullOrWhiteSpace(name)) return (null, "a source needs a name");
        if (string.IsNullOrWhiteSpace(path)) return (null, "a source needs a path");

        string full;
        try { full = System.IO.Path.GetFullPath(path.Trim().Trim('"')); }
        catch (Exception ex) { return (null, $"that isn't a usable path: {ex.Message}"); }

        if (!Directory.Exists(full)) return (null, $"there's no folder at {full}");

        // A drive root, or a home directory, is not a source — it is everything. Refused outright rather than served
        // with a warning, because the failure mode is quiet: it works, and then it keeps working for years.
        var root = System.IO.Path.GetPathRoot(full)?.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        if (string.Equals(full.TrimEnd(System.IO.Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
            return (null, "a whole drive is too much to grant — name the specific folder");

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile is { Length: > 0 } &&
            string.Equals(full.TrimEnd(System.IO.Path.DirectorySeparatorChar),
                profile.TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return (null, "your whole home directory is too much to grant — name the folder inside it");

        lock (_lock)
        {
            var id = Slug(name);
            var source = _sources.FirstOrDefault(s => s.Id == id);
            if (source is null)
            {
                source = new LocalSource { Id = id, AddedAt = DateTimeOffset.UtcNow };
                _sources.Add(source);
            }

            source.Name = name.Trim();
            source.Path = full;
            source.Recursive = recursive;
            source.Include = (include ?? Array.Empty<string>())
                .Select(e => e.Trim().ToLowerInvariant())
                .Select(e => e.StartsWith('.') ? e : "." + e)
                .Where(e => e.Length > 1)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Save();
            return (source, null);
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var s = _sources.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (s is null) return false;
            _sources.Remove(s);
            Save();
            return true;
        }
    }

    /// <summary>
    /// What's in a source, newest first.
    /// </summary>
    public IReadOnlyList<SourceFile> Files(LocalSource source)
    {
        if (!Directory.Exists(source.Path)) return Array.Empty<SourceFile>();

        var allowed = source.Extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            return new DirectoryInfo(source.Path)
                .EnumerateFiles("*", new EnumerationOptions
                {
                    RecurseSubdirectories = source.Recursive,
                    // A folder someone actually uses has thumbnail caches and sync droppings in it, and neither is
                    // something to put on a home page.
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                    IgnoreInaccessible = true,
                })
                .Where(f => allowed.Contains(f.Extension))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxListed)
                .Select(f =>
                {
                    var rel = System.IO.Path.GetRelativePath(source.Path, f.FullName).Replace('\\', '/');
                    return new SourceFile(
                        f.Name,
                        rel,
                        $"/api/sources/{source.Id}/file?path={Uri.EscapeDataString(rel)}",
                        f.Length,
                        new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero));
                })
                .ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[source] {source.Id}: couldn't list {source.Path}: {ex.Message}");
            return Array.Empty<SourceFile>();
        }
    }

    /// <summary>
    /// Resolve one relative path inside a source to a real file, or null.
    ///
    /// <para>
    /// The containment check is the whole security model, so it is done the boring, established way this codebase
    /// already uses elsewhere: make both absolute, and require the resolved path to sit under the root. A path with
    /// <c>..</c> in it resolves upwards and then fails the prefix test, which is the point.
    /// </para>
    /// </summary>
    public string? Resolve(LocalSource source, string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return null;

        string root, full;
        try
        {
            root = System.IO.Path.GetFullPath(source.Path);
            full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, rel.Replace('\\', '/').TrimStart('/')));
        }
        catch { return null; }

        if (!full.StartsWith(root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return null;

        // The extension list applies on the way out as well as in the listing: a source that lists only images must
        // not serve a spreadsheet to anyone who guesses its name.
        if (!source.Extensions.Contains(System.IO.Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
            return null;

        return File.Exists(full) ? full : null;
    }

    /// <summary>What the model can see: the sources that exist and what a panel would say to use one.</summary>
    public string Describe()
    {
        lock (_lock)
        {
            // Said even when there are none — ESPECIALLY when there are none.
            //
            // Returning nothing here was a hole with a predictable shape: asked for a photo panel with no folder
            // granted, the builder had no idea this mechanism existed, so it improvised. It wrote a photo_server.py,
            // pointed the panel at http://localhost:8765/photos.json, and had no shell to start it with — so the
            // panel fetched a dead port, the visual check called it broken, and it rebuilt itself seven times.
            // A capability nobody is told about is a capability that gets reinvented, badly.
            if (_sources.Count == 0)
                return "\n\nLocal files: no folder has been granted yet, so there is currently NO way to read the " +
                       "user's files — a page cannot read a filesystem, and you cannot run a server (you have no " +
                       "shell). Do NOT write one, do not look for a local port that might be serving one, and do not " +
                       "point a panel at a url you cannot prove answers. Granting a folder is the USER's action: ask " +
                       "them which folder, and say that once they grant it a panel reads it with an `internal` " +
                       "loader named `source:<id>`.\n";

            var lines = _sources.Select(s =>
                $"- source:{s.Id} (\"{s.Name}\") — {s.Extensions.Count} file type(s)" +
                (s.Recursive ? ", including subfolders" : ""));
            return "\n\nLocal sources this system has been given access to. A panel reads one with an `internal` " +
                   "loader named `source:<id>`, which returns {files:[{name, rel, url, bytes, modifiedAt}]} newest " +
                   "first. `url` is on this origin and serves the file itself, so it goes straight into an <img>, a " +
                   "<video> or a link:\n" + string.Join("\n", lines) +
                   "\nIf a panel needs a folder that isn't listed here, the user has to grant it — say so and ask; " +
                   "do not go looking around the filesystem for it.\n";
        }
    }

    private static string Slug(string name)
    {
        var slug = new string(name.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-') is { Length: > 0 } s ? s : "source";
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_sources, _json));
        }
        catch (Exception ex) { Console.Error.WriteLine($"[source] couldn't save: {ex.Message}"); }
    }
}
