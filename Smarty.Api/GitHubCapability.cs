using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// GitHub capability — READ-ONLY access to a real repository over the GitHub REST API, so the engineer can
/// ground a diagnosis/fix in the actual remote source (the local <see cref="CodeCapability"/>'s sibling for
/// checked-out trees). Tools: <c>github_list</c>, <c>github_read_file</c>, <c>github_search_code</c>,
/// <c>github_commits</c>. Like the code capability it cannot write — opening a PR is a separate, gated step.
/// Config (under <c>github.*</c>): <c>token</c> (optional — public repos work unauthenticated, private/search
/// need a PAT), <c>repo</c> (optional default "owner/name" so tools needn't be given one each time).
/// </summary>
public sealed class GitHubCapability : ICapability
{
    public string Id => "github";
    public string DisplayName => "GitHub (read-only repository access)";
    public IReadOnlyList<string> RequiredConfig => Array.Empty<string>(); // usable unauthenticated for public repos

    public string? PromptHint =>
        "Read a GitHub repo with github_list (browse), github_read_file (read a file), github_search_code " +
        "(find a symbol/string) and github_commits (recent history). Ground proposed fixes in the ACTUAL code " +
        "— name the file and lines. It's READ-ONLY: propose a patch with write_file (it's delivered to the user " +
        "automatically), never claim to have pushed or opened a PR.";

    public IReadOnlyList<AgentTool> BuildTools(IntegrationConfig config, TaskInfo task)
    {
        var defaultRepo = config.Get(Id, "repo");
        var client = new GitHubClient(config.Get(Id, "token"));

        // Every tool resolves its repo from an explicit arg or the configured default; without either it can't run.
        string? Repo(ToolCallArguments a) => a.GetStringOrNull("repo")?.Trim() is { Length: > 0 } r ? r : defaultRepo;
        ToolOutput NoRepo() => ToolOutput.Error("No repository given. Pass repo as \"owner/name\", or set github.repo in config.");

        return new[]
        {
            new AgentTool(
                "github_list",
                "List the contents of a path in a GitHub repo (files and sub-directories). Omit path for the root.",
                new[]
                {
                    ToolParameter.String("path", "Repo path to list. Omit for the repository root.", required: false),
                    ToolParameter.String("repo", "Repository as \"owner/name\". Omit to use the configured default.", required: false),
                    ToolParameter.String("ref", "Branch, tag or commit SHA. Omit for the default branch.", required: false),
                },
                async (a, ct) =>
                {
                    var repo = Repo(a); if (repo is null) return NoRepo();
                    try { return ToolOutput.Ok(await client.ListAsync(repo, a.GetStringOrNull("path") ?? "", a.GetStringOrNull("ref"), ct)); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { return ToolOutput.Error($"github_list failed: {ex.Message}"); }
                }),

            new AgentTool(
                "github_read_file",
                "Read a file from a GitHub repo by its path, returning a window of its text. Use offset/limit " +
                "(line numbers) to page through a large file.",
                new[]
                {
                    ToolParameter.String("path", "Repo-relative path of the file to read.", required: true),
                    ToolParameter.String("repo", "Repository as \"owner/name\". Omit to use the configured default.", required: false),
                    ToolParameter.String("ref", "Branch, tag or commit SHA. Omit for the default branch.", required: false),
                    ToolParameter.Integer("offset", "Line to start from (1-based). Defaults to 1.", required: false),
                    ToolParameter.Integer("limit", "How many lines to return. Defaults to 200.", required: false),
                },
                async (a, ct) =>
                {
                    var repo = Repo(a); if (repo is null) return NoRepo();
                    try { return ToolOutput.Ok(await client.ReadFileAsync(repo, a.GetString("path"), a.GetStringOrNull("ref"), a.GetInt("offset", 1), a.GetInt("limit", 200), ct)); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { return ToolOutput.DeadEnd($"github_read_file failed: {ex.Message}"); }
                }),

            new AgentTool(
                "github_search_code",
                "Search code in a GitHub repo for a string/symbol (needs a configured token). Returns matching " +
                "file paths to then read with github_read_file.",
                new[]
                {
                    ToolParameter.String("query", "Text/symbol to search for, e.g. \"ProcessPayment\".", required: true),
                    ToolParameter.String("repo", "Repository as \"owner/name\". Omit to use the configured default.", required: false),
                },
                async (a, ct) =>
                {
                    var repo = Repo(a); if (repo is null) return NoRepo();
                    try { return ToolOutput.Ok(await client.SearchCodeAsync(repo, a.GetString("query"), ct)); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { return ToolOutput.Error($"github_search_code failed: {ex.Message}"); }
                }),

            new AgentTool(
                "github_commits",
                "List recent commits on a GitHub repo (optionally only those touching a given path) — useful for " +
                "\"what changed lately\" around a regression.",
                new[]
                {
                    ToolParameter.String("path", "Only commits touching this path. Omit for all recent commits.", required: false),
                    ToolParameter.String("repo", "Repository as \"owner/name\". Omit to use the configured default.", required: false),
                },
                async (a, ct) =>
                {
                    var repo = Repo(a); if (repo is null) return NoRepo();
                    try { return ToolOutput.Ok(await client.RecentCommitsAsync(repo, a.GetStringOrNull("path"), ct)); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { return ToolOutput.Error($"github_commits failed: {ex.Message}"); }
                }),
        };
    }
}

/// <summary>A thin read-only GitHub REST client (HttpClient + System.Text.Json). Token optional: public repos
/// work unauthenticated (rate-limited); a PAT raises limits and is required for private repos + code search.</summary>
public sealed class GitHubClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30), BaseAddress = new Uri("https://api.github.com") };
    private readonly string? _token;

    public GitHubClient(string? token) => _token = string.IsNullOrWhiteSpace(token) ? null : token;

    private async Task<JsonElement> GetAsync(string url, string? accept, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd(accept ?? "application/vnd.github+json");
        req.Headers.UserAgent.ParseAdd("smarty-agent");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (_token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string msg = TryMessage(json) ?? Trim(json, 200);
            throw new InvalidOperationException($"GitHub {(int)resp.StatusCode}: {msg}");
        }
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public async Task<string> ListAsync(string repo, string path, string? @ref, CancellationToken ct)
    {
        var root = await GetAsync(ContentsUrl(repo, path, @ref), null, ct).ConfigureAwait(false);
        if (root.ValueKind != JsonValueKind.Array)
            return $"'{path}' is a file, not a directory — read it with github_read_file.";
        var sb = new StringBuilder($"{repo}/{path.TrimEnd('/')} :\n");
        foreach (var item in root.EnumerateArray())
        {
            string name = Str(item, "name");
            string type = Str(item, "type"); // "file" | "dir"
            sb.Append(type == "dir" ? "- 📁 " : "- ").Append(name).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    public async Task<string> ReadFileAsync(string repo, string path, string? @ref, int offset, int limit, CancellationToken ct)
    {
        var root = await GetAsync(ContentsUrl(repo, path, @ref), null, ct).ConfigureAwait(false);
        if (root.ValueKind == JsonValueKind.Array) throw new InvalidOperationException($"'{path}' is a directory — use github_list.");
        if (!string.Equals(Str(root, "encoding"), "base64", StringComparison.OrdinalIgnoreCase) || Str(root, "content").Length == 0)
            throw new InvalidOperationException($"'{path}' has no readable text content (too large, or a binary file).");

        var text = Encoding.UTF8.GetString(Convert.FromBase64String(Str(root, "content").Replace("\n", "")));
        var lines = text.Replace("\r\n", "\n").Split('\n');
        offset = Math.Clamp(offset, 1, Math.Max(1, lines.Length));
        limit = Math.Clamp(limit, 1, 800);
        int start = offset - 1, take = Math.Min(limit, lines.Length - start);
        var sb = new StringBuilder($"{repo}/{path} — lines {offset}–{offset + take - 1} of {lines.Length}");
        if (offset + take - 1 < lines.Length) sb.Append($" (more below; github_read_file again with offset={offset + take})");
        sb.Append("\n\n");
        for (int i = start; i < start + take; i++) sb.Append(i + 1).Append('\t').Append(lines[i]).Append('\n');
        return sb.ToString().TrimEnd();
    }

    public async Task<string> SearchCodeAsync(string repo, string query, CancellationToken ct)
    {
        if (_token is null) return "Code search needs a configured github.token (the search API requires authentication). " +
                                   "Use github_list / github_read_file to browse instead.";
        var url = $"/search/code?q={Uri.EscapeDataString($"{query} repo:{repo}")}&per_page=20";
        var root = await GetAsync(url, "application/vnd.github.text-match+json", ct).ConfigureAwait(false);
        if (!root.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
            return $"No code matches for '{query}' in {repo}.";
        var sb = new StringBuilder($"{Num(root, "total_count")} match(es) for '{query}' in {repo} (showing {items.GetArrayLength()}):\n");
        foreach (var it in items.EnumerateArray()) sb.Append("- ").Append(Str(it, "path")).Append('\n');
        sb.Append("Read a file with github_read_file.");
        return sb.ToString().TrimEnd();
    }

    public async Task<string> RecentCommitsAsync(string repo, string? path, CancellationToken ct)
    {
        var url = $"/repos/{repo}/commits?per_page=10" + (string.IsNullOrWhiteSpace(path) ? "" : $"&path={Uri.EscapeDataString(path!)}");
        var root = await GetAsync(url, null, ct).ConfigureAwait(false);
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            return $"No recent commits found in {repo}{(string.IsNullOrWhiteSpace(path) ? "" : $" for {path}")}.";
        var sb = new StringBuilder($"Recent commits in {repo}{(string.IsNullOrWhiteSpace(path) ? "" : $" touching {path}")}:\n");
        foreach (var c in root.EnumerateArray())
        {
            string sha = Str(c, "sha"); sha = sha.Length >= 7 ? sha[..7] : sha;
            var commit = c.TryGetProperty("commit", out var cm) ? cm : default;
            string author = commit.ValueKind == JsonValueKind.Object && commit.TryGetProperty("author", out var au) ? Str(au, "name") : "";
            string date = commit.ValueKind == JsonValueKind.Object && commit.TryGetProperty("author", out var au2) ? Str(au2, "date") : "";
            string msg = commit.ValueKind == JsonValueKind.Object ? Str(commit, "message").Split('\n')[0] : "";
            sb.Append($"- {sha} {date} {author}: {Trim(msg, 100)}\n");
        }
        return sb.ToString().TrimEnd();
    }

    private static string ContentsUrl(string repo, string path, string? @ref) =>
        $"/repos/{repo}/contents/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}"
        + (string.IsNullOrWhiteSpace(@ref) ? "" : $"?ref={Uri.EscapeDataString(@ref!)}");

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
    private static string? TryMessage(string json)
    {
        try { return JsonDocument.Parse(json).RootElement.TryGetProperty("message", out var m) ? m.GetString() : null; }
        catch { return null; }
    }
    private static string Trim(string s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
