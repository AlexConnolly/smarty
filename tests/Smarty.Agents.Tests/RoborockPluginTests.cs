using System.Text;
using System.Text.Json;
using Smarty.Agents;
using Smarty.Api;
using Smarty.Api.Plugins;
using Smarty.Plugin.Roborock;
using Smarty.Plugins;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// The Roborock plugin's protocol, which is the part that has to be exactly right and the part no amount of
/// running it proves: a frame the vacuum won't decrypt looks identical to a vacuum that's asleep.
/// </summary>
/// <remarks>
/// The expected values for the key derivation, the request signature and the broker credentials were computed
/// independently — the same inputs through Python's hashlib/hmac — so these assert agreement with the
/// algorithm rather than agreement with this implementation of it.
/// </remarks>
public class RoborockPluginTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ---- the wire format ----

    [Fact]
    public void The_timestamp_is_shuffled_the_way_the_vacuum_expects()
    {
        // 0x12345678 → "12345678", read back in the order 5 6 3 7 1 2 0 4.
        Assert.Equal("67482315", Encoding.UTF8.GetString(RoborockCodec.EncodeTimestamp(0x12345678)));

        // Padded to eight digits, so an early timestamp shuffles the zeros rather than shifting everything.
        Assert.Equal(8, RoborockCodec.EncodeTimestamp(1).Length);
    }

    [Fact]
    public void The_message_key_is_derived_from_the_timestamp_the_local_key_and_the_salt()
    {
        var key = RoborockCodec.KeyFor(0x12345678, "abcdefghij123456");

        Assert.Equal("b4865d7ddfa604469ec185adc03ebfbc", Convert.ToHexString(key).ToLowerInvariant());
        Assert.Equal(16, key.Length); // AES-128: a longer or shorter key would be silently wrong
    }

    [Fact]
    public void A_frame_survives_the_round_trip_with_its_header_intact()
    {
        const string localKey = "abcdefghij123456";
        var payload = Encoding.UTF8.GetBytes("""{"dps":{"101":"{\"id\":123,\"method\":\"get_status\"}"},"t":1700000000}""");
        var sent = new RoborockFrame(654321, 4242, 1_700_000_000, RoborockFrame.RpcRequest, payload);

        var wire = RoborockCodec.Encode(sent, localKey);
        var back = Assert.Single(RoborockCodec.Decode(wire, localKey));

        Assert.Equal(sent.Sequence, back.Sequence);
        Assert.Equal(sent.Random, back.Random);
        Assert.Equal(sent.Timestamp, back.Timestamp);
        Assert.Equal(RoborockFrame.RpcRequest, back.Protocol);
        Assert.Equal(payload, back.Payload);

        // The header is plaintext and fixed-width: version(3) seq(4) random(4) timestamp(4) protocol(2)
        // length(2), then the ciphertext in whole AES blocks, then the checksum.
        Assert.Equal("1.0", Encoding.UTF8.GetString(wire, 0, 3));
        int ciphertext = wire.Length - 19 - 4;
        Assert.Equal(0, ciphertext % 16);
        Assert.True(ciphertext > payload.Length); // PKCS7 always adds at least one byte
    }

    [Fact]
    public void A_frame_encrypted_for_another_vacuum_yields_nothing_rather_than_rubbish()
    {
        var wire = RoborockCodec.Encode(
            new RoborockFrame(1, 2, 1_700_000_000, RoborockFrame.RpcRequest, Encoding.UTF8.GetBytes("{}")),
            "abcdefghij123456");

        var back = Assert.Single(RoborockCodec.Decode(wire, "9999999999999999"));

        // The header still reads — it isn't encrypted — but the body must not come back as plausible bytes.
        Assert.Equal(1u, back.Sequence);
        Assert.Empty(back.Payload);
    }

    [Fact]
    public void Two_frames_in_one_buffer_are_both_read()
    {
        const string localKey = "abcdefghij123456";
        var one = RoborockCodec.Encode(new RoborockFrame(1, 1, 1_700_000_000, 102, Encoding.UTF8.GetBytes("{\"a\":1}")), localKey);
        var two = RoborockCodec.Encode(new RoborockFrame(2, 2, 1_700_000_001, 102, Encoding.UTF8.GetBytes("{\"b\":2}")), localKey);

        var frames = RoborockCodec.Decode(one.Concat(two).ToArray(), localKey);

        Assert.Equal(2, frames.Count);
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(frames[0].Payload));
        Assert.Equal("{\"b\":2}", Encoding.UTF8.GetString(frames[1].Payload));
    }

    [Fact]
    public void The_checksum_is_the_crc32_of_everything_before_it()
    {
        Assert.Equal(0xCBF43926u, Crc32.Of(Encoding.UTF8.GetBytes("123456789")));

        var wire = RoborockCodec.Encode(
            new RoborockFrame(7, 8, 1_700_000_000, 101, Encoding.UTF8.GetBytes("{}")), "abcdefghij123456");
        var body = wire.AsSpan(0, wire.Length - 4);
        var checksum = (uint)((wire[^4] << 24) | (wire[^3] << 16) | (wire[^2] << 8) | wire[^1]);

        Assert.Equal(Crc32.Of(body), checksum);
    }

    // ---- the account ----

    private static readonly RRiot Account = new("user-abc", "sec-def", "hmac-ghi", "key-jkl",
        "https://api-eu.roborock.com", "ssl://mqtt-eu.roborock.com:8883");

    [Fact]
    public void A_request_is_signed_the_way_their_api_checks_it()
    {
        var header = RoborockWebApi.Hawk(Account, "/v3/user/homes/12345", at: 1_700_000_000, nonce: "abcdef");

        Assert.Equal(
            "Hawk id=\"user-abc\", s=\"sec-def\", ts=\"1700000000\", nonce=\"abcdef\", " +
            "mac=\"hp/aZD5o2PmUgCNRBzeN4LYg8GZhx5Doaz8K+Qx/K4I=\"",
            header);

        // The path is part of what's signed, so two paths cannot share a signature.
        Assert.NotEqual(header, RoborockWebApi.Hawk(Account, "/v3/user/homes/99999", at: 1_700_000_000, nonce: "abcdef"));
    }

    [Fact]
    public void The_broker_credentials_are_hashed_from_the_account_not_the_password()
    {
        var (user, password) = RoborockConnection.Credentials(Account);

        Assert.Equal("862fdc2c", user);
        Assert.Equal("056f2281509a9f3c", password);

        // Commands go out on one topic and answers come back on another; getting these the wrong way round
        // publishes into the channel nothing reads.
        Assert.Equal("rr/m/i/user-abc/862fdc2c/1a2b3c", RoborockConnection.InboundTopic(Account, "1a2b3c"));
        Assert.Equal("rr/m/o/user-abc/862fdc2c/1a2b3c", RoborockConnection.OutboundTopic(Account, "1a2b3c"));
    }

    [Fact]
    public void The_home_gives_up_the_vacuums_their_keys_and_the_room_names()
    {
        var home = RoborockWebApi.ReadHome(Json("""
        {
          "products": [{ "id": "p1", "model": "roborock.vacuum.a27", "name": "S8 Pro Ultra" }],
          "devices": [
            { "duid": "abc123", "name": "Hoover", "localKey": "abcdefghij123456", "productId": "p1", "online": true }
          ],
          "receivedDevices": [
            { "duid": "def456", "name": "Upstairs", "localKey": "9999999999999999", "productId": "p1", "online": false }
          ],
          "rooms": [ { "id": 2542609, "name": "Kitchen / Diner" }, { "id": 2542610, "name": "Hallway" } ]
        }
        """));

        // A vacuum shared with the account counts as one of yours — it is in the house and it cleans.
        Assert.Equal(new[] { "Hoover", "Upstairs" }, home.Devices.Select(d => d.Name));
        Assert.Equal("abcdefghij123456", home.Devices[0].LocalKey);
        Assert.Equal("roborock.vacuum.a27", home.Devices[0].Model);
        Assert.True(home.Devices[0].Online);
        Assert.False(home.Devices[1].Online);
        Assert.Equal(new[] { "Kitchen / Diner", "Hallway" }, home.Rooms.Select(r => r.Name));
    }

    // ---- rooms, and which vacuum owns them ----

    private static RoborockDevice Vacuum(string name) => new($"duid-{name}", name, "abcdefghij123456", "", true);

    private static IReadOnlyList<RoborockSegment> Downstairs() => RoborockAccount.ReadSegments(
        Json("""[[16, "2542609"], [17, "2542610"], [18, "999999"]]"""),
        new[] { new RoborockRoom(2542609, "Kitchen / Diner"), new RoborockRoom(2542610, "Hallway") });

    private static IReadOnlyList<RoborockSegment> Upstairs() => RoborockAccount.ReadSegments(
        Json("""[[3, "7000001"], [4, "7000002"]]"""),
        new[] { new RoborockRoom(7000001, "Landing"), new RoborockRoom(7000002, "Main Bedroom") });

    private static VacuumMap Map(string name, IReadOnlyList<RoborockSegment> rooms) => new(Vacuum(name), rooms);

    [Fact]
    public void The_vacuums_numbers_are_joined_to_the_homes_room_names()
    {
        var rooms = Downstairs();

        Assert.Equal("Kitchen / Diner", rooms.Single(r => r.Segment == 16).Name);
        Assert.Equal("Hallway", rooms.Single(r => r.Segment == 17).Name);
        // A segment the home has no name for is still somewhere the vacuum can be sent.
        Assert.Equal("room 18", rooms.Single(r => r.Segment == 18).Name);
    }

    [Fact]
    public void Room_names_are_matched_the_way_someone_would_say_them()
    {
        var one = new[] { Map("Hoover", Downstairs()) };

        Assert.Equal(new[] { 16 }, RoborockAccount.Route(one, new[] { "kitchen" }).Single().Segments);
        Assert.Equal(new[] { 16, 17 }, RoborockAccount.Route(one, new[] { "Kitchen / Diner", " hallway " }).Single().Segments);

        // Naming the same room twice is one room, not two passes.
        Assert.Equal(new[] { 17 }, RoborockAccount.Route(one, new[] { "Hallway", "hall" }).Single().Segments);
    }

    [Fact]
    public void With_two_vacuums_the_rooms_say_which_one_is_meant()
    {
        var house = new[] { Map("Downstairs", Downstairs()), Map("Upstairs", Upstairs()) };

        // Nobody said which vacuum, and nobody had to: only one of them has a kitchen.
        var kitchen = Assert.Single(RoborockAccount.Route(house, new[] { "kitchen" }));
        Assert.Equal("Downstairs", kitchen.Vacuum.Name);
        Assert.Equal(new[] { 16 }, kitchen.Segments);

        // Rooms on different floors in one instruction: both machines go, each with its own list.
        var both = RoborockAccount.Route(house, new[] { "kitchen", "landing", "hallway" });
        Assert.Equal(2, both.Count);
        Assert.Equal("Downstairs", both[0].Vacuum.Name);
        Assert.Equal(new[] { 16, 17 }, both[0].Segments);
        Assert.Equal(new[] { "Kitchen / Diner", "Hallway" }, both[0].Rooms);
        Assert.Equal("Upstairs", both[1].Vacuum.Name);
        Assert.Equal(new[] { 3 }, both[1].Segments);
    }

    [Fact]
    public void An_exact_name_on_one_vacuum_beats_a_partial_one_on_another()
    {
        var awkward = new[]
        {
            Map("Downstairs", Downstairs()),                                         // has "Hallway"
            Map("Upstairs", new[] { new RoborockSegment(3, "Hallway Cupboard") }),    // merely contains it
        };

        var hallway = Assert.Single(RoborockAccount.Route(awkward, new[] { "hallway" }));
        Assert.Equal("Downstairs", hallway.Vacuum.Name);
    }

    [Fact]
    public void Two_vacuums_with_the_same_room_name_is_the_one_thing_it_asks_about()
    {
        // Two hallways on two floors are two different rooms. Picking one would send a machine somewhere
        // nobody asked for, so this is the single case worth putting back to whoever asked.
        var twoHallways = new[]
        {
            Map("Downstairs", Downstairs()),
            Map("Upstairs", new[] { new RoborockSegment(3, "Hallway") }),
        };

        var asked = Assert.Throws<PluginDeadEndException>(
            () => RoborockAccount.Route(twoHallways, new[] { "Hallway" }));

        Assert.Contains("Downstairs", asked.Message);
        Assert.Contains("Upstairs", asked.Message);
    }

    [Fact]
    public void A_room_that_does_not_exist_is_a_dead_end_that_says_which_ones_do()
    {
        var house = new[] { Map("Downstairs", Downstairs()), Map("Upstairs", Upstairs()) };

        var refused = Assert.Throws<PluginDeadEndException>(
            () => RoborockAccount.Route(house, new[] { "conservatory" }));

        Assert.Contains("conservatory", refused.Message);
        // Listed per vacuum, because "which rooms exist" and "which machine has them" are the same question.
        Assert.Contains("Downstairs: Kitchen / Diner", refused.Message);
        Assert.Contains("Upstairs: Landing", refused.Message);

        // A vacuum that has never mapped the place can't be sent to a room at all.
        var unmapped = Assert.Throws<PluginDeadEndException>(
            () => RoborockAccount.Route(new[] { Map("Hoover", Array.Empty<RoborockSegment>()) }, new[] { "kitchen" }));
        Assert.Contains("no rooms on its map", unmapped.Message);
    }

    // ---- what comes back ----

    [Fact]
    public void A_reply_is_matched_to_the_call_that_asked_and_an_error_is_kept()
    {
        var ok = RoborockConnection.Answer(Encoding.UTF8.GetBytes(
            """{"t":1700000000,"dps":{"102":"{\"id\":12345,\"result\":[\"ok\"]}"}}"""));
        Assert.NotNull(ok);
        Assert.Equal(12345, ok!.Value.Id);
        Assert.Null(ok.Value.Error);

        var refused = RoborockConnection.Answer(Encoding.UTF8.GetBytes(
            """{"t":1700000000,"dps":{"102":"{\"id\":12345,\"error\":{\"code\":-10007,\"message\":\"invalid status\"}}"}}"""));
        Assert.NotNull(refused);
        Assert.Contains("invalid status", refused!.Value.Error!);

        // The vacuum pushes its state unasked on the same topic. Nothing is waiting on those, and they must
        // not be mistaken for an answer to something.
        Assert.Null(RoborockConnection.Answer(Encoding.UTF8.GetBytes("""{"t":1,"dps":{"121":8,"122":100}}""")));
        Assert.Null(RoborockConnection.Answer(Encoding.UTF8.GetBytes("not json at all")));
    }

    [Fact]
    public void Status_is_reported_in_words_and_an_unknown_code_is_reported_as_a_code()
    {
        var cleaning = RoborockStatus.Describe("Hoover", Json("""
        [{"state":18,"battery":76,"clean_time":930,"clean_area":21500000,"error_code":0,"fan_power":102,"water_box_status":1}]
        """));

        Assert.Contains("cleaning rooms", cleaning);
        Assert.Contains("battery 76%", cleaning);
        Assert.Contains("balanced", cleaning);
        Assert.Contains("15m 30s into the clean", cleaning);
        Assert.Contains("21.5 m²", cleaning);
        Assert.Contains("mop attached", cleaning);
        Assert.DoesNotContain("ERROR", cleaning);

        var stuck = RoborockStatus.Describe("Hoover", Json("""[{"state":12,"battery":54,"error_code":8}]"""));
        Assert.Contains("in error", stuck);
        Assert.Contains("it is trapped", stuck);

        // Never guessed into a comfortable word: a state this build has never seen is reported as itself.
        Assert.Contains("state code 77", RoborockStatus.Describe("Hoover", Json("""[{"state":77}]""")));
    }

    [Fact]
    public void Whether_it_is_cleaning_comes_from_the_state_code_and_nothing_else()
    {
        // in_cleaning and clean_time describe the LAST cycle and do not clear when the vacuum docks. Reading
        // them as "a clean is underway" reported a charging vacuum as busy - and then refused to start a new
        // clean, because as far as everything downstream was concerned one was already running.
        var docked = RoborockStatus.Read("Wife v2", Json(
            """[{"state":8,"battery":74,"in_cleaning":1,"clean_time":592,"clean_area":11900000}]"""));

        Assert.False(docked.Data.Cleaning);
        Assert.False(docked.Data.Paused);
        Assert.True(docked.Data.Docked);
        Assert.Contains("charging", docked.Text);
        Assert.DoesNotContain("paused", docked.Text, StringComparison.OrdinalIgnoreCase);
        // And the elapsed time of a finished clean is not paraded as though it were still running.
        Assert.DoesNotContain("into the clean", docked.Text);

        // Actually cleaning.
        var running = RoborockStatus.Read("Wife v2", Json("""[{"state":5,"battery":60,"clean_time":300}]"""));
        Assert.True(running.Data.Cleaning);
        Assert.False(running.Data.Docked);
        Assert.Contains("into the clean", running.Text);

        // Genuinely paused: off the dock, part-way through.
        var paused = RoborockStatus.Read("Wife v2", Json("""[{"state":2,"in_cleaning":1,"clean_time":300}]"""));
        Assert.True(paused.Data.Paused);
        Assert.False(paused.Data.Cleaning);

        // Roborock's own paused code, and its charged-and-waiting one.
        Assert.True(RoborockStatus.IsPaused(10, 0));
        Assert.True(RoborockStatus.IsDocked(100));
        Assert.False(RoborockStatus.IsCleaning(100));

        // Idle says nothing about the dock, so it must not be read as a pause.
        Assert.False(RoborockStatus.IsPaused(3, 1));
    }

    [Fact]
    public void Status_carries_the_same_answer_as_data_for_a_panel_to_render()
    {
        var (text, reading) = RoborockStatus.Read("Wife v2", Json(
            """[{"state":5,"battery":52,"fan_power":104,"clean_time":592,"clean_area":11900000,"water_box_status":1}]"""));

        // One execution, two readers: a sentence for whoever asked and the same facts as fields. A panel
        // parsing numbers back out of the sentence is exactly what a declared data model exists to prevent.
        Assert.Contains("52%", text);
        Assert.Equal(52, reading.Battery);
        Assert.Equal("max", reading.Suction);
        Assert.Equal(5, reading.StateCode);
        Assert.Equal(11.9, reading.CleanArea);
        Assert.Equal(592, reading.CleanSeconds);
        Assert.True(reading.Mop);
        Assert.Null(reading.Error);
    }

    [Fact]
    public void A_vacuum_standing_still_with_a_clean_open_is_paused_not_idle()
    {
        // Code 2 is charger_disconnected: off the dock, not moving. On its own that is a fact about the
        // CHARGER, and reporting it literally described a paused clean as "off the charger" - true, useless,
        // and read as "still going" by everything downstream. Roborock says which it is separately.
        var paused = RoborockStatus.Describe("Wife v2", Json(
            """[{"state":2,"battery":52,"in_cleaning":1,"clean_time":592,"clean_area":11900000,"fan_power":104}]"""));
        Assert.Contains("paused", paused, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("off the charger", paused);
        // And the elapsed time is worded as a fact about the clean, not as something still happening.
        Assert.DoesNotContain("cleaning for", paused);
        Assert.Contains("into the clean", paused);

        // The same code with nothing open is genuinely just stopped, and must not claim to be paused.
        var stopped = RoborockStatus.Describe("Wife v2", Json("""[{"state":2,"battery":52,"in_cleaning":0}]"""));
        Assert.DoesNotContain("paused", stopped, StringComparison.OrdinalIgnoreCase);

        // Roborock's own explicit paused code still says paused, whatever in_cleaning holds.
        Assert.Contains("paused", RoborockStatus.Describe("Wife v2", Json("""[{"state":10}]""")),
            StringComparison.OrdinalIgnoreCase);
    }

    // ---- the plugin, as Smarty sees it ----

    /// <summary>An in-memory stand-in for the store the host keeps for each plugin.</summary>
    private sealed class Scratch : IPluginState
    {
        private readonly Dictionary<string, string> _kept = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string key) => _kept.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) _kept.Remove(key);
            else _kept[key] = value;
        }
    }

    /// <summary>A stored session, exactly as the plugin writes one after signing in.</summary>
    private static Scratch SignedIn()
    {
        var state = new Scratch();
        state.Set(RoborockAccount.SessionKey, System.Text.Json.JsonSerializer.Serialize(
            new RoborockSession("token-abc", Account)));
        return state;
    }

    private static IReadOnlyList<AgentTool> Tools(IPluginState state)
    {
        var plugin = new RoborockPlugin();
        var capability = new PluginCapability("roborock", plugin.Name, plugin.Description, plugin.GetCommands(state));
        return capability.BuildTools(new IntegrationConfig(), new TaskInfo { Id = "t", Description = "d" });
    }

    private static async Task<PluginStage?> StageAsync(IPluginState state) =>
        await new RoborockPlugin().GetNextStageAsync(null, PluginValues.Empty, state, default);

    private static Scratch Ready()
    {
        var state = SignedIn();
        state.Set(RoborockPlugin.EmailKey, "someone@example.com");
        state.Set(RoborockPlugin.DeviceKey, "a-device");
        return state;
    }

    [Fact]
    public async Task The_identity_a_code_was_issued_to_is_kept_for_redeeming_it()
    {
        // Roborock issues an emailed code TO a client, and only that client can redeem it. The two stages are
        // separate calls (and may be separated by a restart), so the identity has to be written down between
        // them - generating a fresh one per stage came back as "that code isn't right", which reads exactly
        // like a typo and sends you looking in completely the wrong place.
        var state = new Scratch();
        state.Set(RoborockPlugin.EmailKey, "someone@example.com");
        state.Set(RoborockPlugin.DeviceKey, "a-device");
        state.Set(RoborockPlugin.SentByKey, "v4");

        Assert.Equal("a-device", state.Get(RoborockPlugin.DeviceKey));

        // Without it, the code stage refuses rather than trying with a new identity that cannot possibly work.
        var lost = new Scratch();
        lost.Set(RoborockPlugin.EmailKey, "someone@example.com");
        var refused = await Assert.ThrowsAsync<PluginSetupException>(() =>
            new RoborockPlugin().GetNextStageAsync(
                "code", new PluginValues(new Dictionary<string, string> { ["code"] = "875457" }), lost, default));
        Assert.Contains("start setup again", refused.Message);
    }

    [Fact]
    public async Task Before_signing_in_it_asks_for_an_address_and_offers_nothing()
    {
        var stage = await StageAsync(new Scratch());

        Assert.NotNull(stage);
        Assert.Equal("address", stage!.Id);
        Assert.Equal(new[] { "email" }, stage.Fields.Keys);
        Assert.True(stage.Fields["email"].Required);

        // Not one command until it is signed in, and none of them is a way TO sign in - because signing in is
        // not something a model does.
        Assert.Empty(Tools(new Scratch()));
    }

    [Fact]
    public async Task A_stored_session_is_what_turns_the_vacuum_commands_on()
    {
        var state = Ready();

        Assert.Null(await StageAsync(state));   // nothing left to ask

        var tools = Tools(state);
        Assert.Equal(
            new[]
            {
                "roborock_status", "roborock_rooms", "roborock_clean", "roborock_clean_rooms",
                "roborock_pause", "roborock_stop", "roborock_dock", "roborock_find",
            },
            tools.Select(t => t.Name));

        // The one thing a room clean cannot be run without.
        var rooms = tools.Single(t => t.Name == "roborock_clean_rooms");
        Assert.True(rooms.Parameters.Single(p => p.Name == "rooms").Required);
        Assert.Equal("integer", rooms.Parameters.Single(p => p.Name == "repeat").Type);
        Assert.False(rooms.Parameters.Single(p => p.Name == "vacuum").Required);
    }

    [Fact]
    public void No_command_anywhere_takes_a_credential()
    {
        // The whole reason sign-in moved into setup. A model handed a "code" parameter will fill it in - with
        // something it read somewhere, or something that merely looks right - so it is never handed one.
        var tools = Tools(Ready());

        Assert.DoesNotContain(tools.SelectMany(t => t.Parameters), p =>
            p.Name.Contains("code", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("email", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("token", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(tools, t =>
            t.Name.Contains("sign", StringComparison.OrdinalIgnoreCase) ||
            t.Name.Contains("code", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_session_written_by_something_else_is_treated_as_no_session()
    {
        // Half a file, or one from an older build. Asking again is recoverable; presenting nonsense to
        // Roborock and reporting whatever comes back is not.
        var rubbish = new Scratch();
        rubbish.Set(RoborockPlugin.EmailKey, "someone@example.com");
        rubbish.Set(RoborockAccount.SessionKey, "{ not json");

        Assert.False(RoborockAccount.SignedIn(rubbish));
        Assert.Equal("address", (await StageAsync(rubbish))!.Id);
        Assert.Empty(Tools(rubbish));
    }

    [Fact]
    public async Task A_room_clean_with_no_rooms_named_is_refused_before_anything_is_sent()
    {
        var tool = Tools(Ready()).Single(t => t.Name == "roborock_clean_rooms");

        var output = await tool.InvokeAsync(new ToolCallArguments(Json("{}")));

        Assert.True(output.IsError);
        Assert.Contains("rooms", output.Content);
    }
}
