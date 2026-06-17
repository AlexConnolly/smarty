namespace Smarty.Agents;

/// <summary>
/// Describes a single input field of an <see cref="AgentTool"/>. A tool exposes a list
/// of these, which together form the JSON-Schema <c>parameters</c> object handed to the model.
/// </summary>
public sealed class ToolParameter
{
    public ToolParameter(string name, string type, string description, bool required = false)
    {
        Name = name;
        Type = type;
        Description = description;
        Required = required;
    }

    public string Name { get; }

    /// <summary>JSON-Schema type: <c>string</c>, <c>integer</c>, <c>number</c>, <c>boolean</c>, etc.</summary>
    public string Type { get; }

    public string Description { get; }

    public bool Required { get; }

    public static ToolParameter String(string name, string description, bool required = false)
        => new(name, "string", description, required);

    public static ToolParameter Integer(string name, string description, bool required = false)
        => new(name, "integer", description, required);

    public static ToolParameter Number(string name, string description, bool required = false)
        => new(name, "number", description, required);

    public static ToolParameter Boolean(string name, string description, bool required = false)
        => new(name, "boolean", description, required);
}
