using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Smarty.Plugin.Roborock;

/// <summary>
/// What <c>get_status</c> comes back with, in words. Every code Roborock has published is here; anything else
/// is reported as the bare number rather than guessed at, because "idle" when it actually means "wedged under
/// the sofa" is worse than an unfamiliar code.
/// </summary>
/// <summary>
/// One vacuum's status as data — what a panel renders. Every field is optional because a status is whatever
/// that model's firmware chose to send, and a panel showing "—" for something absent is better than a loader
/// that fails over it.
/// </summary>
internal sealed record VacuumReading(string Name)
{
    public int? StateCode { get; init; }
    public string State { get; init; } = "state unknown";

    /// <summary>A clean is running right now. The one a "stop or start?" decision turns on.</summary>
    public bool Cleaning { get; init; }

    public bool Paused { get; init; }
    public bool Docked { get; init; }
    public bool Online { get; init; } = true;
    public int? Battery { get; init; }
    public string? Suction { get; init; }
    public string? Error { get; init; }
    public int? CleanSeconds { get; init; }
    public double? CleanArea { get; init; }
    public bool Mop { get; init; }
}

internal static class RoborockStatus
{
    public static string Describe(string vacuumName, JsonElement status) => Read(vacuumName, status).Text;

    /// <summary>
    /// The vacuum's status, as a line for whoever asked and as data for a panel to render.
    /// </summary>
    public static (string Text, VacuumReading Data) Read(string vacuumName, JsonElement status)
    {
        if (status.ValueKind == JsonValueKind.Array && status.GetArrayLength() > 0) status = status[0];
        if (status.ValueKind != JsonValueKind.Object)
            return ($"{vacuumName}: the vacuum sent no status.", new VacuumReading(vacuumName));

        var element = status;
        int? Number(string field) =>
            element.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

        var code = Number("state");
        var open = Number("in_cleaning") ?? 0;

        var reading = new VacuumReading(vacuumName)
        {
            StateCode = code,
            State = State(code, open),
            Cleaning = IsCleaning(code),
            Paused = IsPaused(code, open),
            Docked = IsDocked(code),
            Battery = Number("battery"),
            Suction = Number("fan_power") is { } power ? Fan(power) : null,
            Error = Number("error_code") is > 0 and { } fault ? Error(fault) : null,
            CleanSeconds = Number("clean_time"),
            CleanArea = Number("clean_area") is > 0 and { } covered ? Math.Round(covered / 1_000_000.0, 1) : null,
            Mop = Number("water_box_status") == 1,
        };

        var line = new StringBuilder();
        line.Append(vacuumName).Append(": ").Append(reading.State);
        if (reading.Error is { } message) line.Append($" — ERROR: {message}");

        var parts = new List<string>();
        if (reading.Battery is { } battery) parts.Add($"battery {battery}%");
        if (reading.Suction is { } suction) parts.Add($"suction {suction}");
        // "cleaning for 9m" reads as still going. The elapsed time is a fact about the clean, not about whether
        // it is currently moving, so it is worded as one — and only mentioned while a clean is actually open,
        // because these fields describe the LAST cycle and do not clear when the vacuum docks.
        if (reading.Cleaning || reading.Paused)
        {
            if (reading.CleanSeconds is > 0 and { } seconds) parts.Add($"{Duration(seconds)} into the clean");
            if (reading.CleanArea is { } area) parts.Add($"{area.ToString("0.#", CultureInfo.InvariantCulture)} m² covered");
        }
        parts.Add(reading.Mop ? "mop attached" : "no mop");

        if (parts.Count > 0) line.Append(" (").Append(string.Join(", ", parts)).Append(')');
        return (line.ToString(), reading);
    }

    /// <summary>Whether a clean is actually running. The state code is the only thing that says so: the
    /// <c>in_cleaning</c> and <c>clean_time</c> fields describe the last cycle and survive the vacuum docking,
    /// so treating them as "a clean is underway" reports a charging vacuum as busy and refuses to start one.</summary>
    public static bool IsCleaning(int? code) => code is 5 or 11 or 16 or 17 or 18;

    /// <summary>On, or on its way to, the dock — which settles it whatever else the status carries.</summary>
    public static bool IsDocked(int? code) => code is 6 or 8 or 9 or 15 or 100;

    /// <summary>
    /// Stopped part-way through a clean. Roborock's own code 10 says so outright; code 2 is
    /// <c>charger_disconnected</c>, which means off the dock and not moving, and with a clean open that is a
    /// pause. Code 3 (idle) is NOT included: idle says nothing about whether it is on the dock, and inferring
    /// from it called a docked, charging vacuum "paused part-way through a clean".
    /// </summary>
    public static bool IsPaused(int? code, int inCleaning) => code == 10 || (code == 2 && inCleaning > 0);

    private static string Duration(int seconds) =>
        seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60:00}s";

    /// <param name="inCleaning">Roborock's own "a clean is underway" flag — 0 for none, otherwise the kind.
    /// It is the difference between a vacuum standing still because it has finished and one standing still
    /// because it was PAUSED, which the state code alone does not distinguish.</param>
    public static string State(int? code, int inCleaning = 0) => code switch
    {
        null => "state unknown",
        1 => "starting up",

        // Off its dock and not moving. The code (charger_disconnected) is a fact about the charger, not about
        // what the vacuum is doing, so reporting it literally called a paused clean "off the charger" — true,
        // useless, and read as "still going" by anyone downstream. Code 3 (idle) is deliberately NOT treated
        // this way: it says nothing about the dock, and inferring a pause from it called a docked, charging
        // vacuum "paused part-way through a clean" and stopped anything from starting a new one.
        2 when inCleaning > 0 => "paused part-way through a clean",
        2 => "stopped, off its dock",
        3 => "idle",
        4 => "under remote control",
        5 => "cleaning",
        6 => "returning to the dock",
        7 => "in manual mode",
        8 => "charging",
        9 => "charging problem",
        10 => "paused",
        11 => "spot cleaning",
        12 => "in error",
        13 => "shutting down",
        14 => "updating",
        15 => "docking",
        16 => "going to a spot",
        17 => "zone cleaning",
        18 => "cleaning rooms",
        22 => "emptying its bin",
        23 => "washing the mop",
        26 => "going to wash the mop",
        100 => "charged and idle on the dock",
        101 => "offline",
        _ => $"state code {code}",
    };

    public static string Error(int code) => code switch
    {
        0 => "none",
        1 => "the laser sensor is blocked",
        2 => "the bumper is stuck",
        3 => "a wheel is off the ground",
        4 => "a cliff sensor is dirty",
        5 => "the main brush is jammed",
        6 => "a side brush is jammed",
        7 => "a wheel is jammed",
        8 => "it is trapped",
        9 => "the bin is not fitted",
        10 => "the filter is wet or blocked",
        11 => "a magnetic strip is interfering",
        12 => "the battery is low",
        13 => "it can't charge",
        14 => "battery fault",
        15 => "a wall sensor is dirty",
        16 => "it is not level",
        17 => "a side brush fault",
        18 => "a fan fault",
        21 => "the vertical bumper is pressed",
        22 => "the dock locator is dirty",
        23 => "it could not find the dock",
        24 => "it is inside a no-go zone",
        27 => "the mop lifter is jammed",
        28 => "it is stuck on a carpet",
        29 => "the filter is blocked",
        30 => "an invisible wall is in the way",
        31 => "it can't cross the carpet",
        32 => "internal fault",
        _ => $"error code {code}",
    };

    public static string Fan(int code) => code switch
    {
        101 => "quiet",
        102 => "balanced",
        103 => "turbo",
        104 => "max",
        105 => "off",
        106 => "custom",
        108 => "max+",
        _ => $"code {code}",
    };
}
