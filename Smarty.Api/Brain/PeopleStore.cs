using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smarty.Api;

/// <summary>One human, and every surface identity that is them.</summary>
public sealed class Person
{
    public string Email { get; set; } = "";

    public string? Name { get; set; }

    /// <summary>Surface-qualified identities that resolve to this person, e.g. <c>slack:U123</c>,
    /// <c>discord:456</c>, <c>web:local</c>.</summary>
    public List<string> Aliases { get; set; } = new();

    [JsonIgnore]
    public PersonId Id => PersonId.From(Email);
}

/// <summary>
/// Who's who, across surfaces. A person is their email address; every other identity — a Slack user id, a
/// Discord id, the local web user — is an alias that resolves to it.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes one brain work for one person and for an organisation without configuring two systems:
/// the audience model only needs stable person ids, and those come from here. An email thread and a Slack
/// channel with the same humans resolve to the same audience, so knowledge joins up instead of forking per
/// surface.
/// </para>
/// <para>
/// Lookups a host can answer (Slack's <c>users.info</c> gives an email for a user id) are plugged in as a
/// resolver, so this store never knows what a Slack is. Results are cached to disk: a person's email doesn't
/// change often, and re-asking per turn would put a network call in the middle of every message.
/// </para>
/// </remarks>
public sealed class PeopleStore
{
    /// <summary>The stand-in identity for the local single-user app when no real address is configured. A real,
    /// stable person id — so a solo user's brain is genuinely private rather than degrading to public-only —
    /// which they can point at their work address later to join it up with Slack.</summary>
    public const string LocalSelfEmail = "self@smarty.local";

    private readonly object _lock = new();
    private readonly Dictionary<string, Person> _byEmail = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PersonId> _byAlias = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _path;
    private readonly JsonSerializerOptions _json;
    private readonly Func<string, CancellationToken, Task<(string? Email, string? Name)>>? _lookup;

    public PeopleStore(
        string? path = null,
        JsonSerializerOptions? json = null,
        Func<string, CancellationToken, Task<(string? Email, string? Name)>>? lookup = null)
    {
        _path = path;
        _json = json ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _lookup = lookup;
        Load();
    }

    /// <summary>The local app's own user — a configured address, or the stable local stand-in.</summary>
    public PersonId Self { get; private set; } = PersonId.From(LocalSelfEmail);

    /// <summary>Point the local user at a real address (from config), so this machine's brain and the same
    /// human's Slack identity are one person.</summary>
    public void SetSelf(string? email, string? name = null)
    {
        var id = PersonId.From(email);

        // A name with no address still has to land. This returned early on an empty email, so somebody who gave only
        // their name was recorded nowhere — the call reported nothing wrong and the directory stayed empty.
        if (id.IsEmpty)
        {
            if (name is { Length: > 0 }) Link("web:local", Self, name);
            return;
        }

        Self = id;
        Link("web:local", id, name);
    }

    /// <summary>Resolve a surface identity to a person, without asking anyone. Empty when unknown — callers must
    /// treat that as an unidentified participant, never as "no restriction".</summary>
    public PersonId Resolve(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return default;
        var raw = alias.Trim();

        // An address is already the canonical form; "email:x@y" is the same thing wearing a prefix.
        if (raw.StartsWith("email:", StringComparison.OrdinalIgnoreCase)) raw = raw[6..];
        if (raw.Contains('@')) return PersonId.From(raw);

        lock (_lock) return _byAlias.TryGetValue(raw, out var id) ? id : default;
    }

    /// <summary>Resolve a surface identity, asking the host's lookup (and caching the answer) when it isn't
    /// known yet. Falls back to <see cref="Resolve"/>'s answer — including empty — if the lookup can't say.</summary>
    public async Task<PersonId> ResolveAsync(string? alias, CancellationToken ct = default)
    {
        var known = Resolve(alias);
        if (!known.IsEmpty || _lookup is null || string.IsNullOrWhiteSpace(alias)) return known;

        try
        {
            var (email, name) = await _lookup(alias.Trim(), ct).ConfigureAwait(false);
            var id = PersonId.From(email);
            if (id.IsEmpty) return default;
            Link(alias.Trim(), id, name);
            return id;
        }
        catch
        {
            return default; // an identity we can't establish is an unknown participant, not a free pass
        }
    }

    /// <summary>Resolve several identities at once — a channel's membership.</summary>
    public async Task<IReadOnlyList<PersonId>> ResolveAllAsync(
        IEnumerable<string> aliases, CancellationToken ct = default)
    {
        var resolved = new List<PersonId>();
        foreach (var alias in aliases)
        {
            var id = await ResolveAsync(alias, ct).ConfigureAwait(false);
            if (!id.IsEmpty) resolved.Add(id);
        }
        return resolved;
    }

    /// <summary>Attach a surface identity to a person.</summary>
    public void Link(string alias, PersonId person, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(alias) || person.IsEmpty) return;
        alias = alias.Trim();

        lock (_lock)
        {
            if (!_byEmail.TryGetValue(person.Value, out var record))
                _byEmail[person.Value] = record = new Person { Email = person.Value };
            if (!string.IsNullOrWhiteSpace(name)) record.Name = name;
            if (!record.Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase))
                record.Aliases.Add(alias);
            _byAlias[alias] = person;
            Save();
        }
    }

    /// <summary>A display name for a person, falling back to the local part of their address.</summary>
    public string NameOf(PersonId person)
    {
        if (person.IsEmpty) return "someone";
        lock (_lock)
        {
            if (_byEmail.TryGetValue(person.Value, out var record) && record.Name is { Length: > 0 } name)
                return name;
        }
        var at = person.Value.IndexOf('@');
        return at > 0 ? person.Value[..at] : person.Value;
    }

    public IReadOnlyList<Person> All
    {
        get { lock (_lock) return _byEmail.Values.OrderBy(p => p.Email, StringComparer.Ordinal).ToList(); }
    }

    private void Load()
    {
        try
        {
            if (_path is null || !File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<List<Person>>(File.ReadAllText(_path), _json);
            if (loaded is null) return;
            foreach (var person in loaded.Where(p => !string.IsNullOrWhiteSpace(p.Email)))
            {
                _byEmail[person.Email] = person;
                foreach (var alias in person.Aliases) _byAlias[alias] = person.Id;
            }
        }
        catch { /* a corrupt directory shouldn't crash startup — it rebuilds from lookups */ }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_byEmail.Values.ToList(), _json));
        }
        catch { /* best-effort persistence */ }
    }
}
