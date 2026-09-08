using System.Text.Json;
using System.Text.Json.Nodes;

namespace Smarty.Agents;

/// <summary>
/// Turns an <see cref="AgentTool"/> into the JSON-Schema shape providers hand the model as a function
/// declaration. One implementation, shared by every provider, so a tool is described identically wherever it
/// runs — and so a parameter that arrived with a real schema (see <see cref="ToolParameter.Schema"/>) keeps it
/// instead of being flattened to a type-and-description pair that loses its enum, items or union.
/// </summary>
public static class ToolSchema
{
    /// <summary>The <c>parameters</c> object for a tool: <c>{ type: "object", properties: {...}, required: [...] }</c>.</summary>
    public static JsonObject Parameters(AgentTool tool)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var p in tool.Parameters)
        {
            properties[p.Name] = Property(p);
            if (p.Required) required.Add(p.Name);
        }

        var parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0)
            parameters["required"] = required;

        return parameters;
    }

    /// <summary>The full OpenAI-style function declaration for a tool.</summary>
    public static JsonObject Function(AgentTool tool) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["parameters"] = Parameters(tool),
        },
    };

    /// <summary>The display type and description carried by a raw schema fragment — what a
    /// <see cref="ToolParameter"/> shows when its shape came from JSON rather than from a factory method.
    /// A union of types reads as <c>"string|number"</c>; an unreadable fragment falls back to <c>"string"</c>.</summary>
    public static (string Type, string Description) Describe(string schema)
    {
        try
        {
            using var doc = JsonDocument.Parse(schema);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ("string", "");

            string description = root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? "" : "";

            string type = "string";
            if (root.TryGetProperty("type", out var t))
            {
                if (t.ValueKind == JsonValueKind.String)
                    type = t.GetString() ?? "string";
                else if (t.ValueKind == JsonValueKind.Array)
                    type = string.Join('|', t.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()));
            }

            return (string.IsNullOrWhiteSpace(type) ? "string" : type, description);
        }
        catch (JsonException)
        {
            return ("string", "");
        }
    }

    // A parameter's schema fragment: the declared one when it parses (topped up with the description we hold),
    // otherwise the simple type + description form every hand-written tool uses.
    private static JsonNode Property(ToolParameter p)
    {
        if (p.Schema is { } raw)
        {
            try
            {
                // Parse fresh each time: the node is handed to a caller's payload, and a JsonNode can only have
                // one parent.
                if (JsonNode.Parse(raw) is JsonObject obj)
                {
                    if (!obj.ContainsKey("description") && p.Description.Length > 0)
                        obj["description"] = p.Description;
                    return obj;
                }
            }
            catch (JsonException)
            {
                // A malformed fragment shouldn't cost us the tool — fall through to the simple form.
            }
        }

        return new JsonObject
        {
            ["type"] = p.Type,
            ["description"] = p.Description,
        };
    }
}
