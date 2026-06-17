using System.Text.Json;

namespace Smarty.Agents;

/// <summary>
/// Typed accessor over the JSON argument object a model supplies for a tool call.
/// Providers hand us a parsed <see cref="JsonElement"/> object; this wraps it with
/// convenient, forgiving getters.
/// </summary>
public sealed class ToolCallArguments
{
    private readonly JsonElement _root;

    public ToolCallArguments(JsonElement root)
    {
        _root = root.ValueKind == JsonValueKind.Object ? root : default;
    }

    /// <summary>The raw argument object, useful for custom extraction.</summary>
    public JsonElement Raw => _root;

    public bool Has(string name) =>
        _root.ValueKind == JsonValueKind.Object && _root.TryGetProperty(name, out _);

    public string GetString(string name) =>
        GetStringOrNull(name) ?? throw new ArgumentException($"Required tool argument '{name}' is missing.");

    public string? GetStringOrNull(string name)
    {
        if (!TryGet(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Null => null,
            _ => v.GetRawText(),
        };
    }

    public int? GetIntOrNull(string name)
    {
        if (!TryGet(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(v.GetString(), out var i) => i,
            _ => null,
        };
    }

    public int GetInt(string name, int fallback = 0) => GetIntOrNull(name) ?? fallback;

    public bool? GetBoolOrNull(string name)
    {
        if (!TryGet(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => null,
        };
    }

    public bool GetBool(string name, bool fallback = false) => GetBoolOrNull(name) ?? fallback;

    public override string ToString() =>
        _root.ValueKind == JsonValueKind.Object ? _root.GetRawText() : "{}";

    private bool TryGet(string name, out JsonElement value)
    {
        if (_root.ValueKind == JsonValueKind.Object && _root.TryGetProperty(name, out value))
            return true;
        value = default;
        return false;
    }
}
