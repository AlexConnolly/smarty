using System.Text.Json;

namespace Smarty.Agents;

/// <summary>
/// One configured MCP server, kept connected for the life of the host: its child process, its client, and the
/// <see cref="AgentTool"/>s it contributes.
/// </summary>
/// <remarks>
/// The tools handed out are stable objects that resolve the connection on every call, so a server that dies —
/// a browser bridge losing its extension, a crash — is reconnected underneath a worker that's already holding
/// its tools, instead of poisoning every later call. A server that can't be reached contributes no tools and
/// records why; it never throws into the host.
/// </remarks>
public sealed class McpServerConnection : IAsyncDisposable
{
    private readonly Func<McpServerConfig, IMcpTransport> _transportFactory;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private McpClient? _client;
    private IReadOnlyList<AgentTool> _tools = Array.Empty<AgentTool>();
    private IReadOnlyList<string> _serverToolNames = Array.Empty<string>();
    private bool _disposed;

    private readonly McpTools.ImageSink? _images;

    public McpServerConnection(
        McpServerConfig config,
        Action<string>? log = null,
        Func<McpServerConfig, IMcpTransport>? transportFactory = null,
        McpTools.ImageSink? images = null)
    {
        _images = images;
        Config = config;
        _log = log;
        _transportFactory = transportFactory ?? (c => new McpStdioTransport(c));
    }

    public McpServerConfig Config { get; }

    /// <summary>The tools this server contributes — empty until a first successful connection.</summary>
    public IReadOnlyList<AgentTool> Tools => _tools;

    public bool Connected => _client is { IsAlive: true };

    /// <summary>Why the last connection attempt failed, if it did.</summary>
    public string? Error { get; private set; }

    public string? ServerName => _client?.ServerName;

    public string? ServerVersion => _client?.ServerVersion;

    public string? ProtocolVersion => _client?.ProtocolVersion;

    /// <summary>Connect (if needed), handshake, and discover tools. Returns whether the server is usable
    /// afterwards; the reason for a false is in <see cref="Error"/>.</summary>
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (_disposed) return false;
        if (Connected) return true;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return false;
            if (Connected) return true;

            if (_client is { } dead)
            {
                _client = null;
                await dead.DisposeAsync().ConfigureAwait(false);
            }

            var transport = _transportFactory(Config);
            transport.Log += message => _log?.Invoke($"[mcp:{Config.Name}] {message}");

            var client = new McpClient(transport, message => _log?.Invoke($"[mcp:{Config.Name}] {message}"));
            try
            {
                await client.InitializeAsync(Config.ProtocolVersion, "smarty", Config.StartupTimeout, ct)
                    .ConfigureAwait(false);
                var discovered = await client.ListToolsAsync(Config.StartupTimeout, ct).ConfigureAwait(false);

                _client = client;
                client.ToolsChanged += OnToolsChanged;
                Adopt(discovered);
                Error = null;

                _log?.Invoke($"[mcp:{Config.Name}] connected to {client.ServerName ?? "server"} " +
                             $"{client.ServerVersion ?? ""} (protocol {client.ProtocolVersion ?? "?"}) — " +
                             $"{_tools.Count} tool(s): {string.Join(", ", _tools.Select(t => t.Name))}");
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Error = ex.Message;
                _log?.Invoke($"[mcp:{Config.Name}] unavailable: {ex.Message}");
                await client.DisposeAsync().ConfigureAwait(false);
                return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Call one of this server's tools, reconnecting first if the connection has gone.</summary>
    public async Task<JsonElement> CallAsync(string toolName, JsonElement arguments, CancellationToken ct = default)
    {
        if (!await ConnectAsync(ct).ConfigureAwait(false))
            throw new McpException($"the \"{Config.Name}\" MCP server isn't available{(Error is null ? "" : $": {Error}")}");

        var client = _client ?? throw new McpException($"the \"{Config.Name}\" MCP server isn't available.");
        try
        {
            return await client.CallToolAsync(toolName, arguments, Config.CallTimeout, ct).ConfigureAwait(false);
        }
        catch (McpException ex) when (!ex.IsPermanent && !client.IsAlive)
        {
            // The server died mid-call (or between calls). One reconnect-and-retry, because the usual cause is a
            // restarted server rather than a bad request — and the model shouldn't have to work that out.
            _log?.Invoke($"[mcp:{Config.Name}] {ex.Message} — reconnecting and retrying {toolName}");
            if (!await ConnectAsync(ct).ConfigureAwait(false))
                throw new McpException($"the \"{Config.Name}\" MCP server went away{(Error is null ? "" : $": {Error}")}");

            var reconnected = _client ?? throw new McpException($"the \"{Config.Name}\" MCP server went away.");
            return await reconnected.CallToolAsync(toolName, arguments, Config.CallTimeout, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        var client = _client;
        _client = null;
        if (client is not null)
        {
            client.ToolsChanged -= OnToolsChanged;
            await client.DisposeAsync().ConfigureAwait(false);
        }
        _gate.Dispose();
    }

    /// <summary>The generated "how to use this" line for the worker's prompt, when the config didn't supply one.</summary>
    public string PromptHint()
    {
        if (Config.PromptHint is { Length: > 0 } configured) return configured;
        if (_tools.Count == 0) return "";
        return $"The \"{Config.Name}\" MCP server is connected: {string.Join(", ", _tools.Select(t => t.Name))}. " +
               "Read each tool's description before calling it, and work from what it actually returns.";
    }

    private void Adopt(IReadOnlyList<McpToolDefinition> discovered)
    {
        var allowed = Config.AllowedTools;
        var kept = allowed.Count == 0
            ? discovered
            : discovered.Where(t => allowed.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList();

        if (allowed.Count > 0)
        {
            var missing = allowed.Where(a => discovered.All(d => !string.Equals(d.Name, a, StringComparison.OrdinalIgnoreCase))).ToList();
            if (missing.Count > 0)
                _log?.Invoke($"[mcp:{Config.Name}] configured tool(s) not offered by the server: {string.Join(", ", missing)}");
        }

        _serverToolNames = discovered.Select(t => t.Name).ToList();
        _tools = kept.Select(t => McpTools.Build(t, Config, CallAsync, _images)).ToList();
    }

    /// <summary>Every tool the server offers, allow-list or not — so the control centre can show what was filtered out.</summary>
    public IReadOnlyList<string> OfferedToolNames => _serverToolNames;

    private void OnToolsChanged()
    {
        // Re-listing has to happen off this callback (it's raised from the read pump, and a tools/list from there
        // would deadlock behind its own reply).
        _ = Task.Run(async () =>
        {
            try
            {
                if (_client is not { IsAlive: true } client) return;
                Adopt(await client.ListToolsAsync(Config.StartupTimeout).ConfigureAwait(false));
                _log?.Invoke($"[mcp:{Config.Name}] tool list changed → {_tools.Count} tool(s)");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[mcp:{Config.Name}] could not refresh tools: {ex.Message}");
            }
        });
    }
}
