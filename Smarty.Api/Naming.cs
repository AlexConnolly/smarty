using System.Text.Json;

namespace Smarty.Api;

/// <summary>
/// What the assistant is called.
/// </summary>
/// <remarks>
/// <para>
/// Its own tiny store rather than a setting in a config file, because it is chosen by the person using it and can be
/// changed by them — a name baked into the build cannot be either. It is asked for once, at setup, alongside their own
/// name, and both are required: an assistant that refers to itself by a placeholder while knowing exactly who you are is
/// worse than one that knows neither.
/// </para>
/// <para>
/// Deliberately NOT a node in the graph. The graph is the user's memory of their own world, and everything in it turns up
/// in the list shown on every write and every question; the assistant's own name has no business competing for attention
/// there, and would be reconciled over the moment somebody mentioned it in passing.
/// </para>
/// </remarks>
public sealed class Naming
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json;
    private readonly object _lock = new();
    private string _assistant = "";

    public Naming(string path, JsonSerializerOptions json)
    {
        _path = path;
        _json = json;
        Load();
    }

    /// <summary>The chosen name, or empty when nobody has chosen one yet.</summary>
    public string Assistant
    {
        get { lock (_lock) return _assistant; }
    }

    public bool Chosen => Assistant.Length > 0;

    /// <summary>
    /// How the assistant refers to itself in its own prompt.
    /// </summary>
    /// <remarks>
    /// A fallback that reads as a description rather than a name, so an unnamed assistant sounds unnamed instead of
    /// sounding like it is called something odd.
    /// </remarks>
    public string OrDescription => Chosen ? Assistant : "the user's assistant";

    public string Choose(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return Assistant;

        // One word or two, and short. A name is typed into a greeting and a message box, and anything longer is a
        // sentence somebody pasted rather than a name they chose.
        if (trimmed.Length > 24) trimmed = trimmed[..24].Trim();

        lock (_lock)
        {
            _assistant = trimmed;
            Save();
            return _assistant;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            if (doc.RootElement.TryGetProperty("assistant", out var found)
                && found.ValueKind == JsonValueKind.String)
                _assistant = found.GetString() ?? "";
        }
        catch
        {
            _assistant = "";
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new { assistant = _assistant }, _json));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[naming] couldn't save: {ex.Message}");
        }
    }
}
