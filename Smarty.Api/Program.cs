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

// Serve the Smarty.Control command centre at /control from the same origin (built with base "/control/").
// Its API calls hit /api/control/* at the origin root. Build with: cd Smarty.Control && npm run build
string controlRoot = builder.Configuration["ControlRoot"]
    ?? Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "Smarty.Control", "dist"));
if (Directory.Exists(controlRoot))
{
    var controlFiles = new PhysicalFileProvider(controlRoot);
    app.UseStaticFiles(new StaticFileOptions { FileProvider = controlFiles, RequestPath = "/control" });
    // SPA fallback for client-side routes under /control.
    app.MapFallbackToFile("/control/{*path:nonfile}", "index.html",
        new StaticFileOptions { FileProvider = controlFiles });
}

string ollamaBaseUrl = builder.Configuration["Ollama:BaseUrl"] ?? OllamaModelProvider.DefaultBaseUrl;
string defaultModel = builder.Configuration["Ollama:Model"] ?? "qwen3:4b";

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
        var payload = JsonSerializer.Serialize(new { model = embedModel, prompt = text }, json);
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{ollamaBaseUrl}/api/embeddings") { Content = content };
        var apiKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY") ?? Environment.GetEnvironmentVariable("SMARTY_API_KEY");
        if (!string.IsNullOrEmpty(apiKey))
        {
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }
        using var resp = await http.SendAsync(req, ct);
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

// Specialist personas (software engineer, PM…) and the capabilities (integrations) they draw on.
string dataDir = Path.Combine(builder.Environment.ContentRootPath, "data");
var integrations = IntegrationConfig.Load(Path.Combine(dataDir, "integrations.json"));
var capabilities = new CapabilityRegistry(new ICapability[]
{
    new KibanaCapability(), new CodeCapability(), new GitHubCapability(), new JiraCapability(),
    new DataScienceCapability(), new FigmaCapability(),
});
// Personas are now persisted (and editable from Smarty.Control) — built-ins seeded, prompts owned by code.
var personas = new PersonaStore(Path.Combine(dataDir, "personas.json"), json);

// Run startup validation on all registered capabilities (e.g. CLI tools / Python packages).
Console.WriteLine("[startup] Validating capability prerequisites...");
capabilities.ValidateAll();

// Each delegated task gets a working dir under data/tasks/ — task.md plus any files the user attached.
string workspaceRoot = builder.Configuration["Tasks:WorkspaceRoot"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "tasks");

// Smarty.Control command centre: a hub that tracks every conversation (this chat AND Slack, which forwards
// its events here cross-process) and fans them out live to control dashboards. Each new web session gets a
// ControlSink so it's tracked from its first event. A shared token guards the cross-process ingest endpoint.
var controlHub = new ControlHub(Path.Combine(dataDir, "control.json"), json);
string? controlToken = Environment.GetEnvironmentVariable("SMARTY_CONTROL_TOKEN") ?? integrations.Get("control", "token");
var controlCatalog = new ControlCatalog(personas, capabilities, integrations, ollamaBaseUrl, defaultModel);
var controlBuckets = new ControlBuckets(workspaceRoot, personas);

var sessions = new SessionStore(s => s.Sink = new ControlSink(controlHub, s, "chat"));
var orchestrator = new Orchestrator(defaultModel, ollamaBaseUrl, WorkerSystemPrompt, json, trainingLog, memory, projects, projectRuns,
    new OrchestratorOptions
    {
        WorkspaceRoot = workspaceRoot,
        Personas = personas,
        Capabilities = capabilities,
        IntegrationConfig = integrations
    });

app.MapGet("/health", () => Results.Ok(new { status = "ok", model = defaultModel }));

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
app.MapGet("/api/models", async () =>
{
    try
    {
        var body = await http.GetStringAsync($"{ollamaBaseUrl}/api/tags");
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
    // Cancel any hidden child tasks too (a multi-discipline plan's steps run as children with their own
    // tokens) — otherwise an in-flight step keeps running and delivers its result after the user stopped it.
    foreach (var child in session.Tasks.Values.Where(c => c.ParentTaskId == task.Id && c.IsRunning))
    {
        child.Status = "cancelled";
        try { child.Cts.Cancel(); } catch { }
    }
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

// Explicitly stop a run (the UI's Stop button). A disconnect does NOT do this.
app.MapDelete("/api/chat/{id}", (string id, AgentRunStore store) =>
{
    store.Get(id)?.Cancel();
    return Results.Ok();
});

// ======================= Smarty.Control — the command centre =======================
// Read-mostly views over everything Smarty is doing: live conversations (this chat + Slack), tasks/runs,
// files in every bucket, memories, and personas (with the tools each can call — never the system prompt).

object ConvSummary(ControlConversation c) => new
{
    id = c.Id, surface = c.Surface, title = c.Title ?? "(new conversation)", subtitle = c.Subtitle,
    project = c.Project, persona = c.Persona, userName = c.UserName, status = c.Status,
    messageCount = c.MessageCount, startedAt = c.StartedAt, lastActivityAt = c.LastActivityAt,
};
object RunSummary(ControlRun r) => new
{
    id = r.Id, conversationId = r.ConversationId, surface = r.Surface, taskId = r.TaskId,
    task = r.Task, project = r.Project, persona = r.Persona, status = r.Status,
    latestNote = r.LatestNote, pendingQuestion = r.PendingQuestion, result = r.Result,
    startedAt = r.StartedAt, endedAt = r.EndedAt, steps = r.Steps.Count,
};

// All tracked conversations, newest activity first.
app.MapGet("/api/control/conversations", () => Results.Json(controlHub.Conversations().Select(ConvSummary), json));

// One conversation: its reconstructed transcript, files, and the runs that happened on it.
app.MapGet("/api/control/conversations/{id}", (string id) =>
{
    var c = controlHub.Conversation(id);
    if (c is null) return Results.NotFound(new { error = "no such conversation" });
    return Results.Json(new
    {
        summary = ConvSummary(c),
        files = c.Files,
        transcript = c.Transcript.Select(m => new { role = m.Role, text = m.Text, at = m.At }),
        runs = controlHub.RunsFor(id).Select(r => new
        {
            id = r.Id, taskId = r.TaskId, task = r.Task, persona = r.Persona, status = r.Status,
            latestNote = r.LatestNote, pendingQuestion = r.PendingQuestion, result = r.Result,
            startedAt = r.StartedAt, endedAt = r.EndedAt,
            steps = r.Steps.Select(s => new { kind = s.Kind, text = s.Text, tool = s.Tool, args = s.Args, result = s.Result }),
        }),
    }, json);
});

// The live activity stream: a snapshot first, then every event from every conversation as it happens.
app.MapGet("/api/control/stream", async (HttpContext ctx) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache, no-transform";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    await ctx.Response.WriteAsync(":" + new string(' ', 2048) + "\n\n", ctx.RequestAborted);
    var snapshot = JsonSerializer.Serialize(new
    {
        conversations = controlHub.Conversations().Select(ConvSummary),
        runs = controlHub.Runs().Take(100).Select(RunSummary),
    }, json);
    await ctx.Response.WriteAsync($"event: snapshot\ndata: {snapshot}\n\n", ctx.RequestAborted);
    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

    var sub = controlHub.Subscribe();
    try
    {
        await foreach (var frame in sub.Channel.Reader.ReadAllAsync(ctx.RequestAborted))
        {
            await ctx.Response.WriteAsync($"event: activity\ndata: {frame}\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException) { /* dashboard disconnected */ }
    finally { controlHub.Unsubscribe(sub); }
});

// Cross-process ingest: Smarty.Slack forwards its conversation events here. Token-guarded when a token is set.
app.MapPost("/api/control/ingest", (IngestPayload body, HttpContext ctx) =>
{
    if (!string.IsNullOrEmpty(controlToken) &&
        ctx.Request.Headers["X-Control-Token"].ToString() != controlToken)
        return Results.Unauthorized();
    if (body is null || string.IsNullOrWhiteSpace(body.ConversationId)) return Results.BadRequest();
    var meta = new ConversationMeta(body.Title, body.Subtitle, body.Project, body.Persona, body.UserName);
    controlHub.Ingest(body.ConversationId, string.IsNullOrWhiteSpace(body.Surface) ? "slack" : body.Surface,
        body.Event ?? "", body.Data ?? "", meta);
    return Results.Ok();
});

// Every run across every surface (running + past). Optional ?status= filter.
app.MapGet("/api/control/tasks", (string? status) =>
{
    var runs = controlHub.Runs();
    if (!string.IsNullOrWhiteSpace(status))
        runs = runs.Where(r => string.Equals(r.Status, status, StringComparison.OrdinalIgnoreCase)).ToList();
    return Results.Json(runs.Select(RunSummary), json);
});

// Cancel a running task on a local web conversation (Slack tasks can't be cancelled cross-process yet).
app.MapDelete("/api/control/conversations/{id}/tasks/{taskId}", (string id, string taskId) =>
{
    var session = sessions.Get(id);
    if (session is null) return Results.Conflict(new { error = "can only cancel tasks on local chat conversations" });
    taskId = taskId.TrimStart('#').Trim();
    if (!session.Tasks.TryGetValue(taskId, out var task)) return Results.NotFound(new { error = "no such task" });
    if (!task.IsRunning) return Results.Ok(new { id = task.Id, status = task.Status });
    task.Status = "cancelled";
    task.Cts.Cancel();
    foreach (var child in session.Tasks.Values.Where(c => c.ParentTaskId == task.Id && c.IsRunning))
    {
        child.Status = "cancelled";
        try { child.Cts.Cancel(); } catch { }
    }
    session.Append("working_done", JsonSerializer.Serialize(new { id = task.Id, status = task.Status }, json));
    return Results.Ok(new { id = task.Id, status = task.Status });
});

// ---- memories ----
app.MapGet("/api/control/memories", () => Results.Json(
    memory.AllActive().Select(f => new
    {
        id = f.Id, type = f.Type, key = f.Key, value = f.Value, context = f.Context,
        scope = f.Project, asserted = f.Asserted,
    }), json));

app.MapPost("/api/control/memories", (ControlMemoryWrite body) =>
{
    if (body is null) return Results.BadRequest();
    var msg = memory.Set(body.Type ?? "", body.Key ?? "", body.Value ?? "", body.Context, body.Scope);
    return Results.Ok(new { message = msg });
});

app.MapDelete("/api/control/memories/{id}", (string id) =>
    memory.Retire(id) ? Results.Ok() : Results.NotFound(new { error = "no such active memory" }));

// ---- personas (full management; system prompt never exposed or accepted) ----
app.MapGet("/api/control/personas", () => Results.Json(controlCatalog.Personas(), json));

app.MapPost("/api/control/personas", (PersonaWrite body) =>
{
    if (body is null) return Results.BadRequest();
    var p = personas.Upsert(body.Id, body.Name ?? "", body.Description ?? "", body.CapabilityIds ?? new List<string>());
    return p is null
        ? Results.BadRequest(new { error = "a name is required" })
        : Results.Json(controlCatalog.View(p), json);
});

app.MapDelete("/api/control/personas/{id}", (string id) =>
    personas.Delete(id) ? Results.Ok() : Results.Conflict(new { error = "built-in personas can't be deleted" }));

// ---- capabilities (what tools exist, what they do, and whether they're configured) ----
app.MapGet("/api/control/capabilities", () => Results.Json(controlCatalog.Capabilities(), json));
app.MapGet("/api/control/base-tools", () => Results.Json(controlCatalog.BaseTools(), json));

// ---- buckets (files) ----
app.MapGet("/api/control/buckets", () => Results.Json(controlBuckets.List(), json));

app.MapPost("/api/control/buckets/{kind}/{id}/files", async (string kind, string id, HttpRequest req, CancellationToken ct) =>
{
    if (!req.HasFormContentType) return Results.BadRequest(new { error = "expected multipart form upload" });
    var form = await req.ReadFormAsync(ct);
    if (form.Files.Count == 0) return Results.BadRequest(new { error = "no files" });
    var saved = new List<BucketFile>();
    foreach (var file in form.Files)
    {
        await using var s = file.OpenReadStream();
        var info = await controlBuckets.SaveAsync(kind, id, file.FileName, s, ct);
        if (info is null) return Results.BadRequest(new { error = "invalid bucket" });
        saved.Add(info);
    }
    return Results.Json(new { saved }, json);
});

app.MapGet("/api/control/buckets/{kind}/{id}/files/{*name}", (string kind, string id, string name) =>
{
    var path = controlBuckets.ResolveFile(kind, id, name);
    return path is null ? Results.NotFound() : Results.File(path, fileDownloadName: Path.GetFileName(path));
});

app.MapDelete("/api/control/buckets/{kind}/{id}/files/{*name}", (string kind, string id, string name) =>
    controlBuckets.DeleteFile(kind, id, name) ? Results.Ok() : Results.NotFound());

app.Run();

// ---- helpers ----

// Authoritative date/time + environment + truthfulness context, rebuilt fresh each call so the date
// is always current. Shared by the worker agents and the legacy /api/chat path.
// The STATIC host rules — shell + honesty + language. No clock here on purpose: anything that changes per
// call (the date/time) would sit in the system-prompt PREFIX and break prompt-cache reuse on every request.
// The current time is injected separately as a late message (NowLine), so the cacheable prefix stays stable.
string HostRules()
{
    string shell = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "PowerShell" : "/bin/sh";
    return
        $"\n\nYour shell ({shell}) is a FULL shell WITH internet — fetch live data yourself (e.g. Invoke-RestMethod); " +
        "never say you lack access. If a command fails, diagnose and retry until you get a real answer.\n" +
        "Report ONLY what a tool actually returned this turn — never fabricate news, prices, results or dates, " +
        "or claim a source you didn't use. If the tools can't get it after honest tries, say so plainly " +
        "(\"I couldn't retrieve that\"). Always reply in English.";
}

// The volatile clock anchor — injected as a late conversation message (NOT the system prompt) so it never
// invalidates the cached prefix. Rebuilt per call so it's always current.
static string NowLine()
{
    var now = DateTime.Now;
    return $"It is {now:dddd, d MMMM yyyy, HH:mm} ({TimeZoneInfo.Local.StandardName}), year {now.Year} — the " +
           "real present from the system clock, not the future; never call this date impossible or fictional.";
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
    HostRules();

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

    // The clock rides as a late seed (not the system prompt) so a stable baseSystem keeps a cacheable prefix.
    conversation.Add(Message.System(NowLine()));

    var input = new AgentInput
    {
        SystemPrompt = baseSystem + HostRules(),
        Model = ModelSpec.Ollama(string.IsNullOrWhiteSpace(request.Model) ? defaultModel : request.Model!, ollamaBaseUrl),
        Conversation = conversation,
    };

    if (request.EnableTools ?? true)
    {
        string modelName = string.IsNullOrWhiteSpace(request.Model) ? defaultModel : request.Model!;
        var provider = new OllamaModelProvider(ollamaBaseUrl);
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

// Smarty.Control write DTOs.
internal sealed record ControlMemoryWrite(string? Type, string? Key, string? Value, string? Context, string? Scope);
internal sealed record PersonaWrite(string? Id, string? Name, string? Description, List<string>? CapabilityIds);
