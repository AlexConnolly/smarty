using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Smarty.Agents;
using Smarty.Api;
using Smarty.Api.Plugins;
using Smarty.Plugins;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// The plugin system end to end: the conversions a plugin author leans on, the mapping from a command to a
/// tool the model can call, and the whole journey of an uploaded zip — unpacked, loaded into its own world,
/// registered as a capability and a persona, configured, restarted, switched off and removed.
/// </summary>
/// <remarks>
/// The package used for the round trip is the REAL weather plugin's DLL, zipped at test time, so what's
/// exercised is the loader against a separately-compiled assembly rather than a type this project already has
/// loaded — which is the only version of the test that could catch the contract being loaded twice.
/// </remarks>
public class PluginTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "smarty-plugin-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- the conversions a plugin is promised ----

    [Fact]
    public void Values_convert_the_strings_everything_actually_arrives_as()
    {
        var values = new PluginValues(new Dictionary<string, string>
        {
            ["place"] = "  Leicester  ",
            ["days"] = "5",
            ["whole"] = "3.0",
            ["ratio"] = "1.5",
            ["on"] = "yes",
            ["off"] = "0",
            ["blank"] = "   ",
        });

        Assert.Equal("Leicester", values.Text("place"));
        Assert.Equal(5, values.Integer("days"));
        Assert.Equal(3, values.Integer("whole"));       // a JSON number that took the scenic route
        Assert.Equal(1.5, values.Number("ratio"));
        Assert.True(values.Boolean("on"));
        Assert.False(values.Boolean("off"));

        // Absent, blank and unparseable are all "not given" rather than a zero nobody asked for.
        Assert.Null(values.Text("blank"));
        Assert.False(values.Has("blank"));
        Assert.Null(values.Integer("place"));
        Assert.Equal(3, values.Integer("nothing", 3));
        Assert.Equal("home", values.Text("nothing", "home"));

        // Keys are matched the way a model types them, not the way the plugin declared them.
        Assert.Equal("Leicester", values.Text("PLACE"));

        var missing = Assert.Throws<ArgumentException>(() => values.Require("device_id"));
        Assert.Contains("device_id", missing.Message);
    }

    [Fact]
    public void Numbers_are_read_the_same_way_on_every_machine()
    {
        var was = Thread.CurrentThread.CurrentCulture;
        try
        {
            // A locale where the decimal separator is a comma. "1.5" must still be one and a half.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal(1.5, new PluginValues(new Dictionary<string, string> { ["x"] = "1.5" }).Number("x"));
        }
        finally { Thread.CurrentThread.CurrentCulture = was; }
    }

    // ---- a command, as a tool ----

    /// <summary>A plugin with one of everything: a required parameter, each value type, a command that fails
    /// transiently and one that hits a dead end.</summary>
    private sealed class ProbePlugin : IPlugin
    {
        public string Name => "Probe Kit";
        public string Description => "Stands in for a real integration.";

        public Task<PluginStage?> GetNextStageAsync(
            string? previous, PluginValues submitted, IPluginState state, CancellationToken ct)
        {
            if (previous is null)
                return Task.FromResult<PluginStage?>(state.Get("endpoint") is null ? Where : null);

            var endpoint = submitted.Text("endpoint") ?? throw new PluginSetupException("An endpoint is needed.");
            state.Set("endpoint", endpoint);
            state.Set("token", submitted.Text("token"));
            return Task.FromResult<PluginStage?>(null);
        }

        private static PluginStage Where => new("where", "Where is it", null,
            new Dictionary<string, PluginParameter>
            {
                ["endpoint"] = PluginParameter.Text("Its address.", required: true),
                ["token"] = PluginParameter.Secret("Its token."),
            });

        public IReadOnlyList<PluginCommand> GetCommands(IPluginState state) => new[]
        {
            new PluginCommand("remember", "Stores something and reads it back.", new Dictionary<string, PluginParameter>
            {
                ["value"] = PluginParameter.Text("What to keep.", required: true),
            }, values => { state.Set("kept", values.Require("value")); return $"kept: {state.Get("kept")}"; }),

            new PluginCommand("run", "Runs it.", new Dictionary<string, PluginParameter>
            {
                ["target"] = PluginParameter.Text("What to run against.", required: true),
                ["times"] = PluginParameter.Integer("How many."),
                ["ratio"] = PluginParameter.Number("How much."),
                ["dry"] = PluginParameter.Boolean("Pretend."),
            }, values => $"{state.Get("endpoint") ?? "(unset)"} ran {values.Require("target")} " +
                         $"{values.Integer("times", 1)}x dry={values.Boolean("dry", false)}"),

            new PluginCommand("wobble", "Fails, but might not next time.", null,
                _ => throw new InvalidOperationException("the socket hiccuped")),

            new PluginCommand("gone", "Can never work.", null,
                _ => throw new PluginDeadEndException("there is no such device")),
        };
    }

    private static AgentTool Tool(PluginCapability capability, string name) =>
        capability.BuildTools(new IntegrationConfig(), new TaskInfo { Id = "t", Description = "d" })
            .Single(t => t.Name == name);

    private static PluginCapability Probe(params (string Key, string Value)[] state)
    {
        var plugin = new ProbePlugin();
        var kept = new Scratch();
        foreach (var (key, value) in state) kept.Set(key, value);
        return new PluginCapability("probe_kit", plugin.Name, plugin.Description, plugin.GetCommands(kept));
    }

    private static ToolCallArguments Args(string json) => new(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Commands_become_namespaced_tools_with_the_types_they_declared()
    {
        var tools = Probe().BuildTools(new IntegrationConfig(), new TaskInfo { Id = "t", Description = "d" });

        // The plugin names its command "run"; two plugins may both do that, so the tool carries the plugin's slug.
        Assert.Equal(new[] { "probe_kit_remember", "probe_kit_run", "probe_kit_wobble", "probe_kit_gone" },
            tools.Select(t => t.Name));

        var run = tools.Single(t => t.Name == "probe_kit_run");
        Assert.Equal("Runs it.", run.Description);
        Assert.Equal("string", run.Parameters.Single(p => p.Name == "target").Type);
        Assert.Equal("integer", run.Parameters.Single(p => p.Name == "times").Type);
        Assert.Equal("number", run.Parameters.Single(p => p.Name == "ratio").Type);
        Assert.Equal("boolean", run.Parameters.Single(p => p.Name == "dry").Type);
        Assert.True(run.Parameters.Single(p => p.Name == "target").Required);
        Assert.False(run.Parameters.Single(p => p.Name == "times").Required);
    }

    [Fact]
    public async Task A_tool_call_reaches_the_command_with_its_arguments_and_the_saved_configuration()
    {
        var run = Tool(Probe(("endpoint", "https://kit.local")), "probe_kit_run");

        // Numbers and booleans arrive as JSON numbers/booleans, not strings — the bag has to cope with both.
        var output = await run.InvokeAsync(Args("""{ "target": "hallway", "times": 3, "dry": true }"""));

        Assert.False(output.IsError);
        Assert.Equal("https://kit.local ran hallway 3x dry=True", output.Content);
    }

    [Fact]
    public async Task A_missing_required_parameter_is_refused_before_the_plugin_is_asked()
    {
        var output = await Tool(Probe(), "probe_kit_run").InvokeAsync(Args("""{ "times": 2 }"""));

        Assert.True(output.IsError);
        Assert.Contains("target", output.Content);
    }

    [Fact]
    public async Task A_throw_is_retryable_and_a_dead_end_is_not()
    {
        var wobble = await Tool(Probe(), "probe_kit_wobble").InvokeAsync(Args("{}"));
        Assert.True(wobble.IsError);
        Assert.True(wobble.CanRetry);
        Assert.Contains("hiccuped", wobble.Content);

        // The plugin's own way of saying "stop asking" — the agent loop must route around it, not hammer it.
        var gone = await Tool(Probe(), "probe_kit_gone").InvokeAsync(Args("{}"));
        Assert.True(gone.IsError);
        Assert.False(gone.CanRetry);
        Assert.Contains("no such device", gone.Content);
    }

    // ---- packaging ----

    [Fact]
    public void A_zip_cannot_write_outside_the_folder_it_is_unpacked_into()
    {
        var root = TempDir();
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = new StreamWriter(archive.CreateEntry("../escaped.txt").Open()))
            entry.Write("no");
        zip.Position = 0;

        Assert.Throws<InvalidDataException>(() => PluginPackage.Extract(zip, Path.Combine(root, "unpacked")));
        Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
    }

    /// <summary>The real weather plugin's built DLL, zipped the way <c>pack-plugin.ps1</c> does.</summary>
    private static MemoryStream WeatherPackage()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Smarty.Plugin.Weather.dll");
        Assert.True(File.Exists(dll), $"the weather plugin should be built alongside the tests, but {dll} is missing");

        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("Smarty.Plugin.Weather.dll");
            using var target = entry.Open();
            using var source = File.OpenRead(dll);
            source.CopyTo(target);
        }
        zip.Position = 0;
        return zip;
    }

    /// <summary>The Roborock plugin's package, zipped the way pack-plugin.ps1 does — its DLL plus the MQTT
    /// client it brings with it.</summary>
    private static MemoryStream RoborockPackage()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Smarty.Plugin.Roborock.dll");
        Assert.True(File.Exists(dll), $"the Roborock plugin should be built alongside the tests, but {dll} is missing");

        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in new[] { dll, Path.Combine(AppContext.BaseDirectory, "MQTTnet.dll") })
            {
                if (!File.Exists(file)) continue;
                using var target = archive.CreateEntry(Path.GetFileName(file)).Open();
                using var source = File.OpenRead(file);
                source.CopyTo(target);
            }
        zip.Position = 0;
        return zip;
    }

    private sealed record Bench(string Root, PluginStore Store, CapabilityRegistry Capabilities, PersonaStore Personas, PluginHost Host, string StorePath);

    private static Bench NewBench(string? dataDir = null)
    {
        dataDir ??= TempDir();
        var storePath = Path.Combine(dataDir, "plugins.json");
        var store = new PluginStore(storePath, Json);
        var capabilities = new CapabilityRegistry(Array.Empty<ICapability>());
        var personas = new PersonaStore(Path.Combine(dataDir, "personas.json"), Json);
        var root = Path.Combine(dataDir, "plugins");
        return new Bench(dataDir, store, capabilities, personas, new PluginHost(root, store, capabilities, personas), storePath);
    }

    [Fact]
    public async Task A_zip_with_no_plugin_in_it_is_refused_and_installs_nothing()
    {
        var bench = NewBench();
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        using (var readme = new StreamWriter(archive.CreateEntry("README.md").Open()))
            readme.Write("# not a plugin");
        zip.Position = 0;

        var result = await bench.Host.InstallAsync(zip);

        Assert.False(result.Ok);
        Assert.Contains("dll", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(bench.Host.Status);
    }


    [Fact]
    public async Task An_uploaded_plugin_is_invisible_until_its_setup_is_done()
    {
        var bench = NewBench();

        var result = await bench.Host.InstallAsync(WeatherPackage());
        Assert.True(result.Ok, result.Message);

        var status = Assert.Single(bench.Host.Status);
        Assert.Equal("weather", status.Id);
        Assert.True(status.Loaded);
        Assert.False(status.Ready);

        // The gate. Nothing is registered while a stage is outstanding - not the tools, and not the persona,
        // whatever the plugin own GetCommands would have said. A half-set-up plugin cannot reach a model.
        Assert.Null(bench.Capabilities.Get("plugin_weather"));
        Assert.Null(bench.Personas.Get("plugin_weather"));
        Assert.Empty(status.Commands);

        // What it wants is its own words, and the host just draws it.
        Assert.NotNull(status.Setup);
        Assert.Equal("preferences", status.Setup!.Id);
        Assert.Equal(new[] { "default_location", "units" }, status.Setup.Fields.Select(f => f.Name));
    }

    [Fact]
    public async Task Answering_the_last_stage_is_what_turns_a_plugin_on()
    {
        var bench = NewBench();
        Assert.True((await bench.Host.InstallAsync(WeatherPackage())).Ok);

        var done = await bench.Host.SubmitSetupAsync("weather", "preferences",
            new Dictionary<string, string> { ["default_location"] = "Leicester", ["units"] = "celsius" });

        Assert.True(done.Ok, done.Message);
        Assert.True(done.Plugin!.Ready);
        Assert.Null(done.Plugin.Setup);

        // Now, and only now, does it exist as far as the model is concerned.
        var capability = bench.Capabilities.Get("plugin_weather");
        Assert.NotNull(capability);
        Assert.Equal(new[] { "weather_now", "weather_forecast" },
            capability!.BuildTools(new IntegrationConfig(), new TaskInfo { Id = "t", Description = "d" }).Select(t => t.Name));

        var persona = bench.Personas.Get("plugin_weather");
        Assert.NotNull(persona);
        Assert.Equal("Weather", persona!.Name);
        Assert.Contains("plugin_weather", persona.CapabilityIds);
    }

    [Fact]
    public async Task Answering_a_stage_that_is_no_longer_the_one_being_asked_is_refused()
    {
        var bench = NewBench();
        Assert.True((await bench.Host.InstallAsync(WeatherPackage())).Ok);

        // A page left open on a step that has since moved on. Taking it would run a step out of order, with
        // whatever that screen happened to be holding.
        var stale = await bench.Host.SubmitSetupAsync("weather", "some-earlier-stage", new Dictionary<string, string>());

        Assert.False(stale.Ok);
        Assert.Contains("Reload", stale.Message);
        Assert.False(bench.Host.StatusOf("weather")!.Ready);
    }

    [Fact]
    public async Task A_stage_that_stores_something_and_then_fails_does_not_let_the_plugin_through()
    {
        var bench = NewBench();
        Assert.True((await bench.Host.InstallAsync(RoborockPackage())).Ok);

        // Roborock's first stage stores the address and THEN asks Roborock to email a code. With an address
        // that has no account the second half fails — and the write from the first half must not have quietly
        // registered the plugin on the way past.
        var failed = await bench.Host.SubmitSetupAsync("roborock", "address",
            new Dictionary<string, string> { ["email"] = "not-a-real-account-9f3a@example.com" });

        Assert.False(failed.Ok);
        Assert.False(failed.Plugin!.Ready);
        Assert.Equal("address", failed.Plugin.Setup!.Id);
        Assert.NotNull(failed.Plugin.Setup.Problem);
        Assert.Null(bench.Capabilities.Get("plugin_roborock"));
        Assert.Null(bench.Personas.Get("plugin_roborock"));
    }

    [Fact]
    public async Task What_setup_learned_survives_a_restart_and_is_never_sent_to_the_browser()
    {
        var bench = NewBench();
        Assert.True((await bench.Host.InstallAsync(WeatherPackage())).Ok);
        // Deliberately a word that appears nowhere in the plugin's own text, so finding it in what goes to the
        // browser can only mean a stored value leaked.
        const string kept = "Zzyzx-9f3a";
        Assert.True((await bench.Host.SubmitSetupAsync("weather", "preferences",
            new Dictionary<string, string> { ["default_location"] = kept })).Ok);

        Assert.Equal(kept, bench.Store.Get("weather")!.State["default_location"]);

        // The status is what the browser gets, and it carries the STAGE a plugin wants next - never what it
        // already knows. That is what keeps a password or a token out of every page load.
        var status = Assert.Single(bench.Host.Status);
        Assert.Null(status.Setup);
        Assert.DoesNotContain(kept, JsonSerializer.Serialize(status));

        // Restart: it comes back ready, with no stage to answer and nothing re-asked.
        var restarted = NewBench(bench.Root);
        await restarted.Host.StartAllAsync();

        var after = Assert.Single(restarted.Host.Status);
        Assert.True(after.Ready);
        Assert.Null(after.Setup);
        Assert.NotNull(restarted.Capabilities.Get("plugin_weather"));
        Assert.Equal(kept, restarted.Store.Get("weather")!.State["default_location"]);
    }

    [Fact]
    public async Task Setting_up_again_forgets_everything_and_withdraws_the_plugin()
    {
        var bench = NewBench();
        Assert.True((await bench.Host.InstallAsync(WeatherPackage())).Ok);
        Assert.True((await bench.Host.SubmitSetupAsync("weather", "preferences",
            new Dictionary<string, string> { ["default_location"] = "Leicester" })).Ok);

        var reset = await bench.Host.ResetSetupAsync("weather");

        Assert.True(reset.Ok, reset.Message);
        // The way back from a revoked sign-in: what it knew is gone, and so is its reach, until setup is redone.
        Assert.Empty(bench.Store.Get("weather")!.State);
        Assert.False(reset.Plugin!.Ready);
        Assert.Equal("preferences", reset.Plugin.Setup!.Id);
        Assert.Null(bench.Capabilities.Get("plugin_weather"));
        Assert.Null(bench.Personas.Get("plugin_weather"));
    }

    [Fact]
    public async Task Turning_a_plugin_off_withdraws_its_tools_and_its_persona()
    {
        var bench = NewBench();
        Assert.True((await bench.Host.InstallAsync(WeatherPackage())).Ok);
        Assert.True((await bench.Host.SubmitSetupAsync("weather", "preferences", new Dictionary<string, string>())).Ok);

        Assert.True((await bench.Host.SetEnabledAsync("weather", false)).Ok);
        Assert.Null(bench.Capabilities.Get("plugin_weather"));
        Assert.Null(bench.Personas.Get("plugin_weather"));
        Assert.False(Assert.Single(bench.Host.Status).Enabled);

        // Back on, without another upload and without setting it up again.
        Assert.True((await bench.Host.SetEnabledAsync("weather", true)).Ok);
        Assert.NotNull(bench.Capabilities.Get("plugin_weather"));
        Assert.NotNull(bench.Personas.Get("plugin_weather"));
    }

    [Fact]
    public async Task Removing_a_plugin_deletes_its_files_while_the_host_is_still_running()
    {
        var bench = NewBench();
        Assert.True((await bench.Host.InstallAsync(WeatherPackage())).Ok);
        var folder = Path.Combine(bench.Root, "plugins", "weather");
        Assert.True(Directory.Exists(folder));

        var removed = bench.Host.Remove("weather");

        Assert.True(removed.Ok, removed.Message);
        // The DLL was loaded by value precisely so this works - a held file handle would fail the delete here.
        Assert.False(Directory.Exists(folder));
        Assert.Empty(bench.Host.Status);
        Assert.Null(bench.Capabilities.Get("plugin_weather"));
        // And everything it had learned goes with it, because that is where its credentials were.
        Assert.Empty(new PluginStore(bench.StorePath, Json).All);
    }

    [Fact]
    public async Task Re_uploading_an_upgraded_package_keeps_what_setup_already_established()
    {
        var bench = NewBench();
        Assert.True((await bench.Host.InstallAsync(WeatherPackage())).Ok);
        Assert.True((await bench.Host.SubmitSetupAsync("weather", "preferences",
            new Dictionary<string, string> { ["default_location"] = "Leicester" })).Ok);

        // And an edit to the persona it created - an upgrade should undo neither.
        bench.Personas.Upsert("plugin_weather", "Met Office", "Knows the weather.", new[] { "plugin_weather" });

        var again = await bench.Host.InstallAsync(WeatherPackage());

        Assert.True(again.Ok, again.Message);
        Assert.True(again.Plugin!.Ready);   // straight back to working: no stage to answer a second time
        Assert.Equal("Leicester", bench.Store.Get("weather")!.State["default_location"]);
        Assert.Equal("Met Office", bench.Personas.Get("plugin_weather")!.Name);
    }

    [Fact]
    public async Task A_command_with_neither_an_argument_nor_a_default_from_setup_says_so()
    {
        var bench = NewBench();
        Assert.True((await bench.Host.InstallAsync(WeatherPackage())).Ok);
        // Set up, but with the optional default deliberately left blank.
        Assert.True((await bench.Host.SubmitSetupAsync("weather", "preferences", new Dictionary<string, string>())).Ok);

        var tool = bench.Capabilities.Get("plugin_weather")!
            .BuildTools(new IntegrationConfig(), new TaskInfo { Id = "t", Description = "d" })
            .Single(t => t.Name == "weather_now");

        var output = await tool.InvokeAsync(Args("{}"));

        Assert.True(output.IsError);
        Assert.False(output.CanRetry);
        Assert.Contains("default", output.Content);
    }


    /// <summary>A state store that keeps things in memory, for the plugin-side tests.</summary>
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

    /// <summary>A plugin that says what it is called but not what it is for.</summary>
    private sealed class MutePlugin : IPlugin
    {
        public MutePlugin(string name, string description) { Name = name; Description = description; }
        public string Name { get; }
        public string Description { get; }
        public Task<PluginStage?> GetNextStageAsync(
            string? previous, PluginValues submitted, IPluginState state, CancellationToken ct) =>
            Task.FromResult<PluginStage?>(null);
        public IReadOnlyList<PluginCommand> GetCommands(IPluginState state) => Array.Empty<PluginCommand>();
    }

    [Fact]
    public void A_package_built_against_a_newer_contract_says_the_host_is_the_stale_one()
    {
        var host = new Version(2, 0);
        AssemblyName Contract(string version) => new("Smarty.Plugins") { Version = Version.Parse(version) };

        // The failure this replaces: the plugin references a contract type this Smarty hasn't got, the type
        // quietly fails to load, and the only thing left to say is "none of these DLLs implements IPlugin" —
        // which blames the package for the host being behind and sends you off repackaging a perfectly good zip.
        var behind = PluginPackage.BuiltForNewerContract(new[] { Contract("2.1.0.0") }, host);
        Assert.NotNull(behind);
        Assert.Contains("2.1", behind);
        Assert.Contains("2.0", behind);
        Assert.Contains("Restart Smarty", behind);

        // The same contract, or an older one, is fine: nothing has ever been removed from it.
        Assert.Null(PluginPackage.BuiltForNewerContract(new[] { Contract("2.0.0.0") }, host));
        Assert.Null(PluginPackage.BuiltForNewerContract(new[] { Contract("1.1.0.0") }, host));

        // And a package that doesn't reference the contract at all is somebody else's problem to report.
        Assert.Null(PluginPackage.BuiltForNewerContract(new[] { new AssemblyName("MQTTnet") }, host));
    }

    [Fact]
    public void The_contract_carries_a_version_for_that_check_to_read()
    {
        // A contract with no version makes the check above meaningless, and the failure it prevents is silent.
        Assert.True(PluginPackage.ContractVersion >= new Version(2, 0));
    }

    [Fact]
    public void A_plugin_has_to_say_what_it_is_for_not_just_what_it_is_called()
    {
        // Nothing routes work to a plugin that doesn't describe its goal — the description IS the persona's,
        // and it's what the orchestrator reads when deciding who a job belongs to. So it's refused, not warned.
        var mute = PluginPackage.Unusable(new MutePlugin("Roborock", "   "));
        Assert.NotNull(mute);
        Assert.Contains("Description", mute, StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(PluginPackage.Unusable(new MutePlugin("", "Runs the vacuums.")));
        Assert.Null(PluginPackage.Unusable(new MutePlugin("Roborock", "Runs the vacuums.")));
    }

    [Fact]
    public void A_plugins_id_comes_from_its_name()
    {
        Assert.Equal("weather", PluginHost.Slug("Weather"));
        Assert.Equal("roborock_s8", PluginHost.Slug("Roborock  S8!"));
        Assert.Equal("", PluginHost.Slug("  ---  "));
        Assert.Equal("plugin_weather", PluginCapability.IdFor("weather"));
        Assert.Equal("plugin_weather", PluginHost.PersonaIdFor("weather"));
    }
}
