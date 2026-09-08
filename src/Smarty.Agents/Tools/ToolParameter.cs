namespace Smarty.Agents;

/// <summary>
/// Describes a single input field of an <see cref="AgentTool"/>. A tool exposes a list
/// of these, which together form the JSON-Schema <c>parameters</c> object handed to the model.
/// </summary>
public sealed class ToolParameter
{
    public ToolParameter(string name, string type, string description, bool required = false, string? schema = null)
    {
        Name = name;
        Type = type;
        Description = description;
        Required = required;
        Schema = string.IsNullOrWhiteSpace(schema) ? null : schema;
    }

    public string Name { get; }

    /// <summary>JSON-Schema type: <c>string</c>, <c>integer</c>, <c>number</c>, <c>boolean</c>, etc.</summary>
    public string Type { get; }

    public string Description { get; }

    public bool Required { get; }

    /// <summary>
    /// The raw JSON-Schema fragment for this field, used verbatim when it's set — so constraints a
    /// <see cref="Type"/>/<see cref="Description"/> pair can't express (an enum, an array's <c>items</c>, a union
    /// of types) survive the trip to the model. Externally-declared tools (an MCP server's) arrive with a real
    /// schema and keep it; hand-written tools leave this null and get the simple two-field form.
    /// </summary>
    public string? Schema { get; }

    public static ToolParameter String(string name, string description, bool required = false)
        => new(name, "string", description, required);

    public static ToolParameter Integer(string name, string description, bool required = false)
        => new(name, "integer", description, required);

    public static ToolParameter Number(string name, string description, bool required = false)
        => new(name, "number", description, required);

    public static ToolParameter Boolean(string name, string description, bool required = false)
        => new(name, "boolean", description, required);

    /// <summary>A parameter defined by a JSON-Schema fragment (as supplied by an external tool provider).
    /// <paramref name="schema"/> is passed to the model as-is; <see cref="Type"/>/<see cref="Description"/> are
    /// read out of it for display.</summary>
    public static ToolParameter FromSchema(string name, string schema, bool required = false)
    {
        var (type, description) = ToolSchema.Describe(schema);
        return new ToolParameter(name, type, description, required, schema);
    }
}
