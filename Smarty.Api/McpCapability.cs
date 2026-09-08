using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// An MCP server, exposed as a capability — so a server someone else wrote lands in exactly the same place as
/// Kibana or Jira: a persona references a function, the registry resolves it to tools, and the control centre
/// lists them. Nothing above this line knows the tools came over stdio from another process.
/// </summary>
/// <remarks>
/// One of these per configured server (id <c>mcp_&lt;name&gt;</c>), built after <see cref="McpHub.StartAsync"/> has
/// discovered what each server offers. A server that failed to connect contributes no tools and reports itself
/// unconfigured, which is the same contract every other capability honours.
/// </remarks>
public sealed class McpCapability : ICapability
{
    private readonly McpServerConnection _connection;

    public McpCapability(McpServerConnection connection) => _connection = connection;

    /// <summary>The capability id for a server, used by personas that want one specific server by name.</summary>
    public static string IdFor(McpServerConfig config) => $"mcp_{McpServerConfig.Slug(config.Name)}";

    public string Id => IdFor(_connection.Config);

    public string DisplayName => $"{_connection.Config.Name} (MCP server)";

    /// <summary>Nothing: an MCP server's credentials live in its own environment, in mcp.json — never in
    /// integrations.json, and never handled by Smarty.</summary>
    public IReadOnlyList<string> RequiredConfig => Array.Empty<string>();

    public string? PromptHint => _connection.PromptHint() is { Length: > 0 } hint ? hint : null;

    /// <summary>Reports the server's state at startup. Deliberately never throws — an MCP server being absent is
    /// a missing integration, not a broken host.</summary>
    public void ValidateSystemPrerequisites()
    {
        if (_connection.Connected)
            Console.WriteLine($"[startup]   {_connection.Config.Name}: {_connection.Tools.Count} tool(s) — " +
                              string.Join(", ", _connection.Tools.Select(t => t.Name)));
        else
            Console.WriteLine($"[startup]   {_connection.Config.Name}: not connected" +
                              (_connection.Error is { } e ? $" ({e})" : "") + " — its tools won't be offered.");
    }

    public IReadOnlyList<AgentTool> BuildTools(IntegrationConfig config, TaskInfo task) => _connection.Tools;
}
