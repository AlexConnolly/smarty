using System.Text.Json;

namespace Smarty.Agents;

/// <summary>
/// Prices, read from the provider rather than remembered.
///
/// <para>
/// <see cref="ModelPrices"/> ships with a table of USD-per-million figures, and a table of prices is a thing
/// that is right on the day it is written and quietly wrong afterwards. Nothing announces the drift: the cost
/// column keeps showing a confident number, and the number is stale. Together publishes the real figures in the
/// same <c>/v1/models</c> call that lists the models, so there is no reason to be guessing.
/// </para>
/// <para>
/// The parse is separate from the fetch so it can be tested against a real payload without a network or a key.
/// Refreshing is best-effort by design — an unreachable catalogue leaves the seeded numbers in place, which is
/// exactly what they are for.
/// </para>
/// </summary>
public static class ModelPriceCatalogue
{
    /// <summary>
    /// Pull prices out of a Together <c>/v1/models</c> payload.
    /// <para>
    /// Only entries with a usable input AND output price are returned. <c>cached_input</c> is absent for plenty
    /// of models, and is then priced as fresh input rather than as free — an unknown cache discount recorded as
    /// zero understates real spend, and understating is the direction that misleads.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string Model, ModelPrice Price)> Parse(string json)
    {
        var found = new List<(string, ModelPrice)>();
        if (string.IsNullOrWhiteSpace(json)) return found;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return found; }

        using (doc)
        {
            // The endpoint answers with a bare array; a {"data": [...]} envelope is the OpenAI shape and costs
            // nothing to accept as well.
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
                root = data;
            if (root.ValueKind != JsonValueKind.Array) return found;

            foreach (var entry in root.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                var model = id.GetString();
                if (string.IsNullOrWhiteSpace(model)) continue;

                if (!entry.TryGetProperty("pricing", out var pricing) || pricing.ValueKind != JsonValueKind.Object)
                    continue;

                var input = Money(pricing, "input");
                var output = Money(pricing, "output");
                if (input is null || output is null) continue;

                // A dedicated endpoint bills by the hour, so its per-token figures describe nothing anyone pays.
                if (Money(pricing, "hourly") is > 0m) continue;

                var cached = Money(pricing, "cached_input") ?? input;
                found.Add((model!, new ModelPrice(input.Value, output.Value, cached.Value)));
            }
        }

        return found;
    }

    private static decimal? Money(JsonElement pricing, string name)
    {
        if (!pricing.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var d) => d,
            JsonValueKind.String when decimal.TryParse(value.GetString(), out var s) => s,
            _ => null,
        };
    }

    /// <summary>
    /// Fetch the catalogue and fold it into <see cref="ModelPrices"/>. Returns how many models were priced —
    /// zero when the call failed, which is not an error worth stopping anything for.
    /// </summary>
    public static async Task<int> RefreshAsync(
        HttpClient http, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return 0;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.together.xyz/v1/models");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
            // Together answers 403 to a request with no User-Agent, and says nothing about the header.
            request.Headers.TryAddWithoutValidation("User-Agent", "smarty/1.0");

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return 0;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var prices = Parse(body);

            foreach (var (model, price) in prices)
                ModelPrices.Set(model, price.InputPerMillion, price.OutputPerMillion, price.CachedInputPerMillion);

            return prices.Count;
        }
        catch
        {
            // Offline, throttled, or the shape changed. The seeded table is what that case is for.
            return 0;
        }
    }
}
