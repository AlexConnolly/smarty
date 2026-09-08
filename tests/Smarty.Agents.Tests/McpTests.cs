using System.Text.Json;
using System.Text.Json.Nodes;
using Smarty.Agents;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// The MCP client, in full, without a child process: <see cref="FakeMcpServer"/> speaks JSON-RPC back over the
/// transport interface, so the handshake, tool discovery, schema fidelity, result flattening, failure handling and
/// reconnect are all exercised exactly as they run against a real server — just deterministically.
/// </summary>
public class McpTests
{
    private const string BrowserTools = """
    [
      { "name": "tabs_context", "description": "The tabs this session may touch.",
        "inputSchema": { "type": "object", "properties": { "createIfEmpty": { "type": "boolean", "description": "Open a tab if the group is empty." } } } },
      { "name": "navigate", "description": "Go to a URL, or \"back\" / \"forward\".",
        "inputSchema": { "type": "object", "properties": { "url": { "type": "string" }, "tabId": { "type": "number" } }, "required": ["url"] } },
      { "name": "read_page", "description": "The page as a flat list of [ref] role \"name\" lines.",
        "inputSchema": { "type": "object", "properties": { "filter": { "type": "string", "enum": ["all", "interactive"] } } } },
      { "name": "form_input", "description": "Set a form value by ref.",
        "inputSchema": { "type": "object", "properties": { "ref": { "type": "string" }, "value": { "type": ["string", "boolean", "number"] } }, "required": ["ref", "value"] } }
    ]
    """;

    private static McpServerConfig Config(string name = "chrome", params string[] allowed) => new()
    {
        Name = name,
        Command = "fake",
        Functions = new[] { "browser" },
        AllowedTools = allowed,
        StartupTimeout = TimeSpan.FromSeconds(5),
        CallTimeout = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public async Task Discovered_tools_are_prefixed_per_server_and_keep_their_description()
    {
        var server = new FakeMcpServer(BrowserTools);
        await using var connection = new McpServerConnection(Config(), null, _ => server);

        Assert.True(await connection.ConnectAsync());

        Assert.Equal(
            new[] { "chrome_tabs_context", "chrome_navigate", "chrome_read_page", "chrome_form_input" },
            connection.Tools.Select(t => t.Name));
        Assert.Equal("Go to a URL, or \"back\" / \"forward\".", connection.Tools[1].Description);

        // The handshake actually happened, and the server's answer is what's reported.
        Assert.Equal("open-chrome-mcp", connection.ServerName);
        Assert.Equal(McpClient.DefaultProtocolVersion, connection.ProtocolVersion);
    }

    [Fact]
    public async Task Required_arguments_survive_discovery()
    {
        var server = new FakeMcpServer(BrowserTools);
        await using var connection = new McpServerConnection(Config(), null, _ => server);
        await connection.ConnectAsync();

        var navigate = connection.Tools.Single(t => t.Name == "chrome_navigate");
        Assert.Equal(new[] { "url", "tabId" }, navigate.Parameters.Select(p => p.Name));
        Assert.True(navigate.Parameters.Single(p => p.Name == "url").Required);
        Assert.False(navigate.Parameters.Single(p => p.Name == "tabId").Required);
        Assert.Equal("number", navigate.Parameters.Single(p => p.Name == "tabId").Type);
    }

    /// <summary>
    /// The point of carrying the server's own schema rather than a type-and-description pair: an enum and a union
    /// of types reach the model intact. Flattening these would have the model guessing at what a tool accepts.
    /// </summary>
    [Fact]
    public async Task A_servers_own_schema_reaches_the_model_intact()
    {
        var server = new FakeMcpServer(BrowserTools);
        await using var connection = new McpServerConnection(Config(), null, _ => server);
        await connection.ConnectAsync();

        var readPage = ToolSchema.Function(connection.Tools.Single(t => t.Name == "chrome_read_page"));
        var filter = readPage["function"]!["parameters"]!["properties"]!["filter"]!;
        Assert.Equal("""["all","interactive"]""", filter["enum"]!.ToJsonString());

        var formInput = ToolSchema.Function(connection.Tools.Single(t => t.Name == "chrome_form_input"));
        var value = formInput["function"]!["parameters"]!["properties"]!["value"]!;
        Assert.Equal("""["string","boolean","number"]""", value["type"]!.ToJsonString());
        Assert.Equal("""["ref","value"]""", formInput["function"]!["parameters"]!["required"]!.ToJsonString());

        // A hand-written tool is unaffected by the passthrough: it still serialises as type + description.
        var plain = ToolSchema.Function(new AgentTool("plain", "d",
            new[] { ToolParameter.String("q", "the query", required: true) }, _ => "ok"));
        Assert.Equal("""{"type":"string","description":"the query"}""",
            plain["function"]!["parameters"]!["properties"]!["q"]!.ToJsonString());
    }

    [Fact]
    public async Task Calling_a_tool_sends_the_servers_own_name_and_the_arguments_verbatim()
    {
        var server = new FakeMcpServer(BrowserTools);
        await using var connection = new McpServerConnection(Config(), null, _ => server);
        await connection.ConnectAsync();

        var navigate = connection.Tools.Single(t => t.Name == "chrome_navigate");
        var output = await navigate.InvokeAsync(Args("""{ "url": "https://example.com", "tabId": 7 }"""));

        // Prefixed for the model, unprefixed on the wire — the server never sees our naming.
        Assert.Equal("navigate", server.LastCallName);
        Assert.Equal("""{"url":"https://example.com","tabId":7}""", server.LastCallArguments);
        Assert.False(output.IsError);
        Assert.Equal("navigated", output.Content);
    }

    [Fact]
    public async Task Text_blocks_are_joined_and_an_error_result_is_a_tool_error()
    {
        var server = new FakeMcpServer(BrowserTools)
        {
            Result = (_, _) => """
            { "content": [ { "type": "text", "text": "line one" }, { "type": "text", "text": "line two" } ] }
            """,
        };
        await using var connection = new McpServerConnection(Config(), null, _ => server);
        await connection.ConnectAsync();
        var readPage = connection.Tools.Single(t => t.Name == "chrome_read_page");

        var ok = await readPage.InvokeAsync(Args("{}"));
        Assert.False(ok.IsError);
        Assert.Equal("line one\nline two", ok.Content);

        server.Result = (_, _) => """
        { "isError": true, "content": [ { "type": "text", "text": "Error: Nothing to go back to" } ] }
        """;
        var failed = await readPage.InvokeAsync(Args("{}"));
        Assert.True(failed.IsError);
        Assert.True(failed.CanRetry); // a tool-reported failure is worth a different call, not a dead run
        Assert.Contains("Nothing to go back to", failed.Content);
    }

    [Fact]
    public void A_block_with_no_text_channel_is_named_rather_than_dumped()
    {
        var output = McpTools.Interpret(Json("""
        { "content": [ { "type": "image", "mimeType": "image/png", "data": "AAAABBBBCCCC" } ] }
        """));

        Assert.False(output.IsError);
        Assert.Equal("[image: image/png, 12 base64 characters — not viewable through this tool channel]", output.Content);
        Assert.DoesNotContain("AAAABBBB", output.Content);
    }

    [Fact]
    public void Structured_content_is_used_when_there_is_no_prose()
    {
        var output = McpTools.Interpret(Json("""{ "structuredContent": { "free": 412 } }"""));
        Assert.Equal("""{ "free": 412 }""", output.Content);

        var empty = McpTools.Interpret(Json("""{ "content": [] }"""));
        Assert.False(empty.IsError);
        Assert.Contains("no content", empty.Content);
    }

    [Fact]
    public void An_enormous_result_is_capped_with_a_note()
    {
        var huge = new string('x', 500);
        var output = McpTools.Interpret(Json($$"""{ "content": [ { "type": "text", "text": "{{huge}}" } ] }"""), maxResultChars: 100);

        Assert.StartsWith(new string('x', 100), output.Content);
        Assert.Contains("truncated: 400 more characters", output.Content);
    }

    [Fact]
    public async Task An_allow_list_keeps_the_rest_of_a_servers_tools_away_from_the_model()
    {
        var server = new FakeMcpServer(BrowserTools);
        await using var connection = new McpServerConnection(Config("chrome", "navigate", "read_page"), null, _ => server);
        await connection.ConnectAsync();

        Assert.Equal(new[] { "chrome_navigate", "chrome_read_page" }, connection.Tools.Select(t => t.Name));
        // What was filtered is still reported, so the control centre can show the difference.
        Assert.Contains("form_input", connection.OfferedToolNames);
    }

    [Fact]
    public async Task A_server_that_cannot_start_contributes_nothing_and_never_throws()
    {
        var hub = new McpHub(new[] { Config("broken") }, null, _ => new FakeMcpServer(BrowserTools) { StartFailure = "node is not installed" });

        await hub.StartAsync(); // must not throw — MCP being unavailable is not a boot failure

        Assert.Empty(hub.AllTools);
        var status = Assert.Single(hub.Status);
        Assert.False(status.Connected);
        Assert.Contains("node is not installed", status.Error);
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task A_server_that_dies_is_reconnected_for_the_next_call()
    {
        int started = 0;
        var servers = new List<FakeMcpServer>();
        await using var connection = new McpServerConnection(Config(), null, _ =>
        {
            started++;
            var server = new FakeMcpServer(BrowserTools);
            servers.Add(server);
            return server;
        });

        await connection.ConnectAsync();
        var navigate = connection.Tools.Single(t => t.Name == "chrome_navigate");
        Assert.False((await navigate.InvokeAsync(Args("""{ "url": "https://example.com" }"""))).IsError);

        servers[0].Die(); // the browser bridge went away mid-session

        var afterDeath = await navigate.InvokeAsync(Args("""{ "url": "https://example.org" }"""));
        Assert.False(afterDeath.IsError);
        Assert.Equal(2, started);
        Assert.Equal("navigate", servers[1].LastCallName);
    }

    /// <summary>
    /// The regression behind a real failure: asked for a café's opening hours, the worker read a page, navigated
    /// somewhere else, and asked to read again — same <c>{tabId}</c>, different page. The loop's repeat guard saw
    /// identical arguments and refused it twice, so the worker had to fall back to scraping innerText through
    /// chrome_javascript and burned its budget. MCP tools are stateful; the guard must leave them alone.
    /// </summary>
    [Fact]
    public async Task Mcp_tools_are_repeatable_because_the_same_arguments_can_mean_a_different_page()
    {
        var server = new FakeMcpServer(BrowserTools);
        await using var connection = new McpServerConnection(Config(), null, _ => server);
        await connection.ConnectAsync();

        var readPage = connection.Tools.Single(t => t.Name == "chrome_read_page");
        Assert.True(readPage.Repeatable, "an MCP tool's result depends on state outside its arguments");

        int reads = 0;
        server.Result = (tool, _) =>
        {
            if (tool == "read_page") reads++;
            return $$"""{ "content": [ { "type": "text", "text": "page {{reads}}" } ] }""";
        };

        // Read, navigate, read again — byte-identical arguments both times.
        Assert.Equal("page 1", (await readPage.InvokeAsync(Args("""{ "tabId": 7 }"""))).Content);
        await connection.Tools.Single(t => t.Name == "chrome_navigate")
            .InvokeAsync(Args("""{ "url": "https://example.org", "tabId": 7 }"""));
        var second = await readPage.InvokeAsync(Args("""{ "tabId": 7 }"""));

        Assert.Equal("page 2", second.Content);
        Assert.False(second.IsError);
        Assert.DoesNotContain("already", second.Content);
        Assert.Equal(2, reads); // it actually reached the server both times
    }

    [Fact]
    public async Task A_tool_the_server_does_not_have_is_a_dead_end_not_a_retry()
    {
        var server = new FakeMcpServer(BrowserTools) { ErrorCode = -32601, ErrorMessage = "Unknown tool navigate" };
        await using var connection = new McpServerConnection(Config(), null, _ => server);
        await connection.ConnectAsync();

        var output = await connection.Tools.Single(t => t.Name == "chrome_navigate").InvokeAsync(Args("""{ "url": "x" }"""));

        Assert.True(output.IsError);
        Assert.False(output.CanRetry);
        Assert.Contains("Unknown tool navigate", output.Content);
    }

    [Fact]
    public async Task A_request_from_the_server_is_answered_rather_than_left_hanging()
    {
        var server = new FakeMcpServer(BrowserTools);
        await using var connection = new McpServerConnection(Config(), null, _ => server);
        await connection.ConnectAsync();

        server.AskClient("""{ "jsonrpc": "2.0", "id": 99, "method": "ping" }""");
        server.AskClient("""{ "jsonrpc": "2.0", "id": 100, "method": "sampling/createMessage" }""");

        var ping = server.Received.Single(m => m.Contains("\"id\":99"));
        Assert.Contains("\"result\"", ping);
        var unsupported = server.Received.Single(m => m.Contains("\"id\":100"));
        Assert.Contains("-32601", unsupported);
    }

    [Fact]
    public void The_server_file_is_read_the_way_every_other_host_writes_it()
    {
        var servers = McpServerConfig.Parse("""
        {
          "mcpServers": {
            "open chrome": {
              "command": "node",
              "args": ["C:/tools/open-chrome-mcp/server/src/index.js"],
              "env": { "OPEN_CHROME_MCP_PORT": "8777" },
              "functions": ["browser"],
              "tools": ["navigate"],
              "callTimeoutSeconds": 45
            },
            "off": { "command": "npx", "disabled": true }
          }
        }
        """);

        Assert.Equal(2, servers.Count);

        var chrome = servers[0];
        Assert.Equal("open chrome", chrome.Name);
        Assert.Equal("open_chrome", chrome.ResolvedPrefix); // a space can't live in a tool name
        Assert.Equal("node", chrome.Command);
        Assert.Equal(new[] { "C:/tools/open-chrome-mcp/server/src/index.js" }, chrome.Args);
        Assert.Equal("8777", chrome.Env["OPEN_CHROME_MCP_PORT"]);
        Assert.Equal(new[] { "browser" }, chrome.Functions);
        Assert.Equal(new[] { "navigate" }, chrome.AllowedTools);
        Assert.Equal(TimeSpan.FromSeconds(45), chrome.CallTimeout);
        Assert.True(chrome.Enabled);

        Assert.False(servers[1].Enabled);
    }

    [Fact]
    public void A_missing_or_unreadable_server_file_is_no_servers_not_a_crash()
    {
        Assert.Empty(McpServerConfig.Load(Path.Combine(Path.GetTempPath(), $"no-such-mcp-{Guid.NewGuid():N}.json")));

        var path = Path.Combine(Path.GetTempPath(), $"broken-mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ this is not json");
        try
        {
            string? reported = null;
            Assert.Empty(McpServerConfig.Load(path, p => reported = p));
            Assert.NotNull(reported);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The shipped example must be loadable — it's the file users copy, and the loader is what reads it.</summary>
    [Fact]
    public void The_shipped_example_config_parses()
    {
        var example = Path.Combine(RepoRoot(), "Smarty.Api", "mcp.example.json");
        Assert.True(File.Exists(example), $"expected the example config at {example}");

        var servers = McpServerConfig.Load(example);
        var chrome = servers.Single(s => s.Name == "chrome");
        Assert.Equal("node", chrome.Command);
        Assert.Contains("browser", chrome.Functions);
        Assert.Contains("chrome_tabs_context", chrome.PromptHint);
        Assert.False(servers.Single(s => s.Name == "example-disabled-server").Enabled);
    }

    private static ToolCallArguments Args(string json) => new(Json(json));

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Smarty.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("could not find the repository root");
    }
}

/// <summary>
/// An MCP server that answers over the transport interface instead of a pipe: enough of one to drive the client
/// through a real handshake, a real tool listing and real calls, plus the failures that matter (a server that
/// won't start, one that errors, one that dies mid-session).
/// </summary>
internal sealed class FakeMcpServer : IMcpTransport
{
    private readonly string _toolsJson;

    public FakeMcpServer(string toolsJson) => _toolsJson = toolsJson;

    /// <summary>Set to make <see cref="StartAsync"/> fail, the way a missing runtime does.</summary>
    public string? StartFailure { get; init; }

    /// <summary>The <c>tools/call</c> result, given the tool name and its arguments.</summary>
    public Func<string, string, string> Result { get; set; } =
        (_, _) => """{ "content": [ { "type": "text", "text": "navigated" } ] }""";

    public int? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    /// <summary>Everything the client sent — requests, notifications, and its replies to us.</summary>
    public List<string> Received { get; } = new();

    public string? LastCallName { get; private set; }

    public string? LastCallArguments { get; private set; }

    public bool IsAlive { get; private set; }

    public event Action<string>? MessageReceived;

    public event Action<string>? Log;

    public event Action<Exception?>? Closed;

    public Task StartAsync(CancellationToken ct = default)
    {
        if (StartFailure is { } failure) throw new InvalidOperationException(failure);
        IsAlive = true;
        return Task.CompletedTask;
    }

    /// <summary>Drop the connection the way a crashed server does.</summary>
    public void Die()
    {
        IsAlive = false;
        Log?.Invoke("exited with code 1");
        Closed?.Invoke(new IOException("the server exited"));
    }

    /// <summary>Send the client a server-initiated request.</summary>
    public void AskClient(string message) => MessageReceived?.Invoke(message);

    public Task SendAsync(string message, CancellationToken ct = default)
    {
        Received.Add(message);
        if (!IsAlive) throw new IOException("the server is not running");

        using var doc = JsonDocument.Parse(message);
        var root = doc.RootElement;

        // A reply from the client to something we asked, or a notification: nothing to answer.
        if (!root.TryGetProperty("method", out var methodElement)) return Task.CompletedTask;
        if (!root.TryGetProperty("id", out var id)) return Task.CompletedTask;

        string method = methodElement.GetString() ?? "";
        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.GetInt64() };

        switch (method)
        {
            case "initialize":
                reply["result"] = JsonNode.Parse($$"""
                {
                  "protocolVersion": "{{McpClient.DefaultProtocolVersion}}",
                  "capabilities": { "tools": {} },
                  "serverInfo": { "name": "open-chrome-mcp", "version": "0.1.0" }
                }
                """);
                break;

            case "tools/list":
                reply["result"] = JsonNode.Parse($$"""{ "tools": {{_toolsJson}} }""");
                break;

            case "tools/call":
                var parameters = root.GetProperty("params");
                LastCallName = parameters.GetProperty("name").GetString();
                LastCallArguments = parameters.GetProperty("arguments").GetRawText();
                if (ErrorCode is { } code)
                    reply["error"] = new JsonObject { ["code"] = code, ["message"] = ErrorMessage ?? "failed" };
                else
                    reply["result"] = JsonNode.Parse(Result(LastCallName ?? "", LastCallArguments ?? "{}"));
                break;

            default:
                reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"unknown method {method}" };
                break;
        }

        MessageReceived?.Invoke(reply.ToJsonString());
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsAlive = false;
        return ValueTask.CompletedTask;
    }
}
