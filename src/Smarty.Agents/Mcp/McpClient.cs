using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Smarty.Agents;

/// <summary>One tool as an MCP server declares it: a name, a description, and a JSON-Schema object for its input.</summary>
public sealed record McpToolDefinition(string Name, string Description, string InputSchema);

/// <summary>A protocol-level failure from an MCP server (a JSON-RPC error, or a dead connection).</summary>
public sealed class McpException : Exception
{
    public McpException(string message, int code = 0) : base(message) => Code = code;

    /// <summary>The JSON-RPC error code, or 0 when the failure wasn't a protocol error.</summary>
    public int Code { get; }

    /// <summary>True for the codes that mean "this call can never work" rather than "try again".</summary>
    public bool IsPermanent => Code is -32601 or -32602;
}

/// <summary>
/// The MCP client: JSON-RPC 2.0 over an <see cref="IMcpTransport"/>, with just the surface Smarty needs —
/// the initialize handshake, tool discovery, and tool calls.
/// </summary>
/// <remarks>
/// Requests carry an id and are matched to their reply by it, so calls may overlap. Server-initiated requests
/// are answered rather than ignored: an unanswered request leaves a well-behaved server waiting forever, so
/// anything we don't implement gets a method-not-found reply and <c>ping</c> gets an empty result.
/// </remarks>
public sealed class McpClient : IAsyncDisposable
{
    /// <summary>The protocol version asked for by default. The server replies with the version it settled on,
    /// which is what <see cref="ProtocolVersion"/> reports.</summary>
    public const string DefaultProtocolVersion = "2025-06-18";

    private readonly IMcpTransport _transport;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private long _nextId;
    private volatile bool _closed;

    public McpClient(IMcpTransport transport, Action<string>? log = null)
    {
        _transport = transport;
        _log = log;
        _transport.MessageReceived += OnMessage;
        _transport.Closed += OnClosed;
    }

    /// <summary>Raised when the server says its tool list has changed.</summary>
    public event Action? ToolsChanged;

    public string? ServerName { get; private set; }

    public string? ServerVersion { get; private set; }

    /// <summary>The protocol version the server agreed to, once the handshake has run.</summary>
    public string? ProtocolVersion { get; private set; }

    public bool IsAlive => _transport.IsAlive && !_closed;

    /// <summary>Start the transport and run the initialize handshake.</summary>
    public async Task InitializeAsync(
        string protocolVersion, string clientName, TimeSpan timeout, CancellationToken ct = default)
    {
        await _transport.StartAsync(ct).ConfigureAwait(false);

        var result = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = protocolVersion,
            // No capabilities are declared: Smarty consumes tools and offers the server nothing back, so
            // claiming sampling or roots would be a lie a server could act on.
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = clientName, ["version"] = "1.0.0" },
        }, timeout, ct).ConfigureAwait(false);

        ProtocolVersion = Text(result, "protocolVersion");
        if (result.TryGetProperty("serverInfo", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            ServerName = Text(info, "name");
            ServerVersion = Text(info, "version");
        }

        await NotifyAsync("notifications/initialized", null, ct).ConfigureAwait(false);
    }

    /// <summary>Every tool the server offers, following pagination to the end.</summary>
    public async Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var tools = new List<McpToolDefinition>();
        string? cursor = null;

        do
        {
            var parameters = new JsonObject();
            if (cursor is not null) parameters["cursor"] = cursor;

            var result = await RequestAsync("tools/list", parameters, timeout, ct).ConfigureAwait(false);
            if (result.TryGetProperty("tools", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var tool in list.EnumerateArray())
                {
                    if (Text(tool, "name") is not { Length: > 0 } name) continue;
                    var schema = tool.TryGetProperty("inputSchema", out var s) && s.ValueKind == JsonValueKind.Object
                        ? s.GetRawText()
                        : """{"type":"object","properties":{}}""";
                    tools.Add(new McpToolDefinition(name, Text(tool, "description") ?? "", schema));
                }

            cursor = Text(result, "nextCursor");
        }
        while (cursor is { Length: > 0 } && tools.Count < 500); // a server that paginates forever doesn't get to hang startup

        return tools;
    }

    /// <summary>Call a tool. Returns the raw <c>tools/call</c> result — interpreting its content blocks is the
    /// caller's job.</summary>
    public Task<JsonElement> CallToolAsync(
        string name, JsonElement arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        var parameters = new JsonObject { ["name"] = name };
        parameters["arguments"] = arguments.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(arguments.GetRawText())
            : new JsonObject();

        return RequestAsync("tools/call", parameters, timeout, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _transport.MessageReceived -= OnMessage;
        _transport.Closed -= OnClosed;
        OnClosed(null);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<JsonElement> RequestAsync(
        string method, JsonNode? parameters, TimeSpan timeout, CancellationToken ct)
    {
        if (_closed) throw new McpException($"the connection to the MCP server is closed (calling {method}).");

        long id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            var envelope = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters is not null) envelope["params"] = parameters;
            await _transport.SendAsync(envelope.ToJsonString(), ct).ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var registration = linked.Token.Register(() =>
            {
                if (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    completion.TrySetException(new McpException(
                        $"the MCP server did not answer {method} within {timeout.TotalSeconds:0}s."));
                else
                    completion.TrySetCanceled(ct);
            });

            return await completion.Task.ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new McpException($"could not reach the MCP server ({method}): {ex.Message}");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task NotifyAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        var envelope = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null) envelope["params"] = parameters;
        await _transport.SendAsync(envelope.ToJsonString(), ct).ConfigureAwait(false);
    }

    private void OnMessage(string message)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(message); }
        catch (JsonException)
        {
            // Servers occasionally print to stdout despite the spec. It isn't ours to interpret; note and move on.
            _log?.Invoke($"ignored non-JSON output: {Snip(message)}");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            bool hasId = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number;
            string? method = Text(root, "method");

            if (method is not null)
            {
                if (hasId) AnswerServerRequest(method, idElement.GetInt64());
                else if (method == "notifications/tools/list_changed") ToolsChanged?.Invoke();
                return;
            }

            if (!hasId || !_pending.TryRemove(idElement.GetInt64(), out var completion)) return;

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                int code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                completion.TrySetException(new McpException(Text(error, "message") ?? "the MCP server returned an error.", code));
                return;
            }

            completion.TrySetResult(root.TryGetProperty("result", out var result)
                ? result.Clone() // the document is disposed on the way out of this block
                : default);
        }
    }

    // A server may ask US things. Answer everything: ping with an empty result, the rest with method-not-found,
    // so nothing is left waiting on a client that doesn't implement it.
    private void AnswerServerRequest(string method, long id)
    {
        var envelope = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
        if (method == "ping")
            envelope["result"] = new JsonObject();
        else
            envelope["error"] = new JsonObject
            {
                ["code"] = -32601,
                ["message"] = $"{method} is not supported by this client.",
            };

        _ = _transport.SendAsync(envelope.ToJsonString(), CancellationToken.None)
            .ContinueWith(t => _log?.Invoke($"could not answer {method}: {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    private void OnClosed(Exception? reason)
    {
        _closed = true;
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var completion))
                completion.TrySetException(new McpException(
                    $"the MCP server connection closed{(reason is null ? "" : $": {reason.Message}")}."));
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string Snip(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
