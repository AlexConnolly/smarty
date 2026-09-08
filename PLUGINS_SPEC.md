# Smarty — Plugins Specification

A plugin is a DLL somebody wrote, dropped into Smarty as a zip, that turns up in the next task as a set of
tools and a specialist to delegate them to. No rebuild of this repo, no restart, no entry in any config file.

The shape is deliberately the same as an MCP server's — a bundle of commands for one external system — but it
runs **in process**, in C#, against a typed contract. Reach for a plugin when the thing you want to talk to
has a .NET client or a plain HTTP API and you would rather write a class than a stdio server.

---

## 1. The contract

One assembly, `Smarty.Plugins`, with nothing else in it. A plugin project references it and implements
`IPlugin`:

```csharp
public interface IPlugin
{
    string Name { get; }            // "Weather", "Roborock" — the control centre, and the persona
    string Description { get; }     // what it's FOR, one line — becomes the persona's description

    // null = nothing left to ask. `previous` is the stage just submitted (null = start).
    Task<PluginStage?> GetNextStageAsync(
        string? previous, PluginValues submitted, IPluginState state, CancellationToken ct);

    IReadOnlyList<PluginCommand> GetCommands(IPluginState state);
}
```

**Both properties are required, and a blank `Description` is refused at upload.** The name says what the plugin
*is*; the description says what it's *for*, and that is the part that makes it reachable — it becomes the
description of the persona Smarty delegates to, and it is what the orchestrator reads when deciding a job
belongs here. A plugin without one loads perfectly and never gets chosen, which is the worst failure available.

### Setup, and state

There is no configuration. A plugin has **setup**, which is a sequence of stages, and **state**, which is
everything it has learned:

```csharp
public sealed record PluginStage(
    string Id, string Title, string? Instruction,
    IReadOnlyDictionary<string, PluginParameter> Fields);
```

The host asks `GetNextStageAsync(null, …)` to find out where things stand, renders the stage it gets back on
the plugin's card, and hands what was typed to `GetNextStageAsync(thatStageId, submitted, …)`. The plugin does
whatever that step means, writes what's worth keeping into `state`, and returns the next stage — or null, which
means it's ready.

Setup is a sequence rather than a form because some of it can't be a form. A sign-in code doesn't exist until
something asks for it to be sent, so "your email" and "the code we just emailed you" cannot be two boxes on one
screen: the second only becomes answerable because of the first. **The work of a step happens in the step** —
sending the code, exchanging it, testing a connection — for exactly that reason.

Throw **`PluginSetupException`** when what was submitted can't be used. The same stage comes back with the
reason on it, so a mistyped code is corrected in place rather than the whole thing starting over.

`GetNextStageAsync(null, …)` is called on every boot, so on that path it must be cheap and free of side
effects: read state, and either return the first unanswered stage or null.

**`IPluginState`** is `Get(key)` / `Set(key, value)`, persisted by the host beside the install. A `Set` is
durable before it returns, and it asks the host for the plugin's commands again — so a plugin whose surface
depends on what it knows follows its own setup with no restart.

### Setup is a gate, and the host holds it

Until `GetNextStageAsync` returns null, the host registers **nothing**: no capability, no persona, no tools.
Not "the plugin returns an empty list" — the host doesn't ask. A half-set-up plugin cannot reach a model
whether or not its author remembered to make it so.

That gate is also what keeps credentials away from the model. Anything typed during setup goes to the plugin
and into state; **nothing typed during setup is ever a command parameter**. This is not a stylistic preference.
An earlier version of the Roborock plugin exposed `sign_in(code)` as a command, and a worker called it with a
code it had picked up from earlier in the conversation — unprompted, having also called `send_code` on its own
initiative. A model handed a `code` parameter will fill it in, with something it read or something that merely
looks right. The remedy is not to ask it nicely; it is to never hand it one.

### Failure

Return text and it's the answer. Throw and it's a retryable failure. Throw `PluginDeadEndException` and the
agent loop stops asking — for the town that doesn't exist, the device that isn't paired, the permission the
account hasn't got. That distinction is the difference between one wasted turn and ten.

---

## 2. What Smarty does with it

Installing one plugin creates two things.

**A capability**, id `plugin_<slug>`, holding one `AgentTool` per command. Tool names are namespaced with the
plugin's slug — a command called `now` on the Weather plugin is `weather_now` — so two plugins can both have a
`status` command without either knowing the other exists. A required parameter that's missing is refused
before the plugin is asked, with the same message every time.

**A persona**, id `plugin_<slug>`, whose only capability is that one. This is the point of a plugin over a
loose bag of tools: `Description` is written as a role, so there is somebody for the orchestrator to hand
"is the hallway done?" to. The persona is created once and then left alone — renaming or re-describing it in
the control centre sticks across restarts and reinstalls.

Both appear the moment the upload finishes. Worker toolsets are assembled per task, so the next task sees the
new plugin and a task already running keeps the toolset it started with.

---

## 3. Packaging and loading

A package is a **zip holding the plugin's built DLL plus any libraries it needs**. Nothing declares which DLL
is the plugin: every DLL in the zip is a candidate, and the one implementing `IPlugin` with a parameterless
constructor wins. A zip with no such DLL is refused with the reason, and nothing is installed.

```powershell
.\scripts\pack-plugin.ps1 -Project plugins\Smarty.Plugin.Weather\Smarty.Plugin.Weather.csproj
# → dist\Smarty.Plugin.Weather.zip
```

Two loading decisions carry the design:

- **The contract is not isolated.** `Smarty.Plugins` — and the shared framework — resolves to the *host's*
  copy. A second copy loaded from the package would be a second, unrelated `IPlugin` type: the plugin would
  implement an interface Smarty has never heard of, and the only symptom would be "no plugin DLL in the zip"
  on a zip that plainly has one. `pack-plugin.ps1` leaves it out of the package for the same reason; a
  plugin project references it with `Private="false"`.
- **Nothing is loaded from a file handle.** Assemblies are read into memory first, so no file in a plugin's
  folder is locked. That is what makes uninstall and upgrade work while Smarty is running — on Windows a held
  handle makes the delete fail outright, and the only fix would be a restart.

Everything else the plugin ships loads from its own folder, in its own `AssemblyLoadContext`, so a plugin can
depend on whatever version of whatever library it likes.

**The contract is versioned**, and a package records which version it was built against. A host running an
older one refuses it up front and says so:

> `Smarty.Plugin.Roborock.dll: it was built against Smarty.Plugins 1.2, and this Smarty has 1.1. Restart
> Smarty so it picks up the newer contract, then upload again.`

That refusal exists because the alternative is worse than useless. Without it the plugin's type quietly fails
to load, the scan finds nothing implementing `IPlugin`, and the only thing left to report is "none of these
DLLs implements IPlugin" — which blames a perfectly good package for the host being out of date, and sends
you off repackaging it. An older contract is always fine; nothing is ever removed from it. Bump the minor when
something is added, the major only if something changes or goes — 2.0 replaced configuration keys
with setup stages.

An uploaded zip is a file somebody else wrote: entries are resolved against the destination and any that
would land outside it are refused.

That check is about accidents, not about safety. A plugin is **arbitrary code running in the Smarty process**
with everything Smarty can reach — the same trust you extend to an MCP server you configure, or to the shell
tool. Upload plugins you wrote or would run yourself; the password on the control centre is what stands
between the upload form and everyone else.

---

## 4. Where it's kept

Under `data/`, which is gitignored — a plugin's API key is not ours to commit.

```
data/plugins.json          the installed plugins, and everything each one knows
data/plugins/<slug>/       the unpacked package
```

One dictionary per plugin, opaque to the host: what setup put there and what the plugin obtained for itself sit
together, because to the plugin they are the same thing. The host never declares its shape, never renders it as
a form, and **never sends it to the browser**. What the control centre shows is the STAGE a plugin is waiting
on — the plugin's own description of what it wants next — and never what has already been answered. That is
what keeps a session token, or a password typed into a stage, out of every page load.

Uninstalling deletes it, which matters: that dictionary is where the credentials were.

## 5. The control centre

**Plugins** tab. Drop a zip, or *Upload Smarty plugin*.

A plugin that isn't set up shows the step it's waiting on: a title, a line of instruction, and its fields. Fill
them in and it moves on, or comes back with what was wrong. Once it's through, the card shows the commands it
exposes under the tool names the model will call, and the persona it created. Until then it shows no commands,
because it has none.

*Set up again* forgets everything the plugin knew and returns to the first stage — the way back from a revoked
sign-in or the wrong account. *Turn off* withdraws the capability and the persona but keeps what it knows;
*Remove* deletes its files and its state.

A plugin that won't load is **recorded, not repaired**: the reason is on the card and it stays listed. Nothing
retries it in the background, because the fix is a decision only whoever packaged it can make.

| | |
|---|---|
| `GET /api/control/plugins` | every installed plugin, its state, the stage it wants and its commands |
| `POST /api/control/plugins` | multipart zip upload; installs or replaces |
| `POST /api/control/plugins/{id}/setup` | answer the current stage (`{ stage, values }`) |
| `POST /api/control/plugins/{id}/setup/reset` | forget everything and start setup again |
| `POST /api/control/plugins/{id}/enabled` | on/off without uninstalling |
| `DELETE /api/control/plugins/{id}` | uninstall, and delete what it knew |

The submitted stage id is checked against the one actually outstanding, so a page left open on a step that has
since moved on is told to reload rather than answering the wrong question with the wrong screen's contents.

Re-uploading an upgraded package keeps everything setup had established — an upgrade shouldn't make anyone sign
in again.

## 6. Worked example: the weather plugin

`plugins/Smarty.Plugin.Weather` is a real plugin, not a stub. It reads Open-Meteo, which needs no account and
no key, so the only thing between "uploaded" and "answering" is the loader.

One setup stage with two optional answers: `default_location` is what a bare "what's it like out?" resolves
to, and `units` switches the numbers between Celsius and Fahrenheit. Both can be left blank — but the stage
is still required, because "we asked and you said nothing" is a different state from "we never asked", and
only the first should let a plugin start working. Two commands, `now` and `forecast`, reaching the model as
`weather_now` and `weather_forecast`.

```
weather_now {}
  → Leicester, England, United Kingdom — partly cloudy, as of 2026-08-19T19:00 local time.
    Temperature: 20.1°C (feels like 19.7°C) …

weather_now { "location": "Nowherecestershire" }
  → dead end: Nowhere called 'Nowherecestershire' — check the spelling, or add a country (Boston, US).
```

---

## 7. Worked example: the Roborock plugin

`plugins/Smarty.Plugin.Roborock` is the real thing rather than a demonstration, and it exercises the parts of
the design the weather plugin never touches: a secret, a shipped dependency, and a session too expensive to
rebuild per call.

Roborock publish no API, so it takes the route their own app takes — sign in over HTTPS, read the home, then
talk to each vacuum through their MQTT broker in the frame format the machines speak: a small binary header
around an AES body whose key is derived per message from that message's own timestamp, the device's local key
and a fixed salt.

| | |
|---|---|
| `roborock_status` | what each vacuum is doing — cleaning, charging, stuck — with battery, suction and any error |
| `roborock_rooms` | the rooms it can be sent to, by their names in the app |
| `roborock_clean` | a full clean of the whole place |
| `roborock_clean_rooms` | named rooms only — "kitchen, hallway" — optionally two passes |
| `roborock_pause` / `roborock_stop` | pause where it is / stop the clean |
| `roborock_dock` | send it back to charge |
| `roborock_find` | make it call out, for one stuck under something |

Setup is two stages, because it has to be:

| | |
|---|---|
| **address** | the email the Roborock app signs in with. Submitting it is what *sends* the code. |
| **code** | the code from that email, traded for a session that is then kept. |

There is no password, because **Roborock no longer accepts one** — a password login now returns
`2031, "need two step validate"`. And there is no `sign_in` command: the code is typed on the plugin's card and
goes straight to Roborock. No model ever sees it, and no model can invent one, because none of them is offered
a field to put one in.

Until the second stage is answered the plugin has no capability, no persona and no tools. Answering it turns
all eight on at once.

Five things about it are worth copying:

- **It ships MQTTnet in its own zip.** Smarty has no MQTT client and gains no dependency on one; the library
  loads from the plugin's own folder. This is the case plugins exist for.
- **It signs in once, ever.** Roborock rate-limit code requests and reading the home (about forty a day), so a
  plugin that authenticated per command would lock the account out before lunch. The session goes in state and
  the home is cached for six hours; a restart costs nothing at all. A session Roborock stops accepting is
  thrown away rather than presented again, and *Set up again* is the way back.
- **Failure is sorted into "try again" and "never".** A vacuum that doesn't answer in twenty seconds is
  probably asleep, and worth another go. A wrong password, a room that isn't on the map, a vacuum name that
  isn't on the account — those are `PluginDeadEndException`, and they name the rooms or the vacuums that DO
  exist, because that is the one thing worth saying back.
- **Several vacuums is the plugin's problem, not the user's.** Nothing is configured about which machine is
  which. An instruction with no vacuum named goes to all of them — "send it back to the dock" in a house with
  two means both — and each one's outcome is reported on its own line, so one asleep upstairs doesn't sink
  the other. A room clean is routed by the room instead: only one vacuum has a kitchen on its map.

Room names are the plugin's own join: the vacuum knows numbered segments, the account knows named rooms, and
`get_room_mapping` is the only thing that connects them. "Clean the kitchen and the landing" is resolved
against every vacuum's map at once and split into one instruction per machine. Exact names are considered
across all the maps before any partial one, so a vacuum with a room called exactly "Hallway" beats one with a
"Hallway Cupboard" rather than the two being called a tie.

What's left after that is a real tie, and it is the **one** question the plugin puts back: two vacuums that
both have a "Hallway" have two different hallways, and picking one sends a machine somewhere nobody asked for.
Everything else it works out.

---

## 8. Writing one

```
dotnet new classlib -o plugins/Smarty.Plugin.Yours
```

Reference the contract with `Private="false"`, implement `IPlugin`, pack, upload:

```xml
<ProjectReference Include="..\..\src\Smarty.Plugins\Smarty.Plugins.csproj" Private="false" />
```

Write command descriptions for the model — what it does and when to reach for it, not how it works. Name
commands bare (`now`, `start`, `status`); Smarty namespaces them. Return text a person could read, because
that text is what the worker will quote.

Write stage instructions for the PERSON, and put anything secret in a stage rather than a parameter. The test
is simple: if a model filling this in with a plausible-looking value would be bad, it belongs in setup.
