using System.Collections;
using System.Globalization;

namespace Smarty.Plugins;

/// <summary>
/// The string-keyed, string-valued bag a plugin is handed for both its configuration and a command's
/// arguments, plus the conversions that turn those strings back into the types the plugin declared. It IS a
/// <see cref="IReadOnlyDictionary{TKey,TValue}"/>, so a plugin that wants the raw pairs just enumerates it.
/// </summary>
/// <remarks>
/// Every getter is forgiving and every conversion is culture-invariant: a value arrives from a JSON tool call,
/// a text box in the control centre, or a file written months ago, and "1.5" must mean one-and-a-half on a
/// machine with a comma decimal separator. A missing or unparseable value returns null (or the supplied
/// fallback) rather than throwing — <see cref="Require"/> is there for the one case where absence really is
/// fatal, and it names the key so the message is useful.
/// </remarks>
public sealed class PluginValues : IReadOnlyDictionary<string, string>
{
    private readonly Dictionary<string, string> _values;

    public PluginValues(IEnumerable<KeyValuePair<string, string>>? values = null)
    {
        _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values ?? Enumerable.Empty<KeyValuePair<string, string>>())
            if (!string.IsNullOrWhiteSpace(key)) _values[key.Trim()] = value ?? "";
    }

    public static PluginValues Empty { get; } = new();

    /// <summary>True when the key is present AND holds something other than whitespace.</summary>
    public bool Has(string name) => Text(name) is not null;

    /// <summary>The raw text, or null when the key is absent or blank.</summary>
    public string? Text(string name) =>
        name is not null && _values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    public string Text(string name, string fallback) => Text(name) ?? fallback;

    /// <summary>The text, or a <see cref="ArgumentException"/> naming the key. For the value a command cannot run without.</summary>
    public string Require(string name) =>
        Text(name) ?? throw new ArgumentException($"'{name}' is required and wasn't supplied.", name);

    public int? Integer(string name) =>
        Text(name) is { } t && int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i
            // "3.0" is an integer that took the scenic route through a JSON number.
            : Number(name) is { } d && d >= int.MinValue && d <= int.MaxValue && d == Math.Floor(d) ? (int)d
            : null;

    public int Integer(string name, int fallback) => Integer(name) ?? fallback;

    public double? Number(string name) =>
        Text(name) is { } t && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    public double Number(string name, double fallback) => Number(name) ?? fallback;

    /// <summary>True/false, accepting the spellings a model or a form actually produces: true/false, yes/no, on/off, 1/0.</summary>
    public bool? Boolean(string name) => Text(name)?.ToLowerInvariant() switch
    {
        "true" or "yes" or "y" or "on" or "1" => true,
        "false" or "no" or "n" or "off" or "0" => false,
        _ => null,
    };

    public bool Boolean(string name, bool fallback) => Boolean(name) ?? fallback;

    /// <summary>A copy with <paramref name="other"/> layered on top — how a command's arguments are merged over
    /// defaults, and how an edit to configuration is applied without losing the keys it didn't mention.</summary>
    public PluginValues With(IEnumerable<KeyValuePair<string, string>>? other) =>
        new(_values.Concat(other ?? Enumerable.Empty<KeyValuePair<string, string>>()));

    public string this[string key] => _values[key];
    public IEnumerable<string> Keys => _values.Keys;
    public IEnumerable<string> Values => _values.Values;
    public int Count => _values.Count;
    public bool ContainsKey(string key) => _values.ContainsKey(key);
    public bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
