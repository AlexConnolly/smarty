using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// The Kibana / Elasticsearch logs capability — read-only. Gives a worker two tools, <c>log_search</c> (recent
/// matching log lines) and <c>log_summary</c> (top groups in a window), so a "why are we getting all these
/// exceptions" task is answered from REAL log evidence rather than a guess. Hand-rolled HttpClient against the
/// ES <c>_search</c> API (Kibana sits on ES), honouring the repo's no-deps ethos. Config keys (under
/// <c>kibana.*</c>): <c>base_url</c>, <c>api_key</c> (both required); optional <c>index</c> (default <c>*</c>),
/// <c>time_field</c> (default <c>@timestamp</c>), <c>group_field</c> (default <c>log.level</c>).
/// </summary>
public sealed class KibanaCapability : ICapability
{
    public string Id => "kibana";
    public string DisplayName => "Kibana / Elasticsearch logs";
    // api_key is optional — an unauthenticated cluster (local/dev, security disabled) needs only a base_url.
    public IReadOnlyList<string> RequiredConfig => new[] { "base_url" };

    public string? PromptHint =>
        "Query application logs with log_search (recent matching lines) and log_summary (top error groups in a " +
        "window). Diagnose from what the logs actually show — quote concrete error types, counts and times; " +
        "never invent a stack trace or a cause the logs don't support.";

    public IReadOnlyList<AgentTool> BuildTools(IntegrationConfig config, TaskInfo task)
    {
        var baseUrl = config.Get(Id, "base_url");
        if (string.IsNullOrWhiteSpace(baseUrl))
            return Array.Empty<AgentTool>(); // not configured → contributes nothing (persona still runs)

        var levelField = config.Get(Id, "group_field") is { Length: > 0 } gf ? gf : "log.level";
        var client = new KibanaClient(
            baseUrl!, config.Get(Id, "api_key"), config.Get(Id, "index"),
            config.Get(Id, "time_field"), config.Get(Id, "group_field"));

        return new[]
        {
            new AgentTool(
                "log_search",
                "Search application logs (Kibana/Elasticsearch) over a recent time window. Pass a Lucene " +
                "query_string: a bare word matches across all fields (e.g. \"NullReferenceException\"); to " +
                $"filter by an exact field use field:value with the REAL field name — the level field here is " +
                $"\"{levelField}\" (e.g. \"{levelField}:ERROR\"). Run log_summary first if unsure of field " +
                "names. Leave the query empty to match everything. Returns matching log lines, newest first.",
                new[]
                {
                    ToolParameter.String("query", "Lucene query_string, e.g. \"level:ERROR AND timeout\". Empty matches all.", required: false),
                    ToolParameter.String("window", "How far back from now: \"15m\", \"1h\", \"24h\". Defaults to 1h.", required: false),
                },
                async (args, ct) =>
                {
                    try
                    {
                        var query = args.GetStringOrNull("query");
                        var window = args.GetStringOrNull("window") ?? "1h";

                        if (task.GateProvider != null)
                        {
                            bool approved = await task.GateProvider.RequestAccessAsync(
                                "log_search", $"Query Kibana logs with query: \"{query}\", window: \"{window}\"", ct).ConfigureAwait(false);
                            if (!approved)
                            {
                                return ToolOutput.DeadEnd("Access denied by the user. Cannot run log search.");
                            }
                        }

                        var text = await client.SearchAsync(
                            query, window, 25, ct).ConfigureAwait(false);
                        return ToolOutput.Ok(text);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { return ToolOutput.Error($"Log search failed: {ex.Message}"); }
                }),

            new AgentTool(
                "log_summary",
                "Summarise the logs over a window: total matching count plus the top groups (by level/error " +
                "type) so you can see what's spiking. Optionally narrow with a Lucene query_string.",
                new[]
                {
                    ToolParameter.String("window", "How far back from now: \"15m\", \"1h\", \"24h\". Defaults to 1h.", required: false),
                    ToolParameter.String("query", "Optional Lucene query_string to narrow the summary.", required: false),
                },
                async (args, ct) =>
                {
                    try
                    {
                        var window = args.GetStringOrNull("window") ?? "1h";
                        var query = args.GetStringOrNull("query");

                        if (task.GateProvider != null)
                        {
                            bool approved = await task.GateProvider.RequestAccessAsync(
                                "log_summary", $"Query Kibana log summary with query: \"{query}\", window: \"{window}\"", ct).ConfigureAwait(false);
                            if (!approved)
                            {
                                return ToolOutput.DeadEnd("Access denied by the user. Cannot run log summary.");
                            }
                        }

                        var text = await client.SummaryAsync(
                            window, query, ct).ConfigureAwait(false);
                        return ToolOutput.Ok(text);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { return ToolOutput.Error($"Log summary failed: {ex.Message}"); }
                }),
        };
    }
}

/// <summary>A thin read-only Elasticsearch client: POST <c>/{index}/_search</c> with an <c>ApiKey</c> header.
/// Best-effort, defensive parsing — log schemas vary, so it reads fields by dotted path and degrades to a
/// compact source dump rather than failing.</summary>
public sealed class KibanaClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string _searchUrl;
    private readonly string? _apiKey;
    private readonly string _timeField;
    private readonly string _groupField;

    // A valid relative window like "30m", "1h", "24h", "7d" — anything else falls back to 1h.
    private static readonly Regex WindowRe = new(@"^\d+[smhdw]$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public KibanaClient(string baseUrl, string? apiKey, string? index, string? timeField, string? groupField)
    {
        var b = baseUrl.TrimEnd('/');
        var idx = string.IsNullOrWhiteSpace(index) ? "*" : index!.Trim();
        _searchUrl = $"{b}/{Uri.EscapeDataString(idx).Replace("%2A", "*")}/_search";
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _timeField = string.IsNullOrWhiteSpace(timeField) ? "@timestamp" : timeField!.Trim();
        _groupField = string.IsNullOrWhiteSpace(groupField) ? "log.level" : groupField!.Trim();
    }

    private static string SafeWindow(string window) =>
        WindowRe.IsMatch(window.Trim()) ? window.Trim() : "1h";

    private JsonObject BaseQuery(string? query, string window)
    {
        var filters = new JsonArray
        {
            new JsonObject { ["range"] = new JsonObject { [_timeField] = new JsonObject { ["gte"] = $"now-{SafeWindow(window)}" } } },
        };
        JsonNode must = string.IsNullOrWhiteSpace(query)
            ? new JsonObject { ["match_all"] = new JsonObject() }
            : new JsonObject { ["query_string"] = new JsonObject { ["query"] = query } };
        return new JsonObject { ["bool"] = new JsonObject { ["filter"] = filters, ["must"] = must } };
    }

    private async Task<JsonElement> PostAsync(JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _searchUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (_apiKey is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", _apiKey);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Elasticsearch {(int)resp.StatusCode}: {Trim(json, 300)}");
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public async Task<string> SearchAsync(string? query, string window, int size, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["size"] = size,
            ["sort"] = new JsonArray { new JsonObject { [_timeField] = "desc" } },
            ["query"] = BaseQuery(query, window),
        };
        var root = await PostAsync(body, ct).ConfigureAwait(false);

        long total = TotalHits(root);
        if (!root.TryGetProperty("hits", out var hits) || !hits.TryGetProperty("hits", out var arr)
            || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return $"No log entries in the last {SafeWindow(window)} for query '{query ?? "*"}'.";

        var sb = new StringBuilder($"{total} matching entr{(total == 1 ? "y" : "ies")} in the last {SafeWindow(window)} " +
                                   $"for '{query ?? "*"}' (newest first, showing {arr.GetArrayLength()}):\n");
        foreach (var hit in arr.EnumerateArray())
        {
            if (!hit.TryGetProperty("_source", out var src)) continue;
            string ts = Dotted(src, _timeField) ?? "";
            string level = Dotted(src, _groupField) ?? Dotted(src, "level") ?? "";
            string msg = Dotted(src, "message") ?? Dotted(src, "log.message") ?? Dotted(src, "error.message")
                         ?? Trim(src.GetRawText(), 240);
            sb.Append("- ");
            if (ts.Length > 0) sb.Append($"[{ts}] ");
            if (level.Length > 0) sb.Append($"{level.ToUpperInvariant()}: ");
            sb.Append(Trim(msg.Replace("\n", " "), 280)).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    public async Task<string> SummaryAsync(string window, string? query, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["size"] = 0,
            ["query"] = BaseQuery(query, window),
            ["aggs"] = new JsonObject
            {
                ["groups"] = new JsonObject { ["terms"] = new JsonObject { ["field"] = _groupField, ["size"] = 10 } },
            },
        };

        JsonElement root;
        try { root = await PostAsync(body, ct).ConfigureAwait(false); }
        catch (InvalidOperationException)
        {
            // The group_field probably isn't aggregatable (needs a keyword mapping) — fall back to a plain count.
            var countRoot = await PostAsync(new JsonObject { ["size"] = 0, ["query"] = BaseQuery(query, window) }, ct).ConfigureAwait(false);
            return $"{TotalHits(countRoot)} matching entries in the last {SafeWindow(window)} for '{query ?? "*"}'. " +
                   $"(Couldn't group by '{_groupField}' — set kibana.group_field to an aggregatable keyword field for a breakdown.)";
        }

        long total = TotalHits(root);
        var sb = new StringBuilder($"{total} matching entr{(total == 1 ? "y" : "ies")} in the last {SafeWindow(window)} " +
                                   $"for '{query ?? "*"}'.");
        if (root.TryGetProperty("aggregations", out var aggs) && aggs.TryGetProperty("groups", out var groups)
            && groups.TryGetProperty("buckets", out var buckets) && buckets.ValueKind == JsonValueKind.Array
            && buckets.GetArrayLength() > 0)
        {
            sb.Append($"\nTop by {_groupField}:");
            foreach (var b in buckets.EnumerateArray())
            {
                string key = b.TryGetProperty("key", out var k) ? (k.ValueKind == JsonValueKind.String ? k.GetString() ?? "" : k.ToString()) : "";
                long count = b.TryGetProperty("doc_count", out var dc) && dc.TryGetInt64(out var c) ? c : 0;
                sb.Append($"\n- {key}: {count}");
            }
        }
        return sb.ToString();
    }

    private static long TotalHits(JsonElement root)
    {
        if (root.TryGetProperty("hits", out var hits) && hits.TryGetProperty("total", out var total))
        {
            if (total.ValueKind == JsonValueKind.Number && total.TryGetInt64(out var n)) return n; // old ES
            if (total.ValueKind == JsonValueKind.Object && total.TryGetProperty("value", out var v) && v.TryGetInt64(out var n2)) return n2;
        }
        return 0;
    }

    // Read a dotted path ("log.level") from a _source object, walking nested objects. Null if absent.
    private static string? Dotted(JsonElement source, string path)
    {
        JsonElement cur = source;
        foreach (var part in path.Split('.'))
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(part, out var next)) return null;
            cur = next;
        }
        return cur.ValueKind switch
        {
            JsonValueKind.String => cur.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => cur.ToString(),
            _ => null,
        };
    }

    private static string Trim(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
