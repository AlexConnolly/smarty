using System.Text.Json;

namespace Smarty.Agents;

/// <summary>Who authored a <see cref="Message"/>.</summary>
public enum Role
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>A single request from the model to invoke a tool.</summary>
public sealed class ToolCall
{
    public ToolCall(string id, string name, JsonElement arguments)
    {
        Id = id;
        Name = name;
        Arguments = new ToolCallArguments(arguments);
    }

    /// <summary>Provider-supplied id (may be synthesized when the provider omits one).</summary>
    public string Id { get; }

    /// <summary>Name of the tool the model wants to call.</summary>
    public string Name { get; }

    /// <summary>Typed accessor over the model-supplied argument object.</summary>
    public ToolCallArguments Arguments { get; }
}

/// <summary>One turn in a conversation.</summary>
public sealed class Message
{
    public Role Role { get; init; }

    /// <summary>Plain text content. For assistant tool-call turns this can be empty.</summary>
    public string? Content { get; init; }

    /// <summary>Model reasoning, when the provider exposes it separately from <see cref="Content"/>.</summary>
    public string? Reasoning { get; init; }

    /// <summary>Tool calls requested by an assistant turn.</summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>For <see cref="Role.Tool"/> messages: the id of the call this answers.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>For <see cref="Role.Tool"/> messages: the name of the tool that produced the result.</summary>
    public string? ToolName { get; init; }

    public static Message System(string content) => new() { Role = Role.System, Content = content };
    public static Message User(string content) => new() { Role = Role.User, Content = content };
    public static Message Assistant(string? content, string? reasoning = null, IReadOnlyList<ToolCall>? toolCalls = null)
        => new() { Role = Role.Assistant, Content = content, Reasoning = reasoning, ToolCalls = toolCalls };
    public static Message ToolResult(string toolCallId, string toolName, string content)
        => new() { Role = Role.Tool, ToolCallId = toolCallId, ToolName = toolName, Content = content };
}
