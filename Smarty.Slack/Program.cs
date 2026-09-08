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

// Isolated stores — Slack gets its OWN data dir, never the web app's real data. Projects stay empty here (no
// projects on Slack for now); they exist only to satisfy the shared orchestrator's constructor.
Directory.CreateDirectory(config.DataDir);

// Created up front because the people directory resolves Slack user ids through it.
var api = new SlackApiClient(config.BotToken, config.AppToken);
// The brain, and the directory that turns Slack user ids into people. See BRAIN_SPEC.md.
//
// WARNING: recall is NOT scoped to the channel any more. The old store filtered every fact against the room's
// audience, so what three people said in a private channel could not surface anywhere else; the graph has no
// equivalent, so everything in it is visible in every channel. Each edge records its Source, so who said it is
// still known and a filter remains possible — but nothing enforces one, and in a shared workspace that is a leak
// waiting to happen. Fine for one person testing; not fine for a company.
var graph = new Smarty.Brain.Graph(config.DataDir, json);
var brain = new Smarty.Brain.Memory(graph, ModelRouting.Provider(config.Model, config.OllamaBaseUrl),
    config.Model, config.DataDir, json)
{
    Trace = line => Console.WriteLine(line),

    // The boundary this gateway cannot do without: a channel's membership decides what may be recalled in it, so
    // something three people discussed privately stays with those three.
    Sees = BrainContext.Sees,
};
var people = new PeopleStore(Path.Combine(config.DataDir, "people.json"), json,
    lookup: async (alias, ct) => alias.StartsWith("slack:", StringComparison.OrdinalIgnoreCase)
        ? await api.GetUserIdentityAsync(alias[6..], ct).ConfigureAwait(false)
        : (null, null));
var projects = new ProjectStore(Path.Combine(config.DataDir, "projects.json"), json);
var runs = new ProjectRunStore(Path.Combine(config.DataDir, "runs.json"), json);
var training = new TrainingLog(Path.Combine(config.DataDir, "training-data"), json);
// Scheduled tasks (reminders / future actions), persisted so they survive a restart.
var schedules = new ScheduleStore(Path.Combine(config.DataDir, "schedules.json"), json);

// Specialist personas (software engineer, PM…) and the capabilities (integrations) they draw on. Integration
// credentials live in <dataDir>/integrations.json (or SMARTY_<CAP>_<KEY> env vars) and are NEVER shown to the
// model — capabilities read them to build authenticated tools. Built-in personas for now; Kibana is the first
// real integration (read-only log/exception search), which contributes tools only when it's configured.
var integrations = IntegrationConfig.Load(Path.Combine(config.DataDir, "integrations.json"));
var capabilities = new CapabilityRegistry(new ICapability[]
{
    new KibanaCapability(), new CodeCapability(), new GitHubCapability(), new JiraCapability(),
    new DataScienceCapability(), new FigmaCapability(),
});
var personas = new PersonaStore();

// Run startup validation on all registered capabilities
Console.WriteLine("[startup] Validating capability prerequisites...");
capabilities.ValidateAll();

IModelProvider provider = ModelRouting.Provider(config.Model, config.OllamaBaseUrl);

// Read-only file tools, and nothing else. No shell (anyone who can @mention the bot would otherwise get code
// execution on this host) — and, since web research is now the browser, no web either: pointing a
// workspace-wide bot at the host's signed-in Chrome would let any member of the workspace drive someone's
// personal browser sessions. So Slack answers from what it's told, its memory, and the files it's given; it
// says it can't when a question needs the live web.
var readOnlyTools = new AgentTool[]
{
    FileTools.ReadFileTool(),
    FileTools.SummaryTool(provider, config.Model),
};

// Knowledge is scoped by WHO WAS THERE, not by a personal/shared flag: the channel's membership is the
// audience, so "I'm vegetarian" in a DM stays in that DM, and what three people agreed in a private channel is
// recallable when those three talk again. The worker's tools are built PER TASK so they carry that task's room.
var planner = new TaskPlanner(config.Model, config.OllamaBaseUrl, () => readOnlyTools); // recon: read what's attached

// Supervisor: watches running workers and, when one thrashes (relentless failing search), nudges it to wrap
// up with what it has — or aborts a hopeless task. The go/no-go check only runs when cheap signals trip.
var watchdog = new TaskWatchdog(config.Model, config.OllamaBaseUrl);

var options = new OrchestratorOptions
{
    SystemPrompt = SlackPrompts.OrchestratorSystem(config.CompanyName, config.CompanyContext),
    // delegate + task management (no project tools) + the brain (recall/record, scoped to the channel's people).
    // These are schemas: the orchestrator executes them by name against the live room.
    Tools = Orchestrator.TaskTools
        .Concat(MemoryTools.All(brain, () => null, () => null))
        .ToArray(),
    WorkerTools = task => readOnlyTools
        .Concat(MemoryTools.All(brain, () => $"task {task.Id}", () => task.Room.Room.Key))
        .ToArray(),
    RevoiceThink = false,                            // re-voicing only relays completed results; disable think/CoT for speed
    Planner = planner,                               // size-gate + plan complex tasks in the background
    Watchdog = watchdog,                             // supervise workers: go/no-go when a search thrashes
    TurnTimeout = TimeSpan.FromSeconds(90),          // chat turn: cut a spiral early so recovery is quick (longer margin for local models)
    WorkspaceRoot = Path.Combine(config.DataDir, "tasks"), // each task gets task.md + the user's attached files
    Schedules = schedules,                           // schedule_task / cancel_schedule + proactive firing into the thread
    People = people,                                 // slack ids -> people, so rooms resolve to audiences
    Personas = personas,                             // specialist roles delegate can route to
    Capabilities = capabilities,                     // integrations (Kibana…) personas draw on
    IntegrationConfig = integrations,                // credentials for capabilities — never shown to the model
    Routing = new ProjectRouting(graph, null),       // no embedder here, so it falls back to asking which project
};

// Draining the queue. Nothing observes the delay: telling it something is instant, and a question reconciles
// whatever is waiting before it answers.
_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            foreach (var dropped in (await brain.ReconcileAsync().ConfigureAwait(false)).Dropped)
                Console.WriteLine($"[brain] gave up on \"{dropped.Text}\" — {dropped.Error}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[brain] reconciliation loop: {ex.Message}");
        }
    }
});

var orchestrator = new Orchestrator(
    config.Model, config.OllamaBaseUrl, () => SlackPrompts.WorkerSystem(config.CompanyName),
    json, training, brain, projects, runs, options);

string botUserId;
try { botUserId = await api.AuthTestAsync(); }
catch (Exception ex)
{
    Console.Error.WriteLine($"Could not authenticate with Slack: {ex.Message}");
    return 1;
}

Console.WriteLine($"[slack] Smarty is @{botUserId}, working with \"{config.CompanyName}\" via model {config.Model}.");

var qualifier = new EngagementQualifier(provider, config.Model);

// Voice notes: prefer Slack's own transcript; fall back to local Whisper (same engine as the web app) for
// clips Slack didn't transcribe. Both are best-effort — if ffmpeg or the model are missing, the bot simply
// relies on Slack's native transcripts. Ordinary audio UPLOADS are never transcribed-as-instructions.
WhisperTranscriber? whisper = null;
AudioTranscoder? transcoder = null;
if (config.VoiceNotesEnabled)
{
    whisper = new WhisperTranscriber(config.WhisperModelPath, config.WhisperModelUrl);
    // Resolve ffmpeg, installing it via winget if missing (like DataScienceCapability does for Python).
    transcoder = new AudioTranscoder(AudioTranscoder.Ensure());
    Console.WriteLine("[slack] Voice-note transcription enabled (Slack transcript + local Whisper fallback).");
}

var gateway = new SlackGateway(api, orchestrator, qualifier, botUserId, config.DataDir, people,
    config.ControlHubUrl, config.ControlToken, whisper, transcoder);
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
