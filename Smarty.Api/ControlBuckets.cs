namespace Smarty.Api;

/// <summary>One file in a bucket, for listing.</summary>
public sealed record BucketFile(string Name, long Size, DateTimeOffset Modified);

/// <summary>A read-only reference bucket workers can draw on: the global company area, a persona's kit, or a
/// brand kit. Files are dropped in here (from the control centre or by hand) and mounted read-only into tasks.</summary>
public sealed record BucketInfo(string Kind, string Id, string Label, IReadOnlyList<BucketFile> Files);

/// <summary>
/// Lists and accepts uploads into the reference buckets under <c>&lt;workspaceRoot&gt;/_buckets</c> — the
/// <c>global</c> area, per-<c>persona</c> kits, and per-<c>brand</c> kits. All paths are sandboxed under the
/// buckets root; ids and file names are sanitised so an upload can never escape it.
/// </summary>
public sealed class ControlBuckets
{
    private readonly string _bucketsRoot;
    private readonly PersonaStore _personas;

    public ControlBuckets(string workspaceRoot, PersonaStore personas)
    {
        _bucketsRoot = Path.Combine(workspaceRoot, "_buckets");
        _personas = personas;
    }

    public IReadOnlyList<BucketInfo> List()
    {
        var buckets = new List<BucketInfo>
        {
            Read("global", "", "Shared company files"),
        };

        // One bucket per persona, so its kit has an obvious home (even before any file is dropped in).
        foreach (var p in _personas.All)
            buckets.Add(Read("persona", p.Id, $"{p.Name} kit"));

        // Brand kits: the house brand always, plus any client brand folders that exist on disk.
        var brands = new List<string> { "house" };
        var brandRoot = Path.Combine(_bucketsRoot, "brand");
        if (Directory.Exists(brandRoot))
            foreach (var dir in Directory.GetDirectories(brandRoot))
            {
                var slug = Path.GetFileName(dir);
                if (!brands.Contains(slug, StringComparer.OrdinalIgnoreCase)) brands.Add(slug);
            }
        foreach (var slug in brands)
            buckets.Add(Read("brand", slug, slug == "house" ? "House brand" : slug));

        return buckets;
    }

    /// <summary>Resolve the on-disk directory for a bucket, creating it. Returns null if kind/id is invalid
    /// or would escape the buckets root.</summary>
    public string? ResolveDir(string kind, string id)
    {
        string? rel = kind.ToLowerInvariant() switch
        {
            "global" => "global",
            "persona" when IsSafeId(id) => Path.Combine("persona", id),
            "brand" when IsSafeId(id) => Path.Combine("brand", id),
            _ => null,
        };
        if (rel is null) return null;

        string full = Path.GetFullPath(Path.Combine(_bucketsRoot, rel));
        string root = Path.GetFullPath(_bucketsRoot);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null; // defence-in-depth
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Save an uploaded stream into a bucket under a sanitised file name. Returns the stored file's
    /// info, or null if the bucket is invalid.</summary>
    public async Task<BucketFile?> SaveAsync(string kind, string id, string fileName, Stream content, CancellationToken ct)
    {
        var dir = ResolveDir(kind, id);
        if (dir is null) return null;

        string safe = SafeFileName(fileName);
        string dest = Path.Combine(dir, safe);
        await using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
            await content.CopyToAsync(fs, ct).ConfigureAwait(false);

        var info = new FileInfo(dest);
        return new BucketFile(safe, info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>Resolve a file path inside a bucket for download. Null if the bucket/file is invalid or absent.</summary>
    public string? ResolveFile(string kind, string id, string fileName)
    {
        var dir = ResolveDir(kind, id);
        if (dir is null) return null;
        string full = Path.GetFullPath(Path.Combine(dir, SafeFileName(fileName)));
        if (!full.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(full) ? full : null;
    }

    /// <summary>Delete a file from a bucket. Returns true if it existed and was removed.</summary>
    public bool DeleteFile(string kind, string id, string fileName)
    {
        var full = ResolveFile(kind, id, fileName);
        if (full is null) return false;
        try { File.Delete(full); return true; } catch { return false; }
    }

    private BucketInfo Read(string kind, string id, string label)
    {
        var files = new List<BucketFile>();
        var dir = kind == "global"
            ? Path.Combine(_bucketsRoot, "global")
            : Path.Combine(_bucketsRoot, kind, id);
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var fi = new FileInfo(f);
                files.Add(new BucketFile(Path.GetRelativePath(dir, f).Replace('\\', '/'), fi.Length, fi.LastWriteTimeUtc));
            }
        return new BucketInfo(kind, id, label, files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static bool IsSafeId(string id) =>
        !string.IsNullOrWhiteSpace(id) && id.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');

    private static string SafeFileName(string name)
    {
        name = Path.GetFileName(name ?? "");
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim();
        return name.Length == 0 ? "upload.bin" : name;
    }
}
