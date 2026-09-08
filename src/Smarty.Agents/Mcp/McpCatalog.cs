namespace Smarty.Agents;

/// <summary>
/// A server Smarty knows about already: everything needed to run it except the one machine-specific detail
/// (usually a path), so turning it on is a toggle rather than a form.
/// </summary>
/// <param name="Key">Stable id for the template — what the toggle endpoint takes.</param>
/// <param name="Name">The server name it will be installed under (and the prefix its tools get).</param>
/// <param name="Summary">One line: what it gives Smarty.</param>
/// <param name="Function">The persona capability it answers, e.g. <c>browser</c>.</param>
/// <param name="Requires">What must be on the machine, said plainly, for when discovery fails.</param>
public sealed record McpServerTemplate(
    string Key,
    string Name,
    string Summary,
    string Function,
    string Command,
    string Requires)
{
    /// <summary>Fixed arguments, before the discovered/supplied one.</summary>
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    public string? PromptHint { get; init; }

    /// <summary>Where to read about it, for the UI.</summary>
    public string? Homepage { get; init; }

    /// <summary>
    /// What the last argument is, when the server needs one the machine has to supply — a path to a checkout, a
    /// directory to expose. Null when the template is complete on its own.
    /// </summary>
    public string? PathArgument { get; init; }

    /// <summary>Candidate locations to try for <see cref="PathArgument"/>, most likely first.</summary>
    public IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();

    /// <summary>Whether a discovered path must be an existing FILE (a script) rather than a directory.</summary>
    public bool PathIsFile { get; init; } = true;

    /// <summary>The first candidate that actually exists, or null if none do.</summary>
    public string? Discover()
    {
        foreach (var candidate in Candidates)
        {
            var path = Environment.ExpandEnvironmentVariables(candidate);
            if (PathIsFile ? File.Exists(path) : Directory.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>Turn the template into a real server config, given the machine-specific path (or the discovered
    /// one). Returns null when the path is needed and neither was supplied — the caller then asks for it.</summary>
    public McpServerConfig? ToConfig(string? path = null, bool enabled = true)
    {
        var args = Args.ToList();
        if (PathArgument is not null)
        {
            var resolved = string.IsNullOrWhiteSpace(path) ? Discover() : Environment.ExpandEnvironmentVariables(path.Trim());
            if (string.IsNullOrWhiteSpace(resolved)) return null;
            args.Add(resolved);
        }

        return new McpServerConfig
        {
            Name = Name,
            Command = Command,
            Args = args,
            Env = Env.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            Functions = new[] { Function },
            PromptHint = PromptHint,
            Enabled = enabled,
            // npx fetches the package on first run, which can take a while on a cold cache.
            StartupTimeout = Command.StartsWith("npx", StringComparison.OrdinalIgnoreCase)
                ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(30),
        };
    }
}

/// <summary>
/// The servers Smarty ships knowing about, so the common ones are a toggle instead of a command line someone has
/// to get right. A built-in is not special once it's on: it becomes an ordinary entry in mcp.json, editable and
/// removable like any other, and nothing downstream can tell the difference.
/// </summary>
public static class McpCatalog
{
    public static IReadOnlyList<McpServerTemplate> BuiltIns { get; } = new[]
    {
        new McpServerTemplate(
            Key: "open-chrome",
            Name: "chrome",
            Summary: "Drives the Chrome you're already signed into — your profile, your sessions — over the DevTools protocol. This is how Smarty reaches the web.",
            Function: "browser",
            Command: "node",
            Requires: "Node.js, an open-chrome-mcp checkout, and its extension loaded in Chrome (chrome://extensions → Load unpacked).")
        {
            Env = new Dictionary<string, string> { ["OPEN_CHROME_MCP_PORT"] = "8777" },
            Homepage = "https://github.com/AlexConnolly/open-chrome-mcp",
            PathArgument = "Path to open-chrome-mcp's server/src/index.js",
            Candidates = new[]
            {
                @"%USERPROFILE%\open-chrome-mcp\server\src\index.js",
                @"%USERPROFILE%\source\repos\open-chrome-mcp\server\src\index.js",
                @"%USERPROFILE%\dev\open-chrome-mcp\server\src\index.js",
                @"%USERPROFILE%\projects\open-chrome-mcp\server\src\index.js",
                "../open-chrome-mcp/server/src/index.js",
                "../../open-chrome-mcp/server/src/index.js",
            },
            PromptHint =
                "You can drive the user's actual Chrome (tools prefixed chrome_) — their browser, signed in, so " +
                "for anything of theirs go to their account page rather than searching the public web. You act as " +
                "them: never buy, send, post, cancel or delete unless asked. Call chrome_tabs_context FIRST — " +
                "it is what makes tabs addressable. Then chrome_navigate to a URL. " +
                "chrome_find(\"add to basket button\") gets you the ref to act on; chrome_read_page is for when " +
                "you need to know what a page SAYS rather than what to click. Refs stop working when the page " +
                "re-renders — find again rather than reading again. " +
                "The browser is the ONLY way you reach the web, so use it for reading pages too — including " +
                "search results, which are just another page. Travel by clicking a ref, the way a person does. " +
                "NEVER invent, complete or guess an address: type a URL only when the user gave it to you or " +
                "chrome_inspect handed you the real one. A search constrained with site: beats any address you " +
                "can infer. " +
                "Put an image's src straight into your answer as ![alt](src) so the user sees the thing rather " +
                "than reading a description of it — a remote src is fine, it gets served locally before it " +
                "reaches them. " +
                "Close tabs you opened with chrome_tabs_close.",
        },

        new McpServerTemplate(
            Key: "filesystem",
            Name: "files",
            Summary: "Read and write files under one directory you choose. Useful for pointing Smarty at a folder of documents.",
            Function: "documents",
            Command: "npx",
            Requires: "Node.js. The package is fetched by npx on first run.")
        {
            Args = new[] { "-y", "@modelcontextprotocol/server-filesystem" },
            Homepage = "https://github.com/modelcontextprotocol/servers",
            PathArgument = "Directory to expose (the server can read and write inside it)",
            PathIsFile = false,
            Candidates = new[] { @"%USERPROFILE%\Documents" },
            PromptHint =
                "You have file tools (prefixed files_) over ONE directory. List before you read, and read before " +
                "you write. Everything outside that directory is off-limits and the tools will refuse it.",
        },

        new McpServerTemplate(
            Key: "git",
            Name: "git",
            Summary: "Read a git repository's history, diffs and branches.",
            Function: "code",
            Command: "npx",
            Requires: "Node.js and git on PATH.")
        {
            Args = new[] { "-y", "@modelcontextprotocol/server-git", "--repository" },
            Homepage = "https://github.com/modelcontextprotocol/servers",
            PathArgument = "Path to the repository",
            PathIsFile = false,
            Candidates = new[] { "." },
            PromptHint =
                "You can read a git repo (tools prefixed git_): log, diff, show, branches. It is READ-ONLY — " +
                "propose changes, never claim to have committed or pushed anything.",
        },
    };

    public static McpServerTemplate? Find(string key) =>
        BuiltIns.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Bring installed built-ins' prompt hints back in line with the catalogue at load time.
    /// <para>
    /// Enabling a built-in writes its whole config to mcp.json, hint included — which quietly freezes it. The path
    /// in that file is the user's and must never be overwritten; the hint is ours, and a copy taken by an older
    /// version outlives every improvement made to it afterwards. So the hint is re-read from the catalogue here,
    /// and a server that has since been hand-edited is left alone — an edited hint is a deliberate one.
    /// </para>
    /// </summary>
    public static IReadOnlyList<McpServerConfig> RefreshHints(
        IReadOnlyList<McpServerConfig> servers, Action<string>? note = null)
    {
        var refreshed = new List<McpServerConfig>(servers.Count);
        foreach (var server in servers)
        {
            var template = BuiltIns.FirstOrDefault(t =>
                string.Equals(t.Name, server.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Command, server.Command, StringComparison.OrdinalIgnoreCase));

            if (template?.PromptHint is { Length: > 0 } hint && server.PromptHint is { Length: > 0 } stored &&
                !string.Equals(hint, stored, StringComparison.Ordinal))
            {
                note?.Invoke($"{server.Name}: prompt hint refreshed from the built-in catalogue.");
                refreshed.Add(server with { PromptHint = hint });
                continue;
            }

            refreshed.Add(server);
        }
        return refreshed;
    }
}
