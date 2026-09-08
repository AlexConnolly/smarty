namespace Smarty.Agents;

/// <summary>What one MCP server is doing, for diagnostics and the control centre. Carries no credentials —
/// the command line is shown, its environment is not.</summary>
public sealed record McpServerStatus(
    string Name,
    string Command,
    bool Enabled,
    bool Connected,
    string? ServerName,
    string? ServerVersion,
    string? ProtocolVersion,
    string ToolPrefix,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> OfferedTools,
    IReadOnlyList<string> Functions,
    string? Error);

/// <summary>
/// Every MCP server this host runs. Servers are connected once at startup, in parallel, each with its own
/// timeout — a server that isn't there (Node missing, a bad path, an extension not loaded) costs its timeout and
/// nothing else, and the host boots regardless. That's the whole reason MCP can be turned on by default: it
/// either contributes tools or it doesn't.
/// </summary>
public sealed class McpHub : IAsyncDisposable
{
    private readonly List<McpServerConnection> _connections;
    private readonly Action<string>? _log;
    private readonly Func<McpServerConfig, IMcpTransport>? _transportFactory;
    private readonly object _lock = new();

    private readonly McpTools.ImageSink? _images;

    public McpHub(
        IEnumerable<McpServerConfig> servers,
        Action<string>? log = null,
        Func<McpServerConfig, IMcpTransport>? transportFactory = null,
        McpTools.ImageSink? images = null)
    {
        _log = log;
        _transportFactory = transportFactory;
        _images = images;
        _connections = servers.Select(s => new McpServerConnection(s, log, transportFactory, images)).ToList();
    }

    /// <summary>
    /// Add a server (or replace one of the same name) and connect it, without restarting the host. Returns the
    /// live connection so the caller can register its capability and report what it offered.
    /// </summary>
    /// <remarks>
    /// Replacing by name rather than rejecting a duplicate is deliberate: editing a server in the control centre
    /// is a save, and a save that left the old process running would be a lie.
    /// </remarks>
    public async Task<McpServerConnection> AddAsync(McpServerConfig config, CancellationToken ct = default)
    {
        await RemoveAsync(config.Name).ConfigureAwait(false);

        var connection = new McpServerConnection(config, _log, _transportFactory, _images);
        lock (_lock) _connections.Add(connection);

        if (config.Enabled) await connection.ConnectAsync(ct).ConfigureAwait(false);
        else _log?.Invoke($"[mcp:{config.Name}] added but disabled — not started");

        return connection;
    }

    /// <summary>Stop a server and forget it. Its child process is killed, so nothing is left behind.</summary>
    public async Task<bool> RemoveAsync(string name)
    {
        McpServerConnection? existing;
        lock (_lock)
        {
            existing = _connections.FirstOrDefault(c =>
                string.Equals(c.Config.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) _connections.Remove(existing);
        }

        if (existing is null) return false;
        await existing.DisposeAsync().ConfigureAwait(false);
        _log?.Invoke($"[mcp:{name}] removed");
        return true;
    }

    /// <summary>Drop the connection and dial again — the "it's gone quiet, kick it" button.</summary>
    public async Task<McpServerConnection?> ReconnectAsync(string name, CancellationToken ct = default)
    {
        McpServerConnection? existing;
        lock (_lock)
            existing = _connections.FirstOrDefault(c =>
                string.Equals(c.Config.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return null;

        return await AddAsync(existing.Config, ct).ConfigureAwait(false);
    }

    /// <summary>An empty hub, for hosts with MCP switched off.</summary>
    public static McpHub Empty { get; } = new(Array.Empty<McpServerConfig>());

    public IReadOnlyList<McpServerConnection> Connections { get { lock (_lock) return _connections.ToList(); } }

    /// <summary>True when at least one server is configured (enabled or not).</summary>
    public bool Configured { get { lock (_lock) return _connections.Count > 0; } }

    /// <summary>Connect every enabled server. Never throws: a server that fails is recorded and skipped.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        var enabled = Connections.Where(c => c.Config.Enabled).ToList();
        if (enabled.Count == 0)
        {
            if (Configured) _log?.Invoke("[mcp] every configured server is disabled");
            return;
        }

        _log?.Invoke($"[mcp] starting {enabled.Count} server(s): {string.Join(", ", enabled.Select(c => c.Config.Name))}");
        await Task.WhenAll(enabled.Select(async c =>
        {
            try { await c.ConnectAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log?.Invoke($"[mcp:{c.Config.Name}] failed to start: {ex.Message}");
            }
        })).ConfigureAwait(false);

        var all = Connections;
        int live = all.Count(c => c.Connected);
        _log?.Invoke($"[mcp] {live} of {enabled.Count} server(s) connected, " +
                     $"{all.Sum(c => c.Tools.Count)} tool(s) available");
    }

    /// <summary>One server by its configured name.</summary>
    public McpServerConnection? Server(string name)
    {
        lock (_lock)
            return _connections.FirstOrDefault(c => string.Equals(c.Config.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The connections answering a persona capability function (e.g. <c>browser</c>).</summary>
    public IReadOnlyList<McpServerConnection> ServersFor(string function)
    {
        lock (_lock)
            return _connections.Where(c => c.Config.Functions.Contains(function, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Every tool from every connected server.</summary>
    public IReadOnlyList<AgentTool> AllTools => Connections.SelectMany(c => c.Tools).ToList();

    public IReadOnlyList<McpServerStatus> Status => Connections.Select(c => new McpServerStatus(
        c.Config.Name,
        string.Join(' ', new[] { c.Config.Command }.Concat(c.Config.Args)),
        c.Config.Enabled,
        c.Connected,
        c.ServerName,
        c.ServerVersion,
        c.ProtocolVersion,
        c.Config.ResolvedPrefix,
        c.Tools.Select(t => t.Name).ToList(),
        c.OfferedToolNames,
        c.Config.Functions,
        c.Error)).ToList();

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in Connections)
            await connection.DisposeAsync().ConfigureAwait(false);
    }
}
