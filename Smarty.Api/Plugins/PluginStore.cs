using System.Text.Json;

namespace Smarty.Api.Plugins;

/// <summary>
/// What Smarty remembers about one installed plugin between restarts: where its files are, which DLL to load,
/// whether it's switched on, and — the whole point of the configuration form — the values it was given.
/// </summary>
public sealed record PluginInstall
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>Folder name under the plugins root. A name, not a path, so moving the data directory doesn't
    /// strand every plugin at an address that no longer exists.</summary>
    public string Folder { get; init; } = "";

    /// <summary>The DLL that implements IPlugin, relative to <see cref="Folder"/>.</summary>
    public string EntryAssembly { get; init; } = "";

    public string TypeName { get; init; } = "";

    public bool Enabled { get; init; } = true;

    /// <summary>The answers to <c>GetConfigurationKeys</c>, by key. This is the part that has to survive a
    /// restart; everything else could be rediscovered from the folder.</summary>
    /// <summary>
    /// Everything the plugin knows: what setup put there and what the plugin obtained for itself. There is no
    /// separate configuration — a setup stage writes into this, and a plugin reads only this.
    /// </summary>
    /// <remarks>
    /// Never rendered as a form and never sent to the browser. The control centre shows the STAGE a plugin is
    /// waiting on, which is the plugin's own description of what it wants next; it never shows what's been
    /// stored. That is what keeps a session token — or a password typed into a stage — out of every page load,
    /// and it is why the shape here is opaque to the host rather than a set of declared keys.
    /// </remarks>
    public Dictionary<string, string> State { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset Installed { get; init; }
}

/// <summary>
/// The installed plugins, persisted to <c>plugins.json</c>. Deliberately separate from
/// <see cref="IntegrationConfig"/>: a plugin's keys are declared by the plugin at runtime, not by code, so they
/// belong with the record of the plugin rather than in a file of hand-edited integration credentials.
/// </summary>
public sealed class PluginStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, PluginInstall> _installs = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _path;
    private readonly JsonSerializerOptions _json;

    /// <summary>In-memory only — for tests and for hosts that don't manage plugins.</summary>
    public PluginStore() : this(null, new JsonSerializerOptions(JsonSerializerDefaults.Web)) { }

    public PluginStore(string? path, JsonSerializerOptions json)
    {
        _path = path;
        _json = json;
        Load();
    }

    public IReadOnlyList<PluginInstall> All
    {
        get { lock (_lock) return _installs.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToList(); }
    }

    public PluginInstall? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_lock) return _installs.TryGetValue(id.Trim(), out var p) ? p : null;
    }

    public void Put(PluginInstall install)
    {
        lock (_lock)
        {
            _installs[install.Id] = install;
            Save();
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            if (!_installs.Remove(id)) return false;
            Save();
            return true;
        }
    }

    private void Load()
    {
        try
        {
            if (_path is not null && File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<List<PluginInstall>>(File.ReadAllText(_path), _json);
                foreach (var install in loaded ?? new())
                    if (!string.IsNullOrWhiteSpace(install.Id))
                        // Keys are matched case-insensitively everywhere else; a round trip through JSON
                        // would otherwise quietly hand back an ordinal dictionary.
                        _installs[install.Id] = install with
                        {
                            State = new Dictionary<string, string>(
                                install.State ?? new(), StringComparer.OrdinalIgnoreCase),
                        };
            }
        }
        catch { /* a corrupt plugins file shouldn't stop the host booting */ }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_installs.Values.ToList(), _json));
        }
        catch { /* best-effort persistence */ }
    }
}
