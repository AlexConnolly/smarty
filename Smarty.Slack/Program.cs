using System.Text.Json;
using System.Text.Json.Serialization;
using Smarty.Agents;
using Smarty.Api;
using Smarty.Slack;

// ---- Smarty.Slack: the same engine as the web app, listening on Slack threads ----
//
// A thread is a conversation. People tag @smarty to start it listening; from then on a cheap pre-processor
// decides which thread messages are actually for Smarty. Work is delegated to background workers (web
// research only) and their answers come back into the thread — the async, snappy rhythm the chat already has.
// Reuses Smarty.Api's Orchestrator/Session unchanged; only the prompt, toolset and output sink differ.

SlackConfig config;
try { config = SlackConfig.FromEnvironment(); }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

// Isolated stores — Slack gets its OWN data dir, never the web app's real data. Memory/projects stay empty
// here (no projects on Slack for now); they exist only to satisfy the shared orchestrator's constructor.
Directory.CreateDirectory(config.DataDir);
var memory = new MemoryStore(Path.Combine(config.DataDir, "memory.json"), json);
var projects = new ProjectStore(Path.Combine(config.DataDir, "projects.json"), json);
var runs = new ProjectRunStore(Path.Combine(config.DataDir, "runs.json"), json);
var training = new TrainingLog(Path.Combine(config.DataDir, "training-data"), json);
// Scheduled tasks (reminders / future actions), persisted so they survive a restart.
var schedules = new ScheduleStore(Path.Combine(config.DataDir, "schedules.json"), json);

// Load dynamic configuration from the settings database (shared with the web API)
string dbPath = Path.Combine(Directory.GetCurrentDirectory(), "data", "settings.db");
if (!File.Exists(dbPath))
{
    var apiDbPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "Smarty.Api", "data", "settings.db"));
    if (File.Exists(apiDbPath)) dbPath = apiDbPath;
}

var settingsDb = new SettingsDatabase(dbPath);

var integrations = new IntegrationConfig((capId, key) => settingsDb.GetSetting($"{capId}.{key}"));
var capabilities = new CapabilityRegistry(new ICapability[]
{
    new KibanaCapability(), new CodeCapability(), new GitHubCapability(), new JiraCapability(),
});
var personas = new PersonaStore();

string dbOllamaUrl = settingsDb.GetSetting("ollama.baseUrl", config.OllamaBaseUrl)!;
string dbDefaultModel = settingsDb.GetSetting("ollama.model", config.Model)!;
string dbTogetherKey = settingsDb.GetSetting("together.apiKey", "")!;
string dbTogetherUrl = settingsDb.GetSetting("together.baseUrl", "https://api.together.xyz/v1")!;

// Register Together provider
ModelProviderRegistry.Default.Register("together", spec => new TogetherModelProvider(dbTogetherKey, spec.BaseUrl ?? dbTogetherUrl));

// Load primary model spec
string primaryProvider = settingsDb.GetSetting("model.provider", "ollama")!;
string primaryModelName = settingsDb.GetSetting("model.modelName", dbDefaultModel)!;
string? primaryBaseUrl = settingsDb.GetSetting("model.baseUrl");
if (string.IsNullOrWhiteSpace(primaryBaseUrl) && primaryProvider == "ollama")
    primaryBaseUrl = dbOllamaUrl;

var primarySpec = new ModelSpec(primaryProvider, primaryModelName, string.IsNullOrWhiteSpace(primaryBaseUrl) ? null : primaryBaseUrl);
ModelSpec.Default = primarySpec;

// Load secondary model spec
string? secondaryProvider = settingsDb.GetSetting("secondaryModel.provider");
string? secondaryModelName = settingsDb.GetSetting("secondaryModel.modelName");
string? secondaryBaseUrl = settingsDb.GetSetting("secondaryModel.baseUrl");

if (!string.IsNullOrWhiteSpace(secondaryProvider) && !string.IsNullOrWhiteSpace(secondaryModelName))
{
    if (string.IsNullOrWhiteSpace(secondaryBaseUrl) && secondaryProvider == "ollama")
        secondaryBaseUrl = dbOllamaUrl;
    ModelSpec.SecondaryDefault = new ModelSpec(secondaryProvider, secondaryModelName, string.IsNullOrWhiteSpace(secondaryBaseUrl) ? null : secondaryBaseUrl);
}
else
{
    ModelSpec.SecondaryDefault = null;
}

var provider = ModelProviderRegistry.Default.Resolve(primarySpec);
var secondaryModelProvider = ModelSpec.SecondaryDefault != null ? ModelProviderRegistry.Default.Resolve(ModelSpec.SecondaryDefault) : provider;
var secondaryModelNameVal = ModelSpec.SecondaryDefault?.Model ?? primarySpec.Model;

// Cache searches + fetched pages for an hour (persisted to the Slack data dir, survives restarts) so
// repeated lookups don't re-hit — and re-trip the bot-blocks of — the search engines and sites.
WebResearch.Cache = new FileResearchCache(Path.Combine(config.DataDir, "research-cache.json"));

// Web research + read-only file tools — no shell (anyone who can @mention the bot would otherwise get code
// execution on this host). The file tools only READ files the user attached (carried into the task workspace).
var webTools = new AgentTool[]
{
    WebResearch.SearchTool(),
    WebResearch.PageAnswerTool(provider, config.Model),
    FileTools.ReadFileTool(),
    FileTools.SummaryTool(secondaryModelProvider, secondaryModelNameVal),
};

// Per-user memory: a fact is scoped to the SPEAKER by default ("I'm vegetarian" → that person), or shared
// team-wide when flagged (the office address). Reads span the speaker's own scope + the shared scope. The
// worker's tools are built PER TASK so they carry the asker's scope (TaskInfo.UserScope); the orchestrator's
// set_memory is a schema only — it's executed with the live speaker's scope inside the orchestrator.
var planner = new TaskPlanner(primarySpec, ModelProviderRegistry.Default, () => webTools); // recon = web only, read-only

// Supervisor: watches running workers and, when one thrashes (relentless failing search), nudges it to wrap
// up with what it has — or aborts a hopeless task. The go/no-go check only runs when cheap signals trip.
var watchdog = new TaskWatchdog(primarySpec, ModelProviderRegistry.Default);

var options = new OrchestratorOptions
{
    SystemPrompt = SlackPrompts.OrchestratorSystem(config.CompanyName, config.CompanyContext),
    // delegate + task management (no project tools) + per-user memory (recall/record from chat).
    Tools = Orchestrator.TaskTools
        .Append(MemoryTools.SearchTool(memory))
        .Append(MemoryTools.SetChatPersonalTool(memory))
        .ToArray(),
    WorkerTools = task => webTools
        .Append(MemoryTools.SearchPersonalTool(memory, task.UserScope, task.PersonalMemoryEnabled))
        .Append(MemoryTools.SetPersonalTool(memory, task.UserScope, task.PersonalMemoryEnabled))
        .ToArray(),
    RevoiceThink = false,                            // re-voicing only relays completed results; disable think/CoT for speed
    Planner = planner,                               // size-gate + plan complex tasks in the background
    Watchdog = watchdog,                             // supervise workers: go/no-go when a search thrashes
    TurnTimeout = TimeSpan.FromSeconds(90),          // chat turn: cut a spiral early so recovery is quick (longer margin for local models)
    WorkspaceRoot = Path.Combine(config.DataDir, "tasks"), // each task gets task.md + the user's attached files
    Schedules = schedules,                           // schedule_task / cancel_schedule + proactive firing into the thread
    Personas = personas,                             // specialist roles delegate can route to
    Capabilities = capabilities,                     // integrations (Kibana…) personas draw on
    IntegrationConfig = integrations,                // credentials for capabilities — never shown to the model
    Model = primarySpec,
    SecondaryModel = ModelSpec.SecondaryDefault,
};

var orchestrator = new Orchestrator(
    config.Model, config.OllamaBaseUrl, () => SlackPrompts.WorkerSystem(config.CompanyName),
    json, training, memory, projects, runs, options);

if (args.Contains("--demo"))
{
    Console.WriteLine("Running local demo...");
    var session = new Session("demo-session");
    try
    {
        TokenTracker.Reset();
        
        await orchestrator.HandleMessageAsync(
            session,
            "what is wrong with production?",
            CancellationToken.None,
            userScope: "user:demo",
            userName: "DemoUser");

        // Wait for all delegated background tasks to complete
        while (session.Tasks.Values.Any(t => t.IsActive))
        {
            await Task.Delay(1000);
        }

        // Wait for the re-voicing phase to append the final answer to the session history
        int historyCountBefore = session.History.Count;
        for (int i = 0; i < 20; i++)
        {
            if (session.History.Count > historyCountBefore)
                break;
            await Task.Delay(1000);
        }
            
        Console.WriteLine($"[DEMO_RESULT]\n{session.History.Last().Content}");
        Console.WriteLine($"[DEMO_TOKENS] Input: {TokenTracker.TotalInputTokens}, Output: {TokenTracker.TotalOutputTokens}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error running demo: {ex}");
    }
    return 0;
}

var api = new SlackApiClient(config.BotToken, config.AppToken);

string botUserId;
try { botUserId = await api.AuthTestAsync(); }
catch (Exception ex)
{
    Console.Error.WriteLine($"Could not authenticate with Slack: {ex.Message}");
    return 1;
}

Console.WriteLine($"[slack] Smarty is @{botUserId}, working with \"{config.CompanyName}\" via model {config.Model}.");

var qualifier = new EngagementQualifier(provider, config.Model);
var gateway = new SlackGateway(api, orchestrator, qualifier, botUserId, config.DataDir);
var socket = new SlackSocketMode(api);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Fire scheduled tasks into their threads at their time (a proactive nudge). Runs alongside the socket;
// pending tasks already past due (e.g. set before a restart) go off on the first tick.
var scheduler = new Scheduler(schedules, gateway.FireScheduledAsync);
_ = scheduler.RunAsync(cts.Token);

try { await socket.RunAsync(gateway.HandlePayloadAsync, cts.Token); }
catch (OperationCanceledException) { /* shutting down */ }

Console.WriteLine("[slack] stopped.");
return 0;
