using System.Text.Json;

namespace Smarty.Agents;

/// <summary>
/// One MCP server Smarty should run and take tools from: the command that starts it, and the local policy for
/// what it's allowed to contribute. Deliberately the same file shape the other MCP hosts use — a
/// <c>{ "mcpServers": { "&lt;name&gt;": { "command": …, "args": […] } } }</c> map — so a server someone already has
/// configured elsewhere can be pasted straight in.
/// </summary>
/// <remarks>
/// Servers are launched as child processes and spoken to over stdio, which is what nearly every MCP server
/// ships as. Nothing here reaches the model: the command line, the environment and the credentials in it are
/// host config, exactly like an integration's token.
/// </remarks>
public sealed record McpServerConfig
{
    /// <summary>The key this server was configured under — its display name and the basis of its tool prefix.</summary>
    public string Name { get; init; } = "";

    /// <summary>The executable to run, e.g. <c>node</c> or <c>npx</c>.</summary>
    public string Command { get; init; } = "";

    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    /// <summary>Extra environment for the child process, on top of Smarty's own.</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    public string? WorkingDirectory { get; init; }

    /// <summary>False to keep the entry in the file but not start it.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>What every tool from this server is prefixed with, so two servers can both expose
    /// <c>navigate</c>. Defaults to a slug of <see cref="Name"/>.</summary>
    public string? ToolPrefix { get; init; }

    /// <summary>An allow-list of the server's own tool names. Empty means "everything it offers" — narrow it
    /// when a server exposes more than you want the model reaching for.</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();

    /// <summary>The persona capability functions this server answers (e.g. <c>browser</c>), so a persona asks
    /// for a capability rather than naming a specific server.</summary>
    public IReadOnlyList<string> Functions { get; init; } = Array.Empty<string>();

    /// <summary>How to use this server, woven into the worker's prompt when its tools are live. Without one, a
    /// generic line naming the tools is generated.</summary>
    public string? PromptHint { get; init; }

    /// <summary>The MCP protocol version to ask for during the handshake. The server answers with the version
    /// it actually settled on.</summary>
    public string ProtocolVersion { get; init; } = McpClient.DefaultProtocolVersion;

    /// <summary>How long to wait for the handshake and first tool listing before giving up on the server.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long any one tool call may take.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>A cap on how much text one tool result may add to the transcript. A page read or a log drain
    /// can be enormous; the tail is dropped with a note rather than eating the context window.</summary>
    public int MaxResultChars { get; init; } = 20_000;

    /// <summary>The prefix actually used for this server's tool names.</summary>
    public string ResolvedPrefix => Slug(string.IsNullOrWhiteSpace(ToolPrefix) ? Name : ToolPrefix!);

    /// <summary>Read a server map from disk. A missing file means "no MCP servers", not an error — MCP is
    /// opt-in. A malformed one is reported through <paramref name="problem"/> and treated the same way, so a
    /// bad edit can't stop the host booting.</summary>
    public static IReadOnlyList<McpServerConfig> Load(string path, Action<string>? problem = null)
    {
        try
        {
            if (!File.Exists(path)) return Array.Empty<McpServerConfig>();
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            problem?.Invoke($"could not read {path}: {ex.Message}");
            return Array.Empty<McpServerConfig>();
        }
    }

    /// <summary>Parse a server map. Accepts either the <c>{"mcpServers": {…}}</c> wrapper or the bare map.</summary>
    public static IReadOnlyList<McpServerConfig> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Array.Empty<McpServerConfig>();

        var map = root.TryGetProperty("mcpServers", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object
            ? wrapped
            : root;

        var servers = new List<McpServerConfig>();
        foreach (var entry in map.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            if (string.Equals(entry.Name, "mcpServers", StringComparison.Ordinal)) continue;
            servers.Add(ParseServer(entry.Name, entry.Value));
        }
        return servers;
    }

    private static McpServerConfig ParseServer(string name, JsonElement e)
    {
        // "disabled" is the spelling the other hosts use; "enabled" is accepted too because it's the obvious
        // guess and silently ignoring it would be a trap.
        bool enabled = Bool(e, "enabled") ?? !(Bool(e, "disabled") ?? false);

        return new McpServerConfig
        {
            Name = name,
            Command = Expand(Str(e, "command") ?? ""),
            Args = Strings(e, "args").Select(Expand).ToList(),
            Env = Dict(e, "env"),
            WorkingDirectory = Str(e, "cwd") is { Length: > 0 } cwd ? Expand(cwd) : ExpandOrNull(Str(e, "workingDirectory")),
            Enabled = enabled,
            ToolPrefix = Str(e, "prefix") ?? Str(e, "toolPrefix"),
            AllowedTools = Strings(e, "tools").Concat(Strings(e, "allowedTools")).Distinct(StringComparer.Ordinal).ToList(),
            Functions = Strings(e, "functions"),
            PromptHint = Str(e, "promptHint"),
            ProtocolVersion = Str(e, "protocolVersion") ?? McpClient.DefaultProtocolVersion,
            StartupTimeout = Seconds(e, "startupTimeoutSeconds", TimeSpan.FromSeconds(30)),
            CallTimeout = Seconds(e, "callTimeoutSeconds", TimeSpan.FromMinutes(2)),
            MaxResultChars = Int(e, "maxResultChars") ?? 20_000,
        };
    }

    /// <summary>
    /// Write a server map back to disk, so the control centre can edit MCP servers instead of the file being
    /// hand-maintained.
    /// </summary>
    /// <remarks>
    /// This rewrites the file from the parsed model, so any COMMENTS in a hand-written mcp.json are lost the
    /// first time it's saved from the UI. That's the honest trade for editability; the example file keeps the
    /// documentation.
    /// </remarks>
    public static void Save(string path, IEnumerable<McpServerConfig> servers, JsonSerializerOptions? json = null)
    {
        var map = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var s in servers)
        {
            var entry = new Dictionary<string, object>(StringComparer.Ordinal) { ["command"] = s.Command };
            if (s.Args.Count > 0) entry["args"] = s.Args;
            if (s.Env.Count > 0) entry["env"] = s.Env;
            if (!string.IsNullOrWhiteSpace(s.WorkingDirectory)) entry["cwd"] = s.WorkingDirectory!;
            if (!s.Enabled) entry["disabled"] = true;
            if (!string.IsNullOrWhiteSpace(s.ToolPrefix)) entry["prefix"] = s.ToolPrefix!;
            if (s.AllowedTools.Count > 0) entry["tools"] = s.AllowedTools;
            if (s.Functions.Count > 0) entry["functions"] = s.Functions;
            if (!string.IsNullOrWhiteSpace(s.PromptHint)) entry["promptHint"] = s.PromptHint!;
            if (s.ProtocolVersion != McpClient.DefaultProtocolVersion) entry["protocolVersion"] = s.ProtocolVersion;
            if (s.StartupTimeout != TimeSpan.FromSeconds(30)) entry["startupTimeoutSeconds"] = (int)s.StartupTimeout.TotalSeconds;
            if (s.CallTimeout != TimeSpan.FromMinutes(2)) entry["callTimeoutSeconds"] = (int)s.CallTimeout.TotalSeconds;
            if (s.MaxResultChars != 20_000) entry["maxResultChars"] = s.MaxResultChars;
            map[s.Name] = entry;
        }

        var options = new JsonSerializerOptions(json ?? new JsonSerializerOptions(JsonSerializerDefaults.Web))
        {
            WriteIndented = true,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { mcpServers = map }, options));
    }

    /// <summary>A name reduced to something a tool name can be built from: lowercase, letters/digits/underscore.</summary>
    public static string Slug(string value)
    {
        var slug = new string((value ?? "").Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        while (slug.Contains("__")) slug = slug.Replace("__", "_");
        return slug.Trim('_');
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? s : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => (bool?)null,
        } : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static TimeSpan Seconds(JsonElement e, string name, TimeSpan fallback) =>
        Int(e, name) is { } s && s > 0 ? TimeSpan.FromSeconds(s) : fallback;

    private static List<string> Strings(JsonElement e, string name)
    {
        var list = new List<string>();
        if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s) list.Add(s);
        return list;
    }

    private static Dictionary<string, string> Dict(JsonElement e, string name)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
            foreach (var p in v.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } s)
                    map[p.Name] = Expand(s);
        return map;
    }

    // %USERPROFILE%-style references are expanded so a config file can be shared between machines.
    private static string Expand(string value) =>
        string.IsNullOrEmpty(value) ? value : Environment.ExpandEnvironmentVariables(value);

    private static string? ExpandOrNull(string? value) => value is null ? null : Expand(value);
}
