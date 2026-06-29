using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// Optional overrides that re-target the orchestrator at a different surface without forking it. The web
/// app passes nothing (every field null → current behaviour). Smarty.Slack passes a company-aware prompt,
/// a project-free toolset, and a web-only worker toolset, so the SAME engine drives a Slack thread.
/// </summary>
public sealed class OrchestratorOptions
{
    /// <summary>Replaces the default personal-assistant system prompt (which is built around projects).</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Replaces the orchestrator's own toolset. Slack uses the project-free task tools
    /// (<see cref="Orchestrator.TaskTools"/>), so the model never reaches for find_project/create_project.</summary>
    public IReadOnlyList<AgentTool>? Tools { get; init; }

    /// <summary>Builds the toolset a delegated worker runs with. Slack returns web-research only (no shell,
    /// no memory) — safe to expose to a whole workspace. Null = the default shell + web + memory set.</summary>
    public Func<TaskInfo, IReadOnlyList<AgentTool>>? WorkerTools { get; init; }

    /// <summary>Relay a finished worker's result with the model THINKING (default false). On a thinking model
    /// like qwen3, think:false doesn't stop the chain-of-thought — it just stops it being split out of the
    /// content, so deliberation leaks into the reply. Slack sets this true so the reasoning goes to the
    /// (ignored) reasoning channel and the posted message stays clean. The web app keeps false (faster).</summary>
    public bool RevoiceThink { get; init; }

    /// <summary>Per-inference time limit for an orchestrator turn (default 90s). A chat surface where the
    /// model only decides tools + writes a short reply wants this shorter, so a spiralling turn is cut and
    /// recovered quickly instead of burning the full 90s. Slack sets ~45s.</summary>
    public TimeSpan? TurnTimeout { get; init; }

    /// <summary>An optional planner. When set, a delegated worker's FIRST leg is sized up by a cheap gate, and
    /// a complex task gets a plan — both built in the BACKGROUND (inside the task, never on the chat turn, so
    /// the conversation can't hang) and seeded into the executor before it runs. Null = no planning (straight
    /// to the worker), the current behaviour.</summary>
    public TaskPlanner? Planner { get; init; }

    /// <summary>An optional supervisor that watches running workers and steps in when they thrash (a relentless
    /// failing search): it nudges the worker to wrap up, or aborts a hopeless task. Null = no supervision.</summary>
    public TaskWatchdog? Watchdog { get; init; }

    /// <summary>Where delegated tasks get their own working directory (task.md + a files/ folder for the
    /// user's attachments). Each task gets <c>&lt;WorkspaceRoot&gt;/&lt;session&gt;/&lt;taskId&gt;/</c>. Null =
    /// no workspaces (no task.md, attachments aren't carried) — the original behaviour.</summary>
    public string? WorkspaceRoot { get; init; }

    /// <summary>An optional persisted schedule store. When set, the orchestrator exposes schedule_task /
    /// cancel_schedule, and a <see cref="Scheduler"/> fires due tasks back into their thread at their time
    /// (a proactive nudge). Null = no scheduling (the tools report it's unavailable).</summary>
    public ScheduleStore? Schedules { get; init; }

    /// <summary>Optional specialist personas a delegated task can be routed to (software engineer, PM…). When
    /// set, <c>delegate</c> accepts a <c>persona</c> and the roster is surfaced to the orchestrator. Null =
    /// no personas (the persona arg is ignored), the current behaviour.</summary>
    public PersonaStore? Personas { get; init; }

    /// <summary>The capability registry backing personas — resolves a persona's capability ids into the tools a
    /// worker runs with. Required for personas to actually add tools (else they're prompt-only).</summary>
    public CapabilityRegistry? Capabilities { get; init; }

    /// <summary>Integration credentials/config for capabilities, read from disk and NEVER shown to the model.</summary>
    public IntegrationConfig? IntegrationConfig { get; init; }
}

/// <summary>
/// The conversational layer — and the router/mediator at the centre of the system. The orchestrator
/// is the single voice the user talks to; it can't run anything itself. Real work is done by capable
/// background WORKERS (full <see cref="SmartyAgent"/>s with real tools) that it hands tasks to and then
/// relays the results back from, conversationally. It is also a task manager: it can list, peek at,
/// steer (message), and cancel the tasks it has running. Orchestrator + workers share one model.
/// </summary>
public sealed class Orchestrator
{
    private readonly IModelProvider _provider;
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

    public Orchestrator(string model, string ollamaBaseUrl, Func<string> workerSystem, JsonSerializerOptions json, TrainingLog log, MemoryStore memory, ProjectStore projects, ProjectRunStore runs, OrchestratorOptions? options = null)
    {
        string apiKey = Environment.GetEnvironmentVariable("TOGETHER_API_KEY") 
            ?? Environment.GetEnvironmentVariable("OLLAMA_API_KEY") 
            ?? Environment.GetEnvironmentVariable("SMARTY_API_KEY") ?? "";
        
        string? togetherBaseUrl = (ollamaBaseUrl.Contains("localhost") || ollamaBaseUrl.Contains("127.0.0.1")) ? null : ollamaBaseUrl;
        _provider = model.Contains("/") || (ollamaBaseUrl != null && ollamaBaseUrl.Contains("together"))
            ? new TogetherModelProvider(apiKey, togetherBaseUrl)
            : new OllamaModelProvider(ollamaBaseUrl);
        _model = model;
        _ollamaBaseUrl = ollamaBaseUrl;
        _workerSystem = workerSystem;
        _json = json;
        _log = log;
        _memory = memory;
        _projects = projects;
        _runs = runs;
        _system = options?.SystemPrompt ?? OrchestratorSystem;
        _workerToolsFactory = options?.WorkerTools;
        _revoiceThink = options?.RevoiceThink ?? false;
        _planner = options?.Planner;
        _watchdog = options?.Watchdog;
        _turnTimeout = options?.TurnTimeout ?? TimeSpan.FromSeconds(90);
        _workspaceRoot = options?.WorkspaceRoot;
        _schedules = options?.Schedules;
        _personas = options?.Personas;
        _capabilities = options?.Capabilities;
        _integrationConfig = options?.IntegrationConfig ?? new IntegrationConfig();

        if (options?.Tools is { } overrideTools)
        {
            // A re-targeted surface (Slack) supplies its own toolset for both general and pinned modes —
            // it has no projects, so the distinction collapses to one set.
            _orchestratorTools = overrideTools.ToArray();
            _pinnedTools = _orchestratorTools;
        }
        else
        {
            // The orchestrator's tools = task tools + global memory-access + project create/list (all executed
            // in HandleToolCall by name).
            _orchestratorTools = OrchestratorTools
                .Append(MemoryTools.SearchTool(memory))
                .Append(MemoryTools.SetChatTool(memory))
                .Append(ProjectTools.CreateTool(projects))
                .Append(ProjectTools.ListTool(projects))
                .ToArray();
            // A pinned project chat is already inside one project — no resolving, creating or listing projects.
            _pinnedTools = OrchestratorTools.Where(t => t.Name != "find_project")
                .Append(MemoryTools.SearchTool(memory))
                .Append(MemoryTools.SetChatTool(memory))
                .ToArray();
        }
    }

    private readonly AgentTool[] _orchestratorTools;
    private readonly AgentTool[] _pinnedTools;
    private readonly string _system;
    private readonly Func<TaskInfo, IReadOnlyList<AgentTool>>? _workerToolsFactory;
    private readonly bool _revoiceThink;
    private readonly TaskPlanner? _planner;
    private readonly TaskWatchdog? _watchdog;
    private readonly TimeSpan _turnTimeout;
    private readonly string? _workspaceRoot;
    private readonly ScheduleStore? _schedules;
    private readonly PersonaStore? _personas;
    private readonly CapabilityRegistry? _capabilities;
    private readonly IntegrationConfig _integrationConfig;

    /// <summary>The project-free task tools — delegate + task management, with no find_project /
    /// create_project / project_summary. The toolset for a surface that doesn't use projects (Slack).</summary>
    public static IReadOnlyList<AgentTool> TaskTools =>
        OrchestratorTools.Where(t => t.Name is not ("find_project" or "project_summary")).ToArray();

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
        "You are Smarty, the user's personal assistant — one warm, concise voice. You don't do work yourself: " +
        "you hand it to background workers and relay what they find.\n" +
        "\n" +
        "A tool call is the only way to actually do something — never say you've done, saved, noted, booked, or " +
        "checked something unless you called its tool in this reply.\n" +
        "\n" +
        "Tools:\n" +
        "- delegate(task): start background work — any action or live/real data. Say one short line naming what " +
        "you're doing (\"Checking the weather for the weekend\"). The result comes back later.\n" +
        "- message_task(id, msg): the user refines, adds to, answers, or iterates on an existing task (\"make it " +
        "cleaner\", \"try again\") — pass it to that task's id rather than delegating again. Running and finished " +
        "tasks are listed with their ids; a finished one re-opens with all its prior context.\n" +
        "- cancel_task(id): the user calls something off.\n" +
        "- list_tasks() / task_status(id): what's running and how it's going.\n" +
        "- search_memory(query) / set_memory(type, key, value, context): recall or store durable facts about the " +
        "user (where they live, diet, people). Not one-off trivia.\n" +
        "- find_project(statement): when a message refers to ongoing work without naming it (\"the flights\"), " +
        "resolve which project first; if none, ask.\n" +
        "- project_summary(project): when they ask how a project is going, relay its summary rather than answer " +
        "from memory.\n" +
        "- create_project(title, description) / list_projects(): make a project for anything multi-step you'll " +
        "come back to (an event, a trip, a move). Setting one up does not start work — only delegate when they " +
        "ask for something specific (tag it with the slug). A project detail (a booking, a decision) is recorded " +
        "by delegating it into the project, not set_memory.\n" +
        "\n" +
        "When the user attaches a file, the worker gets it automatically — just delegate what to do with it.\n" +
        "Just answer (no tools) for chat or things you know. Reply in English, brief and human.";

    /// <summary>Handle a user message: echo it, run the orchestrator turn, dispatch any delegated work.</summary>
    public async Task HandleMessageAsync(Session session, string userText, CancellationToken ct,
        string? userScope = null, string? userName = null, IReadOnlyList<Attachment>? attachments = null)
    {
        await session.TurnLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Trace("[turn] user message received");
            session.LastUserMessageAt = DateTimeOffset.UtcNow;
            session.CurrentUserScope = userScope; // who's talking — drives per-user memory (null in the web app)
            session.CurrentUserName = userName;
            if (attachments is { Count: > 0 } && ThreadFilesDir(session) is { } filesDir)
            {
                CopyAttachments(filesDir, attachments);
            }
            // Hold the turn's attachments so delegate can copy them into the task workspace; the model sees a
            // human-readable note (names/types, not raw paths — those are carried deterministically).
            session.PendingAttachments = attachments is { Count: > 0 } ? attachments : null;
            string shownText = session.PendingAttachments is null ? userText : userText + AttachmentNote(session.PendingAttachments);
            EmitMessage(session, "user", shownText);
            session.History.Add(Message.User(shownText));

            // The user-facing turn is where the real decisions live (which tools, answer-and-delegate,
            // refine-vs-new, cancel). Let the model think here so it gets them right; re-voice
            // turns stay think:false for speed. A pinned project chat uses the narrower, project-only toolset.
            var tools = session.PinnedProject is null ? _orchestratorTools : _pinnedTools;
            // The turn persists its own exchange (spoken text + tool calls + results) to session.History.
            await RunOrchestratorTurnAsync(
                session, session.History, tools, think: true, ct).ConfigureAwait(false);
            Trace("[turn] orchestrator user-turn done (ack streamed)");
        }
        finally
        {
            session.PendingAttachments = null; // turn-scoped — never leaks into a later turn
            session.TurnLock.Release();
        }
    }

    /// <summary>Fire a scheduled task into its thread. The host re-attaches the session and refreshes the live
    /// thread BEFORE calling this, so the frozen instruction runs against current context (a "check back and
    /// note the decision" task sees what was decided meanwhile). Injected as a SYSTEM turn — no fake user echo —
    /// and the orchestrator carries it out (delegating as needed); the result/file rides the sink into the
    /// thread as a proactive nudge.</summary>
    public async Task RunScheduledAsync(Session session, string taskText, string? userScope = null, string? userName = null)
    {
        await session.TurnLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Trace($"[schedule] firing into {session.Id}: {Snip(taskText, 120)}");
            session.CurrentUserScope = userScope;
            session.CurrentUserName = userName;
            session.History.Add(Message.System(
                "A task you SCHEDULED earlier is now due. Carry it out NOW, using the conversation above as " +
                "context (it may have moved on since you set this). Do it and report the outcome to the thread " +
                "in one short, natural line — a proactive heads-up. If it involves a file from this " +
                "conversation, send it with send_file (delegate the work; the worker has the file tools). Don't " +
                "ask the user to confirm — only stop if you genuinely can't proceed without a decision only they " +
                "can make. Never mention scheduling, tasks, or any internal mechanics.\n\nScheduled task: " + taskText));
            await RunOrchestratorTurnAsync(
                session, session.History, _orchestratorTools, think: true, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            session.CurrentUserScope = null;
            session.CurrentUserName = null;
            session.TurnLock.Release();
        }
    }

    // Parse a Slack session id ("slack:<channel>:<threadTs>") into its channel + thread timestamp. False for
    // any non-Slack session (e.g. the web app), which is how scheduling stays Slack-only for now.
    private static bool TryParseSlackSession(string sessionId, out string channel, out string threadTs)
    {
        channel = ""; threadTs = "";
        const string prefix = "slack:";
        if (string.IsNullOrEmpty(sessionId) || !sessionId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = sessionId.Substring(prefix.Length);
        int i = rest.IndexOf(':');
        if (i <= 0 || i >= rest.Length - 1) return false;
        channel = rest.Substring(0, i);
        threadTs = rest.Substring(i + 1);
        return true;
    }

    /// <summary>
    /// Run one orchestrator "turn" as the user sees it (a single assistant message id), looping the model
    /// only when it calls a tool that returns data it then needs to voice (list/status/cancel/message).
    /// When run on the live session history, the turn persists its OWN exchange (spoken text + tool calls +
    /// their results) so the model can see what it already did next turn. Run on a temp convo (the worker
    /// re-voice), it persists nothing and the caller handles history. Returns the spoken text.
    /// </summary>
    private async Task<string> RunOrchestratorTurnAsync(
        Session session, IReadOnlyList<Message> baseMessages, IReadOnlyList<AgentTool> tools, bool think, CancellationToken ct)
    {
        // Window the history we send to the model (keeps recent turns + durable system seeds) so a long thread
        // doesn't resend everything every turn. persistTurn below still keys off the ORIGINAL baseMessages
        // reference, and this turn's new messages are appended to the full session.History — so trimming the
        // PROMPT never loses real conversation.
        var convo = new List<Message>(WindowForModel(baseMessages));

        // Keep the big system prompt STATIC so its KV cache is reused every turn (lower time-to-first-token).
        // The per-turn dynamic context — date, running tasks, and the user's profile — goes in a late block
        // just before the latest message: the model still sees it, but the cacheable prefix stays intact.
        // Surfacing the profile here also means personal facts (allergies, where they live) are ALWAYS in
        // front of the model, so it doesn't have to decide to search for them.
        var message = convo.LastOrDefault(m => m.Role == Role.User)?.Content ?? "";
        // A pinned project chat is scoped entirely to its project: surface only the project's context (not the
        // global user profile), and tell the model to stay on it. The general chat surfaces the user profile
        // plus any soft project focus.
        string profile, focus;
        if (session.PinnedProject is not null)
        {
            profile = "";
            focus = await PinnedProjectNote(session, message, ct).ConfigureAwait(false);
        }
        else
        {
            profile = await ProfileNote(session, message, session.CurrentUserScope, ct).ConfigureAwait(false);
            focus = await ProjectFocusNote(session, message, ct).ConfigureAwait(false);
        }
        var dynamicContext = (DateContext() + profile + focus + RunningTasksNote(session) + FinishedTasksNote(session) + ScheduledNote(session) + FilesNote(session) + PersonaNote() + BrandsNote()).TrimStart();
        if (dynamicContext.Length > 0)
            convo.Insert(Math.Max(0, convo.Count - 1), Message.System(dynamicContext));

        int msgId = session.NextMessageId();
        session.Append("msg_start", Json(new { id = msgId, role = "assistant" }));

        var spoken = new StringBuilder();
        bool delegatedSomething = false;
        string? delegatedTask = null; // the description of work just kicked off (for a contextual ack)
        bool anyToolCalled = false; // any tool called across this whole user-turn (incl. earlier iterations)
        bool nudgedNoTool = false;  // we re-prompt at most once if the model named a tool but didn't call it
        bool nudgedTruncation = false; // we retry once if a turn ran out of budget / looped before a real reply
        bool spokenFromModel = false; // did the spoken text come from the model inline (vs. a generated ack)?

        // When this turn runs on the live history, we persist the WHOLE exchange below (tool calls + their
        // results, not just the spoken line) — so next turn the model can see what it already did and won't,
        // say, re-delegate work that's already running. A temp convo (re-voice) keeps the old behaviour.
        bool persistTurn = ReferenceEquals(baseMessages, session.History);
        int persistFrom = convo.Count;

        for (int iter = 0; iter < MaxTurnIterations; iter++)
        {
            var request = new ModelRequest
            {
                Model = _model,
                SystemPrompt = _system, // static → KV cache reused every turn; dynamic context is in `convo`
                Messages = convo,
                Tools = tools,
                RepeatPenalty = 1.0,
                MaxOutputTokens = 4096,
                TurnTimeout = _turnTimeout,
                Think = think, // user turns think (right tool choices); re-voice/status don't (speed)
            };

            var sentMessages = convo.ToList(); // exact input for this inference (before we append its output)
            var turnText = new StringBuilder();
            ModelResponse? final = null;
            var streamFilter = new XmlStreamFilter();
            await foreach (var ev in _provider.StreamAsync(request, ct).ConfigureAwait(false))
            {
                switch (ev)
                {
                    case ModelStreamEvent.Content c:
                        turnText.Append(c.Text);
                        spoken.Append(c.Text);
                        spokenFromModel = true;
                        string filteredText = streamFilter.Feed(c.Text);
                        if (filteredText.Length > 0)
                        {
                            session.Append("content", Json(new { id = msgId, text = filteredText }));
                        }
                        break;
                    case ModelStreamEvent.Reasoning r:
                        session.Append("reasoning", Json(new { id = msgId, text = r.Text }));
                        break;
                    case ModelStreamEvent.Completed done:
                        final = done.Response;
                        break;
                }
            }
            string flushedText = streamFilter.Flush();
            if (flushedText.Length > 0)
            {
                session.Append("content", Json(new { id = msgId, text = flushedText }));
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
            if (turnText.ToString().Contains("<tool_call>"))
            {
                var availableTools = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
                if (TryExtractXmlToolCalls(turnText.ToString(), availableTools, out var xmlCalls, out var xmlCleaned))
                {
                    if (calls == null || calls.Count == 0)
                    {
                        calls = xmlCalls;
                    }
                    else
                    {
                        var merged = calls.ToList();
                        merged.AddRange(xmlCalls);
                        calls = merged;
                    }

                    var oldLen = turnText.Length;
                    turnText.Clear();
                    turnText.Append(xmlCleaned);

                    if (spoken.Length >= oldLen)
                    {
                        spoken.Remove(spoken.Length - oldLen, oldLen);
                        spoken.Append(xmlCleaned);
                    }
                }
            }

            // Truncation / loop recovery: the model ran past the token cap, timed out, or looped WITHOUT ever
            // calling a tool — so the streamed text is a broken, half-finished fragment (often reasoning-style
            // narration), not a real reply. A small thinking model can spiral like this on a trivial message
            // with rich context, burning the whole budget on reasoning before it writes anything. Discard the
            // fragment and retry once, telling it to answer directly and briefly. Done before the assistant
            // message is added to the convo, so the broken fragment never pollutes the next attempt.
            if (calls.Count == 0 && !nudgedTruncation && think
                && final.Finish is FinishReason.Length or FinishReason.Loop or FinishReason.Timeout)
            {
                nudgedTruncation = true;
                if (turnText.Length > 0)
                {
                    spoken.Remove(spoken.Length - turnText.Length, turnText.Length); // drop the broken fragment
                    session.Append("content_cleared", Json(new { id = msgId })); // clear the partial; msg_end self-heals
                }
                convo.Add(Message.System(
                    "You ran out of room before giving a real reply — you over-thought a simple message. Answer " +
                    "the user NOW in ONE short, direct line: no analysis, no narrating what you're about to do, " +
                    "just say it."));
                continue;
            }

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
                                $"happened — and your previous text was NOT shown to the user. A tool call is " +
                                $"the only way to do it — if you meant to, call {named} now. If you were only " +
                                "chatting and no action was needed, reply to the user fresh — they haven't seen " +
                                "anything yet.\n" +
                                $"Your draft (for reference only — not shown): {turnText}\n" +
                                "If the draft contained useful information, weave the key points into your " +
                                "new reply naturally. Do NOT say \"as I mentioned\" or reference it as something " +
                                "you already told them.";
                }
                if (nudge is not null)
                {
                    nudgedNoTool = true;
                    if (turnText.Length > 0)
                    {
                        spoken.Remove(spoken.Length - turnText.Length, turnText.Length);
                        session.Append("content_cleared", Json(new { id = msgId }));
                        // The assistant message was already added (line 378) — remove it so the
                        // model can't reference content that was cleared from the user's view,
                        // and so the system nudge follows the user message directly.
                        if (convo.Count > 0 && convo[^1].Role == Role.Assistant)
                            convo.RemoveAt(convo.Count - 1);
                    }
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
                if (r.DelegatedTask is not null) delegatedTask = r.DelegatedTask;
                needAnotherTurn |= r.DataReturning;
                convo.Add(Message.ToolResult(call.Id, call.Name, r.Text));
            }

            // If we're looping to voice tool data, anything the model said in THIS (pre-tool) iteration was a
            // preamble — and the model often answers inline here AND again after the tool, doubling the reply
            // ("Cleveland Street, FitzroviaCleveland Street, Fitzrovia"). Drop this iteration's text so only the
            // final voicing survives. (Delegation acks aren't data-returning, so "On it" is never dropped.)
            if (needAnotherTurn && turnText.Length > 0)
            {
                spoken.Remove(spoken.Length - turnText.Length, turnText.Length);
                session.Append("content_cleared", Json(new { id = msgId }));
            }

            if (!needAnotherTurn)
                break; // the spoken text (ack / answer) stands; no tool data to relay
        }

        // The model often ACTS (delegates, or resumes/steers an existing task) with everything in its
        // reasoning and NO spoken line, leaving the user staring at a blank. Rather than a fixed "On it.",
        // generate one short CONTEXTUAL ack so they're told what's actually happening. The apology is the
        // LAST resort — only when the turn took no action at all (the model just reasoned and the nudge
        // didn't recover it). NEVER apologise for work that actually happened (e.g. a message_task that
        // re-opened a task to refine it — that's a tool call, not a delegate, so it isn't caught above).
        if (spoken.Length == 0)
        {
            string ack;
            if (delegatedSomething)
                ack = delegatedTask is not null
                    ? await GenerateDelegateAckAsync(message, delegatedTask, ct).ConfigureAwait(false)
                    : "On the case 🔍";
            else if (anyToolCalled)
                ack = await GenerateDelegateAckAsync(message, message, ct).ConfigureAwait(false);
            else
                ack = "Sorry — I trailed off there. Could you say that again?";
            spoken.Append(ack);
            session.Append("content", Json(new { id = msgId, text = ack }));
        }

        // Persist this turn's REAL exchange to history — the assistant's tool calls and their results, not
        // just the spoken line. Without the call + result in history, the model can't see its own past
        // actions next turn (e.g. that it already delegated the pub research) and re-does them. Reasoning is
        // dropped to keep history lean; the transient dynamic-context block (before persistFrom) is excluded.
        if (persistTurn)
        {
            for (int i = persistFrom; i < convo.Count; i++)
            {
                var m = convo[i];
                if (m.Role == Role.Assistant)
                {
                    bool empty = string.IsNullOrWhiteSpace(m.Content) && (m.ToolCalls is null || m.ToolCalls.Count == 0);
                    if (empty) continue; // a thinking-only turn with nothing to keep
                    session.History.Add(Message.Assistant(m.Content, null, m.ToolCalls));
                }
                else if (m.Role == Role.Tool)
                {
                    session.History.Add(m); // tool result, paired to its call above
                }
                // skip Role.System — those are internal nudges/recovery prompts, not real conversation
            }
            // An ack/fallback we generated AFTER the loop isn't in any assistant message above — keep it so
            // the thread reads naturally and the model knows what it told the user.
            if (!spokenFromModel && spoken.Length > 0)
                session.History.Add(Message.Assistant(spoken.ToString()));
        }

        // Carry the complete text on msg_end so the client can snap to the authoritative final string —
        // streamed deltas are best-effort for the live "typing" feel; if any were dropped in transit, this
        // self-heals the message without a refresh.
        session.Append("msg_end", Json(new { id = msgId, text = spoken.ToString() }));
        return spoken.ToString();
    }

    /// <summary>The outcome of one orchestrator tool call: the text result, whether the model should voice
    /// it next iteration, and whether it kicked off a background task.</summary>
    private readonly record struct ToolResultInfo(string Text, bool DataReturning, bool WasDelegate, string? DelegatedTask = null);

    private static ToolResultInfo Data(string text) => new(text, DataReturning: true, WasDelegate: false);

    /// <summary>One short, natural, in-language line telling the user what was just kicked off — used when the
    /// model delegated but said nothing itself. Generated from the task so it's specific, not generic filler.</summary>
    private async Task<string> GenerateDelegateAckAsync(string userMessage, string task, CancellationToken ct)
    {
        try
        {
            var convo = new List<Message>
            {
                Message.User(userMessage),
                Message.System(
                    "You've just quietly started working on this for the user, in the background:\n" + task + "\n\n" +
                    "Write your one line now — telling them you're going off to do it and NAMING the specific " +
                    "thing (pull the subject straight from the task). In the SAME language as their message."),
            };
            var request = new ModelRequest
            {
                Model = _model,
                // A tight persona that forces a single clean chat line. Plain text (no schema — the schema made
                // qwen emit malformed JSON that failed to parse) + think:false: fast, and the explicit "no
                // reasoning, just the line" keeps the chain-of-thought out of the output.
                SystemPrompt =
                    "You are Smarty, a warm, witty teammate. Output ONLY a single short chat line (max ~14 words) " +
                    "saying what you're going off to do, naming the specific thing. A little personality/banter is " +
                    "good. No preamble, no quotes, no reasoning — and NEVER a limp \"On it\". Only the line.",
                Messages = convo,
                Think = false,
                MaxOutputTokens = 60,
                TurnTimeout = TimeSpan.FromSeconds(20),
            };
            var response = await ((IModelProvider)_provider).CompleteAsync(request, ct).ConfigureAwait(false);
            var line = CleanAckLine(response.Content);
            return line.Length > 0 ? line : "On the case 🔍"; // non-wet fallback (the line almost always lands)
        }
        catch
        {
            return "On the case 🔍";
        }
    }

    // Take the model's ack down to one clean chat line: drop any stray <think> tags, keep the first non-empty
    // line, strip wrapping quotes.
    private static string CleanAckLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        text = System.Text.RegularExpressions.Regex.Replace(text, "<think>.*?</think>", "",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "</?think>", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var first = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        return first.Trim().Trim('"', '\'', '`').Trim();
    }

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
                // delegate validates a project; it never creates one. In a pinned project chat, default the
                // work to that project so the model doesn't have to name it.
                var project = call.Arguments.GetStringOrNull("project")?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(project)) project = session.PinnedProject;
                if (!string.IsNullOrEmpty(project) && !_projects.Exists(project))
                    return Data($"There's no project \"{project}\". Use find_project to resolve which one this is, " +
                                "or create_project if it's genuinely new (confirm with the user first).");
                // Optional persona routing: validate against the roster so a typo doesn't silently run unspecialised.
                var persona = call.Arguments.GetStringOrNull("persona")?.Trim();
                if (!string.IsNullOrEmpty(persona) && _personas is not null && _personas.Get(persona) is null)
                    return Data($"There's no persona \"{persona}\". Available: " +
                                $"{string.Join(", ", _personas.All.Select(p => p.Id))}. Use one of those, or omit persona.");
                var task = StartTask(session, desc!.Trim(),
                    string.IsNullOrEmpty(project) ? null : project,
                    string.IsNullOrEmpty(persona) ? null : persona);
                var tags = string.Concat(
                    task.Project is null ? "" : $" (project: {task.Project})",
                    task.Persona is null ? "" : $" (persona: {task.Persona})");
                return new ToolResultInfo(
                    $"Task #{task.Id} started in the background{tags}; its result will come back to you to relay.",
                    DataReturning: false, WasDelegate: true, DelegatedTask: desc!.Trim());
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
                {
                    // In per-user/Slack mode the finished result is delivered by the worker's own re-voice, so a
                    // status check that lands AFTER completion must NOT restate it (that's the stale "still
                    // looking / here it is again" double). Elsewhere (web app) the status reply carries it.
                    line += session.Id.StartsWith("slack:", StringComparison.OrdinalIgnoreCase)
                        ? "\n(Finished — the result has already been posted in the thread. Don't repeat it; just confirm it's done if asked.)"
                        : $"\nResult: {t.Result}";
                }
                return Data(line);
            }

            case "cancel_task":
            {
                if (!TryResolve(session, call, out var t, out var miss)) return Data(miss);
                if (!t.IsRunning) return Data($"Task #{t.Id} is already {t.Status}.");
                CancelTaskTree(session, t);
                return Data($"Task #{t.Id} ({t.Description}) has been cancelled.");
            }

            case "message_task":
            {
                if (!TryResolve(session, call, out var t, out var miss)) return Data(miss);
                var msg = call.Arguments.GetStringOrNull("message");
                if (string.IsNullOrWhiteSpace(msg)) return Data("No message text was provided.");
                // Resume from a stateless replay of the task's transcript when it isn't mid-flight:
                //  • WAITING — paused on a question; this message is the answer.
                //  • DONE / FAILED — finished, but the user is refining or retrying ("make it cleaner",
                //    "try again"). Re-open it so the worker continues with ALL its prior context (the data it
                //    loaded, the files it produced, the code it wrote) and just adjusts — instead of redoing
                //    the whole job from scratch. The planning gate is skipped on a resume (transcript is
                //    non-empty), so it picks up, not restarts.
                // A multi-discipline plan re-enters through its coordinator: the refine is routed to the step it
                // touches (or appended as a new step) and cascades forward — keeping the whole change on one task
                // and resuming the relevant step with full context, never starting the plan over.
                if (t.Plan is not null && t.Status is "waiting" or "done" or "failed")
                {
                    _ = Task.Run(() => RefinePlanAsync(session, t, msg!.Trim()));
                    return new ToolResultInfo(
                        $"Re-opened plan #{t.Id} with your change; it's picking up at the step that affects and carrying it forward.",
                        DataReturning: false, WasDelegate: false);
                }
                if (t.Status is "waiting" or "done" or "failed")
                {
                    bool reopen = t.Status is "done" or "failed";
                    _ = Task.Run(() => AnswerTaskAsync(session, t, msg!.Trim()));
                    return new ToolResultInfo(
                        reopen
                            ? $"Re-opened task #{t.Id} with your note; it's resuming from where it left off with full context (not starting over)."
                            : $"Answer passed back to task #{t.Id}; it's picking up where it left off.",
                        DataReturning: false, WasDelegate: false);
                }
                if (!t.IsRunning) return Data($"Task #{t.Id} is {t.Status}; it can't take a message.");
                t.Inbox.Enqueue(msg!.Trim());
                return Data($"Message passed along to task #{t.Id}; it'll pick it up shortly.");
            }

            case "promote_file":
            {
                if (ThreadFilesDir(session) is not { } filesDir)
                    return Data("Files aren't available in this context, so there's nothing to save.");
                var fileName = call.Arguments.GetStringOrNull("name")?.Trim();
                var scope = call.Arguments.GetStringOrNull("scope")?.Trim();
                if (string.IsNullOrWhiteSpace(fileName)) return Data("Which file? Use the name shown by list_files.");
                if (string.IsNullOrWhiteSpace(scope)) return Data("Where to? Give a specialist id (e.g. branding_designer) or \"global\".");

                var safe = string.Concat(Path.GetFileName(fileName).Select(c =>
                    Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                var source = Path.Combine(filesDir, safe);
                if (safe.Length == 0 || !File.Exists(source))
                    return Data($"There's no file called \"{fileName}\" in this conversation. Check list_files for the exact name.");

                string? targetDir; string where;
                if (string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase))
                {
                    targetDir = GlobalBucketDir();
                    where = "the shared company files";
                }
                else if (scope!.StartsWith("brand:", StringComparison.OrdinalIgnoreCase))
                {
                    // brand:<slug> — save into a brand kit, creating that brand if it's new (e.g. a new client).
                    var slug = scope.Substring("brand:".Length).Trim();
                    if (string.IsNullOrEmpty(slug)) return Data("Give a brand slug, e.g. scope \"brand:adidas\" (or \"brand:house\").");
                    targetDir = BrandBucketDir(slug);
                    bool isNew = BrandBucketDir(slug) is { } d && !Directory.Exists(d);
                    where = $"the {slug} brand kit{(isNew ? " (new brand)" : "")}";
                }
                else if (_personas?.Get(scope) is { } persona)
                {
                    targetDir = PersonaBucketDir(persona.Id);
                    where = $"the {persona.Name} kit";
                }
                else
                {
                    var ids = _personas is null ? "" : string.Join(", ", _personas.All.Select(p => p.Id));
                    return Data($"Unknown scope \"{scope}\". Use \"global\", \"brand:<slug>\" (e.g. brand:adidas), or one of: {ids}.");
                }
                if (targetDir is null) return Data("Durable storage isn't available in this context.");

                try
                {
                    Directory.CreateDirectory(targetDir);
                    File.Copy(source, Path.Combine(targetDir, safe), overwrite: true);
                    return Data($"Saved {safe} to {where} — it'll be available in future conversations. " +
                                "Tell the user briefly it's saved; don't mention buckets or internals.");
                }
                catch (Exception ex) { return Data($"Couldn't save {safe}: {ex.Message}"); }
            }

            case "schedule_task":
            {
                if (_schedules is null)
                    return Data("Scheduling isn't available in this context.");
                var when = call.Arguments.GetStringOrNull("when");
                var taskText = call.Arguments.GetStringOrNull("task");
                if (string.IsNullOrWhiteSpace(when) || string.IsNullOrWhiteSpace(taskText))
                    return Data("To schedule something I need both `when` (an absolute time like 2026-06-26T14:27, " +
                                "or a relative \"in 25m\") and `task` (what to do then).");
                if (!ScheduleStore.TryParseWhen(when!.Trim(), DateTimeOffset.Now, out var fireAt))
                    return Data($"I couldn't read the time \"{when}\". Give an absolute local time " +
                                "(e.g. 2026-06-26T14:27) or a relative one (\"in 25m\", \"in 2 hours\", \"in 3 days\").");
                if (fireAt <= DateTimeOffset.UtcNow.AddSeconds(5))
                    return Data("That time is in the past. Give a time in the future.");
                if (!TryParseSlackSession(session.Id, out var channel, out var threadTs))
                    return Data("I can only schedule things inside a Slack thread right now.");
                var st = _schedules.Add(session.Id, channel, threadTs, taskText!.Trim(), fireAt,
                    session.CurrentUserScope, session.CurrentUserName);
                return Data($"Scheduled (#{st.Id}) for {fireAt.ToLocalTime():dddd d MMM, HH:mm} — I'll act in this " +
                            "thread then, nothing before. Tell the user briefly you'll handle it at that time; " +
                            "don't restate any internals.");
            }

            case "cancel_schedule":
            {
                if (_schedules is null) return Data("Scheduling isn't available in this context.");
                var id = call.Arguments.GetStringOrNull("id")?.TrimStart('#').Trim();
                if (string.IsNullOrEmpty(id)) return Data("Which scheduled item? Use the id from the scheduled list.");
                return Data(_schedules.Cancel(id)
                    ? $"Cancelled scheduled item #{id}."
                    : $"There's no pending scheduled item #{id}.");
            }

            case "search_memory":
            {
                var q = call.Arguments.GetStringOrNull("query") ?? "";
                // Per-user mode (Slack): read the speaker's own facts AND the shared team facts together.
                var scope = session.PersonalMemoryEnabled ? session.CurrentUserScope : null;
                var r = scope is { } s
                    ? _memory.SearchScopes(q, new[] { s, (string?)null })
                    : _memory.Search(q);
                if (!session.PersonalMemoryEnabled && session.Id.StartsWith("slack:", StringComparison.OrdinalIgnoreCase))
                {
                    r += "\n(Note: Personal memory is not accessible in public/group channels to protect privacy. Only shared team facts are shown.)";
                }
                Trace($"[mem] search({q}) -> {Snip(r, 160)}");
                return Data(r);
            }

            case "set_memory":
            {
                // Per-user mode (Slack): a fact is scoped to the SPEAKER by default (so "I'm X" attaches to
                // them); the model sets shared=true for a genuine team-wide fact (the office address). No
                // projects here, so this short-circuits the project routing below.
                if (session.CurrentUserScope is { } userScope)
                {
                    var shared = call.Arguments.GetBool("shared", false);
                    if (!shared && !session.PersonalMemoryEnabled)
                    {
                        return Data("Couldn't save that — personal memories cannot be recorded in public/group channels to protect user privacy. If this is a team-wide fact that everyone should share, set `shared = true`.");
                    }
                    return Data(_memory.Set(
                        call.Arguments.GetStringOrNull("type") ?? "",
                        call.Arguments.GetStringOrNull("key") ?? "",
                        call.Arguments.GetStringOrNull("value") ?? "",
                        call.Arguments.GetStringOrNull("context"),
                        shared ? null : userScope));
                }

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
    private TaskInfo StartTask(Session session, string description, string? project = null, string? persona = null)
    {
        var task = new TaskInfo
        {
            Id = session.NextTaskId(),
            Description = description,
            Project = project,
            Persona = persona,
            UserScope = session.CurrentUserScope, // so the worker's memory writes attach to the right person
            UserName = session.CurrentUserName,
            PersonalMemoryEnabled = session.PersonalMemoryEnabled,
        };
        // Give the task its own workspace (task.md + any attached files), so the worker reads the brief and
        // the user's files from one place. No-op when no workspace root is configured.
        task.WorkspaceDir = CreateWorkspace(session, task, session.PendingAttachments);
        session.Tasks[task.Id] = task;
        session.Append("working", Json(new { id = task.Id, task = description }));
        _ = Task.Run(() => RunWorkerAsync(session, task));
        return task;
    }

    /// <summary>Run a freshly delegated task: start its worker from the task description.</summary>
    private Task RunWorkerAsync(Session session, TaskInfo task)
        => DriveWithHeartbeat(session, task, task.Description);

    // Drive a TOP-LEVEL task's leg with a background PROGRESS HEARTBEAT alongside it: while the task runs, post
    // a short "still on it" line into the thread on an exponential backoff (≈1, 2, 4, 8, 16, 32 min, then steady)
    // so a long task never feels dead but also never spams. Several tasks run their own heartbeats independently,
    // so each reports its own progress. Only top-level tasks go through here (a plan's hidden child steps call
    // DriveWorker directly), so the user sees one heartbeat per thing they asked for, not per sub-step.
    private async Task DriveWithHeartbeat(Session session, TaskInfo task, string message)
    {
        using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(task.Cts.Token);
        var beat = HeartbeatAsync(session, task, hbCts.Token);
        try { await DriveWorker(session, task, message).ConfigureAwait(false); }
        finally
        {
            hbCts.Cancel();
            try { await beat.ConfigureAwait(false); } catch { /* best-effort */ }
        }
    }

    // Emit a "progress" event for a running task, backing off exponentially. Surface-agnostic: the Slack sink
    // renders it; the web app (no sink) is skipped entirely — it already shows live progress in its task overlay.
    private async Task HeartbeatAsync(Session session, TaskInfo task, CancellationToken ct)
    {
        if (session.Sink is null) return; // web app — it has the live overlay; don't post heartbeat chatter
        double minutes = 1;
        try
        {
            while (task.Status == "running")
            {
                try { await Task.Delay(TimeSpan.FromMinutes(minutes), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (ct.IsCancellationRequested || task.Status != "running") return;
                session.Append("progress", Json(new
                {
                    id = task.Id,
                    task = task.Description,
                    note = CleanThought(task.LatestThought),
                }));
                minutes = Math.Min(minutes * 2, 32); // 1,2,4,8,16,32 then steady — never fully silent, never spammy
            }
        }
        catch { /* progress is best-effort — never disturb the task */ }
    }

    // A short, clean snippet of the worker's latest thought/activity for a heartbeat: strip any leaked <think>
    // tags, collapse whitespace, truncate. Empty when there's nothing useful to say yet.
    private static string CleanThought(string? thought)
    {
        if (string.IsNullOrWhiteSpace(thought)) return "";
        var t = System.Text.RegularExpressions.Regex.Replace(thought, @"</?think>", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
        return t.Length > 160 ? t[..160].TrimEnd() + "…" : t;
    }

    /// <summary>Resume a worker that paused to ask a question: feed the user's answer back in. The worker
    /// re-runs seeded with its prior transcript, so it continues with full context (a clean, stateless
    /// resume rather than a live suspended process). Safe to fire-and-forget.</summary>
    public async Task AnswerTaskAsync(Session session, TaskInfo task, string answer,
        string? userScope = null, string? userName = null, IReadOnlyList<Attachment>? attachments = null)
    {
        if (userScope is not null) { session.CurrentUserScope = userScope; session.CurrentUserName = userName; }
        EmitMessage(session, "user", answer); // the answer shows in the chat like any user turn
        // Files attached to the answer: drop them into this conversation's file area and tell the worker.
        if (attachments is { Count: > 0 } && ThreadFilesDir(session) is { } filesDir)
        {
            var added = CopyAttachments(filesDir, attachments);
            if (added.Count > 0)
                task.Conversation.Add(Message.System(
                    "The user has added these files to this conversation's files area: " +
                    string.Join(", ", added) + ". List them with list_files; read them with read_file / file_summary."));
        }
        task.Pending = null;
        task.Status = "running";
        session.Append("working", Json(new { id = task.Id, task = task.Description }));
        await DriveWithHeartbeat(session, task, answer).ConfigureAwait(false);
    }

    /// <summary>Drive a worker for one leg of a (possibly multi-leg, interactive) task: stream its progress,
    /// then either pause on a question it asked, or finish — recording the run and re-voicing the result.
    /// The worker is seeded with <see cref="TaskInfo.Conversation"/> (empty on the first leg, the accumulated
    /// transcript on a resume) so it always has its full prior context.</summary>
    private async Task DriveWorker(Session session, TaskInfo task, string message)
    {
        string result = "(no result)";
        bool cancelled = false;
        bool firstLeg = task.Conversation.Count == 0; // capture before the gate/seeds append to the transcript
        Trace($"[worker #{task.Id}] drive: {Snip(message, 120)}");
        try
        {
            // ASSESS — top-level first leg only (a plan's child STEP already has its instruction). ONE sizing call
            // answers both routing questions at once (was two): which disciplines the task needs, and whether it's
            // complex enough to plan. MULTI-discipline → build a step plan and hand to the coordinator (never
            // returns here). SINGLE → set that persona. Then, complex single-discipline work also gets a textual
            // plan seeded so the executor follows a structured approach. Runs inside the background task, so it
            // never slows the chat turn. Fails open (no disciplines, simple) so it can't block the work.
            if (_planner is not null && firstLeg && task.ParentTaskId is null && task.Plan is null)
            {
                task.LatestThought = "sizing up the task…";
                bool complex;
                if (_personas is not null && task.Persona is null)
                {
                    var roster = PersonaRoster();
                    var (rawDisciplines, isComplex) = await _planner.AssessTaskAsync(task.Description, roster, task.Cts.Token).ConfigureAwait(false);
                    complex = isComplex;
                    var disciplines = rawDisciplines.Where(id => _personas.Get(id) is not null)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (disciplines.Count > 1)
                    {
                        task.LatestThought = "planning the approach…";
                        Trace($"[worker #{task.Id}] assess → multi-discipline: {string.Join(", ", disciplines)}");
                        var plan = await _planner.PlanStepsAsync(task.Description, roster, task.Cts.Token).ConfigureAwait(false);
                        plan?.Steps.RemoveAll(s => _personas.Get(s.Persona) is null);
                        if (plan is { Steps.Count: > 1 })
                        {
                            task.Plan = plan;
                            await DrivePlanAsync(session, task, plan, null).ConfigureAwait(false);
                            return;
                        }
                        Trace($"[worker #{task.Id}] assess → plan unusable; falling back to single worker");
                    }
                    else if (disciplines.Count == 1)
                    {
                        task.Persona = disciplines[0];
                        Trace($"[worker #{task.Id}] assess → single discipline: {task.Persona}");
                    }
                }
                else complex = await _planner.IsComplexAsync(task.Description, task.Cts.Token).ConfigureAwait(false);

                if (complex)
                {
                    task.LatestThought = "planning the approach…";
                    Trace($"[worker #{task.Id}] assess → complex; planning");
                    var plan = await _planner.PlanAsync(task.Description, task.Cts.Token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(plan))
                    {
                        task.Conversation.Add(Message.System(
                            "A plan has been prepared for this task. Follow it, adapting as you learn:\n\n" + plan));
                        Trace($"[worker #{task.Id}] plan:\n{Snip(plan, 600)}");
                    }
                }
                else Trace($"[worker #{task.Id}] assess → simple; no single-worker plan");
            }

            // CLOCK — first leg only. The worker's system prompt is STATIC (so its prefix caches across every
            // task); the current time, which would otherwise break that cache every minute, rides here as a late
            // seed instead. Seeded into the transcript so it persists across any Q&A legs.
            if (firstLeg)
            {
                var now = DateTime.Now;
                task.Conversation.Add(Message.System(
                    $"It is {now:dddd, d MMMM yyyy, HH:mm} ({TimeZoneInfo.Local.StandardName}), year {now.Year} — " +
                    "the real present from the system clock, not the future; never call this date impossible or fictional."));
            }

            // WORKSPACE — first leg only: point the worker at its working directory (the brief in task.md and
            // any files the user attached, copied into files/). Seeded into the transcript so it persists across
            // any Q&A legs. Done after the planning gate so it doesn't trip the gate's "first leg" check.
            if (firstLeg && task.WorkspaceDir is { } ws)
            {
                string? filesDir = ThreadFilesDir(session);
                var seed = new StringBuilder(
                    $"You have a working directory for this task at: {ws}\n- The brief is in task.md.\n");
                if (filesDir is not null)
                {
                    Directory.CreateDirectory(filesDir);
                    seed.Append($"- This conversation's files are in: {filesDir}\n");
                    seed.Append(
                        "List them with list_files; read one with read_file / file_summary. Write a new file " +
                        "with write_file, and hand a file to the user with send_file. ONLY files in this " +
                        "conversation are accessible — you cannot see or send files from anywhere else.\n");
                }
                seed.Append("Use run_shell_command too, if you have it. Write any output into this conversation's files area.");
                task.Conversation.Add(Message.System(seed.ToString()));
                Trace($"[worker #{task.Id}] workspace seeded: {ws}");
            }

            // BRAND RESOLUTION — the branding designer works at an agency with many brands; pick which one this
            // task is for (a client, or the house brand) so the right kit gets mounted below. First leg only;
            // persists on the task so a refine stays on the same brand. Defaults to "house" so it never blocks.
            if (task.Persona == "branding_designer" && string.IsNullOrEmpty(task.Brand) && _planner is not null)
            {
                task.Brand = await _planner.ResolveBrandAsync(task.Description, BrandSlugs(), task.Cts.Token).ConfigureAwait(false);
                Trace($"[worker #{task.Id}] brand → {task.Brand}");
            }

            string workerApiKey = Environment.GetEnvironmentVariable("TOGETHER_API_KEY") 
                ?? Environment.GetEnvironmentVariable("OLLAMA_API_KEY") 
                ?? Environment.GetEnvironmentVariable("SMARTY_API_KEY") ?? "";
            string? togetherBaseUrl = (_ollamaBaseUrl.Contains("localhost") || _ollamaBaseUrl.Contains("127.0.0.1")) ? null : _ollamaBaseUrl;
            IModelProvider provider = _model.Contains("/") || (_ollamaBaseUrl != null && _ollamaBaseUrl.Contains("together"))
                ? new TogetherModelProvider(workerApiKey, togetherBaseUrl)
                : new OllamaModelProvider(_ollamaBaseUrl);
            // If this task runs inside a project, inject that project's context and reframe memory toward
            // tracking the project (not the user); writes are auto-tagged to the project's slug.
            // A re-targeted surface (Slack) supplies its own worker toolset (web-only, no shell/memory);
            // the default is the full shell + web + project-memory set.
            var tools = (_workerToolsFactory?.Invoke(task) ?? new AgentTool[]
            {
                ShellTool.Create(),
                WebResearch.SearchTool(),
                WebResearch.PageAnswerTool(provider, _model),
                FileTools.ReadFileTool(),
                FileTools.SummaryTool(provider, _model),
                MemoryTools.SearchTool(_memory, task.Project),
                MemoryTools.SetTool(_memory, task.Project),
            }).ToList();

            // Every worker also gets the conversation-scoped file tools, rooted at THIS thread's file area:
            // write_file (author), list_files (discover), send_file (hand a file back). send_file emits a "file"
            // event on the session stream, which the surface (Slack thread / SSE) turns into an upload — so a
            // worker can return a file the moment it's ready. No workspace root → no file area → skip (web default).
            if (ThreadFilesDir(session) is { } threadFiles)
            {
                Directory.CreateDirectory(threadFiles);
                tools.Add(FileTools.WriteFileTool(threadFiles));
                // Mount read-only buckets the worker can draw on: a global company area, plus the active
                // persona's own kit (e.g. branding_designer's brand assets). list_files surfaces them with their
                // real paths so run_python can use them; writes/sends still land in the conversation.
                tools.Add(FileTools.ListFilesTool(threadFiles, BucketMounts(task)));
                // Per-LEG de-dupe: a chatty worker often calls send_file more than once for the same file in one
                // run (e.g. once with a "here it is" caption, then again with a summary), uploading it to the
                // thread twice. Scoped to this leg only — a later refine ("make it bigger") rebuilds the tools
                // with a fresh set, so it can legitimately re-send the regenerated file.
                var sentFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                tools.Add(FileTools.SendFileTool(threadFiles, (path, caption) =>
                {
                    if (!sentFiles.Add(Path.GetFullPath(path))) return true; // already sent this leg — skip re-upload
                    session.Append("file", Json(new { path, name = Path.GetFileName(path), caption }));
                    return true;
                }));
            }

            // Persona routing: a delegated task tagged with a persona runs that role's capability tools (Kibana,
            // etc.) on TOP of the base set, so a specialist keeps general competence. Credentials are read from
            // IntegrationConfig at build time — never via the model. Unknown/unconfigured capabilities add nothing.
            if (task.Persona is { } pid && _personas?.Get(pid) is { } persona && _capabilities is { } caps)
            {
                foreach (var t in caps.BuildFor(persona.CapabilityIds, _integrationConfig, task))
                    tools.Add(t);
                Trace($"[worker #{task.Id}] persona '{persona.Id}' → {tools.Count} tools total");
            }

            var input = new AgentInput
            {
                SystemPrompt = _workerSystem() + ProjectContext(task.Project) + PersonaContext(task),
                Model = _model.Contains("/") || (_ollamaBaseUrl != null && _ollamaBaseUrl.Contains("together"))
                    ? new ModelSpec("together", _model, togetherBaseUrl)
                    : ModelSpec.Ollama(_model, _ollamaBaseUrl),
                // Seed with the task's running transcript so a resumed worker keeps everything it found and
                // the Q&A so far. AnswerStream appends to this list, so afterwards it IS the full transcript.
                Conversation = task.Conversation,
                Tools = tools,
                DrainInbox = () => DrainInbox(task),
                // Honesty over speed: with reasoning on, the worker inspects tool output, notices junk
                // (e.g. a blank free-space field) and either recovers or says it couldn't — instead of
                // fabricating. Reliability past that is a model problem, fixed by a better model, not by
                // bolting model-specific crutches into this (deliberately model-agnostic) system.
                Think = true,
            };
            if (task.Persona is "data_scientist" or "branding_designer")
            {
                // run_python IS these personas' workhorse — it iterates: the data_scientist profiles data, computes
                // stats and renders charts; the branding_designer assembles a multi-element designed document
                // (reportlab/HTML, embedding logo + charts). The default per-tool budget (5) chokes that mid-build
                // and the watchdog then forces a half-finished fallback. Give run_python real headroom and tolerate
                // the odd failed call (e.g. a missing optional import) without aborting the deliverable.
                input.MaxIterations = 15;
                input.MaxCallsPerTool = 25;
                input.MaxToolFailures = 8;
            }
            var worker = new SmartyAgent(input);

            // Supervise this leg: a background watchdog watches the live transcript and nudges the worker to
            // wrap up (or aborts it) if it starts thrashing — a relentless failing search. Stopped the instant
            // the leg ends, so it never lingers past the worker.
            using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(task.Cts.Token);
            if (_watchdog is not null)
                _ = _watchdog.MonitorAsync(task, monitorCts.Token, Trace);

            var thought = new StringBuilder();
            try
            {
                await foreach (var ev in worker.AnswerStream(message, task.Cts.Token).ConfigureAwait(false))
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
            }
            finally
            {
                monitorCts.Cancel(); // stop supervising the moment this leg ends
            }

            // Capture the worker transcript (task → tool calls → results → answer) for the dataset.
            if (worker.LastRun is { } run)
            {
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

        // Did the worker pause to ask the user something rather than finish? We don't guess this from its
        // prose — we FORCE a structured verdict: a schema-constrained finalization call (below) returns a
        // real question object or nothing. Only run it when the worker finished cleanly (not cancelled/
        // failed). If it asked, park the task as "waiting": its transcript is already on task.Conversation,
        // the question is surfaced to the user, and we neither log a run nor re-voice — the run is paused.
        // A plan's child STEP never pauses to ask the user — the coordinator decides pass/retry/pause for it —
        // so the question-finalize is top-level only.
        var question = (!cancelled && task.Status == "running" && task.ParentTaskId is null)
            ? await FinalizeOutcomeAsync(task, task.Cts.Token).ConfigureAwait(false)
            : null;
        if (question is not null)
        {
            task.Status = "waiting";
            task.Pending = question;
            task.LatestThought = question.Question;
            session.Append("working_done", Json(new { id = task.Id, status = "waiting" }));
            session.Append("question", Json(new
            {
                id = task.Id,
                question = question.Question,
                options = question.Options,
                project = task.Project,
            }));
            Trace($"[worker #{task.Id}] WAITING >>> {Snip(question.Question, 200)}");
            return;
        }

        task.Result = result;
        if (task.Status == "running") task.Status = "done";

        // A child step's lifecycle is internal — only the coordinator's own working/working_done are surfaced,
        // so the user sees one task progressing, not a flurry of sub-tasks.
        if (task.ParentTaskId is null)
            session.Append("working_done", Json(new { id = task.Id, status = task.Status }));
        Trace($"[worker #{task.Id}] RESULT >>> {Snip(result, 500)}");

        // Record what the sub-agent did, scoped to its project (work with no project isn't logged here —
        // it isn't shown anywhere). The full accumulated transcript (across any Q&A legs) is on
        // task.Conversation. Read back by the project overview.
        if (!string.IsNullOrEmpty(task.Project) && task.Conversation.Count > 0)
        {
            var transcript = task.Conversation;
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
        // Re-checked LIVE (not just the latched flag): a cancel can land during the finalize/result window
        // above, and we must not speak a result the user already told us to drop.
        if (cancelled || task.Cts.IsCancellationRequested || task.Status == "cancelled") return;

        // A plan's child STEP doesn't speak to the user — its coordinator relays the whole plan once at the
        // end. Only a top-level task re-voices its own result here.
        if (task.ParentTaskId is null)
            await ReVoiceAsync(session, task, result).ConfigureAwait(false);
    }

    /// <summary>Relay a finished task's result to the user in the orchestrator's voice. The MODEL phrases it
    /// (so it matches the user's language), but on a strict instruction: a blank/"(no result)" result must be
    /// reported as a failure with nothing invented — the re-voice once fabricated a whole "latest news" rundown
    /// from an empty result. Shared by single workers and the plan coordinator.</summary>
    private async Task ReVoiceAsync(Session session, TaskInfo task, string result)
    {
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
            var tools = session.PinnedProject is null ? _orchestratorTools : _pinnedTools;
            string content = await RunOrchestratorTurnAsync(
                session, convo, tools, think: _revoiceThink, CancellationToken.None).ConfigureAwait(false);
            Trace($"[worker #{task.Id}] re-voice done (user now sees the answer)");

            session.History.Add(Message.Assistant(string.IsNullOrWhiteSpace(content) ? result : content));
        }
        finally
        {
            session.TurnLock.Release();
        }
    }

    /// <summary>Run a multi-discipline plan to completion: each step is a hidden child worker of <paramref
    /// name="parent"/>, run in order over the shared thread file area, with each step's output threaded into the
    /// next step's brief. On a step that can't deliver (after one retry) the plan PAUSES at that step and the
    /// coordinator relays honestly what it has — the partial artifacts and transcripts are all preserved, so a
    /// follow-up resumes from there. When <paramref name="refineForCurrent"/> is set, the step at
    /// <see cref="WorkPlan.CurrentStep"/> resumes its existing child with that note (a user refinement) instead
    /// of starting fresh; later steps re-run to cascade the change forward.</summary>
    private async Task DrivePlanAsync(Session session, TaskInfo parent, WorkPlan plan, string? refineForCurrent)
    {
        var prior = new System.Text.StringBuilder();
        int resumeAt = plan.CurrentStep; // the step the refine (if any) targets — captured before we advance
        // Re-establish context from steps already done before the resume point (so a resume threads them forward).
        for (int j = 0; j < resumeAt && j < plan.Steps.Count; j++)
            if (plan.Steps[j].Result is { Length: > 0 } r)
                prior.Append($"\n### Output of step {j + 1} ({plan.Steps[j].Persona}):\n{r}\n");

        // Steps still to run (resume point onward), grouped into WAVES: steps sharing a wave are independent and
        // run concurrently; ascending waves run in turn, each seeing every earlier wave's output. The common case
        // (one step per wave) is exactly the old sequential behaviour.
        var waves = Enumerable.Range(resumeAt, plan.Steps.Count - resumeAt)
            .Select(i => (Step: plan.Steps[i], Index: i))
            .GroupBy(x => x.Step.Wave)
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var wave in waves)
        {
            if (parent.Cts.IsCancellationRequested) { parent.Status = "cancelled"; return; }
            var members = wave.OrderBy(m => m.Index).ToList();
            plan.CurrentStep = members[0].Index;
            parent.Status = "running";
            foreach (var m in members) m.Step.Status = "running";

            string label = members.Count == 1
                ? $"step {members[0].Index + 1}/{plan.Steps.Count} ({members[0].Step.Persona}): {Head(members[0].Step.Instruction, 70)}"
                : $"{members.Count} steps in parallel — {string.Join(", ", members.Select(m => m.Step.Persona))}";
            parent.LatestThought = label;
            session.Append("working", Json(new { id = parent.Id, task = label }));
            Trace($"[plan #{parent.Id}] wave {wave.Key}: {string.Join(", ", members.Select(m => $"#{m.Index + 1}[{m.Step.Persona}]"))}");

            // Run the whole wave concurrently over one frozen prior-context snapshot. The refine note (if any)
            // only goes to the step the user's change actually targets; the rest just (re)run normally.
            string priorSnapshot = prior.ToString();
            async Task<(int Index, PlanStep Step, bool Ok, string Reason)> RunMember((PlanStep Step, int Index) m)
            {
                var (ok, reason) = await RunPlanStepAsync(
                    session, parent, m.Step, priorSnapshot, m.Index == resumeAt ? refineForCurrent : null).ConfigureAwait(false);
                return (m.Index, m.Step, ok, reason);
            }
            var runs = await Task.WhenAll(members.Select(RunMember)).ConfigureAwait(false);

            // Cancelled anywhere in the wave → stop silently (the stop was already acknowledged to the user).
            if (parent.Cts.IsCancellationRequested || runs.Any(r => r.Reason == "cancelled"))
            { parent.Status = "cancelled"; return; }

            // Thread every success into the prior context (index order) so the next wave — and any resume — sees
            // them, even if a sibling in this wave failed.
            foreach (var r in runs.Where(r => r.Ok).OrderBy(r => r.Index))
            {
                r.Step.Status = "done";
                prior.Append($"\n### Output of step {r.Index + 1} ({r.Step.Persona}):\n{r.Step.Result}\n");
            }

            // A genuine failure pauses the plan at the earliest failed step; the wave's other work is preserved.
            if (runs.Where(r => !r.Ok).OrderBy(r => r.Index).ToList() is { Count: > 0 } failed)
            {
                var f = failed[0];
                f.Step.Status = "failed";
                parent.Status = "waiting"; // paused, not dead: resumable from here
                parent.LatestThought = $"stuck on step {f.Index + 1}: {Head(f.Reason, 80)}";
                session.Append("working_done", Json(new { id = parent.Id, status = "waiting" }));
                var doneLabels = plan.Steps.Where(s => s.Status == "done").Select(s => s.Persona).ToList();
                var summary =
                    $"The plan completed {(doneLabels.Count == 0 ? "no steps" : string.Join(", ", doneLabels))}, " +
                    $"then stalled on step {f.Index + 1} ({f.Step.Persona}): {f.Step.Instruction}. " +
                    $"What blocked it: {f.Reason}. The work so far is saved.";
                parent.Result = summary;
                Trace($"[plan #{parent.Id}] PAUSED at step {f.Index + 1}: {Snip(f.Reason, 160)}");
                await ReVoiceAsync(session, parent, summary).ConfigureAwait(false);
                return;
            }
        }

        plan.CurrentStep = plan.Steps.Count;
        // A cancel that landed during the FINAL step would otherwise fall through here and re-voice the result
        // (the per-step boundary check is only hit on the next iteration, which never comes). Catch it.
        if (parent.Cts.IsCancellationRequested) { parent.Status = "cancelled"; return; }
        parent.Status = "done";
        var last = plan.Steps[^1];
        parent.Result = string.IsNullOrWhiteSpace(last.Result) ? "(no result)" : last.Result;
        session.Append("working_done", Json(new { id = parent.Id, status = "done" }));
        Trace($"[plan #{parent.Id}] DONE ({plan.Steps.Count} steps)");
        await ReVoiceAsync(session, parent, parent.Result!).ConfigureAwait(false);
    }

    /// <summary>Run one plan step as a hidden child worker, with one retry on a verified failure. Returns
    /// (true, "") once the step's output satisfies what it was meant to produce, or (false, reason) when it
    /// still can't after the retry — at which point the coordinator pauses the plan. Records the child id on the
    /// step so a later refine can resume it. Reuses an existing child (resume) when one is already attached.</summary>
    private async Task<(bool Ok, string Reason)> RunPlanStepAsync(
        Session session, TaskInfo parent, PlanStep step, string priorContext, string? note)
    {
        string lastReason = "the step produced no usable result";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (parent.Cts.IsCancellationRequested) return (false, "cancelled");

            // Resume an existing child (a refine, or this step ran before) so it ADJUSTS rather than redoes;
            // otherwise spin up a fresh child for this step. Either way it shares the thread file area, so it
            // sees what earlier steps produced.
            TaskInfo child;
            string message;
            if (step.ChildTaskId is { } cid && session.Tasks.TryGetValue(cid, out var existing) && existing.Conversation.Count > 0)
            {
                child = existing;
                child.Status = "running";
                child.Plan = null;
                message = note is { Length: > 0 }
                    ? note
                    : "An earlier step in the plan was revised. Refresh your output to match the updated files " +
                      "in this conversation's files area, keeping everything else as-is.";
                if (attempt > 0) message += $"\n\n(Your previous attempt didn't satisfy the step: {lastReason}. Try again.)";
            }
            else
            {
                child = new TaskInfo
                {
                    Id = session.NextTaskId(),
                    Description = step.Instruction,
                    Persona = step.Persona,
                    ParentTaskId = parent.Id,
                    UserScope = parent.UserScope,
                    UserName = parent.UserName,
                    PersonalMemoryEnabled = parent.PersonalMemoryEnabled,
                };
                child.WorkspaceDir = CreateWorkspace(session, child, null);
                session.Tasks[child.Id] = child;
                step.ChildTaskId = child.Id;
                var sb = new System.Text.StringBuilder(step.Instruction);
                sb.Append("\n\nThis is one step of a larger plan. Expected output: ").Append(step.Produces).Append('.');
                if (priorContext.Length > 0)
                    sb.Append("\n\nWhat earlier steps produced (their files are in this conversation's files area):\n")
                      .Append(priorContext);
                if (attempt > 0)
                    sb.Append($"\n\n(Your previous attempt didn't satisfy the step: {lastReason}. Try again.)");
                message = sb.ToString();
            }

            await DriveWorker(session, child, message).ConfigureAwait(false);
            if (child.Status == "cancelled") return (false, "cancelled");

            var result = child.Result ?? "";
            var (verdictOk, verdictReason) = await _planner!.VerifyStepAsync(
                step.Instruction, step.Produces, result, parent.Cts.Token).ConfigureAwait(false);
            if (verdictOk)
            {
                step.Result = result;
                return (true, "");
            }
            lastReason = string.IsNullOrWhiteSpace(verdictReason) ? "the output didn't meet the step's goal" : verdictReason;
            Trace($"[plan #{parent.Id}] step retry — {Snip(lastReason, 140)}");
        }
        return (false, lastReason);
    }

    /// <summary>Re-enter a finished/paused plan with a user refinement: route it to the step it touches (or
    /// append a new step), rewind the plan to there, and resume — the targeted step adjusts and the rest cascade
    /// forward. Keeps the whole change attached to the one plan task. Fire-and-forget.</summary>
    private async Task RefinePlanAsync(Session session, TaskInfo parent, string message)
    {
        var plan = parent.Plan!;
        EmitMessage(session, "user", message);
        var roster = PersonaRoster();
        var (idx, newPersona) = await _planner!.RouteRefineAsync(plan, roster, message, parent.Cts.Token).ConfigureAwait(false);

        if (idx < 0)
        {
            // A genuinely new discipline — append it as a new step the plan runs next.
            var persona = newPersona is { Length: > 0 } && _personas?.Get(newPersona) is not null
                ? newPersona!
                : plan.Steps[^1].Persona;
            plan.Steps.Add(new PlanStep(persona, message, "the change the user just asked for"));
            plan.CurrentStep = plan.Steps.Count - 1;
            Trace($"[plan #{parent.Id}] refine → new step [{persona}]");
            await DrivePlanAsync(session, parent, plan, message).ConfigureAwait(false);
            return;
        }

        // Rewind to the targeted step; downstream steps re-run to cascade the change.
        plan.CurrentStep = idx;
        for (int i = idx; i < plan.Steps.Count; i++) plan.Steps[i].Status = "pending";
        Trace($"[plan #{parent.Id}] refine → step {idx + 1} ([{plan.Steps[idx].Persona}])");
        await DrivePlanAsync(session, parent, plan, message).ConfigureAwait(false);
    }

    private static string Head(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max].TrimEnd() + "…");

    // Cancel a task AND any hidden child tasks it spawned (a multi-discipline plan runs each step as a child
    // with its own run loop and token). Cancelling only the named task would leave an in-flight step grinding
    // to completion and then delivering its result minutes later — so we cancel the whole tree at once.
    private static void CancelTaskTree(Session session, TaskInfo t)
    {
        Cancel(t);
        foreach (var child in session.Tasks.Values.Where(c => c.ParentTaskId == t.Id))
            Cancel(child);

        static void Cancel(TaskInfo task)
        {
            task.Status = "cancelled";
            try { task.Cts.Cancel(); } catch { /* already disposed/cancelled — fine */ }
        }
    }

    // The orchestrator resends its WHOLE conversation every turn — and again on each worker re-voice — so a long,
    // intense thread balloons token cost roughly quadratically. Cap what we send to the model: keep all System
    // seeds (channel, durable context) plus the last N user turns of detail, cutting at a clean user-message
    // boundary so a tool-call/result pair is never split (a dangling call with no result can make a provider
    // reject the request). Older detail drops off the PROMPT only — durable facts still live in the memory store
    // and project context, and the full transcript is untouched. Short threads are sent whole.
    private const int MaxRecentUserTurns = 12;
    private const int HistoryWindowThreshold = 60;

    private static IReadOnlyList<Message> WindowForModel(IReadOnlyList<Message> all)
    {
        if (all.Count <= HistoryWindowThreshold) return all;
        int usersSeen = 0, cut = -1;
        for (int i = all.Count - 1; i >= 0; i--)
            if (all[i].Role == Role.User && ++usersSeen >= MaxRecentUserTurns) { cut = i; break; }
        if (cut <= 0) return all; // fewer than N user turns despite the size — nothing safe to trim
        var kept = new List<Message>(all.Count - cut + 8);
        for (int i = 0; i < cut; i++) if (all[i].Role == Role.System) kept.Add(all[i]); // durable seeds
        for (int i = cut; i < all.Count; i++) kept.Add(all[i]);                          // recent tail
        return kept;
    }

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
        // Only check content (what the model said to the user), not reasoning — the model
        // often discusses tools in reasoning without intending to call them (e.g. "without
        // needing to delegate"), causing false positives that clear valid replies.
        var hay = content.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(hay)) return null;
        foreach (var t in tools)
        {
            var name = t.Name.ToLowerInvariant();
            if (name == "delegate")
            {
                // For 'delegate', check if it's used as an active command rather than a negation/discussion.
                // Ignore phrases like "no need to delegate", "don't delegate", etc.
                var pattern = @"(?<!\b(no\s+need\s+to|not|don't|won't|can't|cannot|never|without|instead\s+of|rather\s+than|no)\s+)\bdelegate\b";
                if (System.Text.RegularExpressions.Regex.IsMatch(hay, pattern))
                    return t.Name;
            }
            else
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(hay, $@"\b{System.Text.RegularExpressions.Regex.Escape(name)}\b"))
                    return t.Name;
            }
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
    private async Task<string> ProfileNote(Session session, string message, string? userScope, CancellationToken ct)
    {
        IReadOnlyList<MemoryFact> facts;
        if (userScope is null || !session.PersonalMemoryEnabled)
        {
            // Web app, or group Slack channel: relevance over the shared team facts (null scope).
            facts = await _memory.RelevantToScopes(message, k: 6, scopes: new[] { (string?)null }, ct: ct).ConfigureAwait(false);
        }
        else
        {
            // Per-user (Slack DM): this message's relevant facts (the speaker's own + shared), UNIONed with the
            // conversation's sticky working set, so facts that have surfaced before stay in play (re-loaded to
            // their current value each turn) and don't drop out when the topic drifts.
            var fresh = await _memory.RelevantToScopes(message, 6, new[] { userScope, (string?)null }, ct).ConfigureAwait(false);
            facts = HydrateWorkingSet(session, userScope, fresh);
        }
        Trace($"[mem] relevant-to({Snip(message, 50)}) -> {(facts.Count == 0 ? "(none)" : string.Join(", ", facts.Select(f => f.Key + "=" + f.Value)))}");
        
        var sb = new StringBuilder();
        if (!session.PersonalMemoryEnabled && session.Id.StartsWith("slack:", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("\n\nNote: You are in a public or group channel. Personal memories for individual users are disabled here to protect privacy. Only shared team facts are accessible.\n");
        }
        if (facts.Count == 0) return sb.ToString();
        
        sb.Append("\n\nRelevant to this, here's what you know about the user (apply it; never advise against it):\n");
        foreach (var f in facts)
        {
            sb.Append($"- {f.Key}: {f.Value}");
            if (!string.IsNullOrWhiteSpace(f.Context)) sb.Append($" ({f.Context})");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // The sticky "facts in play" for a conversation: keep the IDENTITY of every fact that has surfaced, and
    // re-load its CURRENT value each turn (so updates show and superseded values never go stale). A key that
    // no longer resolves is dropped (self-heal). Bounded LRU so a long thread can't bloat the prompt. Only
    // facts THIS speaker may see (their own scope + shared) are returned — never another person's.
    private List<MemoryFact> HydrateWorkingSet(Session session, string userScope, IReadOnlyList<MemoryFact> fresh)
    {
        const int Cap = 12;
        var set = session.MemoryWorkingSet;

        // Freshly surfaced facts join the set as most-recently-used.
        foreach (var f in fresh)
        {
            var r = new MemoryRef(f.Project, f.Type, f.Key);
            set.RemoveAll(x => x.Equals(r));
            set.Add(r);
        }

        // Re-load current values; drop any key that no longer resolves.
        var resolved = new Dictionary<MemoryRef, MemoryFact>();
        for (int i = set.Count - 1; i >= 0; i--)
        {
            var fact = _memory.GetActive(set[i].Scope, set[i].Type, set[i].Key);
            if (fact is null) { set.RemoveAt(i); continue; }
            resolved[set[i]] = fact;
        }

        // Bound to the most-recently-used keys.
        if (set.Count > Cap) set.RemoveRange(0, set.Count - Cap);

        // Inject only what THIS speaker may see (their scope + the shared scope).
        var allowed = new HashSet<string?> { userScope.Trim().ToLowerInvariant(), null };
        return set.Where(r => allowed.Contains(r.Scope) && resolved.ContainsKey(r))
                  .Select(r => resolved[r]).ToList();
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

    // The dedicated project chat: this whole conversation is about ONE project. Surface its details and hold
    // the model to it — only this project's topic, and a polite deflection for anything unrelated.
    private async Task<string> PinnedProjectNote(Session session, string message, CancellationToken ct)
    {
        var slug = session.PinnedProject;
        if (string.IsNullOrEmpty(slug)) return "";
        var p = _projects.Get(slug);
        if (p is null) { session.PinnedProject = null; session.CurrentProject = null; return ""; }

        var sb = new StringBuilder($"\n\nThis is the DEDICATED chat for the project \"{p.Title}\"");
        if (!string.IsNullOrWhiteSpace(p.Description)) sb.Append($": {p.Description}");
        sb.Append(".\nTalk ONLY about this project — its planning, details, and getting things done for it. " +
                  "Record details and delegate work to THIS project. If the user asks about anything not " +
                  "related to it, briefly say this is the project's space and they can use the main chat for " +
                  "other things — don't act on unrelated requests here.\n");
        var facts = await _memory.RelevantTo(message, k: 6, project: slug, ct: ct).ConfigureAwait(false);
        if (facts.Count > 0)
        {
            sb.Append("What's known about it:\n");
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

    // The worker-side persona framing: the role's expertise prompt, plus a "how to use it" hint for each
    // capability that actually produced tools (so it's only told about integrations it really has). Empty when
    // the task has no persona or personas aren't wired.
    private string PersonaContext(TaskInfo task)
    {
        if (task.Persona is not { } pid || _personas?.Get(pid) is not { } persona) return "";
        var sb = new StringBuilder($"\n\nYou are working as the {persona.Name}.\n{persona.SystemPrompt}\n");
        if (_capabilities is { } caps)
            foreach (var hint in caps.ActiveHints(persona.CapabilityIds, _integrationConfig, task))
                sb.Append($"- {hint}\n");
        return sb.ToString();
    }

    // The persona roster surfaced to the orchestrator. Routing is now delegate's job — a background triage
    // picks the right specialist(s) and, for a cross-discipline job, plans a sequence of them. So the
    // orchestrator should NOT hand-pick: it just delegates the work. The only time it passes `persona` is when
    // the user explicitly names a role ("ask the software engineer…"). Empty when no personas are configured.
    private string PersonaNote()
    {
        if (_personas is null) return "";
        var all = _personas.All;
        if (all.Count == 0) return "";
        var sb = new StringBuilder("\n\nWhen you delegate, the work is automatically routed to the right " +
                                   "specialist — and a task that spans several disciplines is planned as a " +
                                   "sequence of them. So just delegate the work plainly; do NOT pick a specialist " +
                                   "yourself. Only pass `persona` when the user explicitly names a role. The " +
                                   "available specialists (for your awareness only):\n");
        foreach (var p in all)
            sb.Append($"- {p.Id} ({p.Name}): {p.Description}\n");
        return sb.ToString();
    }

    // The brands managed here (house + any client brand kits), surfaced so the orchestrator can talk about them
    // and knows new branding work can either use an existing brand or become a new one (promote_file brand:<slug>).
    // Empty when no workspace root (no buckets) — and quietly omitted when only the house brand exists yet.
    private string BrandsNote()
    {
        if (BrandsRootDir() is null) return "";
        var slugs = BrandSlugs();
        var clients = slugs.Where(s => !string.Equals(s, "house", StringComparison.OrdinalIgnoreCase)).ToList();
        if (clients.Count == 0) return "";
        var sb = new StringBuilder("\n\nBrand kits available for branding work (the agency's own is \"house\"):\n");
        sb.Append("- house (your own brand)\n");
        foreach (var c in clients) sb.Append($"- {c}\n");
        sb.Append("Branding work is automatically matched to the right brand. To save an approved new brand (or " +
                  "update one), use promote_file with scope \"brand:<slug>\".\n");
        return sb.ToString();
    }

    // A compact roster (id: one-liner) handed to the planner's triage/step/route calls. Kept lean on purpose —
    // routing reads ids and a short description, never the full system prompts, so the gate stays cheap.
    private string PersonaRoster()
    {
        if (_personas is null) return "";
        var sb = new StringBuilder();
        foreach (var p in _personas.All)
            sb.Append($"- {p.Id}: {p.Description}\n");
        return sb.ToString();
    }

    // A live snapshot of in-flight tasks, appended to the system prompt so the orchestrator always knows
    // what it has running (and their ids) — even across turns, so "cancel that" / "how's it going" work.
    private static string RunningTasksNote(Session session)
    {
        var running = session.Tasks.Values.Where(t => t.IsRunning && t.ParentTaskId is null)
            .OrderBy(t => int.TryParse(t.Id, out var n) ? n : 0).ToList();
        if (running.Count == 0) return "";
        var sb = new StringBuilder("\n\nTasks ALREADY running in the background (refer to them by id):\n");
        foreach (var t in running)
            sb.Append($"- #{t.Id}: {t.Description}\n");
        sb.Append("Don't delegate anything that overlaps one of these — it's already being done. If the user " +
                  "is adding to one, delegate ONLY the genuinely new part (or steer it with message_task).\n" +
                  "If the user is just CHASING a running task — \"hurry up\", \"ship it\", \"you done?\", \"any " +
                  "progress?\" — do NOT start another task and do NOT re-delegate the same work (that spawns a " +
                  "confusing duplicate and wastes the in-flight one). Either pass the nudge to it with " +
                  "message_task, or simply reassure them it's still going. Starting over is almost never right.\n");
        return sb.ToString();
    }

    // Tasks in this thread that have FINISHED but can be re-opened. A worker resumes from its own saved
    // transcript, so reopening keeps everything it already did (data loaded, files produced, code written) and
    // just adjusts — far cheaper and better than redoing the job. Surfaced so the model refines instead of
    // re-delegating when the user iterates on a result.
    private static string FinishedTasksNote(Session session)
    {
        var finished = session.Tasks.Values.Where(t => t.Status is "done" or "failed" && t.ParentTaskId is null)
            .OrderBy(t => int.TryParse(t.Id, out var n) ? n : 0).ToList();
        if (finished.Count == 0) return "";
        var sb = new StringBuilder("\n\nFinished tasks in this thread (re-open one with message_task — DON'T start over):\n");
        foreach (var t in finished)
            sb.Append($"- #{t.Id} [{t.Status}]: {Snip(t.Description, 140)}\n");
        sb.Append("When the user iterates on what one of these produced — \"make it cleaner\", \"more pink\", " +
                  "\"add a chart\", \"try again\" — message_task that id with just the change. The worker picks up " +
                  "with all its prior context and adjusts (e.g. tweaks and re-renders the report) instead of " +
                  "rebuilding from scratch. Only delegate a NEW task for genuinely new work.\n");
        return sb.ToString();
    }

    // What's scheduled for later in THIS thread, surfaced so the model can reference or cancel it (and won't
    // re-schedule the same thing). Empty when scheduling isn't wired or nothing's pending here.
    private string ScheduledNote(Session session)
    {
        if (_schedules is null) return "";
        var pending = _schedules.PendingFor(session.Id);
        if (pending.Count == 0) return "";
        var sb = new StringBuilder("\n\nScheduled for later in this thread (refer to / cancel by id):\n");
        foreach (var t in pending)
            sb.Append($"- #{t.Id} at {t.FireAt.ToLocalTime():ddd d MMM HH:mm}: {t.TaskText}\n");
        sb.Append("Use cancel_schedule(id) to drop one. Don't re-schedule something already listed here.\n");
        return sb.ToString();
    }

    private static List<TaskInfo> OrderedTasks(Session session) =>
        session.Tasks.Values.Where(t => t.ParentTaskId is null)
            .OrderBy(t => int.TryParse(t.Id, out var n) ? n : 0).ToList();

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

    // What we ask the worker to report once it has finished — and the schema we FORCE that report into.
    private const string FinalizeInstruction =
        "You have stopped working on the task above. Report your outcome as JSON.\n" +
        "- If you completed the task or fully answered it, set status = \"done\" (leave question/options empty).\n" +
        "- ONLY if you are genuinely blocked and cannot proceed without a decision that just the user can make " +
        "(a real choice or a missing preference — NEVER to confirm something you could simply do), set " +
        "status = \"question\": put one clear, specific question in \"question\", and the 2–4 most likely short " +
        "answers in \"options\" (the user can also type their own).\n" +
        "Decide strictly from the conversation above — don't invent a reason to ask.";

    // A strict JSON Schema for the worker's outcome. Passed to the model as Ollama's `format`, so the reply
    // is GUARANTEED to parse and to carry a real status — the question is a field, never a guess from prose.
    private static JsonNode OutcomeSchema() => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("done", "question") },
            ["question"] = new JsonObject { ["type"] = "string" },
            ["options"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        },
        ["required"] = new JsonArray("status"),
    };

    /// <summary>Force the worker's outcome into a structured verdict instead of parsing it out of free text.
    /// After the worker has finished, one schema-constrained call classifies the run as finished or
    /// blocked-on-the-user — the model literally cannot return anything but JSON matching
    /// <see cref="OutcomeSchema"/>. Returns the structured question when blocked, or null when finished (so
    /// the worker's own answer is relayed verbatim). Best-effort: any failure falls back to "finished".</summary>
    private async Task<PendingQuestion?> FinalizeOutcomeAsync(TaskInfo task, CancellationToken ct)
    {
        try
        {
            var convo = new List<Message>(task.Conversation) { Message.System(FinalizeInstruction) };
            var request = new ModelRequest
            {
                Model = _model,
                Messages = convo,
                Think = false, // just classifying a conclusion it already reasoned to — keep it fast
                ResponseFormat = OutcomeSchema(),
                MaxOutputTokens = 512,
                TurnTimeout = TimeSpan.FromSeconds(40),
            };

            var response = await ((IModelProvider)_provider).CompleteAsync(request, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.Content)) return null;

            string content = response.Content.Trim();
            int firstBrace = content.IndexOf('{');
            int lastBrace = content.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                content = content.Substring(firstBrace, lastBrace - firstBrace + 1);
            }

            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (!string.Equals(status, "question", StringComparison.OrdinalIgnoreCase)) return null;

            var q = root.TryGetProperty("question", out var qEl) && qEl.ValueKind == JsonValueKind.String
                ? qEl.GetString()?.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(q)) return null; // said "question" but gave none → treat as finished

            var options = new List<string>();
            if (root.TryGetProperty("options", out var optEl) && optEl.ValueKind == JsonValueKind.Array)
                foreach (var o in optEl.EnumerateArray())
                    if (o.ValueKind == JsonValueKind.String && o.GetString()?.Trim() is { Length: > 0 } opt)
                        options.Add(opt);

            Trace($"[worker #{task.Id}] finalize -> QUESTION: {Snip(q!, 160)}");
            return new PendingQuestion(q!, options);
        }
        catch (Exception ex)
        {
            Trace($"[worker #{task.Id}] finalize failed ({ex.Message}) — treating as finished");
            return null; // never let finalization swallow a real answer; relay verbatim
        }
    }

    // A short, human-readable line listing what the user attached, appended to their message so the model
    // knows files are in play.
    private static string AttachmentNote(IReadOnlyList<Attachment> attachments)
    {
        var sb = new StringBuilder();
        foreach (var a in attachments)
        {
            sb.Append($"\n\n[user uploaded file \"{a.Name}\"{(a.Size > 0 ? $" ({HumanSize(a.Size)})" : "")}]");
        }
        return sb.ToString();
    }

    private string FilesNote(Session session)
    {
        if (ThreadFilesDir(session) is not { } filesDir || !Directory.Exists(filesDir)) return "";
        try
        {
            var files = new DirectoryInfo(filesDir).GetFiles().OrderBy(f => f.Name).ToList();
            if (files.Count == 0) return "";
            var sb = new StringBuilder("\n\nFiles available in this conversation's files area (the 'pot' of files):\n");
            foreach (var f in files)
            {
                sb.Append($"- {f.Name} ({HumanSize(f.Length)})\n");
            }
            sb.Append("These files persist across turns. You can refer to them or delegate tasks using them without asking the user to re-upload.");
            return sb.ToString();
        }
        catch { return ""; }
    }

    private static string HumanSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };

    // Materialise a task's working directory: <root>/<session>/<taskId>/ with task.md (the brief) and a
    // files/ folder holding copies of the turn's attachments. Returns the absolute workspace path, or null
    // when no workspace root is configured. Best-effort — a copy failure is logged and skipped, never fatal.
    private static string SafeSession(string? id) =>
        string.Concat((id ?? "session").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // The CONVERSATION's file area: one folder per thread/session holding every file shared into, or produced
    // in, this conversation. The scoped file tools (write/list/send) are rooted here, so files persist across
    // the thread's tasks — you can re-send something shared three tasks ago — and never cross between threads.
    private string? ThreadFilesDir(Session session)
    {
        if (string.IsNullOrEmpty(_workspaceRoot)) return null;
        return Path.Combine(_workspaceRoot, SafeSession(session.Id), "files");
    }

    // File buckets: read-only areas mounted alongside a conversation's own files, so persistent assets live in
    // ONE managed place instead of being re-uploaded every thread. Two scopes:
    //  • global  — <root>/_buckets/global         (every worker, every thread)
    //  • persona — <root>/_buckets/persona/<id>   (only when that persona runs — e.g. the brand kit)
    // The dirs are created so a human can drop files in; an empty bucket simply contributes nothing.
    private string? GlobalBucketDir() =>
        string.IsNullOrEmpty(_workspaceRoot) ? null : Path.Combine(_workspaceRoot, "_buckets", "global");

    private string? PersonaBucketDir(string personaId) =>
        string.IsNullOrEmpty(_workspaceRoot) ? null
            : Path.Combine(_workspaceRoot, "_buckets", "persona", SafeSession(personaId));

    // Brand buckets: one named kit per brand this (agency) manages — the house brand plus one per client.
    // <root>/_buckets/brand/<slug>/ holds that brand's tokens, logo, assets and optional template. The set of
    // folders IS the registry of brands; creating a brand = promoting an approved tokenset into a new slug.
    private string? BrandsRootDir() =>
        string.IsNullOrEmpty(_workspaceRoot) ? null : Path.Combine(_workspaceRoot, "_buckets", "brand");

    private string? BrandBucketDir(string slug) =>
        BrandsRootDir() is { } root ? Path.Combine(root, SafeSession(slug)) : null;

    /// <summary>The brands currently managed (the slugs of existing brand buckets). The filesystem is the
    /// registry — no separate store to drift. "house" is implied even before its folder exists.</summary>
    private IReadOnlyList<string> BrandSlugs()
    {
        var slugs = new List<string> { "house" };
        if (BrandsRootDir() is { } root && Directory.Exists(root))
            foreach (var d in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(d);
                if (!string.IsNullOrEmpty(name) && !slugs.Contains(name, StringComparer.OrdinalIgnoreCase))
                    slugs.Add(name);
            }
        return slugs;
    }

    /// <summary>The read-only buckets a given task's worker can see: the global area always, plus a persona kit.
    /// For the branding designer the "kit" is the resolved BRAND bucket (house or a client), so it designs in the
    /// right brand. Dirs are ensured so they're discoverable; empty ones list nothing.</summary>
    private IReadOnlyList<FileTools.FileMount> BucketMounts(TaskInfo task)
    {
        var mounts = new List<FileTools.FileMount>();
        if (GlobalBucketDir() is { } g)
        {
            try { Directory.CreateDirectory(g); } catch { /* best-effort */ }
            mounts.Add(new FileTools.FileMount("Shared company files", g));
        }

        if (task.Persona == "branding_designer")
        {
            var slug = string.IsNullOrWhiteSpace(task.Brand) ? "house" : task.Brand!;
            if (BrandBucketDir(slug) is { } bdir)
            {
                try { Directory.CreateDirectory(bdir); } catch { /* best-effort */ }
                var label = string.Equals(slug, "house", StringComparison.OrdinalIgnoreCase)
                    ? "House brand kit" : $"{slug} brand kit";
                mounts.Add(new FileTools.FileMount(label, bdir));
            }
        }
        else if (task.Persona is { Length: > 0 } pid && PersonaBucketDir(pid) is { } pdir)
        {
            try { Directory.CreateDirectory(pdir); } catch { /* best-effort */ }
            var label = _personas?.Get(pid)?.Name is { Length: > 0 } n ? $"{n} kit" : $"{pid} kit";
            mounts.Add(new FileTools.FileMount(label, pdir));
        }
        return mounts;
    }

    private string? CreateWorkspace(Session session, TaskInfo task, IReadOnlyList<Attachment>? attachments)
    {
        if (string.IsNullOrEmpty(_workspaceRoot)) return null;
        try
        {
            string dir = Path.Combine(_workspaceRoot, SafeSession(session.Id), task.Id);
            Directory.CreateDirectory(dir);

            var brief = new StringBuilder($"# Task\n\n{task.Description}\n");
            // Attachments go to the CONVERSATION's file area (shared across the thread's tasks), not under this
            // one task — so a later task (or a scheduled one) can read or re-send them.
            if (attachments is { Count: > 0 } && ThreadFilesDir(session) is { } filesDir)
            {
                var copied = CopyAttachments(filesDir, attachments);
                if (copied.Count > 0)
                {
                    brief.Append("\n## Files provided (in this conversation's files area)\n\n");
                    foreach (var name in copied) brief.Append($"- {name}\n");
                }
            }
            File.WriteAllText(Path.Combine(dir, "task.md"), brief.ToString());
            Trace($"[workspace] #{task.Id} -> {dir}");
            return dir;
        }
        catch (Exception ex)
        {
            Trace($"[workspace] #{task.Id} failed: {ex.Message}");
            return null;
        }
    }

    // Copy attachments into a files/ directory, returning the (sanitised) file names actually copied.
    // Best-effort per file — a copy that fails is logged and skipped, not fatal.
    private List<string> CopyAttachments(string filesDir, IReadOnlyList<Attachment> attachments)
    {
        var copied = new List<string>();
        Directory.CreateDirectory(filesDir);
        foreach (var a in attachments)
        {
            string safeName = string.Concat(a.Name.Select(c =>
                Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            try
            {
                File.Copy(a.LocalPath, Path.Combine(filesDir, safeName), overwrite: true);
                copied.Add(safeName);
            }
            catch (Exception ex) { Trace($"[workspace] copy {a.Name} failed: {ex.Message}"); }
        }
        return copied;
    }

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
            "a clear, self-contained description of the work — the work is automatically routed to the right " +
            "specialist, and a job that spans several disciplines is planned and run as a sequence of them, so " +
            "you do NOT need to pick a specialist. Optionally tag it to a project (by slug) so the worker has " +
            "that project's context. Only set `persona` when the user explicitly names a role to use.",
            new[]
            {
                ToolParameter.String("task", "A clear, self-contained description of the work to do.", required: true),
                ToolParameter.String("project", "Optional project slug to run the task within.", required: false),
                ToolParameter.String("persona", "Optional — only when the user explicitly names a role. A specialist persona id (e.g. software_engineer, product_manager, data_scientist). Leave blank to auto-route.", required: false),
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
            "Send a follow-up to a background task by its id — to steer one that's running, answer one that's " +
            "waiting on a question, or RE-OPEN a finished (done/failed) one to refine, extend, or retry it. A " +
            "reopened task resumes from its own saved context (data, files, code it already produced) and just " +
            "adjusts, so prefer this over delegating a fresh task when the user iterates on a prior result.",
            new[]
            {
                ToolParameter.String("id", "The id of the running task to message.", required: true),
                ToolParameter.String("message", "The follow-up to pass along to the task.", required: true),
            },
            NoOp),
        new("promote_file",
            "Save a file from THIS conversation into a DURABLE bucket so it's available in every future " +
            "conversation (it otherwise lives only here). Use when the user wants to keep something as a reusable " +
            "asset — e.g. a brand's tokens, logo, or template. scope is one of: \"brand:<slug>\" to save into a " +
            "brand kit (use the client's slug, or \"house\" for your own brand; a NEW slug creates that brand — " +
            "this is how an approved branding set becomes a stored brand); \"global\" (shared with everyone); or " +
            "a specialist id. Only do this on a clear request to save/keep an asset; ordinary working files stay " +
            "in the conversation.",
            new[]
            {
                ToolParameter.String("name", "Name of the file in this conversation to save, as shown by list_files.", required: true),
                ToolParameter.String("scope", "Where to save it: \"brand:<slug>\" (e.g. brand:adidas, brand:house), \"global\", or a specialist id.", required: true),
            },
            NoOp),
        new("schedule_task",
            "Schedule something to happen LATER in THIS conversation — a reminder or an action at a future " +
            "time (\"remind me at 4pm\", \"send me the ticket 5 minutes before my 14:32 train\", \"check back " +
            "next week and note the decision\"). At that time you'll proactively act in this same thread. Give " +
            "`when` as an absolute LOCAL time in ISO form (e.g. 2026-06-26T14:27) — do any \"X before/after\" " +
            "arithmetic yourself — or a relative \"in N minutes/hours/days\". `task` is exactly what to do then, " +
            "written so it stands on its own; the live thread context is also available when it runs.",
            new[]
            {
                ToolParameter.String("when", "Absolute ISO local time (2026-06-26T14:27) or relative (\"in 25m\", \"in 2 hours\", \"in 3 days\").", required: true),
                ToolParameter.String("task", "What to do at that time, self-contained.", required: true),
            },
            NoOp),
        new("cancel_schedule",
            "Cancel a previously scheduled item by its id (as shown in the scheduled-for-later list).",
            new[] { ToolParameter.String("id", "The id of the scheduled item to cancel.", required: true) },
            NoOp),
    };

    private static Task<ToolOutput> NoOp(ToolCallArguments _, CancellationToken __) =>
        Task.FromResult(ToolOutput.Ok("ok"));

    private static bool TryExtractXmlToolCalls(string content, Dictionary<string, AgentTool> availableTools, out List<ToolCall> calls, out string cleaned)
    {
        calls = new List<ToolCall>();
        cleaned = content;
        
        if (string.IsNullOrWhiteSpace(content) || !content.Contains("<tool_call>"))
            return false;

        var removals = new List<(int start, int length)>();
        int index = 0;
        int searchStart = 0;
        while (true)
        {
            int startIdx = content.IndexOf("<tool_call>", searchStart);
            if (startIdx < 0) break;
            
            int endIdx = content.IndexOf("</tool_call>", startIdx);
            if (endIdx < 0)
            {
                endIdx = content.Length;
            }
            else
            {
                endIdx += "</tool_call>".Length;
            }

            int innerStart = startIdx + "<tool_call>".Length;
            int innerLength = (endIdx == content.Length) ? (content.Length - innerStart) : (endIdx - "</tool_call>".Length - innerStart);
            if (innerLength < 0) innerLength = 0;
            string inner = content.Substring(innerStart, innerLength).Trim();
            
            if (TryParseXmlToolCall(inner, availableTools, index++, out var toolCall))
            {
                calls.Add(toolCall);
                removals.Add((startIdx, endIdx - startIdx));
            }
            
            searchStart = endIdx;
            if (searchStart >= content.Length) break;
        }

        if (calls.Count == 0)
            return false;

        foreach (var (start, length) in removals.OrderByDescending(r => r.start))
        {
            cleaned = cleaned.Remove(start, length);
        }
        
        cleaned = Regex.Replace(cleaned, @"```[a-zA-Z]*\s*```", "").Trim();
        return true;
    }

    private static bool TryParseXmlToolCall(string inner, Dictionary<string, AgentTool> availableTools, int index, out ToolCall toolCall)
    {
        toolCall = default;
        
        int firstBracket = inner.IndexOf('<');
        string toolName = firstBracket >= 0 ? inner.Substring(0, firstBracket).Trim() : inner.Trim();
        
        if (string.IsNullOrEmpty(toolName) || !availableTools.ContainsKey(toolName))
            return false;

        var argsObj = new JsonObject();
        int pos = firstBracket;
        while (pos >= 0 && pos < inner.Length)
        {
            int keyStart = inner.IndexOf("<arg_key>", pos);
            if (keyStart < 0) break;
            
            int keyEnd = inner.IndexOf("</arg_key>", keyStart);
            string keyName;
            string valStr = "";
            int nextPos = -1;

            if (keyEnd >= 0)
            {
                keyName = inner.Substring(keyStart + "<arg_key>".Length, keyEnd - (keyStart + "<arg_key>".Length)).Trim();
                
                int valStart = inner.IndexOf("<arg_value>", keyEnd);
                if (valStart >= 0)
                {
                    int valEnd = inner.IndexOf("</arg_value>", valStart);
                    if (valEnd >= 0)
                    {
                        valStr = inner.Substring(valStart + "<arg_value>".Length, valEnd - (valStart + "<arg_value>".Length));
                        nextPos = valEnd + "</arg_value>".Length;
                    }
                    else
                    {
                        valStr = inner.Substring(valStart + "<arg_value>".Length);
                        nextPos = inner.Length;
                    }
                }
                else
                {
                    nextPos = keyEnd + "</arg_key>".Length;
                }
            }
            else
            {
                int valEnd = inner.IndexOf("</arg_value>", keyStart);
                string segment;
                if (valEnd >= 0)
                {
                    segment = inner.Substring(keyStart + "<arg_key>".Length, valEnd - (keyStart + "<arg_key>".Length));
                    nextPos = valEnd + "</arg_value>".Length;
                }
                else
                {
                    segment = inner.Substring(keyStart + "<arg_key>".Length);
                    nextPos = inner.Length;
                }

                int arrowIdx = segment.IndexOf('→');
                if (arrowIdx >= 0)
                {
                    keyName = segment.Substring(0, arrowIdx).Trim();
                    valStr = segment.Substring(arrowIdx + 1);
                }
                else
                {
                    keyName = segment.Trim();
                }
            }

            if (!string.IsNullOrEmpty(keyName))
            {
                argsObj[keyName] = valStr;
            }
            
            pos = nextPos;
        }

        using var doc = JsonDocument.Parse(argsObj.ToJsonString());
        toolCall = new ToolCall($"call_xml_{index}", toolName, doc.RootElement.Clone());
        return true;
    }

    private sealed class XmlStreamFilter
    {
        private bool _inToolCall = false;
        private readonly System.Text.StringBuilder _buffer = new();

        public string Feed(string chunk)
        {
            var output = new System.Text.StringBuilder();
            foreach (char c in chunk)
            {
                if (!_inToolCall)
                {
                    if (_buffer.Length == 0)
                    {
                        if (c == '<')
                        {
                            _buffer.Append(c);
                        }
                        else
                        {
                            output.Append(c);
                        }
                    }
                    else
                    {
                        _buffer.Append(c);
                        string bufStr = _buffer.ToString();
                        const string target = "<tool_call>";
                        if (target.StartsWith(bufStr))
                        {
                            if (bufStr == target)
                            {
                                _inToolCall = true;
                                _buffer.Clear();
                            }
                        }
                        else
                        {
                            output.Append(bufStr);
                            _buffer.Clear();
                        }
                    }
                }
                else
                {
                    if (_buffer.Length == 0)
                    {
                        if (c == '<')
                        {
                            _buffer.Append(c);
                        }
                    }
                    else
                    {
                        _buffer.Append(c);
                        string bufStr = _buffer.ToString();
                        const string target = "</tool_call>";
                        if (target.StartsWith(bufStr))
                        {
                            if (bufStr == target)
                            {
                                _inToolCall = false;
                                _buffer.Clear();
                            }
                        }
                        else
                        {
                            _buffer.Clear();
                            if (c == '<')
                            {
                                _buffer.Append(c);
                            }
                        }
                    }
                }
            }
            return output.ToString();
        }

        public string Flush()
        {
            if (!_inToolCall && _buffer.Length > 0)
            {
                string remaining = _buffer.ToString();
                _buffer.Clear();
                return remaining;
            }
            _buffer.Clear();
            return "";
        }
    }
}
