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

string ollamaBaseUrl = builder.Configuration["Ollama:BaseUrl"] ?? OllamaModelProvider.DefaultBaseUrl;
string defaultModel = builder.Configuration["Ollama:Model"] ?? "qwen3:4b";

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
        using var resp = await http.PostAsync($"{ollamaBaseUrl}/api/embeddings", content, ct);
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

// Each project's living README — a plain-language summary regenerated whenever the project is touched.
string readmesDir = builder.Configuration["Projects:ReadmesDir"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "readmes");
var projectReadmes = new ProjectReadmeStore(readmesDir);

var sessions = new SessionStore();
var orchestrator = new Orchestrator(defaultModel, ollamaBaseUrl, WorkerSystemPrompt, json, trainingLog, memory, projects, projectRuns, projectReadmes);

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
        readme = projectReadmes.Get(p.Slug),
        memories = memory.Active(p.Slug)
            .OrderByDescending(f => f.Asserted)
            .Select(f => new { type = f.Type, key = f.Key, value = f.Value, context = f.Context, asserted = f.Asserted }),
        runs = projectRuns.ForProject(p.Slug)
            .Select(r => new { id = r.Id, task = r.Task, status = r.Status, startedAt = r.StartedAt, endedAt = r.EndedAt, steps = r.Steps, result = r.Result }),
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
    "You are a capable, relentless assistant. Complete the task you are given by using your tools. For " +
    "anything that needs live or web information (news, prices, facts, current events, a specific site), " +
    "use web_search to find relevant pages, then get_page_answer with a real result URL and a precise " +
    "question to read that page and pull out the answer. Use the shell (run_shell_command) for system " +
    "info, files, local commands, or APIs the web tools can't handle. Base every factual claim ONLY on " +
    "what a tool actually returned this turn — if the tools don't give you the answer, say so rather than " +
    "filling it in from memory. You also have search_memory (recall what's known about the user — search " +
    "keywords) and set_memory (store a durable fact). If the task is to RECORD, note, log or save details " +
    "you've been GIVEN, just store them with set_memory and confirm — do NOT web-search, verify or 'confirm' " +
    "facts the user already stated; there's nothing to look up. Use them when personal/project context helps. " +
    "Be efficient: one or two good sources is usually enough — STOP and answer " +
    "as soon as a tool has given you what the task needs. Only keep going if a source failed or the answer " +
    "is genuinely incomplete; don't pile on extra sources for thoroughness' sake. Give a clear, complete " +
    "answer to the task." +
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

    var input = new AgentInput
    {
        SystemPrompt = baseSystem + HostContext(),
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

internal sealed record FeedbackMessage(int MessageId, string Rating, string? Note);
