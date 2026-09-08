using System.Text.Json;
using Smarty.Agents;
using Smarty.Plugins;

namespace Smarty.Api.Plugins;

/// <summary>
/// An uploaded plugin, wearing the same face as Kibana or a connected MCP server: a capability whose tools a
/// persona can be given. Nothing above this class knows the commands came out of a zip someone dropped on the
/// control centre an hour ago.
/// </summary>
/// <remarks>
/// The commands are built ONCE, when the plugin is registered or its configuration changes, and handed to this
/// constructor — so a plugin holding a client or a session builds it once rather than on every task, and a
/// worker's toolset costs nothing to assemble. The plugin's own configuration is already closed over by then,
/// which is why <see cref="RequiredConfig"/> is empty: nothing this capability needs lives in
/// integrations.json, so claiming keys there would report every working plugin as unconfigured.
/// </remarks>
public sealed class PluginCapability : ICapability
{
    private readonly IReadOnlyList<AgentTool> _tools;

    public PluginCapability(string pluginId, string name, string description, IReadOnlyList<PluginCommand> commands)
    {
        Id = IdFor(pluginId);
        PluginId = pluginId;
        DisplayName = $"{name} (plugin)";
        Commands = commands;
        _tools = commands.Select(c => ToTool(pluginId, c)).ToList();

        PromptHint = _tools.Count == 0
            ? null
            : $"{name}{(string.IsNullOrWhiteSpace(description) ? "" : " — " + description.TrimEnd() + " ")}" +
              $"Use {NaturalList(_tools.Select(t => t.Name))} to do it, and report what they actually return.";
    }

    /// <summary>The capability id a plugin claims. Prefixed so an uploaded plugin can never shadow a built-in
    /// integration by being called "github".</summary>
    public static string IdFor(string pluginId) => $"plugin_{pluginId}";

    public string Id { get; }

    public string PluginId { get; }

    public string DisplayName { get; }

    public IReadOnlyList<PluginCommand> Commands { get; }

    /// <summary>Nothing — a plugin's configuration lives with the plugin, not in integrations.json.</summary>
    public IReadOnlyList<string> RequiredConfig => Array.Empty<string>();

    public string? PromptHint { get; }

    public void ValidateSystemPrerequisites() =>
        Console.WriteLine($"[startup]   {DisplayName}: {_tools.Count} command(s) — " +
                          string.Join(", ", _tools.Select(t => t.Name)));

    public IReadOnlyList<AgentTool> BuildTools(IntegrationConfig config, TaskInfo task) => _tools;

    /// <summary>The tool name a command answers to: the plugin's slug, then the command. Two plugins can both
    /// have a "status" command, and neither has to know the other exists.</summary>
    public static string ToolName(string pluginId, string command)
    {
        var clean = new string((command ?? "").Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        while (clean.Contains("__")) clean = clean.Replace("__", "_");
        return clean.StartsWith(pluginId + "_", StringComparison.Ordinal) ? clean : $"{pluginId}_{clean}";
    }

    private static AgentTool ToTool(string pluginId, PluginCommand command)
    {
        var required = command.Parameters.Where(p => p.Value.Required).Select(p => p.Key).ToList();
        var parameters = command.Parameters
            .Select(p => new ToolParameter(p.Key, JsonType(p.Value.Type), p.Value.Description, p.Value.Required))
            .ToList();

        return new AgentTool(
            ToolName(pluginId, command.Name),
            command.Description,
            parameters,
            async (args, ct) =>
            {
                var values = Values(args);

                // Caught here rather than left to the plugin, so every plugin gets the same clear message
                // instead of each one inventing its own null check — or not writing one.
                var missing = required.Where(r => !values.Has(r)).ToList();
                if (missing.Count > 0)
                    return ToolOutput.Error($"Missing required parameter(s): {string.Join(", ", missing)}.");

                try
                {
                    // The model gets the line. The same execution's structured half is for a panel, and never
                    // travels into a conversation — it is the same answer, not a second one.
                    var result = await command.ExecuteAsync(values, ct).ConfigureAwait(false);
                    return string.IsNullOrWhiteSpace(result.Text)
                        ? ToolOutput.Ok("(the command returned nothing)")
                        : ToolOutput.Ok(result.Text);
                }
                catch (OperationCanceledException) { throw; }
                // The plugin's own way of saying "this can never work" — no amount of retrying finds a town
                // that doesn't exist or a vacuum that isn't paired.
                catch (PluginDeadEndException ex) { return ToolOutput.DeadEnd(ex.Message); }
                catch (Exception ex) { return ToolOutput.Error($"{command.Name} failed: {ex.Message}"); }
            });
    }

    /// <summary>The model's JSON arguments as the flat string map a plugin is promised. A nested object or array
    /// arrives as its raw JSON, which is the only lossless thing a string can hold.</summary>
    private static PluginValues Values(ToolCallArguments args)
    {
        var values = new List<KeyValuePair<string, string>>();
        if (args.Raw.ValueKind == JsonValueKind.Object)
            foreach (var property in args.Raw.EnumerateObject())
                values.Add(new(property.Name, property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? "",
                    JsonValueKind.Null or JsonValueKind.Undefined => "",
                    _ => property.Value.GetRawText(),
                }));
        return new PluginValues(values);
    }

    private static string JsonType(PluginValueType type) => type switch
    {
        PluginValueType.Integer => "integer",
        PluginValueType.Number => "number",
        PluginValueType.Boolean => "boolean",
        _ => "string",
    };

    private static string NaturalList(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
        };
    }
}
