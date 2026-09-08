using System.Text.Json;
using Smarty.Plugins;

namespace Smarty.Plugin.Roborock;

/// <summary>
/// One signed-in Roborock account, held open. Everything the commands need — the session, the list of
/// vacuums, the map between the home's room names and each vacuum's segment numbers, and the connection to
/// the broker — is fetched once and kept.
/// </summary>
/// <remarks>
/// The session is kept in the plugin's own state rather than re-obtained, and that is a requirement rather
/// than an optimisation: Roborock signs you in with a code emailed to you, the code is single-use, and asking
/// for another is rate-limited. A session that didn't outlive the process would mean a new email every restart.
/// Reading the home is limited too (about forty a day), so it is cached and refreshed on a timer.
/// </remarks>
internal sealed class RoborockAccount : IAsyncDisposable
{
    private static readonly TimeSpan HomeIsStaleAfter = TimeSpan.FromHours(6);

    /// <summary>Where the signed-in session lives between restarts.</summary>
    internal const string SessionKey = "session";

    private readonly RoborockWebApi _api;
    private readonly IPluginState _state;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private RoborockSession? _session;
    private RoborockHome? _home;
    private DateTimeOffset _homeRead;
    private RoborockConnection? _connection;

    /// <summary>Segment number → room name, per device. Read from the vacuum itself, because the numbers are
    /// the vacuum's and the names are the home's, and only this mapping joins them.</summary>
    private readonly Dictionary<string, IReadOnlyList<RoborockSegment>> _segments = new(StringComparer.Ordinal);

    public RoborockAccount(HttpClient http, string email, string device, IPluginState state)
    {
        _api = new RoborockWebApi(http, email, device);
        _state = state;
    }

    /// <summary>Whether there is a stored session to work with. What the plugin offers depends on it: without
    /// one there is nothing to do but sign in.</summary>
    public static bool SignedIn(IPluginState state) => Stored(state) is not null;

    // Signing in lives in the plugin's setup stages, not here: it is a conversation with a PERSON, and this
    // class is only ever reached from a command, which is a conversation with a model.

    private static RoborockSession? Stored(IPluginState state)
    {
        if (state.Get(SessionKey) is not { } json) return null;
        // A session written by an older build, or half-written, is no session at all — better to ask for a new
        // code than to present something Roborock will refuse in a way nobody can read.
        try { return JsonSerializer.Deserialize<RoborockSession>(json); }
        catch (JsonException) { return null; }
    }

    private void SignedOut()
    {
        _session = null;
        _home = null;
        _state.Set(SessionKey, null);
    }

    public async Task<IReadOnlyList<RoborockDevice>> VacuumsAsync(CancellationToken ct)
    {
        var home = await HomeAsync(ct).ConfigureAwait(false);
        if (home.Devices.Count == 0)
            throw new PluginDeadEndException("There are no vacuums on this Roborock account.");
        return home.Devices;
    }

    /// <summary>
    /// Which vacuums a command acts on: the one named, or ALL of them when nothing was named. "Send it back to
    /// the dock" said to a house with two vacuums means both, so nothing here asks which one and nothing has to
    /// be configured in advance — the only question worth putting back to someone is a genuinely ambiguous
    /// room name, and <see cref="RouteAsync"/> is where that happens.
    /// </summary>
    public async Task<IReadOnlyList<RoborockDevice>> TargetsAsync(string? named, CancellationToken ct)
    {
        var vacuums = await VacuumsAsync(ct).ConfigureAwait(false);
        if (named is null) return vacuums;

        var match = vacuums.FirstOrDefault(v => v.Name.Equals(named, StringComparison.OrdinalIgnoreCase))
                    ?? vacuums.FirstOrDefault(v => v.Name.Contains(named, StringComparison.OrdinalIgnoreCase));

        return match is not null
            ? new[] { match }
            : throw new PluginDeadEndException(
                $"No vacuum called '{named}'. On this account: {string.Join(", ", vacuums.Select(v => v.Name))}.");
    }

    public async Task<JsonElement> CallAsync(RoborockDevice vacuum, string method, object? parameters, CancellationToken ct)
    {
        var connection = await ConnectionAsync(ct).ConfigureAwait(false);
        return await connection.CallAsync(vacuum, method, parameters, ct).ConfigureAwait(false);
    }

    /// <summary>The rooms this vacuum can be sent to, by the names they have in the Roborock app.</summary>
    public async Task<IReadOnlyList<RoborockSegment>> RoomsAsync(RoborockDevice vacuum, CancellationToken ct)
    {
        lock (_segments)
            if (_segments.TryGetValue(vacuum.Duid, out var known)) return known;

        var home = await HomeAsync(ct).ConfigureAwait(false);
        var mapping = await CallAsync(vacuum, "get_room_mapping", null, ct).ConfigureAwait(false);
        var rooms = ReadSegments(mapping, home.Rooms);

        lock (_segments) _segments[vacuum.Duid] = rooms;
        return rooms;
    }

    /// <summary>
    /// The vacuum's answer to <c>get_room_mapping</c> — <c>[[segment, "homeRoomId"], …]</c> — joined to the
    /// home's room names. A segment the home has no name for keeps its number, because a nameless room is
    /// still one the vacuum can be sent to.
    /// </summary>
    internal static IReadOnlyList<RoborockSegment> ReadSegments(JsonElement mapping, IReadOnlyList<RoborockRoom> homeRooms)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var room in homeRooms) names[room.Id.ToString()] = room.Name;

        var rooms = new List<RoborockSegment>();
        if (mapping.ValueKind != JsonValueKind.Array) return rooms;

        foreach (var pair in mapping.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2) continue;
            if (pair[0].ValueKind != JsonValueKind.Number) continue;

            var segment = pair[0].GetInt32();
            var homeRoomId = pair[1].ValueKind == JsonValueKind.String ? pair[1].GetString() : pair[1].GetRawText();

            rooms.Add(new RoborockSegment(
                segment,
                homeRoomId is not null && names.TryGetValue(homeRoomId, out var name) && name.Length > 0
                    ? name
                    : $"room {segment}"));
        }
        return rooms;
    }

    /// <summary>
    /// Work out, from the room names alone, which vacuum is being asked to clean what. In a house with an
    /// upstairs and a downstairs machine, "clean the kitchen" is not ambiguous — only one of them has a
    /// kitchen on its map — so nothing needs to be said about which vacuum, and nothing needs configuring.
    /// </summary>
    public async Task<IReadOnlyList<RoomAssignment>> RouteAsync(
        string? named, IEnumerable<string> roomNames, CancellationToken ct)
    {
        var targets = await TargetsAsync(named, ct).ConfigureAwait(false);
        var maps = new List<VacuumMap>();
        foreach (var vacuum in targets)
            maps.Add(new VacuumMap(vacuum, await RoomsAsync(vacuum, ct).ConfigureAwait(false)));
        return Route(maps, roomNames);
    }

    /// <summary>
    /// Room names as a person said them, against every vacuum's map, split into one instruction per vacuum.
    /// </summary>
    /// <remarks>
    /// Exact names are considered across ALL the maps before any partial one is, so a vacuum with a room called
    /// exactly "Hallway" wins over one with a "Hallway Cupboard" rather than the two being called a tie. What
    /// is left after that is a real tie — two vacuums that both have a "Hallway" are two different hallways,
    /// and picking one would be a guess with a machine at the end of it, so it asks.
    /// </remarks>
    internal static IReadOnlyList<RoomAssignment> Route(IReadOnlyList<VacuumMap> maps, IEnumerable<string> names)
    {
        if (maps.All(m => m.Rooms.Count == 0))
            throw new PluginDeadEndException(
                maps.Count == 1
                    ? $"{maps[0].Vacuum.Name} has no rooms on its map, so it can't be sent to one. Let it finish a full clean first."
                    : "None of the vacuums has any rooms on its map yet. Let one finish a full clean first.");

        var assigned = new Dictionary<string, List<RoborockSegment>>(StringComparer.Ordinal);
        var order = new List<RoborockDevice>();

        foreach (var name in names)
        {
            var wanted = name.Trim();
            if (wanted.Length == 0) continue;

            var hits = maps
                .Select(m => (m.Vacuum, Room: m.Rooms.FirstOrDefault(r => r.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))))
                .Where(x => x.Room is not null).ToList();

            if (hits.Count == 0)
                hits = maps
                    .Select(m => (m.Vacuum, Room: m.Rooms.FirstOrDefault(r => r.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))))
                    .Where(x => x.Room is not null).ToList();

            if (hits.Count == 0)
                throw new PluginDeadEndException(
                    $"No vacuum has a room called '{wanted}'. " +
                    string.Join(" ", maps.Where(m => m.Rooms.Count > 0).Select(m =>
                        $"{m.Vacuum.Name}: {string.Join(", ", m.Rooms.Select(r => r.Name))}.")));

            if (hits.Count > 1)
                throw new PluginDeadEndException(
                    $"{hits.Count} vacuums have a room matching '{wanted}' — say which one: " +
                    string.Join(", ", hits.Select(h => h.Vacuum.Name)) + ".");

            var (vacuum, room) = hits[0];
            if (!assigned.TryGetValue(vacuum.Duid, out var rooms))
            {
                assigned[vacuum.Duid] = rooms = new List<RoborockSegment>();
                order.Add(vacuum);
            }
            if (rooms.All(r => r.Segment != room!.Segment)) rooms.Add(room!);
        }

        if (order.Count == 0)
            throw new PluginDeadEndException("No rooms were given. Name at least one, or start a full clean instead.");

        return order.Select(v => new RoomAssignment(
            v,
            assigned[v.Duid].Select(r => r.Segment).ToList(),
            assigned[v.Duid].Select(r => r.Name).ToList())).ToList();
    }

    private async Task<RoborockHome> HomeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _session ??= Stored(_state) ?? throw new PluginDeadEndException(
                "Not signed in to Roborock yet. Ask it to send a sign-in code, then sign in with the code from the email.");

            if (_home is null || DateTimeOffset.UtcNow - _homeRead > HomeIsStaleAfter)
            {
                try
                {
                    _home = await _api.HomeAsync(_session, ct).ConfigureAwait(false);
                }
                catch (RoborockSignedOutException)
                {
                    // Presenting it again would fail the same way every time, so it goes, and what's left is
                    // the one thing that can fix it.
                    SignedOut();
                    throw new PluginDeadEndException(
                        "Roborock no longer accepts the stored sign-in. Ask it to send a new code and sign in again.");
                }
                _homeRead = DateTimeOffset.UtcNow;
            }
            return _home;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RoborockConnection> ConnectionAsync(CancellationToken ct)
    {
        await HomeAsync(ct).ConfigureAwait(false); // guarantees a session

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _connection ??= new RoborockConnection(_session!.Rriot); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>A room on the vacuum's map: the number it answers to, and what it's called in the app.</summary>
internal sealed record RoborockSegment(int Segment, string Name);

/// <summary>One vacuum and the rooms its own map holds.</summary>
internal sealed record VacuumMap(RoborockDevice Vacuum, IReadOnlyList<RoborockSegment> Rooms);

/// <summary>One vacuum and the rooms of its map it is being sent to, by number and by name.</summary>
internal sealed record RoomAssignment(RoborockDevice Vacuum, IReadOnlyList<int> Segments, IReadOnlyList<string> Rooms);
