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
    private readonly ProjectRunStore _runs;

    // Bound the per-turn tool loop: a turn may call a data tool (list/status), read the result, then
    // voice it — but it must not spin.
    private const int MaxTurnIterations = 4;

    // Set SMARTY_TRACE=1 to log worker tool calls/results and the final result to stderr (debugging).
    private static readonly bool TraceOn = Environment.GetEnvironmentVariable("SMARTY_TRACE") == "1";
    private static void Trace(string msg) { if (TraceOn) Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {msg}"); }
    private static string Snip(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : "…" + s[^max..]).Replace("\n", " ⏎ ");

    public Orchestrator(string model, string ollamaBaseUrl, Func<string> workerSystem, JsonSerializerOptions json, TrainingLog log, MemoryStore memory, ProjectStore projects, ProjectRunStore runs)
    {
        _provider = new OllamaModelProvider(ollamaBaseUrl);
        _model = model;
        _ollamaBaseUrl = ollamaBaseUrl;
        _workerSystem = workerSystem;
        _json = json;
        _log = log;
        _memory = memory;
        _projects = projects;
        _runs = runs;
        // The orchestrator's tools = task tools + global memory-access + project create/list (all executed
        // in HandleToolCall by name).
        _orchestratorTools = OrchestratorTools
            .Append(MemoryTools.SearchTool(memory))
            .Append(MemoryTools.SetChatTool(memory))
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
        "A tool call is the ONLY way to actually do something. Never claim you've done something — remembered, " +
        "noted, saved, started, changed, checked, booked, cancelled — unless you actually called its tool in " +
        "THIS reply. If your reasoning concludes you should call a tool, EMIT the call; do not just describe " +
        "it and then reply as though it happened (e.g. don't say \"I've noted that\" without calling set_memory).\n" +
        "- delegate(task): start a new background task — anything needing an action or live/real data. Also " +
        "say a SHORT, plain acknowledgement — \"Sure, I'll check that\" / \"On it\" / \"Leave it with me\". " +
        "Don't restate the task, list what you'll do, or pad it; the result comes back later.\n" +
        "- message_task(id, msg): user refines or adds to something ALREADY running — steer that task; do " +
        "NOT also delegate (that duplicates the work).\n" +
        "- cancel_task(id): user backs off (\"never mind\", \"stop\", \"forget it\").\n" +
        "- list_tasks() / task_status(id): what's running / how it's going (use the ids you're shown).\n" +
        "- search_memory(query): recall what you know about the USER — keywords, not sentences. Only when " +
        "their request actually needs personal context (where they live, preferences, people). Don't fish.\n" +
        "- set_memory(type, key, value, context): remember a durable fact about the USER (where they live, " +
        "diet, people); a new value for an existing key updates it. Not one-off trivia. A PROJECT detail — a " +
        "booking, decision or date for a project — is NOT saved here: delegate it into the project so the " +
        "project's worker records it (the authority on that project, with its context loaded).\n" +
        "- find_project(statement): when a message refers to ongoing work without naming it (\"the flights\", " +
        "\"book the table\"), resolve WHICH project it's about before acting. If it finds none, ask the user; " +
        "never assume.\n" +
        "- project_summary(project): when the user asks how a project is GOING / where it stands / what's left, " +
        "pull its summary and relay that — don't answer from memory. Resolve with find_project first if unsure " +
        "which project they mean.\n" +
        "- create_project(title, description) / list_projects(): a project is for anything with several " +
        "moving parts you'll come back to — planning or organising an EVENT or party, a trip or holiday, a " +
        "house move, any multi-step undertaking. The moment a request is more than a single question or a " +
        "couple of quick back-and-forths — especially \"help me plan/organise/sort out X\" — treat it as a " +
        "project, NOT a one-off task: propose one, and once the user's happy create it (resolve with " +
        "find_project first; never auto-create or duplicate). Creating a project just SETS IT UP — do NOT " +
        "kick off work or delegate open-ended planning on your own; only delegate when the user actually asks " +
        "for something specific to be done (tag that task with the slug). Only genuine one-offs need no project.\n" +
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
        var focus = await ProjectFocusNote(session, message, ct).ConfigureAwait(false);
        var dynamicContext = (DateContext() + profile + focus + RunningTasksNote(session)).TrimStart();
        if (dynamicContext.Length > 0)
            convo.Insert(Math.Max(0, convo.Count - 1), Message.System(dynamicContext));

        int msgId = session.NextMessageId();
        session.Append("msg_start", Json(new { id = msgId, role = "assistant" }));

        var spoken = new StringBuilder();
        bool delegatedSomething = false;
        bool anyToolCalled = false; // any tool called across this whole user-turn (incl. earlier iterations)
        bool nudgedNoTool = false;  // we re-prompt at most once if the model named a tool but didn't call it

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
            {
                // Two failure shapes to rescue once (both deterministic, tool-agnostic):
                // (a) the model emitted NO message to the user and called nothing — it dumped its reply into
                //     reasoning and "stopped after thinking"; a turn with no words and no action is always broken.
                // (b) it NAMED a tool (in reasoning/reply) but didn't call it — meant to act, forgot. (Skipped
                //     when a tool was already called this turn, so a post-tool confirmation isn't nudged.)
                string? nudge = null;
                if (!nudgedNoTool)
                {
                    if (turnText.Length == 0)
                        nudge = "Your reply had only internal reasoning — no message to the user and no tool " +
                                "call, so nothing actually happened. If you meant to do something, call the " +
                                "tool now; otherwise write your actual reply to the user.";
                    else if (!anyToolCalled &&
                             ReferencesUncalledTool(turnText.ToString(), final.Reasoning, tools) is { } named)
                        nudge = $"You referred to the {named} tool but didn't actually call it, so nothing " +
                                $"happened. A tool call is the only way to do it — if you meant to, call {named} " +
                                "now. If you were only chatting and no action was needed, just reply normally.";
                }
                if (nudge is not null)
                {
                    nudgedNoTool = true;
                    convo.Add(Message.System(nudge));
                    continue;
                }
                break;
            }

            anyToolCalled = true;
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
            const string ack = "Sure, I'll check that.";
            spoken.Append(ack);
            session.Append("content", Json(new { id = msgId, text = ack }));
        }
        // Last-resort: the turn produced no message and no delegated work (e.g. the model only reasoned and
        // the nudge didn't recover it). Never leave the user staring at an empty reply.
        else if (spoken.Length == 0)
        {
            const string fallback = "Sorry — I trailed off there. Could you say that again?";
            spoken.Append(fallback);
            session.Append("content", Json(new { id = msgId, text = fallback }));
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
            {
                var (text, slug) = await ResolveProjectAsync(call.Arguments.GetStringOrNull("statement") ?? "", ct).ConfigureAwait(false);
                if (slug is not null) session.CurrentProject = slug; // focus the conversation on the resolved project
                return Data(text);
            }

            case "project_summary":
            {
                var slug = call.Arguments.GetStringOrNull("project")?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(slug)) slug = session.CurrentProject;
                if (string.IsNullOrEmpty(slug))
                    return Data("Which project? Resolve it with find_project first (or have the user name it), then ask again.");
                var p = _projects.Get(slug);
                if (p is null)
                    return Data($"There's no project \"{slug}\". Use find_project to work out which one, or list_projects.");

                session.CurrentProject = slug; // stay focused on what we're reporting on
                var facts = _memory.Active(slug);
                var sb = new StringBuilder($"Project \"{p.Title}\"");
                if (!string.IsNullOrWhiteSpace(p.Description)) sb.Append($" — {p.Description}");
                sb.Append(".\n\n");

                // The live facts ARE the state — always current. Report from these.
                if (facts.Count > 0)
                    foreach (var f in facts.OrderBy(f => f.Type, StringComparer.Ordinal))
                        sb.Append($"- {f.Key}: {f.Value}{(string.IsNullOrWhiteSpace(f.Context) ? "" : $" ({f.Context})")}\n");
                else
                    sb.Append("Nothing recorded yet — it's only just been set up.");

                var runs = _runs.CountFor(slug);
                if (runs > 0) sb.Append($"\n\n({runs} background {(runs == 1 ? "task has" : "tasks have")} run for it.)");
                return Data(sb.ToString().TrimEnd());
            }

            case "create_project":
            {
                var (msg, newSlug) = _projects.Create(
                    call.Arguments.GetStringOrNull("title") ?? "",
                    call.Arguments.GetStringOrNull("description") ?? "");
                if (newSlug is not null)
                {
                    session.CurrentProject = newSlug; // focus it, so details stated next land on the project
                    _ = RegenerateSummaryAsync(newSlug);
                }
                return Data(msg);
            }

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
            {
                // Project facts are the authority's job: written by a worker running INSIDE the project, with
                // the project's context loaded, so the write is reconciled — never recorded here. Scope =
                // explicit project arg, else the project in focus. If that's a real project, DON'T write: tell
                // the model to delegate the recording into it. set_memory here only saves facts about the user.
                var proj = call.Arguments.GetStringOrNull("project")?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(proj)) proj = session.CurrentProject;
                if (!string.IsNullOrEmpty(proj) && _projects.Exists(proj))
                    return Data($"That's a detail for project \"{proj}\" — record it via the project's worker, " +
                                $"not here. delegate with project: \"{proj}\" and a task that simply tells the " +
                                "worker to SAVE these exact details to memory (e.g. \"Record to memory: …the " +
                                "facts…\"). Make clear it's just saving given facts — not researching, booking, " +
                                "or confirming anything. set_memory here is only for facts about the USER.");
                return Data(_memory.Set(
                    call.Arguments.GetStringOrNull("type") ?? "",
                    call.Arguments.GetStringOrNull("key") ?? "",
                    call.Arguments.GetStringOrNull("value") ?? "",
                    call.Arguments.GetStringOrNull("context")));
            }

            default:
                // Bad/hallucinated tool name — tell the model the real ones so it can correct itself, rather
                // than guess again and burn iterations.
                return Data($"There's no tool called '{call.Name}'. Available tools: " +
                            $"{string.Join(", ", _orchestratorTools.Select(t => t.Name))}. Use one of those, " +
                            "or just reply if no tool is needed.");
        }
    }

    // Resolve a free-text statement ("what time are the flights next week") to the project it belongs to,
    // by searching the projects' titles, descriptions and the facts recorded inside them. The hard rule:
    // if it doesn't resolve confidently, we say so and tell the model to ASK — never to invent a project.
    // Returns the message for the model AND the resolved slug (non-null only on a confident match, so the
    // caller can focus the conversation on it). Ambiguous / no-match returns a null slug → the model asks.
    private async Task<(string Text, string? Slug)> ResolveProjectAsync(string statement, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(statement))
            return ("Give me the user's statement and I'll work out which project it's about.", null);

        var active = _projects.ActiveProjects();
        if (active.Count == 0)
            return ("There are no projects yet. If this is the start of a genuinely ongoing thing, ASK the user " +
                    "before creating a project — don't assume.", null);

        var ranked = await _memory.RankProjects(statement, active, ct).ConfigureAwait(false);
        var top = ranked.Count > 0 ? ranked[0] : default;

        // A clear winner: strong score and no near-rival.
        if (ranked.Count > 0 && top.Score >= ResolveStrong &&
            (ranked.Count < 2 || ranked[1].Score < ResolveStrong))
            return ($"That's the project \"{top.Title}\" (slug: {top.Slug}) — matched on \"{top.Matched}\". " +
                    "Use that slug.", top.Slug);

        // Nothing close enough — don't guess, don't create.
        if (ranked.Count == 0 || top.Score < ResolveWeak)
            return ("No existing project matches that. Do NOT create one yourself — ask the user whether this " +
                    "belongs to something they're already planning, or whether to start a new project for it.", null);

        // In the ambiguous band (or two strong rivals): surface candidates and let the user decide.
        var cands = ranked.Where(r => r.Score >= ResolveWeak).Take(3);
        return ("Couldn't pin that to one project. It might be:\n" +
                string.Join("\n", cands.Select(c => $"- {c.Slug}: {c.Title} (matched \"{c.Matched}\")")) +
                "\nAsk the user which one it is — or whether it's something new.", null);
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
        IReadOnlyList<Message>? transcript = null; // captured for the project run log, even if cancelled
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
            {
                transcript = run.Messages;
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

        // Record what the sub-agent did, scoped to its project (work with no project isn't logged here —
        // it isn't shown anywhere). Read back by the project overview.
        if (!string.IsNullOrEmpty(task.Project) && transcript is not null)
        {
            var run = new ProjectRun
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Project = task.Project!,
                Task = task.Description,
                Status = task.Status,
                StartedAt = task.StartedAt,
                EndedAt = DateTimeOffset.UtcNow,
                Steps = BuildSteps(transcript),
                Result = result,
            };
            _runs.Add(run);
            // Refresh the project's summary + give the run a short label, both in the background.
            _ = RegenerateSummaryAsync(task.Project!);
            _ = GenerateRunTitleAsync(run.Id, task.Description, result);
        }

        // A cancellation was already acknowledged to the user when they asked to stop — don't re-voice.
        if (cancelled) return;

        // Did the task come back with nothing usable? Then we must NOT relay it as if it had data — the
        // re-voice would otherwise invent a plausible answer (it once fabricated a whole "latest news"
        // rundown from a blank result). We still let the MODEL phrase the reply (so it matches the user's
        // language — no hard-coded line), but on a strict "it failed, invent nothing" instruction.
        bool failed = string.IsNullOrWhiteSpace(result)
            || string.Equals(result.Trim(), "(no result)", StringComparison.OrdinalIgnoreCase);

        await session.TurnLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var instruction = failed
                ? $"A background task you tried could not be completed (it returned no result).\nTask: {task.Description}\n\n" +
                  "Tell the user — in the SAME language they have been speaking — that you weren't able to get " +
                  "this just now, and offer to try again. Do NOT invent, pad, guess, or fill in any answer from " +
                  "your own knowledge. Don't mention tasks, workers, delegation, or any internal mechanics."
                : $"A background task you delegated has finished.\nTask: {task.Description}\nResult:\n{result}\n\n" +
                  "Relay this to the user now in a natural, friendly, concise way — as if you just finished " +
                  "checking for them. Do not mention tasks, workers, delegation, or any internal mechanics; " +
                  "just give them the answer conversationally.\n" +
                  "CRITICAL: this result came from live tools with real, current data — you do NOT. Relay its " +
                  "findings faithfully. Do NOT contradict, second-guess, water down, or 'correct' them using " +
                  "your own memory or assumptions about what year it is. If the result says something is " +
                  "happening now, it is happening now. But if it says it couldn't find/get something, tell the " +
                  "user that plainly — never invent or pad it.";

            var convo = new List<Message>(session.History) { Message.System(instruction) };

            Trace($"[worker #{task.Id}] re-voice start{(failed ? " (failed task — honest, no invent)" : "")}");
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

    private static string Head(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max].TrimEnd() + "…");

    // Refresh a project's SHORT narrative summary — one or two sentences on where it stands, like telling a
    // friend. Stored on the project (not a separate doc), regenerated in the background whenever it's touched.
    // The rich facts carry the detail; this carries the story. Best-effort.
    private async Task RegenerateSummaryAsync(string slug)
    {
        try
        {
            var p = _projects.Get(slug);
            if (p is null) return;
            var facts = _memory.Active(slug);

            var state = new StringBuilder($"Project: {p.Title}");
            if (!string.IsNullOrWhiteSpace(p.Description)) state.Append($" ({p.Description})");
            state.Append('\n');
            if (facts.Count == 0) state.Append("Nothing recorded yet.\n");
            else
                foreach (var f in facts.OrderBy(f => f.Type, StringComparer.Ordinal))
                    state.Append($"- {f.Key}: {f.Value}\n");

            var request = new ModelRequest
            {
                Model = _model,
                SystemPrompt =
                    "In ONE or two sentences, say where this project stands, in a natural tone. Use ONLY the " +
                    "facts given — do NOT invent numbers, names, tasks, or anything not listed (don't guess " +
                    "what's 'left to do'). Plain prose: no bullets, headings, lists, emoji or fluff. Example " +
                    "tone: \"Booked at Bella's for the 14th, 10 covers — venue and menu are sorted.\" If barely " +
                    "started, one short line. Output only the sentence(s).",
                Messages = new List<Message> { Message.User(state.ToString().TrimEnd()) },
                Tools = Array.Empty<AgentTool>(),
                MaxOutputTokens = 120,
                TurnTimeout = TimeSpan.FromSeconds(30),
                Think = false,
            };
            var sb = new StringBuilder();
            await foreach (var ev in _provider.StreamAsync(request, CancellationToken.None).ConfigureAwait(false))
                if (ev is ModelStreamEvent.Content c) sb.Append(c.Text);
            var text = sb.ToString().Trim();
            if (text.Length > 0) { _projects.SetSummary(slug, text); Trace($"[summary] {slug} ({text.Length} chars)"); }
        }
        catch (Exception ex) { Trace($"[summary] {slug} failed: {ex.Message}"); }
    }

    // A short human label for a run ("Booked the venue", "Found 3 pubs"), generated in the background so the
    // project overview can list adjustments as tidy titles instead of the verbose delegated-task text.
    private async Task GenerateRunTitleAsync(string runId, string task, string? result)
    {
        try
        {
            var request = new ModelRequest
            {
                Model = _model,
                SystemPrompt = "Reply with a 3-to-6 word title summarising what was done, in past tense " +
                               "(e.g. \"Booked the venue\", \"Found 3 pubs\"). No quotes, no punctuation, no " +
                               "preamble — just the title.",
                Messages = new List<Message>
                {
                    Message.User($"Task: {task}\nResult: {Head(result ?? "", 400)}\n\nTitle:"),
                },
                Tools = Array.Empty<AgentTool>(),
                MaxOutputTokens = 24,
                TurnTimeout = TimeSpan.FromSeconds(30),
                Think = false,
            };
            var sb = new StringBuilder();
            await foreach (var ev in _provider.StreamAsync(request, CancellationToken.None).ConfigureAwait(false))
                if (ev is ModelStreamEvent.Content c) sb.Append(c.Text);
            var title = sb.ToString().Trim().Trim('"', '.', '\'').Trim();
            if (title.Length is > 0 and <= 80) _runs.SetTitle(runId, title);
        }
        catch (Exception ex) { Trace($"[runtitle] {runId} failed: {ex.Message}"); }
    }

    // If the model's reasoning/reply names one of its tools by name but it called nothing, return that tool
    // name (so we can nudge it to actually call it). Matches the literal tool identifiers (e.g. set_memory,
    // find_project) on word boundaries — distinctive enough that plain chat won't trip it.
    private static string? ReferencesUncalledTool(string content, string? reasoning, IReadOnlyList<AgentTool> tools)
    {
        var hay = (content + " " + reasoning).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(hay)) return null;
        foreach (var t in tools)
        {
            var name = t.Name.ToLowerInvariant();
            if (System.Text.RegularExpressions.Regex.IsMatch(hay, $@"\b{System.Text.RegularExpressions.Regex.Escape(name)}\b"))
                return t.Name;
        }
        return null;
    }

    // Flatten a worker's message transcript into display steps: thinking blocks, tool calls (with their
    // arguments and matched results), and the final answer — the read-only "what it did" timeline.
    private static List<RunStep> BuildSteps(IReadOnlyList<Message> messages)
    {
        var steps = new List<RunStep>();
        var toolById = new Dictionary<string, RunStep>(StringComparer.Ordinal);
        foreach (var m in messages)
        {
            if (m.Role == Role.Assistant)
            {
                if (!string.IsNullOrWhiteSpace(m.Reasoning))
                    steps.Add(new RunStep { Kind = "thinking", Text = m.Reasoning!.Trim() });
                if (m.ToolCalls is { Count: > 0 })
                    foreach (var c in m.ToolCalls)
                    {
                        var s = new RunStep { Kind = "tool", Tool = c.Name, Args = c.Arguments.ToString() };
                        steps.Add(s);
                        if (!string.IsNullOrEmpty(c.Id)) toolById[c.Id] = s;
                    }
                if (!string.IsNullOrWhiteSpace(m.Content))
                    steps.Add(new RunStep { Kind = "answer", Text = m.Content!.Trim() });
            }
            else if (m.Role == Role.Tool)
            {
                if (m.ToolCallId is { } id && toolById.TryGetValue(id, out var s))
                    s.Result = m.Content;
                else
                    steps.Add(new RunStep { Kind = "tool", Tool = m.ToolName, Result = m.Content });
            }
        }
        return steps;
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

    // The READ side of project focus: once find_project has resolved a reference, surface that project's
    // relevant facts in chat so the orchestrator can answer about it without delegating — and remind it that
    // recording a new detail goes THROUGH the project (a task), not set_memory up here. Writes are routed;
    // reads are surfaced.
    private async Task<string> ProjectFocusNote(Session session, string message, CancellationToken ct)
    {
        var slug = session.CurrentProject;
        if (string.IsNullOrEmpty(slug)) return "";
        var p = _projects.Get(slug);
        if (p is null) { session.CurrentProject = null; return ""; } // project gone — drop the stale focus

        var sb = new StringBuilder($"\n\nYou're currently focused on the project \"{p.Title}\" (slug: {slug}). ");
        sb.Append("To record a new detail about it (a booking, decision, date), delegate that into this " +
                  "project — its worker is the authority and records it with the project's context; don't " +
                  "set_memory it here. If the user clearly moves on, drop the focus.\n");
        var facts = await _memory.RelevantTo(message, k: 5, project: slug, ct: ct).ConfigureAwait(false);
        if (facts.Count > 0)
        {
            sb.Append("Relevant project details so far:\n");
            foreach (var f in facts)
            {
                sb.Append($"- {f.Key}: {f.Value}");
                if (!string.IsNullOrWhiteSpace(f.Context)) sb.Append($" ({f.Context})");
                sb.Append('\n');
            }
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
        new("project_summary",
            "Get a project's up-to-date summary — what's been decided/booked and what work has run — so you " +
            "can tell the user how it's going, what's left, or where things stand. Pass the project slug (or " +
            "leave blank if one's already in focus). If unsure which project, resolve it with find_project first.",
            new[] { ToolParameter.String("project", "Project slug (optional if one is already in focus).", required: false) },
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
