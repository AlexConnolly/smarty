using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// Jira capability (Atlassian Cloud REST v3) — makes the product_manager persona real: search and read issues,
/// and create a ticket when asked. Tools: <c>jira_search</c>, <c>jira_get_issue</c>, <c>jira_create_issue</c>.
/// Config (under <c>jira.*</c>): <c>base_url</c> (e.g. https://acme.atlassian.net), <c>email</c>, <c>api_token</c>
/// (all required for auth); <c>project</c> (optional default project key). Credentials are read here and never
/// shown to the model. Reads are free; creating a ticket is a real, deliberate write — the tool says so.
/// </summary>
public sealed class JiraCapability : ICapability
{
    public string Id => "jira";
    public string DisplayName => "Jira (issues)";
    public IReadOnlyList<string> RequiredConfig => new[] { "base_url", "email", "api_token" };

    public string? PromptHint =>
        "Use Jira to search/read issues (jira_search, jira_get_issue) and, when explicitly asked, create a " +
        "ticket (jira_create_issue). Ground statements about work in real issues — quote keys and statuses. " +
        "Only create a ticket when the user actually wants one; confirm the summary first if it's ambiguous.";

    public IReadOnlyList<AgentTool> BuildTools(IntegrationConfig config, TaskInfo task)
    {
        var baseUrl = config.Get(Id, "base_url");
        var email = config.Get(Id, "email");
        var token = config.Get(Id, "api_token");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
            return Array.Empty<AgentTool>(); // not fully configured → contributes nothing

        var client = new JiraClient(baseUrl!, email!, token!);
        var defaultProject = config.Get(Id, "project");

        return new[]
        {
            new AgentTool(
                "jira_search",
                "Search Jira issues. Pass `jql` for a precise query (e.g. \"project = CHK AND status = Open " +
                "ORDER BY updated DESC\"), or `text` for a free-text search across summary/description. Returns " +
                "matching issues (key, status, summary).",
                new[]
                {
                    ToolParameter.String("text", "Free-text to search for (used if jql is omitted).", required: false),
                    ToolParameter.String("jql", "A precise JQL query (overrides text).", required: false),
                },
                async (a, ct) =>
                {
                    var jql = a.GetStringOrNull("jql");
                    if (string.IsNullOrWhiteSpace(jql))
                    {
                        var text = a.GetStringOrNull("text");
                        if (string.IsNullOrWhiteSpace(text)) return ToolOutput.Error("Provide either jql or text to search.");
                        var proj = string.IsNullOrWhiteSpace(defaultProject) ? "" : $"project = {defaultProject} AND ";
                        jql = $"{proj}text ~ \"{text!.Replace("\"", "\\\"")}\" ORDER BY updated DESC";
                    }
                    try { return ToolOutput.Ok(await client.SearchAsync(jql!, ct)); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { return ToolOutput.Error($"jira_search failed: {ex.Message}"); }
                }),

            new AgentTool(
                "jira_get_issue",
                "Get one Jira issue by its key (e.g. CHK-1234): summary, status, type, assignee and description.",
                new[] { ToolParameter.String("key", "The issue key, e.g. CHK-1234.", required: true) },
                async (a, ct) =>
                {
                    try { return ToolOutput.Ok(await client.GetIssueAsync(a.GetString("key").Trim(), ct)); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { return ToolOutput.DeadEnd($"jira_get_issue failed: {ex.Message}"); }
                }),

            new AgentTool(
                "jira_create_issue",
                "Create a NEW Jira ticket (a real write). Provide a clear summary and description; optionally an " +
                "issue type (default Task) and project key (default from config). Use only when the user actually " +
                "wants a ticket created.",
                new[]
                {
                    ToolParameter.String("summary", "Short one-line summary/title of the ticket.", required: true),
                    ToolParameter.String("description", "The ticket body — context, repro, acceptance.", required: false),
                    ToolParameter.String("type", "Issue type (e.g. Task, Bug, Story). Defaults to Task.", required: false),
                    ToolParameter.String("project", "Project key. Omit to use the configured default.", required: false),
                },
                async (a, ct) =>
                {
                    var project = a.GetStringOrNull("project")?.Trim() is { Length: > 0 } p ? p : defaultProject;
                    if (string.IsNullOrWhiteSpace(project))
                        return ToolOutput.Error("No project key — pass project, or set jira.project in config.");
                    try
                    {
                        return ToolOutput.Ok(await client.CreateIssueAsync(
                            project!, a.GetString("summary").Trim(), a.GetStringOrNull("description") ?? "",
                            a.GetStringOrNull("type")?.Trim() is { Length: > 0 } t ? t : "Task", ct));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { return ToolOutput.Error($"jira_create_issue failed: {ex.Message}"); }
                }),
        };
    }
}

/// <summary>A thin Atlassian Cloud (Jira REST v3) client: basic auth with email + API token. Reads issues,
/// runs JQL, and creates tickets. Descriptions are Atlassian Document Format (ADF) — rendered to/from plain
/// text best-effort.</summary>
public sealed class JiraClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _baseUrl;
    private readonly string _auth;

    public JiraClient(string baseUrl, string email, string apiToken)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{apiToken}"));
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, _baseUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", _auth);
        req.Headers.Accept.ParseAdd("application/json");
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Jira {(int)resp.StatusCode}: {Trim(json, 240)}");
        return string.IsNullOrWhiteSpace(json) ? default : JsonDocument.Parse(json).RootElement.Clone();
    }

    public async Task<string> SearchAsync(string jql, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["jql"] = jql,
            ["maxResults"] = 20,
            ["fields"] = new JsonArray("summary", "status", "issuetype", "priority", "assignee"),
        };
        var root = await SendAsync(HttpMethod.Post, "/rest/api/3/search/jql", body, ct).ConfigureAwait(false);
        if (!root.TryGetProperty("issues", out var issues) || issues.GetArrayLength() == 0)
            return $"No issues match: {jql}";
        var sb = new StringBuilder($"{issues.GetArrayLength()} issue(s) for: {jql}\n");
        foreach (var it in issues.EnumerateArray())
        {
            string key = Str(it, "key");
            var f = it.TryGetProperty("fields", out var ff) ? ff : default;
            string status = f.ValueKind == JsonValueKind.Object && f.TryGetProperty("status", out var st) ? Str(st, "name") : "";
            string summary = f.ValueKind == JsonValueKind.Object ? Str(f, "summary") : "";
            sb.Append($"- {key} [{status}]: {summary}\n");
        }
        return sb.ToString().TrimEnd();
    }

    public async Task<string> GetIssueAsync(string key, CancellationToken ct)
    {
        var root = await SendAsync(HttpMethod.Get,
            $"/rest/api/3/issue/{Uri.EscapeDataString(key)}?fields=summary,status,issuetype,priority,assignee,description", null, ct)
            .ConfigureAwait(false);
        var f = root.TryGetProperty("fields", out var ff) ? ff : default;
        string Get(string p) => f.ValueKind == JsonValueKind.Object && f.TryGetProperty(p, out var v) && v.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        string assignee = f.ValueKind == JsonValueKind.Object && f.TryGetProperty("assignee", out var asg) && asg.ValueKind == JsonValueKind.Object ? Str(asg, "displayName") : "Unassigned";
        string desc = f.ValueKind == JsonValueKind.Object && f.TryGetProperty("description", out var d) ? RenderAdf(d) : "";

        var sb = new StringBuilder($"{Str(root, "key")}: {(f.ValueKind == JsonValueKind.Object ? Str(f, "summary") : "")}\n");
        sb.Append($"Type: {Get("issuetype")} | Status: {Get("status")} | Priority: {Get("priority")} | Assignee: {assignee}\n");
        if (!string.IsNullOrWhiteSpace(desc)) sb.Append('\n').Append(Trim(desc, 1500));
        return sb.ToString().TrimEnd();
    }

    public async Task<string> CreateIssueAsync(string project, string summary, string description, string type, CancellationToken ct)
    {
        var fields = new JsonObject
        {
            ["project"] = new JsonObject { ["key"] = project },
            ["summary"] = summary,
            ["issuetype"] = new JsonObject { ["name"] = type },
        };
        if (!string.IsNullOrWhiteSpace(description)) fields["description"] = BuildAdf(description);
        var root = await SendAsync(HttpMethod.Post, "/rest/api/3/issue", new JsonObject { ["fields"] = fields }, ct).ConfigureAwait(false);
        string key = Str(root, "key");
        return key.Length > 0
            ? $"Created {key}: {summary}\n{_baseUrl}/browse/{key}"
            : "Jira reported success but returned no issue key.";
    }

    // Minimal ADF document wrapping a plain-text body (one paragraph per line).
    private static JsonObject BuildAdf(string text)
    {
        var content = new JsonArray();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            content.Add(new JsonObject
            {
                ["type"] = "paragraph",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = line.Length == 0 ? " " : line }),
            });
        return new JsonObject { ["type"] = "doc", ["version"] = 1, ["content"] = content };
    }

    // Walk an ADF node tree and concatenate its text nodes — good enough to show a description as plain text.
    private static string RenderAdf(JsonElement node)
    {
        var sb = new StringBuilder();
        void Walk(JsonElement n)
        {
            if (n.ValueKind == JsonValueKind.Object)
            {
                if (n.TryGetProperty("type", out var t) && t.GetString() == "text" && n.TryGetProperty("text", out var tx))
                    sb.Append(tx.GetString());
                if (n.TryGetProperty("type", out var t2) && (t2.GetString() == "paragraph") && sb.Length > 0)
                    sb.Append('\n');
                if (n.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
                    foreach (var ch in c.EnumerateArray()) Walk(ch);
            }
        }
        Walk(node);
        return sb.ToString().Trim();
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static string Trim(string s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
