using System.Globalization;
using System.Text;
using System.Text.Json;
using Smarty.Plugins;

namespace Smarty.Plugin.Weather;

/// <summary>
/// The plugin that proves the mechanism: two commands over Open-Meteo, which needs no account and no key, so
/// the only thing standing between "uploaded" and "answering" is the loader itself.
/// </summary>
/// <remarks>
/// Both of its configuration keys do real work — the default location is what a bare "what's the weather"
/// resolves to, and the unit changes the numbers — so a round trip through the control centre and a restart is
/// visible in the answer rather than only in a file.
/// </remarks>
public sealed class WeatherPlugin : IPlugin
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public string Name => "Weather";

    public string Description =>
        "Reports current conditions and the days ahead for anywhere in the world, from the Open-Meteo forecast service.";

    /// <summary>
    /// One screen, both answers optional — and it is still a required step, because "we asked and you said
    /// nothing" is a different state from "we never asked", and only the first should let the plugin start
    /// working. Answering it once is what marks setup done.
    /// </summary>
    public Task<PluginStage?> GetNextStageAsync(
        string? previous, PluginValues submitted, IPluginState state, CancellationToken ct)
    {
        if (previous is null)
            return Task.FromResult<PluginStage?>(state.Get(Asked) is null ? Preferences : null);

        state.Set("default_location", submitted.Text("default_location"));
        state.Set("units", submitted.Text("units", "celsius").StartsWith("f", StringComparison.OrdinalIgnoreCase)
            ? "fahrenheit"
            : "celsius");
        state.Set(Asked, "yes");
        return Task.FromResult<PluginStage?>(null);
    }

    private const string Asked = "asked";

    private static PluginStage Preferences => new(
        "preferences",
        "Where and in what units",
        "Both are optional. Leave the location blank and every question has to name a place.",
        new Dictionary<string, PluginParameter>
        {
            ["default_location"] = PluginParameter.Text(
                "Where \"what's it like out?\" means when nobody says — e.g. Leicester."),
            ["units"] = PluginParameter.Text("celsius or fahrenheit. Defaults to celsius."),
        });

    public IReadOnlyList<PluginCommand> GetCommands(IPluginState state)
    {
        var home = state.Get("default_location");
        bool fahrenheit = (state.Get("units") ?? "celsius").StartsWith("f", StringComparison.OrdinalIgnoreCase);

        // Resolve the place once per call: an explicit argument wins, the configured default stands in, and
        // with neither the command says so rather than guessing at a city.
        async Task<Place> Where(PluginValues p, CancellationToken ct)
        {
            var asked = p.Text("location") ?? home;
            if (asked is null)
                throw new PluginDeadEndException(
                    "No location given, and the Weather plugin has no default one set up.");
            return await GeocodeAsync(asked, ct);
        }

        return new[]
        {
            new PluginCommand(
                "now",
                "Current weather at a place: temperature, what it feels like, wind, and whether it is raining.",
                new Dictionary<string, PluginParameter>
                {
                    ["location"] = PluginParameter.Text(
                        "Town, city or region, optionally with a country (Leicester, GB). Omit for the configured default."),
                },
                async (p, ct) =>
                {
                    var place = await Where(p, ct);
                    return await CurrentAsync(place, fahrenheit, ct);
                }),

            new PluginCommand(
                "forecast",
                "The daily forecast for a place: high, low, and how likely rain is, for the next few days.",
                new Dictionary<string, PluginParameter>
                {
                    ["location"] = PluginParameter.Text("Town, city or region. Omit for the configured default."),
                    ["days"] = PluginParameter.Integer("How many days ahead, 1 to 16. Defaults to 3."),
                },
                async (p, ct) =>
                {
                    var place = await Where(p, ct);
                    int days = Math.Clamp(p.Integer("days", 3), 1, 16);
                    return await ForecastAsync(place, days, fahrenheit, ct);
                }),
        };
    }

    private readonly record struct Place(string Label, double Latitude, double Longitude);

    private static async Task<Place> GeocodeAsync(string query, CancellationToken ct)
    {
        var url = "https://geocoding-api.open-meteo.com/v1/search" +
                  $"?name={Uri.EscapeDataString(query)}&count=1&language=en&format=json";
        using var doc = await GetJsonAsync(url, ct);

        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            throw new PluginDeadEndException(
                $"Nowhere called '{query}' — check the spelling, or add a country (Boston, US).");

        var first = results[0];
        var name = first.GetProperty("name").GetString() ?? query;
        var country = first.TryGetProperty("country", out var c) ? c.GetString() : null;
        var region = first.TryGetProperty("admin1", out var a) ? a.GetString() : null;
        var label = string.Join(", ", new[] { name, region, country }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return new Place(label, first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble());
    }

    private static async Task<string> CurrentAsync(Place place, bool fahrenheit, CancellationToken ct)
    {
        var url = ForecastUrl(place, fahrenheit) +
                  "&current=temperature_2m,apparent_temperature,relative_humidity_2m,precipitation,weather_code,wind_speed_10m";
        using var doc = await GetJsonAsync(url, ct);
        var now = doc.RootElement.GetProperty("current");
        var units = doc.RootElement.GetProperty("current_units");

        string Unit(string field) => units.TryGetProperty(field, out var u) ? u.GetString() ?? "" : "";
        string Value(string field) => now.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble().ToString("0.#", CultureInfo.InvariantCulture)
            : "?";

        var sb = new StringBuilder();
        sb.AppendLine($"{place.Label} — {Describe(now.GetProperty("weather_code").GetInt32())}, " +
                      $"as of {now.GetProperty("time").GetString()} local time.");
        sb.AppendLine($"Temperature: {Value("temperature_2m")}{Unit("temperature_2m")} " +
                      $"(feels like {Value("apparent_temperature")}{Unit("apparent_temperature")})");
        sb.AppendLine($"Humidity: {Value("relative_humidity_2m")}{Unit("relative_humidity_2m")}");
        sb.AppendLine($"Wind: {Value("wind_speed_10m")} {Unit("wind_speed_10m")}");
        sb.Append($"Precipitation: {Value("precipitation")} {Unit("precipitation")}");
        return sb.ToString();
    }

    private static async Task<string> ForecastAsync(Place place, int days, bool fahrenheit, CancellationToken ct)
    {
        var url = ForecastUrl(place, fahrenheit) +
                  $"&forecast_days={days}" +
                  "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,precipitation_sum";
        using var doc = await GetJsonAsync(url, ct);
        var daily = doc.RootElement.GetProperty("daily");
        var units = doc.RootElement.GetProperty("daily_units");
        var degrees = units.TryGetProperty("temperature_2m_max", out var du) ? du.GetString() ?? "" : "";

        var dates = daily.GetProperty("time");
        var codes = daily.GetProperty("weather_code");
        var highs = daily.GetProperty("temperature_2m_max");
        var lows = daily.GetProperty("temperature_2m_min");
        var rainChance = daily.GetProperty("precipitation_probability_max");
        var rain = daily.GetProperty("precipitation_sum");

        var sb = new StringBuilder();
        sb.AppendLine($"{place.Label} — {dates.GetArrayLength()}-day forecast:");
        for (int i = 0; i < dates.GetArrayLength(); i++)
        {
            var day = DateTime.TryParse(dates[i].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d.ToString("ddd d MMM", CultureInfo.InvariantCulture)
                : dates[i].GetString() ?? "?";
            sb.AppendLine($"{day}: {Describe(Whole(codes, i))}, {Fraction(highs, i)}{degrees} / {Fraction(lows, i)}{degrees}, " +
                          $"rain {Fraction(rainChance, i)}% ({Fraction(rain, i)} mm)");
        }
        return sb.ToString().TrimEnd();

        static int Whole(JsonElement array, int i) =>
            array[i].ValueKind == JsonValueKind.Number ? array[i].GetInt32() : -1;

        static string Fraction(JsonElement array, int i) =>
            array[i].ValueKind == JsonValueKind.Number
                ? array[i].GetDouble().ToString("0.#", CultureInfo.InvariantCulture)
                : "?";
    }

    private static string ForecastUrl(Place place, bool fahrenheit) =>
        "https://api.open-meteo.com/v1/forecast" +
        $"?latitude={place.Latitude.ToString(CultureInfo.InvariantCulture)}" +
        $"&longitude={place.Longitude.ToString(CultureInfo.InvariantCulture)}" +
        "&timezone=auto" +
        (fahrenheit ? "&temperature_unit=fahrenheit&wind_speed_unit=mph" : "");

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Open-Meteo returned {(int)response.StatusCode}: {(body.Length <= 300 ? body : body[..300] + "…")}");
        return JsonDocument.Parse(body);
    }

    /// <summary>The WMO code the API reports, in words. Anything unlisted is reported as the bare code rather
    /// than silently becoming "clear".</summary>
    private static string Describe(int code) => code switch
    {
        0 => "clear sky",
        1 => "mainly clear",
        2 => "partly cloudy",
        3 => "overcast",
        45 or 48 => "fog",
        51 or 53 or 55 => "drizzle",
        56 or 57 => "freezing drizzle",
        61 => "light rain",
        63 => "rain",
        65 => "heavy rain",
        66 or 67 => "freezing rain",
        71 => "light snow",
        73 => "snow",
        75 => "heavy snow",
        77 => "snow grains",
        80 or 81 => "rain showers",
        82 => "violent rain showers",
        85 or 86 => "snow showers",
        95 => "thunderstorm",
        96 or 99 => "thunderstorm with hail",
        _ => $"weather code {code}",
    };
}
