using System.Text;
using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// Figma capability — READ-ONLY access to a team's design system over the Figma REST API, so the branding
/// designer can ground a deliverable in the real brand rather than guessing. Figma's API cannot author or edit
/// designs (that needs the in-app Plugin API), so this is deliberately a brand SOURCE, not a producer: it reads
/// the published styles (the brand's named colours/type), lists the frames/nodes in a file, and exports nodes
/// (a logo, an asset) as image URLs the worker then downloads with run_python and places in its output.
/// Config (under <c>figma.*</c>): <c>token</c> (required — a Figma personal access token), <c>file</c>
/// (optional default file key so tools needn't be given one each time). Not configured → contributes no tools.
/// </summary>
public sealed class FigmaCapability : ICapability
{
    public string Id => "figma";
    public string DisplayName => "Figma (read-only design system)";
    public IReadOnlyList<string> RequiredConfig => new[] { "token" };

    public string? PromptHint =>
        "Read the team's Figma design system (read-only — Figma can't author designs): figma_styles (the brand's " +
        "published colours and text styles), figma_frames (pages/frames and their node ids), and figma_export " +
        "(export node ids as PNG, returns URLs you can download in run_python to place a logo/asset). It cannot " +
        "create or edit Figma files — use it to learn the brand, then build the deliverable yourself.";

    public void ValidateSystemPrerequisites() { }

    public IReadOnlyList<AgentTool> BuildTools(IntegrationConfig config, TaskInfo task)
    {
        var token = config.Get(Id, "token");
        if (string.IsNullOrWhiteSpace(token)) return Array.Empty<AgentTool>(); // unconfigured → no tools, persona still runs
        var defaultFile = config.Get(Id, "file");
        var client = new FigmaClient(token!);

        string? FileKey(ToolCallArguments a) => a.GetStringOrNull("file")?.Trim() is { Length: > 0 } f ? f : defaultFile;
        ToolOutput NoFile() => ToolOutput.Error("No Figma file given. Pass file (the file key from its URL), or set figma.file in config.");

        return new[]
        {
            new AgentTool(
                "figma_styles",
                "Read the brand's published styles from a Figma file — its named colours (with hex where " +
                "available) and text styles. Use these as the brand palette/type when designing.",
                new[] { ToolParameter.String("file", "Figma file key (from the file URL). Omit to use the configured default.", required: false) },
                async (a, ct) =>
                {
                    var key = FileKey(a); if (key is null) return NoFile();
                    try { return ToolOutput.Ok(await client.StylesAsync(key, ct).ConfigureAwait(false)); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { return ToolOutput.Error($"figma_styles failed: {ex.Message}"); }
                }),

            new AgentTool(
                "figma_frames",
                "List the pages and top-level frames in a Figma file, with their node ids — so you can find the " +
                "logo or an asset to export with figma_export.",
                new[] { ToolParameter.String("file", "Figma file key. Omit to use the configured default.", required: false) },
                async (a, ct) =>
                {
                    var key = FileKey(a); if (key is null) return NoFile();
                    try { return ToolOutput.Ok(await client.FramesAsync(key, ct).ConfigureAwait(false)); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { return ToolOutput.Error($"figma_frames failed: {ex.Message}"); }
                }),

            new AgentTool(
                "figma_export",
                "Export one or more Figma nodes as PNG images. Returns temporary image URLs — download them in " +
                "run_python (urllib/requests) and place them in your deliverable. Get node ids from figma_frames.",
                new[]
                {
                    ToolParameter.String("node_ids", "Comma-separated Figma node ids to export (e.g. \"1:23,4:56\").", required: true),
                    ToolParameter.String("file", "Figma file key. Omit to use the configured default.", required: false),
                },
                async (a, ct) =>
                {
                    var key = FileKey(a); if (key is null) return NoFile();
                    var ids = a.GetStringOrNull("node_ids")?.Trim();
                    if (string.IsNullOrEmpty(ids)) return ToolOutput.Error("Pass node_ids (comma-separated), from figma_frames.");
                    try { return ToolOutput.Ok(await client.ExportAsync(key, ids!, ct).ConfigureAwait(false)); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { return ToolOutput.Error($"figma_export failed: {ex.Message}"); }
                }),
        };
    }
}

/// <summary>A thin read-only Figma REST client (HttpClient + System.Text.Json), authenticated with a personal
/// access token via the <c>X-Figma-Token</c> header. Read endpoints only — Figma's API has no design-authoring.</summary>
public sealed class FigmaClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30), BaseAddress = new Uri("https://api.figma.com") };
    private readonly string _token;

    public FigmaClient(string token) => _token = token;

    private async Task<JsonElement> GetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Figma-Token", _token);
        req.Headers.UserAgent.ParseAdd("smarty-agent");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Figma {(int)resp.StatusCode}: {TryErr(json) ?? Trim(json, 200)}");
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>Published styles for a file: colour and text styles by name. Hex is pulled best-effort by
    /// reading the underlying style nodes' solid fills; if that lookup fails we still return the names/types.</summary>
    public async Task<string> StylesAsync(string fileKey, CancellationToken ct)
    {
        var root = await GetAsync($"/v1/files/{Uri.EscapeDataString(fileKey)}/styles", ct).ConfigureAwait(false);
        if (!root.TryGetProperty("meta", out var meta) || !meta.TryGetProperty("styles", out var styles)
            || styles.ValueKind != JsonValueKind.Array || styles.GetArrayLength() == 0)
            return "No published styles in this Figma file.";

        var fillIds = new List<string>();
        var rows = new List<(string Type, string Name, string NodeId, string Desc)>();
        foreach (var s in styles.EnumerateArray())
        {
            string type = Str(s, "style_type");
            string name = Str(s, "name");
            string nodeId = Str(s, "node_id");
            string desc = Str(s, "description");
            rows.Add((type, name, nodeId, desc));
            if (string.Equals(type, "FILL", StringComparison.OrdinalIgnoreCase) && nodeId.Length > 0)
                fillIds.Add(nodeId);
        }

        // Best-effort: resolve hex for FILL styles via one /nodes call. Failure here must not lose the names.
        var hexById = new Dictionary<string, string>();
        if (fillIds.Count > 0)
        {
            try
            {
                var nodes = await GetAsync(
                    $"/v1/files/{Uri.EscapeDataString(fileKey)}/nodes?ids={Uri.EscapeDataString(string.Join(",", fillIds))}", ct)
                    .ConfigureAwait(false);
                if (nodes.TryGetProperty("nodes", out var nmap) && nmap.ValueKind == JsonValueKind.Object)
                    foreach (var entry in nmap.EnumerateObject())
                        if (entry.Value.TryGetProperty("document", out var docNode) && TryHex(docNode, out var hex))
                            hexById[entry.Name] = hex;
            }
            catch { /* keep names-only */ }
        }

        var sb = new StringBuilder("Brand styles (read-only):\n");
        foreach (var grp in rows.GroupBy(r => r.Type).OrderBy(g => g.Key))
        {
            sb.Append($"\n{grp.Key}:\n");
            foreach (var r in grp.OrderBy(r => r.Name))
            {
                sb.Append($"- {r.Name}");
                if (hexById.TryGetValue(r.NodeId, out var hex)) sb.Append($"  {hex}");
                if (r.Desc.Length > 0) sb.Append($"  — {r.Desc}");
                sb.Append('\n');
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Pages and their top-level frames, with node ids for export.</summary>
    public async Task<string> FramesAsync(string fileKey, CancellationToken ct)
    {
        var root = await GetAsync($"/v1/files/{Uri.EscapeDataString(fileKey)}", ct).ConfigureAwait(false);
        if (!root.TryGetProperty("document", out var doc) || !doc.TryGetProperty("children", out var pages)
            || pages.ValueKind != JsonValueKind.Array)
            return "Couldn't read the file's structure.";

        var sb = new StringBuilder($"{Str(root, "name")} — pages & frames (node ids for figma_export):\n");
        foreach (var page in pages.EnumerateArray())
        {
            sb.Append($"\n📄 {Str(page, "name")}\n");
            if (page.TryGetProperty("children", out var frames) && frames.ValueKind == JsonValueKind.Array)
                foreach (var frame in frames.EnumerateArray())
                    sb.Append($"  - {Str(frame, "name")}  [id: {Str(frame, "id")}]\n");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Render the given node ids to PNG and return their (temporary) image URLs.</summary>
    public async Task<string> ExportAsync(string fileKey, string nodeIds, CancellationToken ct)
    {
        var ids = string.Join(",", nodeIds.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));
        var root = await GetAsync(
            $"/v1/images/{Uri.EscapeDataString(fileKey)}?ids={Uri.EscapeDataString(ids)}&format=png&scale=2", ct)
            .ConfigureAwait(false);
        if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Object)
            return "No images were returned for those node ids.";

        var sb = new StringBuilder("Exported PNGs (download these in run_python, they expire shortly):\n");
        int n = 0;
        foreach (var img in images.EnumerateObject())
            if (img.Value.ValueKind == JsonValueKind.String && img.Value.GetString() is { Length: > 0 } url)
            { sb.Append($"- {img.Name}: {url}\n"); n++; }
        return n == 0 ? "Those node ids didn't render to any image." : sb.ToString().TrimEnd();
    }

    // Pull a hex colour from a style node's first solid fill (Figma colours are 0..1 r/g/b).
    private static bool TryHex(JsonElement node, out string hex)
    {
        hex = "";
        if (!node.TryGetProperty("fills", out var fills) || fills.ValueKind != JsonValueKind.Array) return false;
        foreach (var fill in fills.EnumerateArray())
        {
            if (Str(fill, "type") != "SOLID" || !fill.TryGetProperty("color", out var c)) continue;
            int r = To255(c, "r"), g = To255(c, "g"), b = To255(c, "b");
            hex = $"#{r:X2}{g:X2}{b:X2}";
            return true;
        }
        return false;
    }

    private static int To255(JsonElement color, string ch) =>
        color.TryGetProperty(ch, out var v) && v.ValueKind == JsonValueKind.Number
            ? Math.Clamp((int)Math.Round(v.GetDouble() * 255), 0, 255) : 0;

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static string? TryErr(string json)
    {
        try { var r = JsonDocument.Parse(json).RootElement; return r.TryGetProperty("err", out var e) ? e.GetString() : null; }
        catch { return null; }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
}
