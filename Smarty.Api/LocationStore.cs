using System.Text.Json;

namespace Smarty.Api;

/// <summary>Where the user is: the fix the browser reported, and what that place is called.</summary>
public sealed record UserLocation(
    double Latitude,
    double Longitude,
    double? AccuracyMetres,
    string? Place,
    DateTimeOffset At);

/// <summary>
/// The user's current location, so anything that depends on where they are can just know.
/// <para>
/// "Book me an Uber to London Bridge" needs a pickup point, and the only reason it can't have one is that
/// nothing ever told it. Coordinates alone aren't enough either — a model can't do much with 51.52, -0.09, so the
/// place name is resolved and both go in.
/// </para>
/// <para>
/// Deliberately the CURRENT fix and nothing else. A history of where someone has been is a different thing
/// entirely — it answers questions nobody asked it to answer — so each update replaces the last and no trail is
/// kept. The browser's own permission prompt is the consent gate: no grant, no fix, and revoking it in the
/// browser stops the updates at source.
/// </para>
/// </summary>
public sealed class LocationStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json;
    private readonly object _lock = new();
    private UserLocation? _current;

    /// <summary>
    /// How long a fix is worth believing. Beyond this it's reported as "last known" with its age, because a
    /// day-old position stated as fact is how you end up ordering a taxi to yesterday's city.
    /// </summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromHours(2);

    public LocationStore(string path, JsonSerializerOptions json)
    {
        _path = path;
        _json = json;
        Load();
    }

    public UserLocation? Current { get { lock (_lock) return _current; } }

    /// <summary>Overridable clock, so staleness can be tested without waiting two hours.</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.Now;

    /// <summary>
    /// Record a fix. Rejects anything that isn't a real coordinate rather than storing nonsense that would then
    /// be stated confidently to the model.
    /// </summary>
    public bool Set(double latitude, double longitude, double? accuracyMetres, string? place)
    {
        if (double.IsNaN(latitude) || double.IsNaN(longitude)) return false;
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180) return false;

        lock (_lock)
        {
            // Keep the place name we already resolved when the new fix is the same spot and arrives unnamed —
            // a jitter of a few metres shouldn't blank out the address.
            var keptPlace = place;
            if (string.IsNullOrWhiteSpace(keptPlace) && _current is { } prev && Near(prev, latitude, longitude))
                keptPlace = prev.Place;

            _current = new UserLocation(latitude, longitude, accuracyMetres, keptPlace, Now());
            Save();
            return true;
        }
    }

    /// <summary>Forget it. Theirs to remove, and it should take one call.</summary>
    public bool Clear()
    {
        lock (_lock)
        {
            if (_current is null) return false;
            _current = null;
            try { if (File.Exists(_path)) File.Delete(_path); } catch (IOException) { /* best effort */ }
            return true;
        }
    }

    /// <summary>
    /// Roughly the same place — within about 100m. Deliberately looser than the geocoder's ~11m cache: this only
    /// applies when a lookup FAILED, and a name from a hundred metres away beats no name at all.
    /// </summary>
    private static bool Near(UserLocation previous, double latitude, double longitude) =>
        Math.Abs(previous.Latitude - latitude) < 0.001 && Math.Abs(previous.Longitude - longitude) < 0.001;

    /// <summary>
    /// The location as the model should see it, or empty when there's nothing to say.
    /// <para>
    /// Ends with the instruction that matters more than the coordinates: acting on someone's location without
    /// checking is how a taxi turns up at the wrong address. It may USE this to work out what they mean; it must
    /// confirm before doing anything with it.
    /// </para>
    /// </summary>
    public string Note()
    {
        var location = Current;
        if (location is null) return "";

        var age = Now() - location.At;
        var stamp = age <= Fresh
            ? "currently"
            : $"as of {Age(age)} ago (their last known position — they may well have moved)";

        var place = string.IsNullOrWhiteSpace(location.Place) ? "" : $"{location.Place}, ";
        var accuracy = location.AccuracyMetres is { } metres && metres > 0
            ? $", accurate to about {Math.Round(metres)}m"
            : "";

        return $"The user is {stamp} at {place}latitude {location.Latitude:0.#####}, " +
               $"longitude {location.Longitude:0.#####}{accuracy}. Use it to work out what they mean by \"here\", " +
               "\"nearby\", \"my place\" or a journey with no starting point given — but ALWAYS confirm the " +
               "specific place with them before acting on it (booking, ordering, sending anyone anywhere). Never " +
               "state it back as their address unprompted.";
    }

    private static string Age(TimeSpan age) => age switch
    {
        { TotalMinutes: < 90 } => $"{Math.Max(1, (int)age.TotalMinutes)} minutes",
        { TotalHours: < 36 } => $"{(int)age.TotalHours} hours",
        _ => $"{(int)age.TotalDays} days",
    };

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            _current = JsonSerializer.Deserialize<UserLocation>(File.ReadAllText(_path), _json);
        }
        catch (Exception) { /* a bad file just means we don't know where they are */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_current, _json));
        }
        catch (Exception) { /* best effort — losing the fix costs a page refresh */ }
    }
}

/// <summary>
/// Coordinates to a place name, via OpenStreetMap's Nominatim.
/// <para>
/// Cached on the rounded position (about 100m) and held for the process's life: standing still must not mean a
/// request per fix, both because their usage policy asks for restraint and because every lookup hands someone
/// else the user's coordinates. A failed lookup is not an error — the coordinates alone still work, they're just
/// less useful, so the caller keeps whatever it had.
/// </para>
/// </summary>
public sealed class ReverseGeocoder
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly Dictionary<string, string?> _cache = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>
    /// How precisely the cache distinguishes positions: 4 decimal places, about 11 metres.
    /// <para>
    /// 3 places (~110m) is a city block, which is enough to name the wrong street — and the street is the part
    /// worth having. The cost is more lookups when actually moving; standing still still resolves once, since GPS
    /// jitter mostly stays inside 11m.
    /// </para>
    /// </summary>
    private const string CachePrecision = "0.0000";

    public async Task<string?> PlaceFor(double latitude, double longitude, CancellationToken ct)
    {
        var key = $"{latitude.ToString(CachePrecision)},{longitude.ToString(CachePrecision)}";
        lock (_lock)
            if (_cache.TryGetValue(key, out var hit)) return hit;

        string? place = null;
        try
        {
            var url = $"https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat={latitude:0.#####}" +
                      $"&lon={longitude:0.#####}&zoom=18&addressdetails=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Nominatim requires an identifying User-Agent and refuses requests without one.
            request.Headers.TryAddWithoutValidation("User-Agent", "smarty/1.0 (personal assistant)");

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                place = Describe(doc.RootElement);
            }
        }
        catch (Exception) { /* no name available; the coordinates still are */ }

        lock (_lock) _cache[key] = place;
        return place;
    }

    /// <summary>
    /// A short, human place name rather than the full postal string. "Cleveland Street, Fitzrovia, London" is what
    /// someone would say; the twelve-part display_name with a postcode and a country code is not.
    /// </summary>
    private static string? Describe(JsonElement root)
    {
        if (!root.TryGetProperty("address", out var address) || address.ValueKind != JsonValueKind.Object)
            return root.TryGetProperty("display_name", out var display) ? display.GetString() : null;

        string? Field(params string[] names)
        {
            foreach (var name in names)
                if (address.TryGetProperty(name, out var v) && v.GetString() is { Length: > 0 } s) return s;
            return null;
        }

        var parts = new[]
        {
            Field("road", "pedestrian", "footway", "neighbourhood"),
            Field("suburb", "neighbourhood", "city_district", "village"),
            Field("city", "town", "municipality", "county"),
        }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();

        return parts.Count > 0
            ? string.Join(", ", parts)
            : root.TryGetProperty("display_name", out var whole) ? whole.GetString() : null;
    }
}
