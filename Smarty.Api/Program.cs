using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileProviders;
using Smarty.Agents;
using Smarty.Api;

var builder = WebApplication.CreateBuilder(args);

// Open by design — this is a local tester API. Allow any frontend to talk to it.
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddSingleton<AgentRunStore>();

string dataDir = Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);
string dbPath = Path.Combine(dataDir, "settings.db");
var settingsDb = new SettingsDatabase(dbPath);
builder.Services.AddSingleton(settingsDb);

var sessions = new SessionStore();
builder.Services.AddSingleton(sessions);
builder.Services.AddHostedService<SlackProcessManager>();

// Load token usage to initialize TokenTracker
try
{
    string tokenUsagePath = Path.Combine(builder.Environment.ContentRootPath, "..", "token_usage.json");
    if (!File.Exists(tokenUsagePath)) tokenUsagePath = "token_usage.json";
    if (File.Exists(tokenUsagePath))
    {
        var usageJson = File.ReadAllText(tokenUsagePath);
        using var doc = JsonDocument.Parse(usageJson);
        long input = doc.RootElement.TryGetProperty("Input", out var inProp) ? inProp.GetInt64() : 0L;
        long output = doc.RootElement.TryGetProperty("Output", out var outProp) ? outProp.GetInt64() : 0L;
        TokenTracker.Initialize(input, output);
    }
}
catch { }

ConfigureProviders(settingsDb);

// Local Whisper speech-to-text. Model is downloaded once to disk and cached.
string whisperModelPath = builder.Configuration["Whisper:ModelPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "models", "ggml-base.bin");
string whisperModelUrl = builder.Configuration["Whisper:ModelUrl"]
    ?? "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";
builder.Services.AddSingleton(new WhisperTranscriber(whisperModelPath, whisperModelUrl));

// Bind all interfaces so a phone on the same Wi-Fi can reach it directly (http://<pc-lan-ip>:5179)
// — that path streams properly, unlike a free Cloudflare quick tunnel which buffers SSE.
string url = builder.Configuration["Urls"] ?? "http://0.0.0.0:5179";
builder.WebHost.UseUrls(url);

var app = builder.Build();
app.UseCors();

// Serve the built React frontend from the same origin, so a single tunnel exposes the whole
// app (UI + API) and the UI's relative "/api" calls just work. Build it with: cd Smarty.Chat && npm run build
string webRoot = builder.Configuration["WebRoot"]
    ?? Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "Smarty.Chat", "dist"));
if (Directory.Exists(webRoot))
{
    var files = new PhysicalFileProvider(webRoot);
    var staticOptions = new StaticFileOptions { FileProvider = files };
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(staticOptions);
    app.MapFallbackToFile("index.html", staticOptions);
}

// Cache searches + fetched pages for an hour (persisted under data/, survives restarts) so repeated
// lookups reuse the earlier result instead of re-hitting — and re-tripping the bot-blocks of — the network.
string researchCachePath = builder.Configuration["Research:CachePath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "research-cache.json");
WebResearch.Cache = new FileResearchCache(researchCachePath);

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};
var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

// Conversational orchestrator + async workers. Orchestrator and worker share one model (two roles).
// Passive capture of interactions + feedback → a fine-tune dataset that builds from real usage.
string trainingDir = builder.Configuration["Training:Dir"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "training-data");
var trainingLog = new TrainingLog(trainingDir, json);

// Long-term memory of the user — structured facts, persisted to disk (see MEMORY_SPEC.md).
// Embedder: turns text into a vector via a local Ollama embed model, so memory relevance is semantic.
string embedModel = builder.Configuration["Memory:EmbedModel"] ?? "nomic-embed-text";
Func<string, CancellationToken, Task<float[]?>> embed = async (text, ct) =>
{
    try
    {
        string currentOllamaUrl = settingsDb.GetSetting("ollama.baseUrl", "http://localhost:11434")!;
        var payload = JsonSerializer.Serialize(new { model = embedModel, prompt = text }, json);
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync($"{currentOllamaUrl}/api/embeddings", content, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("embedding", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(e => (float)e.GetDouble()).ToArray()
            : null;
    }
    catch { return null; } // embedding is best-effort; the store falls back to keyword matching
};
string memoryPath = builder.Configuration["Memory:Path"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "memory.json");
var memory = new MemoryStore(memoryPath, json, embed);

// Projects — long-running endeavours the chat routes context/jobs by (see PROJECTS_SPEC.md).
string projectsPath = builder.Configuration["Projects:Path"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "projects.json");
var projects = new ProjectStore(projectsPath, json);

// What each project's background workers actually did — thinking + tool calls + result — kept per project
// so the project overview can show it back (read-only).
string runsPath = builder.Configuration["Projects:RunsPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "runs.json");
var projectRuns = new ProjectRunStore(runsPath, json);

// Each delegated task gets a working dir under data/tasks/ — task.md plus any files the user attached.
string workspaceRoot = builder.Configuration["Tasks:WorkspaceRoot"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "tasks");

var capabilities = new CapabilityRegistry(new ICapability[]
{
    new KibanaCapability(), new CodeCapability(), new GitHubCapability(), new JiraCapability(),
});
var personas = new PersonaStore();
var integrations = new IntegrationConfig((capId, key) => settingsDb.GetSetting($"{capId}.{key}"));

var orchestrator = new Orchestrator(
    ModelSpec.Default.Model,
    ModelSpec.Default.BaseUrl ?? settingsDb.GetSetting("ollama.baseUrl", "http://localhost:11434")!,
    WorkerSystemPrompt, json, trainingLog, memory, projects, projectRuns,
    new OrchestratorOptions 
    { 
        WorkspaceRoot = workspaceRoot,
        Model = ModelSpec.Default,
        SecondaryModel = ModelSpec.SecondaryDefault,
        Capabilities = capabilities,
        Personas = personas,
        IntegrationConfig = integrations
    });

app.MapGet("/health", (SettingsDatabase db) => Results.Ok(new { status = "ok", model = db.GetSetting("ollama.model", "qwen3.5:latest") }));

// The projects on the go — for the slide-out bar. Only active projects, with how much is recorded against
// each. Projects are the only thing surfaced here: work with no project isn't shown anywhere.
app.MapGet("/api/projects", () => Results.Ok(
    projects.ActiveProjects().Select(p => new
    {
        slug = p.Slug,
        title = p.Title,
        description = p.Description,
        runs = projectRuns.CountFor(p.Slug),
        facts = memory.Active(p.Slug).Count,
    })));

// One project's overview: everything remembered about it, and everything its background workers did
// (thinking + tool calls + results), newest first. Read-only.
app.MapGet("/api/projects/{slug}", (string slug) =>
{
    var p = projects.Get(slug);
    if (p is null) return Results.NotFound(new { error = "no such project" });
    return Results.Ok(new
    {
        slug = p.Slug,
        title = p.Title,
        description = p.Description,
        status = p.Status,
        summary = p.Summary,
        memories = memory.Active(p.Slug)
            .OrderByDescending(f => f.Asserted)
            .Select(f => new { type = f.Type, key = f.Key, value = f.Value, context = f.Context, asserted = f.Asserted }),
        runs = projectRuns.ForProject(p.Slug)
            .Select(r => new { id = r.Id, task = r.Task, title = r.Title, status = r.Status, startedAt = r.StartedAt, endedAt = r.EndedAt, steps = r.Steps, result = r.Result }),
    });
});

// Open passthrough of the available Ollama models, so the UI can offer a picker.
app.MapGet("/api/models", async (SettingsDatabase db) =>
{
    try
    {
        string currentOllamaUrl = db.GetSetting("ollama.baseUrl", "http://localhost:11434")!;
        var body = await http.GetStringAsync($"{currentOllamaUrl}/api/tags");
        return Results.Content(body, "application/json");
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 502);
    }
});

// Transcribe a voice note (16 kHz mono WAV) to text via local Whisper.
app.MapPost("/api/transcribe", async (HttpRequest req, WhisperTranscriber whisper, CancellationToken ct) =>
{
    Stream audio;
    if (req.HasFormContentType)
    {
        var form = await req.ReadFormAsync(ct);
        var file = form.Files.GetFile("audio") ?? form.Files.FirstOrDefault();
        if (file is null) return Results.BadRequest(new { error = "no audio file" });
        audio = file.OpenReadStream();
    }
    else
    {
        audio = req.Body;
    }

    try
    {
        var text = await whisper.TranscribeAsync(audio, ct);
        return Results.Json(new { text });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

// ---- Conversational session (orchestrator + async workers) ----

// Send a user message. Returns immediately; the orchestrator's reply (and any later results pushed
// back from background workers) arrive asynchronously on the session's event stream.
app.MapPost("/api/session/{id}/message", (string id, SessionMessage body) =>
{
    string text = (body?.Content ?? "").Trim();
    if (text.Length == 0) return Results.BadRequest(new { error = "empty message" });
    var session = sessions.GetOrCreate(id);
    _ = Task.Run(async () =>
    {
        try { await orchestrator.HandleMessageAsync(session, text, CancellationToken.None); }
        catch (Exception ex) { Console.Error.WriteLine($"[orchestrator] {ex}"); }
    });
    return Results.Ok();
});

// Pin a session to a project: it becomes that project's dedicated chat — the orchestrator stays scoped
// to the project (topic, tools, memory). Called once when the project's chat is opened. Pass null to unpin.
app.MapPost("/api/session/{id}/project", (string id, ProjectPin body) =>
{
    var slug = string.IsNullOrWhiteSpace(body?.Slug) ? null : body!.Slug!.Trim().ToLowerInvariant();
    if (slug is not null && !projects.Exists(slug)) return Results.NotFound(new { error = "no such project" });
    var session = sessions.GetOrCreate(id);
    session.PinnedProject = slug;
    session.CurrentProject = slug;
    return Results.Ok();
});

// Thumbs up/down on an assistant message — the label that turns a logged interaction into a good/bad
// training example. Joins back to the interaction log by (session, msg_id).
app.MapPost("/api/session/{id}/feedback", (string id, FeedbackMessage body) =>
{
    if (body is null) return Results.BadRequest();
    trainingLog.Feedback(new
    {
        ts = DateTimeOffset.UtcNow,
        session = id,
        msg_id = body.MessageId,
        rating = body.Rating,
        note = body.Note,
    });
    return Results.Ok();
});

// Quietly cancel a running background task from the UI. This intentionally does not add a chat
// message; the task pill is the control surface for this.
app.MapDelete("/api/session/{id}/task/{taskId}", (string id, string taskId) =>
{
    var session = sessions.Get(id);
    if (session is null) return Results.NotFound(new { error = "session not found" });

    taskId = taskId.TrimStart('#').Trim();
    if (!session.Tasks.TryGetValue(taskId, out var task))
        return Results.NotFound(new { error = $"task #{taskId} not found" });

    if (!task.IsRunning)
        return Results.Ok(new { id = task.Id, status = task.Status });

    task.Status = "cancelled";
    task.Cts.Cancel();
    session.Append("working_done", JsonSerializer.Serialize(new { id = task.Id, status = task.Status }, json));
    return Results.Ok(new { id = task.Id, status = task.Status });
});

// Answer a task that paused to ask a question. The worker resumes from where it left off (seeded with its
// own prior transcript), so it continues with full context. The answer, reply, and any further result all
// arrive on the session stream.
app.MapPost("/api/session/{id}/task/{taskId}/answer", (string id, string taskId, SessionMessage body) =>
{
    string text = (body?.Content ?? "").Trim();
    if (text.Length == 0) return Results.BadRequest(new { error = "empty answer" });

    var session = sessions.Get(id);
    if (session is null) return Results.NotFound(new { error = "session not found" });

    taskId = taskId.TrimStart('#').Trim();
    if (!session.Tasks.TryGetValue(taskId, out var task))
        return Results.NotFound(new { error = $"task #{taskId} not found" });
    if (task.Status != "waiting")
        return Results.Conflict(new { error = $"task #{task.Id} isn't waiting for an answer (status: {task.Status})" });

    _ = Task.Run(async () =>
    {
        try { await orchestrator.AnswerTaskAsync(session, task, text); }
        catch (Exception ex) { Console.Error.WriteLine($"[answer-task] {ex}"); }
    });
    return Results.Ok();
});

// The session's persistent event stream (SSE). The client keeps this open continuously and replays
// from `from` on reconnect, so async pushes are never missed. This stream never ends on its own.
app.MapGet("/api/session/{id}", async (string id, int? from, HttpContext ctx) =>
{
    var session = sessions.GetOrCreate(id);

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache, no-transform";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    int next = Math.Max(0, from ?? 0);
    try
    {
        await ctx.Response.WriteAsync(":" + new string(' ', 2048) + "\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

        while (true)
        {
            var change = session.WaitForChangeAsync();
            bool wrote = false;
            while (session.TryGet(next, out var ev))
            {
                await ctx.Response.WriteAsync($"event: {ev.Event}\n", ctx.RequestAborted);
                await ctx.Response.WriteAsync($"data: {ev.Data}\n\n", ctx.RequestAborted);
                wrote = true;
                next++;
            }
            if (wrote) await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

            await Task.WhenAny(change, Task.Delay(Timeout.Infinite, ctx.RequestAborted));
            if (ctx.RequestAborted.IsCancellationRequested) break;
        }
    }
    catch (OperationCanceledException)
    {
        // client went away — session lives on
    }
});

// Start an agent run in the BACKGROUND and return its id immediately. The run keeps going whether
// or not anyone is connected — disconnecting never loses the answer.
app.MapPost("/api/chat", (ChatRequest request, AgentRunStore store) =>
{
    var (input, prompt) = BuildAgent(request);
    var session = store.Create();
    _ = Task.Run(() => RunAgentAsync(session, input, prompt));
    return Results.Json(new { runId = session.Id });
});

// Subscribe to a run's event stream over SSE, starting at offset `from`. Replays buffered events
// then streams live ones. If the client disconnects, the run continues; reconnect with `from` set
// to the number of events already received to resume exactly where you left off.
app.MapGet("/api/chat/{id}", async (string id, int? from, AgentRunStore store, HttpContext ctx) =>
{
    var session = store.Get(id);
    if (session is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache, no-transform";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    int next = Math.Max(0, from ?? 0);
    try
    {
        // Defeat CDN/proxy buffering (e.g. Cloudflare quick tunnels otherwise hold the whole SSE
        // response and deliver it all at once): a padding comment + immediate flush forces the edge
        // to start streaming straight away. The ":" prefix makes it an SSE comment clients ignore.
        await ctx.Response.WriteAsync(":" + new string(' ', 2048) + "\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

        while (true)
        {
            var change = session.WaitForChangeAsync();

            // Drain everything currently buffered, then flush ONCE. On a reconnect/replay this sends
            // the whole backlog as a single batch (instead of one flush per event), so the client gets
            // it in one chunk and renders once — avoiding the "frozen then boom" catch-up on refresh.
            bool wrote = false;
            while (session.TryGet(next, out var ev))
            {
                await ctx.Response.WriteAsync($"event: {ev.Event}\n", ctx.RequestAborted);
                await ctx.Response.WriteAsync($"data: {ev.Data}\n\n", ctx.RequestAborted);
                wrote = true;
                next++;
            }
            if (wrote)
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

            if (session.Completed && next >= session.Count)
                break;

            await Task.WhenAny(change, Task.Delay(Timeout.Infinite, ctx.RequestAborted));
            if (ctx.RequestAborted.IsCancellationRequested)
                break;
        }
    }
    catch (OperationCanceledException)
    {
        // Client went away — the run keeps running server-side. Nothing to do.
    }
});

// ---- Command Centre settings endpoints ----

app.MapGet("/api/settings", (SettingsDatabase db) =>
{
    return Results.Ok(db.GetAllSettings());
});

app.MapPost("/api/settings", (Dictionary<string, string> newSettings, SettingsDatabase db) =>
{
    if (newSettings == null) return Results.BadRequest("Settings cannot be null.");
    db.SaveSettings(newSettings);
    // Reload model providers in memory
    try
    {
        ConfigureProviders(db, orchestrator);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"Settings saved but providers failed to reload: {ex.Message}" }, statusCode: 400);
    }
    return Results.Ok(db.GetAllSettings());
});

app.MapGet("/api/tokens", () =>
{
    long input = TokenTracker.TotalInputTokens;
    long output = TokenTracker.TotalOutputTokens;
    return Results.Ok(new { input, output, total = input + output });
});

app.MapPost("/api/tokens/reset", () =>
{
    TokenTracker.Reset();
    try
    {
        File.WriteAllText("token_usage.json", $"{{\"Input\": 0, \"Output\": 0}}");
    }
    catch { }
    return Results.Ok(new { input = 0L, output = 0L, total = 0L });
});

app.MapGet("/api/tasks", (SessionStore sessionStore) =>
{
    var tasksList = new List<object>();
    foreach (var session in sessionStore.GetAll())
    {
        foreach (var task in session.Tasks.Values)
        {
            tasksList.Add(new
            {
                id = task.Id,
                sessionId = session.Id,
                description = task.Description,
                project = task.Project,
                persona = task.Persona,
                status = task.Status,
                startedAt = task.StartedAt,
                latestThought = task.LatestThought,
                result = task.Result,
                progressLog = task.ProgressLog.Select(p => new { timestamp = p.Timestamp, message = p.Message }).ToList()
            });
        }
    }
    return Results.Ok(tasksList);
});

app.MapDelete("/api/tasks/{taskId}", (string taskId, SessionStore sessionStore) =>
{
    taskId = taskId.TrimStart('#').Trim();
    foreach (var session in sessionStore.GetAll())
    {
        if (session.Tasks.TryGetValue(taskId, out var task))
        {
            if (task.IsRunning)
            {
                task.Status = "cancelled";
                task.Cts.Cancel();
                session.Append("working_done", JsonSerializer.Serialize(new { id = task.Id, status = task.Status }, json));
            }
            return Results.Ok(new { id = task.Id, status = task.Status });
        }
    }
    return Results.NotFound(new { error = $"task #{taskId} not found" });
});

app.MapGet("/api/capabilities", (SettingsDatabase db) =>
{
    var capRegistry = new CapabilityRegistry(new ICapability[]
    {
        new KibanaCapability(), new CodeCapability(), new GitHubCapability(), new JiraCapability(),
    });
    
    var list = new List<object>();
    var integrationConfig = new IntegrationConfig((capId, key) => db.GetSetting($"{capId}.{key}"));
    
    foreach (var cap in capRegistry.BuildFor(new string[] { "kibana", "code", "github", "jira" }, integrationConfig, new TaskInfo { Id = "diag", Description = "Diagnostic" }).Select(t => t.Name).ToList())
    {
        // Actually, let's just inspect capabilities from the registry directly!
    }

    foreach (var cap in new ICapability[] { new KibanaCapability(), new CodeCapability(), new GitHubCapability(), new JiraCapability() })
    {
        var required = cap.RequiredConfig;
        
        var optional = new List<string>();
        if (cap.Id == "kibana") optional.AddRange(new[] { "api_key", "index", "time_field", "group_field" });
        else if (cap.Id == "github") optional.AddRange(new[] { "token", "repo" });
        
        bool isConnected = required.Count == 0 || required.All(reqKey => !string.IsNullOrWhiteSpace(integrationConfig.Get(cap.Id, reqKey)));
        if (cap.Id == "github")
        {
            isConnected = !string.IsNullOrWhiteSpace(integrationConfig.Get(cap.Id, "token"));
        }

        list.Add(new
        {
            id = cap.Id,
            displayName = cap.DisplayName,
            requiredConfig = required,
            optionalConfig = optional,
            promptHint = cap.PromptHint,
            isConnected = isConnected
        });
    }

    var slackEnabled = db.GetSetting("slack.enabled", "false") == "true";
    var slackBotToken = db.GetSetting("slack.botToken", "")!;
    var slackAppToken = db.GetSetting("slack.appToken", "")!;
    var slackConnected = slackEnabled && !string.IsNullOrWhiteSpace(slackBotToken) && !string.IsNullOrWhiteSpace(slackAppToken);

    list.Add(new
    {
        id = "slack",
        displayName = "Slack Gateway",
        requiredConfig = new List<string> { "enabled", "botToken", "appToken" },
        optionalConfig = new List<string> { "companyName", "companyContext", "dataDir" },
        promptHint = "Connects Smarty to Slack threads, allowing users to tag @smarty to start background agent workflows.",
        isConnected = slackConnected
    });
    
    return Results.Ok(list);
});

app.MapGet("/api/personas", () =>
{
    var store = new PersonaStore();
    return Results.Ok(store.All.Select(p => new
    {
        id = p.Id,
        name = p.Name,
        description = p.Description,
        systemPrompt = p.SystemPrompt,
        capabilityIds = p.CapabilityIds
    }));
});

app.Run();

// ---- helpers ----

// Authoritative date/time + environment + truthfulness context, rebuilt fresh each call so the date
// is always current. Shared by the worker agents and the legacy /api/chat path.
string HostContext()
{
    var now = DateTime.Now;
    string shell = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "PowerShell" : "/bin/sh";
    return
        $"\n\nIt is {now:dddd, d MMMM yyyy, HH:mm} ({TimeZoneInfo.Local.StandardName}), year {now.Year} — the " +
        "real present from the system clock, not the future; never call this date impossible or fictional.\n" +
        $"Your shell ({shell}) is a FULL shell WITH internet — fetch live data yourself (e.g. Invoke-RestMethod); " +
        "never say you lack access. If a command fails, diagnose and retry until you get a real answer.\n" +
        "Report ONLY what a tool actually returned this turn — never fabricate news, prices, results or dates, " +
        "or claim a source you didn't use. If the tools can't get it after honest tries, say so plainly " +
        "(\"I couldn't retrieve that\"). Always reply in English.";
}

// System prompt for a worker (the "hands"): a capable, relentless task-doer with real tools.
string WorkerSystemPrompt() =>
    "You are a capable, relentless assistant. Complete the task with your tools, and base every factual " +
    "claim ONLY on what a tool returned this turn — if the tools don't get it, say so; never fill from memory.\n" +
    "- Web/live info (news, prices, facts, a site): web_search returns only links + snippets, NOT the answer. " +
    "You MUST then get_page_answer on a real result URL to read the page — never answer a content question " +
    "from search snippets alone. One or two pages you've actually READ is enough; then stop.\n" +
    "- run_shell_command: system info, files, local commands, APIs the web can't reach.\n" +
    "- set_memory / search_memory: store or recall durable facts. To RECORD details you've been GIVEN, just " +
    "save them — don't web-search or 'confirm' what the user already stated.\n" +
    "Give a clear, complete answer to the task.\n" +
    "If you genuinely CAN'T proceed without something only the user can decide (a missing choice, a real " +
    "ambiguity, a go/no-go) — don't guess and don't give up. Stop and clearly state what you need them to " +
    "decide, and suggest the few answers you think most likely. Ask ONLY when truly blocked — never to " +
    "confirm something you can just do. When they answer, you'll continue with everything you've found still " +
    "in context." +
    HostContext();

(AgentInput input, string prompt) BuildAgent(ChatRequest request)
{
    var messages = request.Messages ?? new List<ChatMessage>();

    int lastUser = messages.FindLastIndex(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
    string prompt = lastUser >= 0 ? messages[lastUser].Content : (messages.LastOrDefault()?.Content ?? "");

    var conversation = new List<Message>();
    for (int i = 0; i < messages.Count; i++)
    {
        if (i == lastUser) continue; // the prompt is passed to AnswerStream, not seeded
        var m = messages[i];
        switch (m.Role.ToLowerInvariant())
        {
            case "user": conversation.Add(Message.User(m.Content)); break;
            case "assistant": conversation.Add(Message.Assistant(m.Content)); break;
        }
    }

    string baseSystem = string.IsNullOrWhiteSpace(request.System)
        ? "You are a helpful assistant."
        : request.System!;

    string dbOllamaUrl = settingsDb.GetSetting("ollama.baseUrl", "http://localhost:11434")!;
    string dbDefaultModel = settingsDb.GetSetting("ollama.model", "qwen3.5:latest")!;

    var input = new AgentInput
    {
        SystemPrompt = baseSystem + HostContext(),
        Model = ModelSpec.Ollama(string.IsNullOrWhiteSpace(request.Model) ? dbDefaultModel : request.Model!, dbOllamaUrl),
        Conversation = conversation,
    };

    if (request.EnableTools ?? true)
    {
        string modelName = string.IsNullOrWhiteSpace(request.Model) ? dbDefaultModel : request.Model!;
        var provider = new OllamaModelProvider(dbOllamaUrl);
        input.Tools.Add(ShellTool.Create());
        input.Tools.Add(WebResearch.SearchTool());
        input.Tools.Add(WebResearch.PageAnswerTool(provider, modelName));
    }

    return (input, prompt);
}

async Task RunAgentAsync(AgentRunSession session, AgentInput input, string prompt)
{
    void Emit(string @event, object data) => session.Append(@event, JsonSerializer.Serialize(data, json));

    try
    {
        var agent = new SmartyAgent(input);
        await foreach (var ev in agent.AnswerStream(prompt, session.Token))
        {
            switch (ev)
            {
                case AgentEvent.ContentDelta d: Emit("content", new { text = d.Text }); break;
                case AgentEvent.ContentCleared: Emit("content_cleared", new { }); break;
                case AgentEvent.ReasoningDelta r: Emit("reasoning", new { text = r.Text }); break;
                case AgentEvent.ToolStarted s: Emit("tool_started", new { name = s.ToolName, arguments = s.Arguments }); break;
                case AgentEvent.ToolCompleted c: Emit("tool_completed", new { name = c.ToolName, result = c.Result }); break;
                case AgentEvent.Completed done: Emit("completed", new { answer = done.Answer }); break;
            }
        }
    }
    catch (OperationCanceledException)
    {
        Emit("cancelled", new { });
    }
    catch (Exception ex)
    {
        Emit("error", new { message = ex.Message });
    }
    finally
    {
        // Always end with a terminal "done" so subscribers know the run is finished.
        Emit("done", new { });
        session.Complete();
    }
}

void ConfigureProviders(SettingsDatabase db, Orchestrator? orchestratorInstance = null)
{
    string dbOllamaUrl = db.GetSetting("ollama.baseUrl", "http://localhost:11434")!;
    string dbDefaultModel = db.GetSetting("ollama.model", "qwen3.5:latest")!;
    string dbTogetherKey = db.GetSetting("together.apiKey", "")!;
    string dbTogetherUrl = db.GetSetting("together.baseUrl", "https://api.together.xyz/v1")!;

    // Register together provider with active settings
    ModelProviderRegistry.Default.Register("together", spec => new TogetherModelProvider(dbTogetherKey, spec.BaseUrl ?? dbTogetherUrl));

    // Load primary model spec
    string primaryProvider = db.GetSetting("model.provider", "ollama")!;
    string primaryModelName = db.GetSetting("model.modelName", dbDefaultModel)!;
    string? primaryBaseUrl = db.GetSetting("model.baseUrl");
    if (string.IsNullOrWhiteSpace(primaryBaseUrl) && primaryProvider == "ollama")
        primaryBaseUrl = dbOllamaUrl;

    var primarySpec = new ModelSpec(primaryProvider, primaryModelName, string.IsNullOrWhiteSpace(primaryBaseUrl) ? null : primaryBaseUrl);
    ModelSpec.Default = primarySpec;

    // Load secondary model spec
    string? secondaryProvider = db.GetSetting("secondaryModel.provider");
    string? secondaryModelName = db.GetSetting("secondaryModel.modelName");
    string? secondaryBaseUrl = db.GetSetting("secondaryModel.baseUrl");

    ModelSpec? secondarySpec = null;
    if (!string.IsNullOrWhiteSpace(secondaryProvider) && !string.IsNullOrWhiteSpace(secondaryModelName))
    {
        if (string.IsNullOrWhiteSpace(secondaryBaseUrl) && secondaryProvider == "ollama")
            secondaryBaseUrl = dbOllamaUrl;
        secondarySpec = new ModelSpec(secondaryProvider, secondaryModelName, string.IsNullOrWhiteSpace(secondaryBaseUrl) ? null : secondaryBaseUrl);
        ModelSpec.SecondaryDefault = secondarySpec;
    }
    else
    {
        ModelSpec.SecondaryDefault = null;
    }

    if (orchestratorInstance is not null)
    {
        orchestratorInstance.UpdateModels(primarySpec, secondarySpec);
    }
}

// ---- request DTOs ----

internal sealed record ChatRequest(
    string? System,
    string? Model,
    bool? EnableTools,
    List<ChatMessage>? Messages);

internal sealed record ChatMessage(string Role, string Content);

internal sealed record SessionMessage(string Content);
internal sealed record ProjectPin(string? Slug);

internal sealed record FeedbackMessage(int MessageId, string Rating, string? Note);

internal sealed record GateResolutionRequest(bool Approved, bool RememberForTask);


