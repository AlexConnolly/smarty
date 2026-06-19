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
var sessions = new SessionStore();
var orchestrator = new Orchestrator(defaultModel, ollamaBaseUrl, WorkerSystemPrompt, json, trainingLog);

app.MapGet("/health", () => Results.Ok(new { status = "ok", model = defaultModel }));

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
    string nowLocal = now.ToString("F", CultureInfo.CurrentCulture);
    string timeZone = TimeZoneInfo.Local.StandardName;
    return
        "\n\n=== CURRENT DATE AND TIME (AUTHORITATIVE) ===\n" +
        $"It is now {nowLocal} ({timeZone}). The current year is {now.Year}. This is the REAL, " +
        $"verified system clock. Your training data ends before {now.Year}, so {now.Year} may FEEL like " +
        "the future to you — it is not. It is the present. Never say or reason that this date is " +
        "impossible, fictional, hypothetical, a typo, or in the future. Treat it as simple fact and " +
        "use it whenever the date or time is relevant.\n" +
        "=== ENVIRONMENT ===\n" +
        $"Host: {RuntimeInformation.OSDescription}. The run_shell_command tool runs " +
        $"{(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows PowerShell" : "/bin/sh")}; use " +
        "commands valid for it. It is a FULL shell WITH internet access — you can fetch live data " +
        "(news, prices, weather, web pages, APIs) with a web request (e.g. Invoke-RestMethod on Windows). " +
        "Never tell the user you lack internet access or live data; go and fetch it with the tool. If a " +
        "command fails, do not give up or merely explain the error — diagnose it, adjust, and try again " +
        "until you have a real answer.\n" +
        "=== TRUTHFULNESS (CRITICAL) ===\n" +
        "NEVER invent, fabricate, guess, or rely on memory for factual data such as news headlines, prices, " +
        "search results, dates, or web content. Report ONLY information that actually appeared in a tool " +
        "result this turn. Do not claim you fetched, parsed, or used a source unless you genuinely did. If " +
        "your tools fail or return nothing usable after honest attempts, say so plainly — e.g. 'I couldn't " +
        "retrieve that' — and do not present made-up data as real. Always respond in English.";
}

// System prompt for a worker (the "hands"): a capable, relentless task-doer with real tools.
string WorkerSystemPrompt() =>
    "You are a capable, relentless assistant. Complete the task you are given by using your tools. For " +
    "anything that needs live or web information (news, prices, facts, current events, a specific site), " +
    "use web_search to find relevant pages, then get_page_answer with a real result URL and a precise " +
    "question to read that page and pull out the answer. Use the shell (run_shell_command) for system " +
    "info, files, local commands, or APIs the web tools can't handle. Base every factual claim ONLY on " +
    "what a tool actually returned this turn — if the tools don't give you the answer, say so rather than " +
    "filling it in from memory. Be efficient: one or two good sources is usually enough — STOP and answer " +
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
