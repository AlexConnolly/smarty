using Smarty.Plugins;

namespace Smarty.Api.Plugins;

/// <summary>One field of a setup stage, as the control centre draws it.</summary>
public sealed record PluginFieldView(string Name, string Type, string Description, bool Required, bool Secret);

/// <summary>The step a plugin is waiting on, and why. Sent to the browser; what has already been ANSWERED
/// never is.</summary>
public sealed record PluginStageView(
    string Id,
    string Title,
    string? Instruction,
    IReadOnlyList<PluginFieldView> Fields,
    string? Problem);

public sealed record PluginParamView(string Name, string Type, string Description, bool Required);

/// <summary>A command, and the tool name the model will actually call it by.</summary>
public sealed record PluginCommandView(string Name, string Tool, string Description, IReadOnlyList<PluginParamView> Parameters);

/// <summary>An installed plugin's whole state in one object: what it is, whether it loaded, why not if not,
/// what setup is waiting on, and what it can do once setup is done.</summary>
public sealed record PluginStatus(
    string Id,
    string Name,
    string Description,
    bool Enabled,
    bool Loaded,
    bool Ready,
    string? Error,
    string CapabilityId,
    string PersonaId,
    PluginStageView? Setup,
    IReadOnlyList<PluginCommandView> Commands,
    DateTimeOffset Installed);

/// <summary>What an install/setup/toggle did, in the words the control centre puts on screen.</summary>
public sealed record PluginResult(bool Ok, string Message, PluginStatus? Plugin = null);

/// <summary>
/// The installed plugins, live. Owns the folder they live in, the record of what's installed, and the wiring
/// that makes a plugin's commands reachable: a capability in the registry and a persona that can be delegated
/// to. Everything here happens without a restart — the same rule the MCP servers follow, and for the same
/// reason: worker toolsets are assembled per task, so the next task sees the new plugin and a running one
/// keeps what it started with.
/// </summary>
/// <remarks>
/// <para>
/// Setup gates everything, and the gate is HERE rather than in each plugin. Before anything is registered the
/// host asks the plugin what setup still needs; while the answer is a stage, the plugin gets no capability, no
/// persona and no tools — so a half-set-up plugin is invisible to the model whether or not its author
/// remembered to make it so. It also means a credential typed into a stage is never a command parameter, and
/// so is never something a model holds, passes on, or invents a plausible-looking value for.
/// </para>
/// <para>
/// A plugin that won't load is recorded, not repaired and not retried in the background. The status carries
/// the reason and the control centre shows it; putting it right is an upload or a removal, which is a decision
/// only the person who packaged it can make.
/// </para>
/// </remarks>
public sealed class PluginHost
{
    private sealed class Live
    {
        public LoadedPlugin? Plugin;
        public PluginCapability? Capability;
        public PluginStage? Waiting;
        public string? Problem;
        public string? Error;
    }

    /// <summary>
    /// One plugin's private store. Reads go straight to the install record; a write persists and then asks the
    /// plugin for its commands again, because a plugin that just obtained something is a plugin whose surface
    /// may have changed. Only reachable from a running command, which only exists once setup is done.
    /// </summary>
    private sealed class State : IPluginState
    {
        private readonly PluginHost _host;
        private readonly string _id;

        public State(PluginHost host, string id) { _host = host; _id = id; }

        public string? Get(string key) =>
            !string.IsNullOrWhiteSpace(key) && _host._store.Get(_id) is { } install &&
            install.State.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;

        public void Set(string key, string? value) => _host.WriteState(_id, key, value);
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Live> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, State> _state = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True while the host is asking a plugin for its commands, so a plugin that stores something
    /// from inside that call doesn't ask for the list again from inside building the list.</summary>
    private bool _rebuilding;

    private readonly string _root;
    private readonly PluginStore _store;
    private readonly CapabilityRegistry _capabilities;
    private readonly PersonaStore _personas;
    private readonly Action<string> _log;

    public PluginHost(
        string root,
        PluginStore store,
        CapabilityRegistry capabilities,
        PersonaStore personas,
        Action<string>? log = null)
    {
        _root = root;
        _store = store;
        _capabilities = capabilities;
        _personas = personas;
        _log = log ?? (_ => { });
    }

    /// <summary>The persona an installed plugin creates — the specialist Smarty delegates to when a task is
    /// about the thing this plugin does. Created only once setup is done.</summary>
    public static string PersonaIdFor(string pluginId) => $"plugin_{pluginId}";

    /// <summary>Load and register everything already installed. Called once at boot; a plugin that fails is
    /// recorded against its id and the rest carry on.</summary>
    public async Task StartAllAsync(CancellationToken ct = default)
    {
        foreach (var install in _store.All)
        {
            if (!install.Enabled)
            {
                _log($"[plugins] {install.Name} ({install.Id}) is switched off.");
                continue;
            }

            var result = await ActivateAsync(install, ct).ConfigureAwait(false);
            _log(!result.Ok
                ? $"[plugins] {install.Name} ({install.Id}) didn't load: {result.Message}"
                : result.Plugin?.Ready == true
                    ? $"[plugins] {install.Name} ({install.Id}) — {result.Plugin.Commands.Count} command(s)."
                    : $"[plugins] {install.Name} ({install.Id}) is waiting on setup " +
                      $"(\"{result.Plugin?.Setup?.Title}\") — it contributes nothing until that's done.");
        }
    }

    public IReadOnlyList<PluginStatus> Status => _store.All.Select(Describe).ToList();

    public PluginStatus? StatusOf(string id) => _store.Get(id) is { } install ? Describe(install) : null;

    /// <summary>
    /// Install (or replace) a plugin from an uploaded zip. The package is unpacked somewhere disposable and
    /// the plugin is loaded out of it BEFORE anything is committed, so a zip with no plugin in it is rejected
    /// without leaving a half-installed folder behind.
    /// </summary>
    public async Task<PluginResult> InstallAsync(Stream zip, CancellationToken ct = default)
    {
        var staging = Path.Combine(_root, ".staging", Guid.NewGuid().ToString("n"));

        try
        {
            using (var buffer = new MemoryStream())
            {
                await zip.CopyToAsync(buffer, ct).ConfigureAwait(false);
                if (buffer.Length == 0) return new PluginResult(false, "The upload was empty.");
                if (buffer.Length > PluginPackage.MaxPackageBytes)
                    return new PluginResult(false,
                        $"That package is {buffer.Length / 1024 / 1024} MB — the limit is {PluginPackage.MaxPackageBytes / 1024 / 1024} MB.");
                buffer.Position = 0;

                try { PluginPackage.Extract(buffer, staging); }
                catch (InvalidDataException ex) { return new PluginResult(false, ex.Message); }
                catch (Exception ex) { return new PluginResult(false, $"That doesn't unpack as a zip: {ex.Message}"); }
            }

            using var probe = PluginPackage.Load(staging, $"probe-{Guid.NewGuid():n}", out var problem);
            if (probe is null) return new PluginResult(false, problem ?? "No plugin found in the package.");

            var id = Slug(probe.Instance.Name);
            if (id.Length == 0) return new PluginResult(false, "The plugin's name has no letters or digits in it, so it can't be identified.");

            var name = probe.Instance.Name.Trim();
            var description = (probe.Instance.Description ?? "").Trim();
            var entry = probe.AssemblyFile;
            var typeName = probe.TypeName;

            // Everything needed off the probe is now a plain string; let its world go before touching disk.
            probe.Dispose();

            var existing = _store.Get(id);
            var folder = Path.Combine(_root, id);

            // Replacing an installed plugin keeps what it had already learned and its persona — an upgrade
            // should not make someone sign in again or re-describe the specialist.
            Deactivate(id, keepPersona: true);
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
                Directory.Move(staging, folder);
            }
            catch (Exception ex) { return new PluginResult(false, $"Couldn't put the files in place: {ex.Message}"); }

            var install = new PluginInstall
            {
                Id = id,
                Name = name,
                Description = description,
                Folder = id,
                EntryAssembly = entry,
                TypeName = typeName,
                Enabled = existing?.Enabled ?? true,
                State = new Dictionary<string, string>(existing?.State ?? new(), StringComparer.OrdinalIgnoreCase),
                Installed = existing?.Installed ?? DateTimeOffset.UtcNow,
            };
            _store.Put(install);

            if (!install.Enabled)
                return new PluginResult(true, $"{name} was replaced, and left switched off.", Describe(install));

            var activated = await ActivateAsync(install, ct).ConfigureAwait(false);
            if (!activated.Ok) return activated;

            return new PluginResult(true,
                activated.Plugin?.Ready == true
                    ? $"{name} is installed — {activated.Plugin.Commands.Count} command(s) available now, no restart needed."
                    : $"{name} is installed. Finish setting it up below; until then it contributes nothing.",
                activated.Plugin);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { /* scratch */ }
        }
    }

    /// <summary>
    /// Answer the stage a plugin is waiting on. The plugin decides what that answer means and what comes next;
    /// when it says there is nothing left, the plugin's commands and persona go live in the same breath.
    /// </summary>
    public async Task<PluginResult> SubmitSetupAsync(
        string id, string? stageId, IReadOnlyDictionary<string, string>? values, CancellationToken ct = default)
    {
        var install = _store.Get(id);
        if (install is null) return new PluginResult(false, "No such plugin.");
        if (!install.Enabled) return new PluginResult(false, $"{install.Name} is switched off.");

        Live live;
        PluginStage waiting;
        lock (_lock)
        {
            if (!_live.TryGetValue(id, out live!) || live.Plugin is null)
                return new PluginResult(false, $"{install.Name} isn't loaded, so there's nothing to set up.");

            if (live.Waiting is null)
                return new PluginResult(true, $"{install.Name} is already set up.", Describe(install));

            // Answering a stage that isn't the one being asked for is a stale page, not an instruction. Taking
            // it would run a step out of order, with whatever the previous screen happened to hold.
            if (!string.Equals(stageId, live.Waiting.Id, StringComparison.Ordinal))
                return new PluginResult(false,
                    $"That answered '{stageId}', but {install.Name} is waiting on '{live.Waiting.Id}'. Reload and try again.",
                    Describe(install));

            waiting = live.Waiting;
        }

        PluginStage? next;
        try
        {
            next = await live.Plugin!.Instance
                .GetNextStageAsync(waiting.Id, new PluginValues(values ?? new Dictionary<string, string>()),
                    StateFor(id), ct)
                .ConfigureAwait(false);
        }
        catch (PluginSetupException ex)
        {
            // The plugin's own way of saying "that answer won't do". The same stage comes back with the reason
            // on it, so a mistyped code is corrected in place rather than starting the whole thing again.
            lock (_lock) live.Problem = ex.Message;
            return new PluginResult(false, ex.Message, Describe(install));
        }
        catch (Exception ex)
        {
            lock (_lock) live.Problem = ex.Message;
            return new PluginResult(false, $"That step failed: {ex.Message}", Describe(install));
        }

        lock (_lock) { live.Waiting = next; live.Problem = null; }

        if (next is not null)
            return new PluginResult(true, next.Title, Describe(_store.Get(id) ?? install));

        // Setup is done. Everything that was withheld until now goes in.
        var ready = Register(_store.Get(id) ?? install);
        return ready.Ok
            ? new PluginResult(true,
                $"{install.Name} is set up — {ready.Plugin?.Commands.Count ?? 0} command(s) available now.", ready.Plugin)
            : ready;
    }

    /// <summary>Forget everything a plugin learned and start its setup again — the way back when a sign-in is
    /// revoked, or the wrong account was used.</summary>
    public async Task<PluginResult> ResetSetupAsync(string id, CancellationToken ct = default)
    {
        var install = _store.Get(id);
        if (install is null) return new PluginResult(false, "No such plugin.");

        var cleared = install with { State = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) };
        _store.Put(cleared);

        // Its commands were built from what it knew, and it now knows nothing.
        _capabilities.Unregister(PluginCapability.IdFor(id));
        _personas.Delete(PersonaIdFor(id));
        lock (_lock) { if (_live.TryGetValue(id, out var live)) live.Capability = null; }

        var result = await ActivateAsync(cleared, ct).ConfigureAwait(false);
        return result.Ok
            ? new PluginResult(true, $"{cleared.Name} is back to the start of setup.", result.Plugin)
            : result;
    }

    public async Task<PluginResult> SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        var install = _store.Get(id);
        if (install is null) return new PluginResult(false, "No such plugin.");

        var updated = install with { Enabled = enabled };
        _store.Put(updated);

        if (!enabled)
        {
            Deactivate(id);
            return new PluginResult(true, $"{updated.Name} is off — its commands and its persona are withdrawn.", Describe(updated));
        }

        var activated = await ActivateAsync(updated, ct).ConfigureAwait(false);
        if (!activated.Ok) return activated;

        return new PluginResult(true,
            activated.Plugin?.Ready == true
                ? $"{updated.Name} is on — {activated.Plugin.Commands.Count} command(s)."
                : $"{updated.Name} is on, and waiting on setup.",
            activated.Plugin);
    }

    /// <summary>Uninstall: withdraw the capability and persona, let its world go, and delete its files — and
    /// with them everything it had learned, which is where its credentials were.</summary>
    public PluginResult Remove(string id)
    {
        var install = _store.Get(id);
        if (install is null) return new PluginResult(false, "No such plugin.");

        Deactivate(id);
        _store.Remove(id);
        lock (_lock) _state.Remove(id);

        var folder = Path.Combine(_root, install.Folder);
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex)
        {
            return new PluginResult(true,
                $"{install.Name} is removed and no longer loaded, but its files are still on disk ({ex.Message}).");
        }

        return new PluginResult(true, $"{install.Name} is removed.");
    }

    /// <summary>
    /// Is this tool name one a live plugin provides?
    /// </summary>
    /// <remarks>
    /// So Proact can hold the user's own devices and services. A vacuum is the clearest case: knowing it has not run
    /// today is exactly the kind of thing an assistant should notice, and it cannot notice it without being able to
    /// ask.
    ///
    /// <para>
    /// Every command, not a read-only subset. That was a deliberate decision by the owner of the machine after the
    /// narrower option was put to them — these are their own devices, the plugins are ones they installed, and a
    /// mistake here is a vacuum running at an odd hour rather than money spent or a message sent. The hard limits in
    /// <see cref="Proact"/> are unchanged: nothing that sends, buys or books arrives through a plugin.
    /// </para>
    /// </remarks>
    public bool Provides(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return false;

        lock (_lock)
            return _live.Any(p => p.Value.Capability.Commands.Any(c =>
                string.Equals(PluginCapability.ToolName(p.Key, c.Name), toolName!.Trim(),
                    StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Does a live plugin declare this command? Asked before a panel is published against it.
    /// </summary>
    /// <remarks>
    /// The same two spellings <see cref="RunAsync"/> accepts, because a check that is stricter than the thing it
    /// guards would refuse panels that work. Deliberately false for a plugin that is installed but not ready: a
    /// command that cannot be run now is not something to bind a panel to.
    /// </remarks>
    public bool Has(string? pluginId, string? command)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(command)) return false;

        PluginCapability? capability;
        lock (_lock) capability = _live.TryGetValue(pluginId!.Trim(), out var live) ? live.Capability : null;
        if (capability is null) return false;

        var wanted = command!.Trim();
        return capability.Commands.Any(c =>
            string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(PluginCapability.ToolName(pluginId!.Trim(), c.Name), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Run one of a plugin's commands directly, outside a conversation — for a panel on the home page, which
    /// needs the same answers a worker gets and needs them as data rather than as a sentence.
    /// </summary>
    /// <remarks>
    /// Deliberately the SAME commands, not a second surface. A panel that read its own endpoint would drift
    /// from what the model sees the moment either changed, and there would be two places to fix a vacuum.
    /// </remarks>
    public async Task<PluginOutput> RunAsync(
        string pluginId, string command, IReadOnlyDictionary<string, string>? parameters, CancellationToken ct = default)
    {
        PluginCapability? capability;
        lock (_lock) capability = _live.TryGetValue(pluginId, out var live) ? live.Capability : null;

        if (capability is null)
            throw new InvalidOperationException(
                _store.Get(pluginId) is null
                    ? $"There's no plugin called '{pluginId}'."
                    : $"The {pluginId} plugin isn't ready — finish setting it up first.");

        var wanted = command?.Trim() ?? "";
        var found = capability.Commands.FirstOrDefault(c =>
                        string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase))
                    // Panels tend to be written against the tool name, since that is what everything else shows.
                    ?? capability.Commands.FirstOrDefault(c =>
                        string.Equals(PluginCapability.ToolName(pluginId, c.Name), wanted, StringComparison.OrdinalIgnoreCase));

        if (found is null)
            throw new InvalidOperationException(
                $"{capability.DisplayName} has no command '{wanted}'. It has: " +
                string.Join(", ", capability.Commands.Select(c => c.Name)) + ".");

        return await found.ExecuteAsync(new PluginValues(parameters), ct).ConfigureAwait(false);
    }

    // ---- the wiring ----

    /// <summary>
    /// Load the plugin if it isn't already, ask what setup still needs, and register its commands only if the
    /// answer is "nothing". This is the gate: while a stage is outstanding the plugin has no capability, no
    /// persona and no tools, whatever its own <c>GetCommands</c> would have said.
    /// </summary>
    private async Task<PluginResult> ActivateAsync(PluginInstall install, CancellationToken ct)
    {
        Live live;
        lock (_lock)
        {
            if (!_live.TryGetValue(install.Id, out live!)) _live[install.Id] = live = new Live();

            if (live.Plugin is null)
            {
                var folder = Path.Combine(_root, install.Folder);
                if (!Directory.Exists(folder))
                {
                    live.Error = $"Its folder is gone ({folder}). Upload the package again.";
                    return new PluginResult(false, live.Error, Describe(install));
                }

                var loaded = PluginPackage.Load(folder, $"plugin-{install.Id}-{Guid.NewGuid():n}", out var problem);
                if (loaded is null)
                {
                    live.Error = problem ?? "It didn't load.";
                    return new PluginResult(false, live.Error, Describe(install));
                }
                live.Plugin = loaded;
            }
            live.Error = null;
        }

        PluginStage? waiting;
        try
        {
            waiting = await live.Plugin!.Instance
                .GetNextStageAsync(null, PluginValues.Empty, StateFor(install.Id), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_lock) live.Error = $"It threw while working out what setup it needs: {ex.Message}";
            _capabilities.Unregister(PluginCapability.IdFor(install.Id));
            return new PluginResult(false, $"It threw while working out what setup it needs: {ex.Message}", Describe(install));
        }

        lock (_lock) { live.Waiting = waiting; live.Problem = null; }

        if (waiting is null) return Register(install);

        // Nothing is registered while setup is outstanding — not the tools, and not the persona, because a
        // specialist with nothing behind it is worse than no specialist at all.
        _capabilities.Unregister(PluginCapability.IdFor(install.Id));
        _personas.Delete(PersonaIdFor(install.Id));
        lock (_lock) live.Capability = null;
        return new PluginResult(true, waiting.Title, Describe(install));
    }

    /// <summary>Build the plugin's commands from what it knows and put them in front of the model. Only ever
    /// reached with setup complete.</summary>
    private PluginResult Register(PluginInstall install)
    {
        lock (_lock)
        {
            if (!_live.TryGetValue(install.Id, out var live) || live.Plugin is null)
                return new PluginResult(false, "It isn't loaded.", Describe(install));

            IReadOnlyList<PluginCommand> commands;
            _rebuilding = true;
            try { commands = live.Plugin.Instance.GetCommands(StateFor(install.Id)) ?? Array.Empty<PluginCommand>(); }
            catch (Exception ex)
            {
                live.Error = $"It threw while building its commands: {ex.Message}";
                _capabilities.Unregister(PluginCapability.IdFor(install.Id));
                live.Capability = null;
                return new PluginResult(false, live.Error, Describe(install));
            }
            finally { _rebuilding = false; }

            live.Error = null;
            live.Capability = new PluginCapability(install.Id, install.Name, install.Description, commands);
            _capabilities.Register(live.Capability);

            // Created once, then left alone: the persona is the plugin's front door, and a name or description
            // edited in the control centre shouldn't be overwritten every time the host restarts.
            var personaId = PersonaIdFor(install.Id);
            if (_personas.Get(personaId) is null)
                _personas.Upsert(personaId, install.Name, install.Description, new[] { live.Capability.Id });

            return new PluginResult(true, "", Describe(install));
        }
    }

    /// <summary>
    /// Store something on a plugin's behalf, and rebuild what it offers only if it is past setup. Both a
    /// running command and a setup stage write here; only the first may change what the model can see, and the
    /// rebuild is also skipped while the plugin is mid-<c>GetCommands</c>, which is the only way this could
    /// reach itself.
    /// </summary>
    private void WriteState(string id, string key, string? value)
    {
        PluginInstall updated;
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(key) || _store.Get(id) is not { } install) return;

            var state = new Dictionary<string, string>(install.State, StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(value)) state.Remove(key);
            else state[key] = value;

            updated = install with { State = state };
            _store.Put(updated);

            if (_rebuilding || !updated.Enabled) return;

            // A SETUP STAGE writes state too — an address, before the code that address is about has even been
            // sent. Rebuilding here would put the plugin in front of the model halfway through being set up,
            // which is the one thing the gate exists to prevent. Only a write from a running command, with no
            // stage outstanding, may rebuild.
            if (_live.TryGetValue(id, out var live) && live.Waiting is not null) return;
        }

        var rebuilt = Register(updated);
        if (!rebuilt.Ok) _log($"[plugins] {updated.Name} stored '{key}' but couldn't rebuild: {rebuilt.Message}");
    }

    /// <summary>Withdraw a plugin and let its world go. The persona goes with it — one with no tools behind it
    /// is worse than none — except when this is the first half of an upgrade, which is putting the same plugin
    /// back a moment later.</summary>
    private void Deactivate(string id, bool keepPersona = false)
    {
        lock (_lock)
        {
            _capabilities.Unregister(PluginCapability.IdFor(id));
            if (!keepPersona) _personas.Delete(PersonaIdFor(id));

            if (!_live.TryGetValue(id, out var live)) return;
            live.Plugin?.Dispose();
            live.Plugin = null;
            live.Capability = null;
            live.Waiting = null;
            live.Problem = null;
            live.Error = null;
        }
    }

    private State StateFor(string id)
    {
        lock (_lock)
        {
            if (!_state.TryGetValue(id, out var state)) _state[id] = state = new State(this, id);
            return state;
        }
    }

    private PluginStatus Describe(PluginInstall install)
    {
        Live? live;
        lock (_lock) _live.TryGetValue(install.Id, out live);

        var setup = live?.Waiting is { } stage
            ? new PluginStageView(
                stage.Id,
                stage.Title,
                stage.Instruction,
                stage.Fields.Select(f => new PluginFieldView(
                    f.Key,
                    f.Value.Type.ToString().ToLowerInvariant(),
                    f.Value.Description,
                    f.Value.Required,
                    f.Value.Type == PluginValueType.Secret)).ToList(),
                live.Problem)
            : null;

        var commands = (live?.Capability?.Commands ?? Array.Empty<PluginCommand>())
            .Select(c => new PluginCommandView(
                c.Name,
                PluginCapability.ToolName(install.Id, c.Name),
                c.Description,
                c.Parameters.Select(p => new PluginParamView(
                    p.Key, p.Value.Type.ToString().ToLowerInvariant(), p.Value.Description, p.Value.Required)).ToList()))
            .ToList();

        return new PluginStatus(
            install.Id, install.Name, install.Description, install.Enabled,
            Loaded: live?.Plugin is not null,
            Ready: live?.Capability is not null,
            Error: live?.Error,
            CapabilityId: PluginCapability.IdFor(install.Id),
            PersonaId: PersonaIdFor(install.Id),
            setup, commands, install.Installed);
    }

    /// <summary>A plugin's id, from its name: lowercase, letters and digits, underscores between.</summary>
    public static string Slug(string? name)
    {
        var slug = new string((name ?? "").Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        while (slug.Contains("__")) slug = slug.Replace("__", "_");
        return slug.Trim('_');
    }
}
