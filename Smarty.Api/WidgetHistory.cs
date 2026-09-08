using System.Text.Json;
using System.Text.Json.Nodes;

namespace Smarty.Api;

/// <summary>
/// One reading of a panel's tracked numbers, at a moment.
/// </summary>
public sealed class WidgetSample
{
    public DateTimeOffset At { get; set; }

    /// <summary>Field name → the number it held. Only the fields the kind asked to remember.</summary>
    public Dictionary<string, double> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// What a panel remembers about itself.
///
/// <para>
/// A panel without memory can only ever say what is true now. "247 views" is a fact; "247 views, up 31 since
/// Tuesday, and flat for three days" is the thing worth looking at — and the second one cannot be computed from the
/// first, however often you fetch it. Nothing was keeping the earlier readings, so every load threw away the only
/// data that would have made the next one meaningful.
/// </para>
/// <para>
/// Kept per panel rather than per kind, because the history belongs to the listing, not to the shape of listings.
/// Numbers only: a series is a thing you can subtract, and the point of remembering is to be able to say what
/// changed. Strings that change are a different feature and not this one.
/// </para>
/// </summary>
public sealed class WidgetMemory
{
    /// <summary>
    /// How many readings a panel keeps.
    ///
    /// <para>
    /// Enough for a month of hourly readings, which covers every cadence a panel actually uses and every graph one
    /// can usefully draw in a box this size. Past that the oldest go, because a panel is a glance and nobody is
    /// mining a year of it out of a JSON file.
    /// </para>
    /// </summary>
    public const int MaxSamples = 720;

    /// <summary>
    /// The shortest gap between two kept readings.
    ///
    /// <para>
    /// The refresher can load a panel every fifteen seconds and often does; that is the right cadence for showing a
    /// number and the wrong one for remembering it. Without this a day of a fast panel is nothing but the last hour,
    /// and the graph it draws is a flat line at the current value.
    /// </para>
    /// </summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Pull the tracked numbers out of a loaded payload.
    ///
    /// <para>
    /// Uses the same path language the loader's map uses, so a kind says <c>views</c> or <c>listing.watchers</c> and
    /// means the same thing in both places. A field that isn't there, or isn't a number, is skipped rather than
    /// recorded as zero — a gap in a series is honest, and a zero is a lie that shows up as a cliff on a graph.
    /// </para>
    /// </summary>
    public static Dictionary<string, double> Read(string? dataJson, IReadOnlyList<string> track)
    {
        var found = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(dataJson) || track.Count == 0) return found;

        JsonDocument document;
        try { document = JsonDocument.Parse(dataJson!); }
        catch (JsonException) { return found; }

        using var _ = document;
        var root = document.RootElement;

        foreach (var path in track)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (WidgetShape.Walk(root, path) is not { } node) continue;
            if (AsNumber(node) is not { } number) continue;
            found[LeafOf(path)] = number;
        }

        return found;
    }

    /// <summary>
    /// A number, whether the source wrote it as one.
    ///
    /// <para>
    /// A price arrives as 203.07, as "203.07", and as "£203.07" depending on who is serving it, and all three are the
    /// same reading. Refusing the last two would mean the panels most worth tracking — the scraped ones — remember
    /// nothing at all.
    /// </para>
    /// </summary>
    internal static double? AsNumber(JsonNode node)
    {
        if (node is not JsonValue value) return null;

        if (value.TryGetValue<double>(out var d)) return d;
        if (value.TryGetValue<long>(out var l)) return l;
        if (!value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)) return null;

        // Currency symbols, thousands separators, a trailing unit. Keep the sign and one decimal point.
        var cleaned = new string(text.Where(ch => char.IsDigit(ch) || ch is '.' or '-' or ',').ToArray())
            .Replace(",", "");
        var trimmed = cleaned.TrimEnd('.', '-');
        return double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>The last segment of a path, so <c>listing.watchers</c> is remembered as <c>watchers</c>.</summary>
    internal static string LeafOf(string path)
    {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 1; i >= 0; i--)
            if (!int.TryParse(parts[i], out _) && parts[i] != "*")
                return parts[i];
        return path;
    }

    /// <summary>
    /// Add a reading, if it is worth adding, and drop what has aged out.
    /// </summary>
    /// <returns>The series as it now stands.</returns>
    public static List<WidgetSample> Append(List<WidgetSample> series, Dictionary<string, double> values,
        DateTimeOffset now)
    {
        if (values.Count == 0) return series;

        if (series.Count > 0)
        {
            var last = series[^1];

            // Too soon to be a new reading. Overwritten rather than skipped, so the series always ends at the
            // freshest value it has — a graph whose last point is four minutes stale looks like a stall.
            if (now - last.At < MinGap)
            {
                last.At = now;
                last.Values = values;
                return series;
            }
        }

        series.Add(new WidgetSample { At = now, Values = values });
        if (series.Count > MaxSamples) series.RemoveRange(0, series.Count - MaxSamples);
        return series;
    }

    /// <summary>
    /// The series as a component wants it: one array per tracked field, oldest first, plus what changed.
    ///
    /// <para>
    /// Shaped here rather than in the component because every panel that remembers anything needs the same three
    /// things — the numbers, the first and last, and the difference — and thirteen generated components each doing
    /// their own subtraction is thirteen chances to get it wrong.
    /// </para>
    /// </summary>
    public static object Shape(List<WidgetSample> series)
    {
        var fields = series.SelectMany(s => s.Values.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var shaped = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in fields)
        {
            var points = series
                .Where(s => s.Values.ContainsKey(field))
                .Select(s => new { at = s.At, value = s.Values[field] })
                .ToList();
            if (points.Count == 0) continue;

            double first = points[0].value, last = points[^1].value;
            shaped[field] = new
            {
                values = points.Select(p => p.value).ToArray(),
                at = points.Select(p => p.at).ToArray(),
                first,
                last,
                change = last - first,
                // Guarded, because a series that starts at zero is exactly the series someone wants a percentage of.
                percent = Math.Abs(first) > double.Epsilon ? (last - first) / Math.Abs(first) * 100 : (double?)null,
                min = points.Min(p => p.value),
                max = points.Max(p => p.value),
                since = points[0].at,
            };
        }

        return shaped;
    }

    /// <summary>
    /// The series in words, for the assistant rather than the page.
    ///
    /// <para>
    /// The reason memory is worth having at all. A panel that knows a listing has sat at the same price with two more
    /// watchers in nine days is a panel the assistant can open a conversation about — "that listing has been up three
    /// weeks and the views have flattened" is only sayable by something that kept the earlier numbers.
    /// </para>
    /// </summary>
    public static string Describe(string title, List<WidgetSample> series)
    {
        if (series.Count < 2) return "";

        var lines = new List<string>();
        var span = series[^1].At - series[0].At;
        var over = span.TotalDays >= 1.5 ? $"{span.TotalDays:0} days"
            : span.TotalHours >= 1.5 ? $"{span.TotalHours:0} hours"
            : $"{span.TotalMinutes:0} minutes";

        foreach (var field in series.SelectMany(s => s.Values.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var points = series.Where(s => s.Values.ContainsKey(field)).Select(s => s.Values[field]).ToList();
            if (points.Count < 2) continue;

            double first = points[0], last = points[^1], change = last - first;
            var movement = Math.Abs(change) < 0.0001
                ? $"unchanged at {Trim(last)}"
                : $"{Trim(first)} → {Trim(last)} ({(change > 0 ? "+" : "")}{Trim(change)})";
            lines.Add($"{field}: {movement}");
        }

        return lines.Count == 0 ? "" : $"- {title}, over the last {over} — {string.Join("; ", lines)}";
    }

    private static string Trim(double v) =>
        Math.Abs(v - Math.Round(v)) < 0.001 ? Math.Round(v).ToString("0") : v.ToString("0.##");
}
