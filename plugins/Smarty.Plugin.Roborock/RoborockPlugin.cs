using System.Text;
using System.Text.Json;
using Smarty.Plugins;

namespace Smarty.Plugin.Roborock;

/// <summary>
/// The Roborock vacuums on an account: what they're doing, and telling them to do something else.
/// </summary>
/// <remarks>
/// Roborock publishes no API, so this is the route their own app takes — sign in over HTTPS, read the home,
/// then talk to each vacuum through their MQTT broker in the encrypted frame format the machines speak. The
/// account credentials are configuration, never parameters: a command that took a password would put it in a
/// model's context, and this plugin's commands take a room name at most.
/// <para>
/// More than one vacuum is the plugin's problem, not the user's. Nothing is configured about which machine is
/// which: an instruction with no vacuum named goes to all of them, and a room clean is routed by the room —
/// only one vacuum has a kitchen on its map. The single question it will put back is a room name that two
/// vacuums both answer to, which is two different rooms and not something to guess at.
/// </para>
/// </remarks>
public sealed class RoborockPlugin : IPlugin
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string Name => "Roborock";

    public string Description =>
        "Runs the Roborock vacuums: reports what each one is doing, sends them to clean the whole place or " +
        "named rooms, pauses or stops a clean, and sends them back to the dock.";

    internal const string EmailKey = "email";

    /// <summary>The installation identity Roborock knows us by. Generated once and kept, because a code is
    /// issued to a client and only that client can redeem it.</summary>
    internal const string DeviceKey = "device";

    /// <summary>Which of Roborock's two endpoint families sent the code, so the same one redeems it.</summary>
    internal const string SentByKey = "code_sent_by";

    /// <summary>
    /// Signing in, one step at a time — and it has to be steps, because the second question only becomes
    /// answerable because of the first: the code doesn't exist until submitting the address sends it.
    /// </summary>
    /// <remarks>
    /// Nothing here is a command, which is the point. A sign-in code is a live credential, and a command
    /// parameter is something a model fills in — it will pass one on, and given the chance it will make one up.
    /// The code is typed on the plugin's card and goes straight to Roborock; no model ever sees it.
    /// </remarks>
    public async Task<PluginStage?> GetNextStageAsync(
        string? previous, PluginValues submitted, IPluginState state, CancellationToken ct)
    {
        // Asked on every boot: read what we have and say what's left, with no side effects.
        if (previous is null)
            return RoborockAccount.SignedIn(state) ? null : Address;

        switch (previous)
        {
            case "address":
            {
                var email = submitted.Text(EmailKey)
                            ?? throw new PluginSetupException("An email address is needed — the one the Roborock app signs in with.");

                state.Set(EmailKey, email);

                // Kept for the whole sign-in and beyond: Roborock ties the emailed code to the client that
                // asked for it, so the next stage has to introduce itself as the same one.
                var device = state.Get(DeviceKey) ?? RoborockWebApi.NewDevice();
                state.Set(DeviceKey, device);

                try
                {
                    var sentBy = await new RoborockWebApi(Http, email, device).RequestCodeAsync(ct);
                    state.Set(SentByKey, sentBy);
                }
                catch (PluginDeadEndException ex) { throw new PluginSetupException(ex.Message); }
                catch (Exception ex) { throw new PluginSetupException($"Roborock wouldn't send a code: {ex.Message}"); }

                return Code(email);
            }

            case "code":
            {
                var email = state.Get(EmailKey)
                            ?? throw new PluginSetupException("The address was lost — start setup again.");
                var code = submitted.Text("code")
                           ?? throw new PluginSetupException("Enter the code from the Roborock email.");

                var device = state.Get(DeviceKey)
                             ?? throw new PluginSetupException("That sign-in has been lost — start setup again for a new code.");

                try
                {
                    var session = await new RoborockWebApi(Http, email, device)
                        .SignInWithCodeAsync(code, state.Get(SentByKey), ct);
                    state.Set(RoborockAccount.SessionKey, JsonSerializer.Serialize(session));
                }
                catch (PluginDeadEndException ex) { throw new PluginSetupException(ex.Message); }
                catch (Exception ex) { throw new PluginSetupException($"That didn't sign in: {ex.Message}"); }

                return null;
            }

            default:
                return RoborockAccount.SignedIn(state) ? null : Address;
        }
    }

    private static PluginStage Address => new(
        "address",
        "Your Roborock account",
        "Roborock no longer accepts a password — it emails a one-time code. Submitting this sends it.",
        new Dictionary<string, PluginParameter>
        {
            [EmailKey] = PluginParameter.Text("The email address the Roborock app signs in with.", required: true),
        });

    private static PluginStage Code(string email) => new(
        "code",
        "The code Roborock just emailed",
        $"Check {email}. The code is single-use and expires, so if it's been a while, start setup again for a new one.",
        new Dictionary<string, PluginParameter>
        {
            // Secret so it is never rendered back into a page, the same as any other live credential.
            ["code"] = PluginParameter.Secret("The code from the email.", required: true),
        });

    public IReadOnlyList<PluginCommand> GetCommands(IPluginState state)
    {
        var email = state.Get(EmailKey);
        var device = state.Get(DeviceKey);
        if (email is null || device is null || !RoborockAccount.SignedIn(state)) return Array.Empty<PluginCommand>();

        var account = new RoborockAccount(Http, email, device, state);

        var whichVacuum = new Dictionary<string, PluginParameter>
        {
            ["vacuum"] = PluginParameter.Text(
                "Only if one vacuum in particular was meant, by its name in the Roborock app. Omit to act on all of them."),
        };

        // Every instruction fans out over the vacuums it applies to. One that can't be reached is reported on
        // its own line rather than sinking the others — with two machines, "one of them is asleep" is the
        // answer, not a failure — but if none of them could be reached, that IS the failure.
        async Task<string> ForEach(PluginValues p, CancellationToken ct, Func<RoborockDevice, Task<string>> act)
        {
            var vacuums = await account.TargetsAsync(p.Text("vacuum"), ct);
            var lines = new List<string>();
            var failures = new List<Exception>();

            foreach (var vacuum in vacuums)
            {
                try { lines.Add(await act(vacuum)); }
                catch (Exception ex) { failures.Add(ex); lines.Add($"{vacuum.Name}: {ex.Message}"); }
            }

            if (failures.Count == vacuums.Count) throw failures[0];
            return string.Join("\n", lines);
        }

        PluginCommand Instruct(string name, string description, string method, string said) =>
            new(name, description, whichVacuum, (p, ct) => ForEach(p, ct, async vacuum =>
            {
                await account.CallAsync(vacuum, method, null, ct);
                return $"{vacuum.Name}: {said}.";
            }));

        return new PluginCommand[]
        {
            new PluginCommand(
                "status",
                "What the vacuums are doing right now: cleaning, charging, stuck, battery level, and any error. " +
                "Reports on every vacuum unless one is named.",
                whichVacuum,
                async (p, ct) =>
                {
                    var vacuums = await account.TargetsAsync(p.Text("vacuum"), ct);
                    var lines = new List<string>();
                    var readings = new List<VacuumReading>();
                    var failures = new List<Exception>();

                    foreach (var vacuum in vacuums)
                    {
                        // The account's own view, so a vacuum that's off doesn't cost twenty seconds of waiting.
                        if (!vacuum.Online)
                        {
                            lines.Add($"{vacuum.Name}: offline.");
                            readings.Add(new VacuumReading(vacuum.Name) { State = "offline", Online = false });
                            continue;
                        }

                        try
                        {
                            var status = await account.CallAsync(vacuum, "get_status", null, ct);
                            var (text, reading) = RoborockStatus.Read(vacuum.Name, status);
                            lines.Add(text);
                            readings.Add(reading);
                        }
                        catch (Exception ex)
                        {
                            failures.Add(ex);
                            lines.Add($"{vacuum.Name}: {ex.Message}");
                            readings.Add(new VacuumReading(vacuum.Name) { State = ex.Message, Online = false });
                        }
                    }

                    if (vacuums.Count > 0 && failures.Count == vacuums.Count) throw failures[0];

                    // The same answer twice over: a line for whoever asked, and the readings for a panel. The
                    // first vacuum is lifted out as well, because a panel about one vacuum shouldn't have to
                    // index into a list to draw a battery.
                    return new PluginOutput(string.Join(Environment.NewLine, lines), new
                    {
                        vacuums = readings,
                        any = readings.FirstOrDefault(),
                        cleaning = readings.Any(r => r.Cleaning),
                    });
                }),

            new PluginCommand(
                "rooms",
                "The rooms that can be cleaned, and which vacuum has each one on its map. Read this before " +
                "cleaning named rooms if you aren't sure what they're called.",
                whichVacuum,
                (p, ct) => ForEach(p, ct, async vacuum =>
                {
                    var rooms = await account.RoomsAsync(vacuum, ct);
                    return rooms.Count == 0
                        ? $"{vacuum.Name} has no rooms on its map yet — it needs to finish a full clean first."
                        : $"{vacuum.Name} can clean: {string.Join(", ", rooms.Select(r => r.Name))}.";
                })),

            new PluginCommand(
                "clean",
                "Start a full clean of the whole place. Sends every vacuum out unless one is named.",
                whichVacuum,
                (p, ct) => ForEach(p, ct, async vacuum =>
                {
                    await account.CallAsync(vacuum, "app_start", null, ct);
                    return $"{vacuum.Name}: cleaning the whole place.";
                })),

            new PluginCommand(
                "clean_rooms",
                "Send a vacuum to clean specific rooms and nothing else — e.g. \"kitchen, hallway\". Works out " +
                "which vacuum has each room; you only need to name one if two vacuums share a room name.",
                new Dictionary<string, PluginParameter>
                {
                    ["rooms"] = PluginParameter.Text("The rooms to clean, comma-separated, as named in the app.", required: true),
                    ["vacuum"] = PluginParameter.Text("Only if the rooms alone don't say which vacuum is meant."),
                    ["repeat"] = PluginParameter.Integer("How many passes over each room, 1 or 2. Defaults to 1."),
                },
                async (p, ct) =>
                {
                    var wanted = p.Require("rooms").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    var work = await account.RouteAsync(p.Text("vacuum"), wanted, ct);
                    int repeat = Math.Clamp(p.Integer("repeat", 1), 1, 2);

                    var lines = new List<string>();
                    foreach (var (vacuum, segments, rooms) in work)
                    {
                        // Two shapes are in the wild for this one call: older firmware takes a bare list of
                        // room numbers, newer takes an object that can also carry the repeat count. Try the
                        // one that can express what was asked, fall back to the one that always works.
                        try
                        {
                            await account.CallAsync(vacuum, "app_segment_clean",
                                new object[] { new Dictionary<string, object> { ["segments"] = segments, ["repeat"] = repeat } }, ct);
                        }
                        catch (Exception) when (repeat == 1)
                        {
                            await account.CallAsync(vacuum, "app_segment_clean", segments, ct);
                        }

                        lines.Add($"{vacuum.Name}: cleaning {string.Join(" and ", rooms)}" +
                                  (repeat > 1 ? $", {repeat} passes." : "."));
                    }
                    return string.Join("\n", lines);
                }),

            Instruct("pause", "Pause the clean where it is, so it can be resumed.", "app_pause", "paused"),
            Instruct("stop", "Stop the clean. The vacuum stays where it is rather than returning to the dock.", "app_stop", "stopped"),
            Instruct("dock", "Send the vacuum back to its dock to charge.", "app_charge", "on its way back to the dock"),
            Instruct("find", "Make the vacuum announce itself, for finding one that's stuck somewhere out of sight.", "find_me", "calling out"),
        };
    }
}
