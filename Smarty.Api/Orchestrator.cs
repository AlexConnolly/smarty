using System.Text;
using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// The conversational layer — and the router/mediator at the centre of the system. The orchestrator
/// is the single voice the user talks to; it can't run anything itself. Real work is done by capable
/// background WORKERS (full <see cref="SmartyAgent"/>s with real tools) that it hands tasks to and then
/// relays the results back from, conversationally. It is also a task manager: it can list, peek at,
/// steer (message), and cancel the tasks it has running. Orchestrator + workers share one model.
/// </summary>
public sealed class Orchestrator
{
    private readonly OllamaModelProvider _provider;
    private readonly string _model;
    private readonly string _ollamaBaseUrl;
    private readonly Func<string> _workerSystem;
    private readonly JsonSerializerOptions _json;
    private readonly TrainingLog _log;
    private readonly MemoryStore _memory;
    private readonly ProjectStore _projects;

    // Bound the per-turn tool loop: a turn may call a data tool (list/status), read the result, then
    // voice it — but it must not spin.
    private const int MaxTurnIterations = 4;

    // Set SMARTY_TRACE=1 to log worker tool calls/results and the final result to stderr (debugging).
    private static readonly bool TraceOn = Environment.GetEnvironmentVariable("SMARTY_TRACE") == "1";
    private static void Trace(string msg) { if (TraceOn) Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}"); }
    private static string Snip(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : "…" + s[^max..]).Replace("\n", " ⏎ ");

    public Orchestrator(string model, string ollamaBaseUrl, Func<string> workerSystem, JsonSerializerOptions json, TrainingLog log, MemoryStore memory, ProjectStore projects)
    {
        _provider = new OllamaModelProvider(ollamaBaseUrl);
        _model = model;
        _ollamaBaseUrl = ollamaBaseUrl;
        _workerSystem = workerSystem;
        _json = json;
        _log = log;
        _memory = memory;
        _projects = projects;
        // The orchestrator's tools = task tools + global memory-access + project create/list (all executed
        // in HandleToolCall by name).
        _orchestratorTools = OrchestratorTools
            .Append(MemoryTools.SearchTool(memory))
            .Append(MemoryTools.SetTool(memory))
            .Append(ProjectTools.CreateTool(projects))
            .Append(ProjectTools.ListTool(projects))
            .ToArray();
    }

    private readonly AgentTool[] _orchestratorTools;

    // Project agent messages/tools into clean, training-shaped JSON (system + messages + tools → output).
    private static object ProjectMessages(IEnumerable<Message> messages) =>
        messages.Select(m => new
        {
            role = m.Role.ToString().ToLowerInvariant(),
            content = m.Content,
            reasoning = m.Reasoning,
            tool_calls = m.ToolCalls?.Select(c => new { id = c.Id, name = c.Name, arguments = c.Arguments.ToString() }),
            tool_name = m.ToolName,
            tool_call_id = m.ToolCallId,
        }).ToList();

    private static object ToolSchemas(IReadOnlyList<AgentTool> tools) =>
        tools.Select(t => new
        {
            name = t.Name,
            description = t.Description,
            parameters = t.Parameters.Select(p => new { name = p.Name, type = p.Type, description = p.Description, required = p.Required }),
        }).ToList();

    private const string OrchestratorSystem =
        "You are Smarty, the user's personal assistant — one warm, concise voice they talk to. You don't do " +
        "work yourself: you hand it to background workers and relay what they find. To the user it's all you.\n" +
        "\n" +
        "A tool call is the ONLY way to actually do something — never say you've started, changed, checked, " +
        "or cancelled anything unless you called its tool in THIS reply.\n" +
        "- delegate(task): start a new background task — anything needing an action or live/real data. Also " +
        "say one short line that you're on it.\n" +
        "- message_task(id, msg): user refines or adds to something ALREADY running — steer that task; do " +
        "NOT also delegate (that duplicates the work).\n" +
        "- cancel_task(id): user backs off (\"never mind\", \"stop\", \"forget it\").\n" +
        "- list_tasks() / task_status(id): what's running / how it's going (use the ids you're shown).\n" +
        "- search_memory(query): recall what you know about the USER — keywords, not sentences. Only when " +
        "their request actually needs personal context (where they live, preferences, people). Don't fish.\n" +
        "- set_memory(type, key, value, context): remember a durable fact they share; a new value for an " +
        "existing key updates it. Not passing one-off details.\n" +
        "- find_project(statement): when a message refers to ongoing work without naming it (\"the flights\", " +
        "\"book the table\"), resolve WHICH project it's about before acting. If it finds none, ask the user; " +
        "never assume.\n" +
        "- create_project(title, description) / list_projects(): for genuinely long-running endeavours (a " +
        "holiday being planned, a house move). Before creating, resolve with find_project; only create once " +
        "the user confirms — never auto-create or duplicate. To work on a project, delegate with its slug " +
        "(that gives the worker the project's context). Most tasks need no project.\n" +
        "\n" +
        "Just answer (no tools) for chat, jokes, opinions, or things you already know. If a message has both " +
        "an easy part and a work part, answer the easy part AND delegate the work part in the same reply. " +
        "Reply in English, brief and human.";

    /// <summary>Handle a user message: echo it, run the orchestrator turn, dispatch any delegated work.</summary>
    public async Task HandleMessageAsync(Session session, string userText, CancellationToken ct)
    {
        await session.TurnLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Trace("[turn] user message received");
            session.LastUserMessageAt = DateTimeOffset.UtcNow;
            EmitMessage(session, "user", userText);
            session.History.Add(Message.User(userText));

            // The user-facing turn is where the real decisions live (which tools, answer-and-delegate,
            // refine-vs-new, cancel). Let the model think here so it gets them right; re-voice
            // turns stay think:false for speed.
            string content = await RunOrchestratorTurnAsync(
                session, session.History, _orchestratorTools, think: true, ct).ConfigureAwait(false);
            Trace("[turn] orchestrator user-turn done (ack streamed)");

            session.History.Add(Message.Assistant(string.IsNullOrWhiteSpace(content) ? "(on it)" : content));
        }
        finally
        {
            session.TurnLock.Release();
        }
    }

    /// <summary>
    /// Run one orchestrator "turn" as the user sees it (a single assistant message id), looping the model
    /// only when it calls a tool that returns data it then needs to voice (list/status/cancel/message).
    /// Tool calls and results stay local to this turn; only the final spoken text is persisted by callers.
    /// Returns the spoken text.
    /// </summary>
    private async Task<string> RunOrchestratorTurnAsync(
        Session session, IReadOnlyList<Message> baseMessages, IReadOnlyList<AgentTool> tools, bool think, CancellationToken ct)
    {
        var convo = new List<Message>(baseMessages);

        // Keep the big system prompt STATIC so its KV cache is reused every turn (lower time-to-first-token).
        // The per-turn dynamic context — date, running tasks, and the user's profile — goes in a late block
        // just before the latest message: the model still sees it, but the cacheable prefix stays intact.
        // Surfacing the profile here also means personal facts (allergies, where they live) are ALWAYS in
        // front of the model, so it doesn't have to decide to search for them.
        var message = convo.LastOrDefault(m => m.Role == Role.User)?.Content ?? "";
        var profile = await ProfileNote(message, ct).ConfigureAwait(false);
        var dynamicContext = (DateContext() + profile + RunningTasksNote(session)).TrimStart();
        if (dynamicContext.Length > 0)
            convo.Insert(Math.Max(0, convo.Count - 1), Message.System(dynamicContext));

        int msgId = session.NextMessageId();
        session.Append("msg_start", Json(new { id = msgId, role = "assistant" }));

        var spoken = new StringBuilder();
        bool delegatedSomething = false;

        for (int iter = 0; iter < MaxTurnIterations; iter++)
        {
            var request = new ModelRequest
            {
                Model = _model,
                SystemPrompt = OrchestratorSystem, // static → KV cache reused every turn; dynamic context is in `convo`
                Messages = convo,
                Tools = tools,
                RepeatPenalty = 1.0,
                MaxOutputTokens = 4096,
                TurnTimeout = TimeSpan.FromSeconds(90),
                Think = think, // user turns think (right tool choices); re-voice/status don't (speed)
            };

            var sentMessages = convo.ToList(); // exact input for this inference (before we append its output)
            var turnText = new StringBuilder();
            ModelResponse? final = null;
            await foreach (var ev in _provider.StreamAsync(request, ct).ConfigureAwait(false))
            {
                switch (ev)
                {
                    case ModelStreamEvent.Content c:
                        turnText.Append(c.Text);
                        spoken.Append(c.Text);
                        session.Append("content", Json(new { id = msgId, text = c.Text }));
                        break;
                    case ModelStreamEvent.Reasoning r:
                        session.Append("reasoning", Json(new { id = msgId, text = r.Text }));
                        break;
                    case ModelStreamEvent.Completed done:
                        final = done.Response;
                        break;
                }
            }
            final ??= new ModelResponse();

            _log.Interaction(new
            {
                ts = DateTimeOffset.UtcNow,
                session = session.Id,
                msg_id = msgId,
                role = "orchestrator",
                model = _model,
                think,
                system = request.SystemPrompt,
                messages = ProjectMessages(sentMessages),
                tools = ToolSchemas(tools),
                output = new
                {
                    content = turnText.ToString(),
                    reasoning = final.Reasoning,
                    tool_calls = final.ToolCalls.Select(c => new { id = c.Id, name = c.Name, arguments = c.Arguments.ToString() }),
                },
                finish = final.Finish.ToString(),
            });

            var calls = final.ToolCalls;
            convo.Add(Message.Assistant(turnText.ToString(), final.Reasoning, calls.Count > 0 ? calls : null));

            if (calls.Count == 0)
                break;

            bool needAnotherTurn = false;
            foreach (var call in calls)
            {
                var r = await HandleToolCall(session, call, ct).ConfigureAwait(false);
                delegatedSomething |= r.WasDelegate;
                needAnotherTurn |= r.DataReturning;
                convo.Add(Message.ToolResult(call.Id, call.Name, r.Text));
            }

            if (!needAnotherTurn)
                break; // the spoken text (ack / answer) stands; no tool data to relay
        }

        // Guarantee the conversational ack even if the model jumped straight to delegating with no words.
        if (delegatedSomething && spoken.Length == 0)
        {
            const string ack = "Sure — let me look into that for you.";
            spoken.Append(ack);
            session.Append("content", Json(new { id = msgId, text = ack }));
        }

        // Carry the complete text on msg_end so the client can snap to the authoritative final string —
        // streamed deltas are best-effort for the live "typing" feel; if any were dropped in transit, this
        // self-heals the message without a refresh.
        session.Append("msg_end", Json(new { id = msgId, text = spoken.ToString() }));
        return spoken.ToString();
    }

    /// <summary>The outcome of one orchestrator tool call: the text result, whether the model should voice
    /// it next iteration, and whether it kicked off a background task.</summary>
    private readonly record struct ToolResultInfo(string Text, bool DataReturning, bool WasDelegate);

    private static ToolResultInfo Data(string text) => new(text, DataReturning: true, WasDelegate: false);

    /// <summary>Execute one orchestrator tool call against the session's task registry / stores.</summary>
    private async Task<ToolResultInfo> HandleToolCall(Session session, ToolCall call, CancellationToken ct)
    {
        switch (call.Name.ToLowerInvariant())
        {
            case "delegate":
            {
                var desc = call.Arguments.GetStringOrNull("task");
                if (string.IsNullOrWhiteSpace(desc))
                    return Data("No task description was provided — ask the user what they'd like done.");
                // delegate validates a project; it never creates one.
                var project = call.Arguments.GetStringOrNull("project")?.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(project) && !_projects.Exists(project))
                    return Data($"There's no project \"{project}\". Use find_project to resolve which one this is, " +
                                "or create_project if it's genuinely new (confirm with the user first).");
                var task = StartTask(session, desc!.Trim(), string.IsNullOrEmpty(project) ? null : project);
                return new ToolResultInfo(
                    $"Task #{task.Id} started in the background{(task.Project is null ? "" : $" (project: {task.Project})")}; its result will come back to you to relay.",
                    DataReturning: false, WasDelegate: true);
            }

            case "find_project":
                return Data(await ResolveProjectAsync(call.Arguments.GetStringOrNull("statement") ?? "", ct).ConfigureAwait(false));

            case "create_project":
                return Data(_projects.Create(
                    call.Arguments.GetStringOrNull("title") ?? "",
                    call.Arguments.GetStringOrNull("description") ?? ""));

            case "list_projects":
                return Data(_projects.List());

            case "list_tasks":
            {
                var all = OrderedTasks(session);
                if (all.Count == 0) return Data("No tasks have been started yet.");
                return Data(string.Join("\n", all.Select(t => $"#{t.Id} [{t.Status}] {t.Description}")));
            }

            case "task_status":
            {
                if (!TryResolve(session, call, out var t, out var miss)) return Data(miss);
                var age = (DateTimeOffset.UtcNow - t.StartedAt).TotalSeconds;
                var progress = string.IsNullOrWhiteSpace(t.LatestThought) ? "(no detail yet)" : t.LatestThought;
                var line = $"#{t.Id} [{t.Status}] {t.Description}\nRunning {age:F0}s. Latest: {progress}";
                if (t.Status == "done" && !string.IsNullOrWhiteSpace(t.Result))
                    line += $"\nResult: {t.Result}";
                return Data(line);
            }

            case "cancel_task":
            {
                if (!TryResolve(session, call, out var t, out var miss)) return Data(miss);
                if (!t.IsRunning) return Data($"Task #{t.Id} is already {t.Status}.");
                t.Status = "cancelled";
                t.Cts.Cancel();
                return Data($"Task #{t.Id} ({t.Description}) has been cancelled.");
            }

            case "message_task":
            {
                if (!TryResolve(session, call, out var t, out var miss)) return Data(miss);
                if (!t.IsRunning) return Data($"Task #{t.Id} is already {t.Status}; it can't take a message.");
                var msg = call.Arguments.GetStringOrNull("message");
                if (string.IsNullOrWhiteSpace(msg)) return Data("No message text was provided.");
                t.Inbox.Enqueue(msg!.Trim());
                return Data($"Message passed along to task #{t.Id}; it'll pick it up shortly.");
            }

            case "search_memory":
            {
                var q = call.Arguments.GetStringOrNull("query") ?? "";
                var r = _memory.Search(q);
                Trace($"[mem] search({q}) -> {Snip(r, 160)}");
                return Data(r);
            }

            case "set_memory":
                return Data(_memory.Set(
                    call.Arguments.GetStringOrNull("type") ?? "",
                    call.Arguments.GetStringOrNull("key") ?? "",
                    call.Arguments.GetStringOrNull("value") ?? "",
                    call.Arguments.GetStringOrNull("context")));

            default:
                return Data($"Unknown tool '{call.Name}'.");
        }
    }

    // Resolve a free-text statement ("what time are the flights next week") to the project it belongs to,
    // by searching the projects' titles, descriptions and the facts recorded inside them. The hard rule:
    // if it doesn't resolve confidently, we say so and tell the model to ASK — never to invent a project.
    private async Task<string> ResolveProjectAsync(string statement, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(statement))
            return "Give me the user's statement and I'll work out which project it's about.";

        var active = _projects.ActiveProjects();
        if (active.Count == 0)
            return "There are no projects yet. If this is the start of a genuinely ongoing thing, ASK the user " +
                   "before creating a project — don't assume.";

        var ranked = await _memory.RankProjects(statement, active, ct).ConfigureAwait(false);
        var top = ranked.Count > 0 ? ranked[0] : default;

        // A clear winner: strong score and no near-rival.
        if (ranked.Count > 0 && top.Score >= ResolveStrong &&
            (ranked.Count < 2 || ranked[1].Score < ResolveStrong))
            return $"That's the project \"{top.Title}\" (slug: {top.Slug}) — matched on \"{top.Matched}\". " +
                   "Use that slug.";

        // Nothing close enough — don't guess, don't create.
        if (ranked.Count == 0 || top.Score < ResolveWeak)
            return "No existing project matches that. Do NOT create one yourself — ask the user whether this " +
                   "belongs to something they're already planning, or whether to start a new project for it.";

        // In the ambiguous band (or two strong rivals): surface candidates and let the user decide.
        var cands = ranked.Where(r => r.Score >= ResolveWeak).Take(3);
        return "Couldn't pin that to one project. It might be:\n" +
               string.Join("\n", cands.Select(c => $"- {c.Slug}: {c.Title} (matched \"{c.Matched}\")")) +
               "\nAsk the user which one it is — or whether it's something new.";
    }

    // Calibrated against nomic-embed-text, whose cosines have a compressed range: unrelated statements
    // still sit ~0.42–0.51 (topical adjacency), while a genuine reference scores ~0.61+. So the bar for
    // "confident" is 0.55 (true matches clear it, noise doesn't); anything below the weak floor is treated
    // as no-match → ask the user. Biasing uncertainty toward "ask" is deliberate: never assume or invent.
    private const double ResolveStrong = 0.55; // a confident statement→project match
    private const double ResolveWeak = 0.52;    // below this, treat as "no match" (ask, don't guess)

    /// <summary>Register a delegated task, announce it, and spawn its worker + status monitor.</summary>
    private TaskInfo StartTask(Session session, string description, string? project = null)
    {
        var task = new TaskInfo { Id = session.NextTaskId(), Description = description, Project = project };
        session.Tasks[task.Id] = task;
        session.Append("working", Json(new { id = task.Id, task = description }));
        _ = Task.Run(() => RunWorkerAsync(session, task));
        return task;
    }

    /// <summary>Run a delegated task on a background worker, tracking its progress and any mid-flight
    /// steering, then re-voice the result (unless it was cancelled, which is acknowledged elsewhere).</summary>
    private async Task RunWorkerAsync(Session session, TaskInfo task)
    {
        string result = "(no result)";
        bool cancelled = false;
        Trace($"[worker #{task.Id}] start: {Snip(task.Description, 120)}");
        try
        {
            var provider = new OllamaModelProvider(_ollamaBaseUrl);
            // If this task runs inside a project, inject that project's context and reframe memory toward
            // tracking the project (not the user); writes are auto-tagged to the project's slug.
            var input = new AgentInput
            {
                SystemPrompt = _workerSystem() + ProjectContext(task.Project),
                Model = ModelSpec.Ollama(_model, _ollamaBaseUrl),
                Tools =
                {
                    ShellTool.Create(),
                    WebResearch.SearchTool(),
                    WebResearch.PageAnswerTool(provider, _model),
                    MemoryTools.SearchTool(_memory, task.Project),
                    MemoryTools.SetTool(_memory, task.Project),
                },
                DrainInbox = () => DrainInbox(task),
                // Honesty over speed: with reasoning on, the worker inspects tool output, notices junk
                // (e.g. a blank free-space field) and either recovers or says it couldn't — instead of
                // fabricating. Reliability past that is a model problem, fixed by a better model, not by
                // bolting model-specific crutches into this (deliberately model-agnostic) system.
                Think = true,
            };
            var worker = new SmartyAgent(input);

            var thought = new StringBuilder();
            await foreach (var ev in worker.AnswerStream(task.Description, task.Cts.Token).ConfigureAwait(false))
            {
                switch (ev)
                {
                    case AgentEvent.ReasoningDelta r:
                        thought.Append(r.Text);
                        task.LatestThought = Tail(thought, 240);
                        break;
                    case AgentEvent.ContentDelta c:
                        thought.Append(c.Text);
                        task.LatestThought = Tail(thought, 240);
                        break;
                    case AgentEvent.ToolStarted ts:
                        task.LatestThought = $"running {ts.ToolName}…";
                        Trace($"[tool] start {ts.ToolName} {Snip(ts.Arguments, 160)}");
                        break;
                    case AgentEvent.ToolCompleted tc:
                        Trace($"[tool] done  {tc.ToolName} -> {Snip(tc.Result, 300)}");
                        break;
                    case AgentEvent.Completed done:
                        result = done.Answer;
                        break;
                }
            }

            // Capture the full worker transcript (task → tool calls → results → answer) for the dataset.
            if (worker.LastRun is { } run)
                _log.Interaction(new
                {
                    ts = DateTimeOffset.UtcNow,
                    session = session.Id,
                    task_id = task.Id,
                    role = "worker",
                    model = _model,
                    think = true,
                    system = input.SystemPrompt,
                    tools = ToolSchemas(input.Tools),
                    task = task.Description,
                    transcript = ProjectMessages(run.Messages),
                    result,
                });
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            task.Status = "failed";
            result = $"(the task could not be completed: {ex.Message})";
        }

        if (task.Status == "cancelled") cancelled = true;
        task.Result = result;
        if (task.Status == "running") task.Status = "done";

        session.Append("working_done", Json(new { id = task.Id, status = task.Status }));
        Trace($"[worker #{task.Id}] RESULT >>> {Snip(result, 500)}");

        // A cancellation was already acknowledged to the user when they asked to stop — don't re-voice.
        if (cancelled) return;

        await session.TurnLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var convo = new List<Message>(session.History)
            {
                Message.System(
                    $"A background task you delegated has finished.\nTask: {task.Description}\nResult:\n{result}\n\n" +
                    "Relay this to the user now in a natural, friendly, concise way — as if you just finished " +
                    "checking for them. Do not mention tasks, workers, delegation, or any internal mechanics; " +
                    "just give them the answer conversationally.\n" +
                    "CRITICAL: this result came from live tools with real, current data — you do NOT. Relay its " +
                    "findings faithfully. Do NOT contradict, second-guess, water down, or 'correct' them using " +
                    "your own memory or assumptions about what year it is. If the result says something is " +
                    "happening now, it is happening now."),
            };

            Trace($"[worker #{task.Id}] re-voice start");
            string content = await RunOrchestratorTurnAsync(
                session, convo, Array.Empty<AgentTool>(), think: false, CancellationToken.None).ConfigureAwait(false);
            Trace($"[worker #{task.Id}] re-voice done (user now sees the answer)");

            session.History.Add(Message.Assistant(string.IsNullOrWhiteSpace(content) ? result : content));
        }
        finally
        {
            session.TurnLock.Release();
        }
    }

    // Hand the worker any follow-ups the user routed to it while it was running, as steering notes.
    private static IReadOnlyList<Message> DrainInbox(TaskInfo task)
    {
        if (task.Inbox.IsEmpty) return Array.Empty<Message>();
        var msgs = new List<Message>();
        while (task.Inbox.TryDequeue(out var m))
            msgs.Add(Message.System(
                $"While you've been working, the user sent a follow-up about this task: \"{m}\". " +
                "Take it into account and adjust what you're doing accordingly."));
        return msgs;
    }

    // Anchor the orchestrator in real time. Without this it drifts to its training-era "now" and treats
    // the present as the future (e.g. "the 2026 World Cup hasn't happened yet"), and answers current-events
    // questions from stale memory instead of delegating for live data.
    private static string DateContext()
    {
        var now = DateTime.Now;
        return
            $"\n\nToday is {now:dddd, d MMMM yyyy}. {now.Year} is the real present, not the future — never call a " +
            "present or past date impossible, fictional, or not-yet-happened. You can't know current/live facts " +
            "(news, scores, prices, weather, what's happening now) from memory; delegate to fetch anything live.";
    }

    // The facts RELEVANT TO THIS MESSAGE, surfaced automatically so the model always has the personal
    // context it needs (allergies, where they live) without deciding to search — but only what's relevant,
    // not the whole store. The system does the retrieval; embeddings will make "relevant" semantic later.
    private async Task<string> ProfileNote(string message, CancellationToken ct)
    {
        var facts = await _memory.RelevantTo(message, k: 6, ct: ct).ConfigureAwait(false);
        Trace($"[mem] relevant-to({Snip(message, 50)}) -> {(facts.Count == 0 ? "(none)" : string.Join(", ", facts.Select(f => f.Key + "=" + f.Value)))}");
        if (facts.Count == 0) return "";
        var sb = new StringBuilder("\n\nRelevant to this, here's what you know about the user (apply it; never advise against it):\n");
        foreach (var f in facts)
        {
            sb.Append($"- {f.Key}: {f.Value}");
            if (!string.IsNullOrWhiteSpace(f.Context)) sb.Append($" ({f.Context})");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // When a worker runs inside a project, give it the project's framing + accumulated facts, and reframe
    // memory toward tracking the project rather than the user.
    private string ProjectContext(string? slug)
    {
        if (string.IsNullOrEmpty(slug)) return "";
        var p = _projects.Get(slug);
        if (p is null) return "";

        var sb = new StringBuilder($"\n\nYou are working inside the project \"{p.Title}\"");
        if (!string.IsNullOrWhiteSpace(p.Description)) sb.Append($": {p.Description}");
        sb.Append(".\nUse memory here to track details about THIS PROJECT (decisions, dates, findings) — not facts about the user.\n");

        var facts = _memory.Active(slug);
        if (facts.Count > 0)
        {
            sb.Append("Known about this project so far:\n");
            foreach (var f in facts.OrderBy(f => f.Type, StringComparer.Ordinal))
            {
                sb.Append($"- {f.Key}: {f.Value}");
                if (!string.IsNullOrWhiteSpace(f.Context)) sb.Append($" ({f.Context})");
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    // A live snapshot of in-flight tasks, appended to the system prompt so the orchestrator always knows
    // what it has running (and their ids) — even across turns, so "cancel that" / "how's it going" work.
    private static string RunningTasksNote(Session session)
    {
        var running = session.Tasks.Values.Where(t => t.IsRunning)
            .OrderBy(t => int.TryParse(t.Id, out var n) ? n : 0).ToList();
        if (running.Count == 0) return "";
        var sb = new StringBuilder("\n\nTasks you currently have running in the background (refer to them by id):\n");
        foreach (var t in running)
            sb.Append($"- #{t.Id}: {t.Description}\n");
        return sb.ToString();
    }

    private static List<TaskInfo> OrderedTasks(Session session) =>
        session.Tasks.Values.OrderBy(t => int.TryParse(t.Id, out var n) ? n : 0).ToList();

    private static bool TryResolve(Session session, ToolCall call, out TaskInfo task, out string miss)
    {
        var id = call.Arguments.GetStringOrNull("id")?.TrimStart('#').Trim();
        if (!string.IsNullOrEmpty(id) && session.Tasks.TryGetValue(id, out task!))
        {
            miss = "";
            return true;
        }
        task = null!;
        miss = id is null ? "No task id was provided." : $"There's no task #{id}.";
        return false;
    }

    private static string Tail(StringBuilder sb, int max) =>
        sb.Length <= max ? sb.ToString() : sb.ToString(sb.Length - max, max);

    private string Json(object value) => JsonSerializer.Serialize(value, _json);

    // Emit a complete message (start + one content block + end) — used to echo the user's turn.
    private void EmitMessage(Session session, string role, string text)
    {
        int id = session.NextMessageId();
        session.Append("msg_start", Json(new { id, role }));
        session.Append("content", Json(new { id, text }));
        session.Append("msg_end", Json(new { id, text }));
    }

    // The orchestrator's tools. Schemas only — calls are read off the response and handled against the
    // session's task registry in HandleToolCall, so the executor bodies are inert.
    private static readonly AgentTool[] OrchestratorTools =
    {
        new("delegate",
            "Hand a task to a capable background worker that has real tools (shell, internet, files). Provide " +
            "a clear, self-contained description of the work. Optionally tag it to a project (by slug) so the " +
            "worker has that project's context — leave blank for one-off tasks.",
            new[]
            {
                ToolParameter.String("task", "A clear, self-contained description of the work to do.", required: true),
                ToolParameter.String("project", "Optional project slug to run the task within.", required: false),
            },
            NoOp),
        new("find_project",
            "Work out which existing project a statement refers to when it isn't named (\"the flights next " +
            "week\", \"book the table\"). Searches the projects' titles AND the facts recorded inside them, so " +
            "a passing detail resolves to the right one. If nothing matches, it tells you to ask the user — it " +
            "never invents a project. Pass the user's words.",
            new[] { ToolParameter.String("statement", "The user's statement to resolve to a project.", required: true) },
            NoOp),
        new("list_tasks",
            "List the background tasks that have been started and their current status.",
            Array.Empty<ToolParameter>(),
            NoOp),
        new("task_status",
            "Get the status and latest progress of one background task by its id.",
            new[] { ToolParameter.String("id", "The id of the task to check.", required: true) },
            NoOp),
        new("cancel_task",
            "Stop a running background task by its id.",
            new[] { ToolParameter.String("id", "The id of the task to cancel.", required: true) },
            NoOp),
        new("message_task",
            "Send a follow-up message to a background task that is already running, by its id.",
            new[]
            {
                ToolParameter.String("id", "The id of the running task to message.", required: true),
                ToolParameter.String("message", "The follow-up to pass along to the task.", required: true),
            },
            NoOp),
    };

    private static Task<ToolOutput> NoOp(ToolCallArguments _, CancellationToken __) =>
        Task.FromResult(ToolOutput.Ok("ok"));
}
