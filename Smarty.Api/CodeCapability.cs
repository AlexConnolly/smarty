using System.Text;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// The code capability — READ-ONLY access to a checked-out repository, so a worker can ground a proposed fix
/// in the ACTUAL source instead of inferring from logs alone. Three tools, all sandboxed under a single
/// configured root (no path can escape it): <c>code_tree</c> (browse), <c>code_search</c> (grep for a symbol
/// or string), <c>code_read</c> (read a file window). It deliberately CANNOT modify the repo — a fix is
/// proposed by writing a patch to the conversation's file area (write_file/send_file), never applied here.
/// Config key (under <c>code.*</c>): <c>root</c> (required) — absolute path to the repo.
/// </summary>
public sealed class CodeCapability : ICapability
{
    public string Id => "code";
    public string DisplayName => "Code (read-only repository access)";
    public IReadOnlyList<string> RequiredConfig => new[] { "root" };

    public string? PromptHint =>
        "Read the codebase with code_tree (browse files), code_search (find a symbol/string) and code_read " +
        "(read a file). Ground any proposed fix in the ACTUAL code — name the file and method and show the " +
        "change. It's READ-ONLY: you can't modify the repo, but you can write a proposed patch with write_file " +
        "and send it with send_file. Propose the fix; never claim you've applied it.";

    // Directories and files that are never worth searching/listing in a source tree.
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", ".idea", "bin", "obj", "node_modules", "dist", "build", "packages", ".next", "target" };
    private const long MaxFileBytes = 512 * 1024;

    public IReadOnlyList<AgentTool> BuildTools(IntegrationConfig config, TaskInfo task)
    {
        var root = config.Get(Id, "root");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Array.Empty<AgentTool>(); // not configured / missing → contributes nothing

        string rootFull = Path.GetFullPath(root!);

        return new[]
        {
            new AgentTool(
                "code_tree",
                "List source files in the repository (relative paths), optionally under a sub-path. Use it to " +
                "get the lay of the land before reading or searching.",
                new[] { ToolParameter.String("path", "Optional sub-directory to list under. Omit for the whole repo.", required: false) },
                (args, _) => Task.FromResult(Tree(rootFull, args.GetStringOrNull("path")))),

            new AgentTool(
                "code_search",
                "Search the repository for a string (case-insensitive substring) and return matching " +
                "file:line: snippet results. Use it to locate a symbol, method, message string or config " +
                "(e.g. \"ProcessPayment\", \"payment-gateway\").",
                new[]
                {
                    ToolParameter.String("query", "Text to search for (case-insensitive).", required: true),
                    ToolParameter.String("glob", "Optional file-name filter, e.g. \"*.cs\". Omit to search all text files.", required: false),
                },
                (args, _) => Task.FromResult(Search(rootFull, args.GetString("query"), args.GetStringOrNull("glob")))),

            new AgentTool(
                "code_read",
                "Read a file from the repository (by its relative path) and return a window of it. Use " +
                "offset/limit to page through a large file.",
                new[]
                {
                    ToolParameter.String("path", "Repo-relative path of the file to read (as shown by code_tree/code_search).", required: true),
                    ToolParameter.Integer("offset", "Line number to start from (1-based). Defaults to 1.", required: false),
                    ToolParameter.Integer("limit", "How many lines to return. Defaults to 200.", required: false),
                },
                (args, _) => Task.FromResult(Read(rootFull, args.GetString("path"), args.GetInt("offset", 1), args.GetInt("limit", 200)))),
        };
    }

    // Resolve a repo-relative path to a full path that MUST stay under root — the read-only sandbox boundary.
    private static string? Resolve(string rootFull, string? rel)
    {
        var full = Path.GetFullPath(Path.Combine(rootFull, (rel ?? "").Replace('\\', '/').TrimStart('/')));
        if (string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase)) return full;
        return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static bool InSkippedDir(string rootFull, string fullPath)
    {
        var rel = Path.GetRelativePath(rootFull, fullPath);
        return rel.Split('/', '\\').Any(seg => SkipDirs.Contains(seg));
    }

    private static IEnumerable<string> Files(string rootFull, string? glob = null)
    {
        IEnumerable<string> all;
        try { all = Directory.EnumerateFiles(rootFull, string.IsNullOrWhiteSpace(glob) ? "*" : glob!, SearchOption.AllDirectories); }
        catch { yield break; }
        foreach (var f in all)
            if (!InSkippedDir(rootFull, f)) yield return f;
    }

    private static bool LooksBinary(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[Math.Min(4096, fs.Length)];
            int n = fs.Read(buf, 0, buf.Length);
            for (int i = 0; i < n; i++) if (buf[i] == 0) return true;
            return false;
        }
        catch { return true; }
    }

    private static ToolOutput Tree(string rootFull, string? sub)
    {
        var start = Resolve(rootFull, sub);
        if (start is null) return ToolOutput.DeadEnd("That path is outside the repository.");
        if (!Directory.Exists(start)) return ToolOutput.DeadEnd($"There's no directory '{sub}' in the repo.");

        var rels = Files(start).Take(300)
            .Select(f => Path.GetRelativePath(rootFull, f).Replace('\\', '/'))
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (rels.Count == 0) return ToolOutput.Ok("No source files found.");
        var sb = new StringBuilder($"{rels.Count} file(s):\n");
        foreach (var r in rels) sb.Append("- ").Append(r).Append('\n');
        return ToolOutput.Ok(sb.ToString().TrimEnd());
    }

    private static ToolOutput Search(string rootFull, string query, string? glob)
    {
        query = query.Trim();
        if (query.Length == 0) return ToolOutput.Error("A search query is required.");

        var hits = new List<string>();
        int filesHit = 0;
        const int MaxHits = 60;
        foreach (var file in Files(rootFull, glob))
        {
            if (hits.Count >= MaxHits) break;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxFileBytes || LooksBinary(file)) continue;
                var rel = Path.GetRelativePath(rootFull, file).Replace('\\', '/');
                int line = 0; bool any = false;
                foreach (var text in File.ReadLines(file))
                {
                    line++;
                    if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        var snip = text.Trim();
                        if (snip.Length > 200) snip = snip[..200] + "…";
                        hits.Add($"{rel}:{line}: {snip}");
                        any = true;
                        if (hits.Count >= MaxHits) break;
                    }
                }
                if (any) filesHit++;
            }
            catch { /* skip unreadable file */ }
        }

        if (hits.Count == 0) return ToolOutput.DeadEnd($"No matches for '{query}'.");
        var sb = new StringBuilder($"{hits.Count} match(es) across {filesHit} file(s) for '{query}'" +
                                   (hits.Count >= MaxHits ? " (truncated)" : "") + ":\n");
        foreach (var h in hits) sb.Append(h).Append('\n');
        sb.Append("Read a file with code_read to see the full context.");
        return ToolOutput.Ok(sb.ToString().TrimEnd());
    }

    private static ToolOutput Read(string rootFull, string relPath, int offset, int limit)
    {
        var full = Resolve(rootFull, relPath);
        if (full is null) return ToolOutput.DeadEnd("That path is outside the repository.");
        if (!File.Exists(full)) return ToolOutput.DeadEnd($"There's no file '{relPath}' in the repo.");
        try
        {
            if (new FileInfo(full).Length > MaxFileBytes || LooksBinary(full))
                return ToolOutput.DeadEnd($"'{relPath}' isn't a readable text file.");
            var lines = File.ReadAllLines(full);
            offset = Math.Clamp(offset, 1, Math.Max(1, lines.Length));
            limit = Math.Clamp(limit, 1, 800);
            int start = offset - 1;
            int take = Math.Min(limit, lines.Length - start);
            var sb = new StringBuilder($"{relPath} — lines {offset}–{offset + take - 1} of {lines.Length}");
            if (offset + take - 1 < lines.Length)
                sb.Append($" (more below; code_read again with offset={offset + take})");
            sb.Append("\n\n");
            for (int i = start; i < start + take; i++)
                sb.Append(i + 1).Append('\t').Append(lines[i]).Append('\n');
            return ToolOutput.Ok(sb.ToString().TrimEnd());
        }
        catch (Exception ex) { return ToolOutput.Error($"Couldn't read '{relPath}': {ex.Message}"); }
    }
}
