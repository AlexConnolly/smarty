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

var provider = new OllamaModelProvider(config.OllamaBaseUrl);

// Cache searches + fetched pages for an hour (persisted to the Slack data dir, survives restarts) so
// repeated lookups don't re-hit — and re-trip the bot-blocks of — the search engines and sites.
WebResearch.Cache = new FileResearchCache(Path.Combine(config.DataDir, "research-cache.json"));

// Web research only — no shell (anyone who can @mention the bot would otherwise get code execution on this
// host) and no memory (multi-user; deferred). Built once and shared by every delegated worker.
var workerTools = new AgentTool[]
{
    WebResearch.SearchTool(),
    WebResearch.PageAnswerTool(provider, config.Model),
};

var options = new OrchestratorOptions
{
    SystemPrompt = SlackPrompts.OrchestratorSystem(config.CompanyName, config.CompanyContext),
    Tools = Orchestrator.TaskTools,                  // delegate + task management, no project tools
    WorkerTools = _ => workerTools,
    RevoiceThink = true,                             // keep relayed results clean (CoT → ignored reasoning channel)
};

var orchestrator = new Orchestrator(
    config.Model, config.OllamaBaseUrl, () => SlackPrompts.WorkerSystem(config.CompanyName),
    json, training, memory, projects, runs, options);

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
var gateway = new SlackGateway(api, orchestrator, qualifier, botUserId);
var socket = new SlackSocketMode(api);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try { await socket.RunAsync(gateway.HandlePayloadAsync, cts.Token); }
catch (OperationCanceledException) { /* shutting down */ }

Console.WriteLine("[slack] stopped.");
return 0;
