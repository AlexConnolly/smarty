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

    // Bound the per-turn tool loop: a turn may call a data tool (list/status), read the result, then
    // voice it — but it must not spin.
    private const int MaxTurnIterations = 4;

    // Proactive status: once a task has been running this long with the user quiet, drop a brief "still
    // on it" line; repeat at this cadence. Stay silent if the user spoke within the quiet window.
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(45);

    public Orchestrator(string model, string ollamaBaseUrl, Func<string> workerSystem, JsonSerializerOptions json)
    {
        _provider = new OllamaModelProvider(ollamaBaseUrl);
        _model = model;
        _ollamaBaseUrl = ollamaBaseUrl;
        _workerSystem = workerSystem;
        _json = json;
    }

    private const string OrchestratorSystem =
        "You are the user's personal assistant and their single point of contact. Your goal is simple: " +
        "talk with the user, and help them get things done by running tasks. Be warm, natural, and brief — " +
        "like a sharp human assistant, not a chatbot.\n" +
        "\n" +
        "You don't do the work yourself. Capable background workers do — you hand them tasks and relay what " +
        "they find back in your own voice. The user never sees the workers; to them, it's all just you.\n" +
        "\n" +
        "TOOLS — these are how you actually make things happen. Calling a tool is the ONLY way to do the " +
        "thing; describing it in words does nothing. Never tell the user you've started, changed, checked, " +
        "or cancelled something unless you have called its tool in THIS reply.\n" +
        "- delegate(task): start a NEW background task. Give a clear, self-contained description. Use this " +
        "for anything that needs an action or live/real data.\n" +
        "- message_task(id, message): send a note to a task that is ALREADY running — to add detail, change " +
        "its scope, or steer it.\n" +
        "- cancel_task(id): stop a running task.\n" +
        "- list_tasks(): see what's running. task_status(id): check how one task is going.\n" +
        "\n" +
        "HOW TO DECIDE:\n" +
        "- Small talk, greetings, jokes, opinions, or things you already know — just answer. No tools.\n" +
        "- Something that needs real work or live data — call delegate, and say one short line that you're " +
        "on it.\n" +
        "- A message with BOTH (e.g. \"tell me a joke and also check my disk\") — do both in the same reply: " +
        "answer the easy part in your words AND call delegate for the work part. Don't drop either.\n" +
        "- The user refines or changes something you're ALREADY doing (\"actually make it X\", \"also include " +
        "Y\", \"wait — C and D, not just C\") — do NOT start a second task. Call message_task on the running " +
        "task with the change.\n" +
        "- The user backs off something in progress (\"actually, don't worry\", \"never mind\", \"forget it\", " +
        "\"stop\", \"cancel that\", \"leave it\") — call cancel_task on that task.\n" +
        "\n" +
        "You are always shown the tasks currently running, with their ids — use those ids. Always reply in " +
        "English. Keep it human and concise.";

    /// <summary>Handle a user message: echo it, run the orchestrator turn, dispatch any delegated work.</summary>
    public async Task HandleMessageAsync(Session session, string userText, CancellationToken ct)
    {
        await session.TurnLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            session.LastUserMessageAt = DateTimeOffset.UtcNow;
            EmitMessage(session, "user", userText);
            session.History.Add(Message.User(userText));

            // The user-facing turn is where the real decisions live (which tools, answer-and-delegate,
            // refine-vs-new, cancel). Let the model think here so it gets them right; re-voice/status
            // turns stay think:false for speed.
            string content = await RunOrchestratorTurnAsync(
                session, session.History, OrchestratorTools, think: true, ct).ConfigureAwait(false);

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

        int msgId = session.NextMessageId();
        session.Append("msg_start", Json(new { id = msgId, role = "assistant" }));

        var spoken = new StringBuilder();
        bool delegatedSomething = false;

        for (int iter = 0; iter < MaxTurnIterations; iter++)
        {
            var request = new ModelRequest
            {
                Model = _model,
                SystemPrompt = OrchestratorSystem + RunningTasksNote(session),
                Messages = convo,
                Tools = tools,
                RepeatPenalty = 1.0,
                MaxOutputTokens = 4096,
                TurnTimeout = TimeSpan.FromSeconds(90),
                Think = think, // user turns think (right tool choices); re-voice/status don't (speed)
            };

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

            var calls = final.ToolCalls;
            convo.Add(Message.Assistant(turnText.ToString(), final.Reasoning, calls.Count > 0 ? calls : null));

            if (calls.Count == 0)
                break;

            bool needAnotherTurn = false;
            foreach (var call in calls)
            {
                string result = HandleToolCall(session, call, out bool dataReturning, out bool wasDelegate);
                delegatedSomething |= wasDelegate;
                needAnotherTurn |= dataReturning;
                convo.Add(Message.ToolResult(call.Id, call.Name, result));
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

        session.Append("msg_end", Json(new { id = msgId }));
        return spoken.ToString();
    }

    /// <summary>Execute one orchestrator tool call against the session's task registry. Returns the text
    /// result; <paramref name="dataReturning"/> is true when the model should voice that result.</summary>
    private string HandleToolCall(Session session, ToolCall call, out bool dataReturning, out bool wasDelegate)
    {
        dataReturning = false;
        wasDelegate = false;

        switch (call.Name.ToLowerInvariant())
        {
            case "delegate":
            {
                var desc = call.Arguments.GetStringOrNull("task");
                if (string.IsNullOrWhiteSpace(desc))
                {
                    dataReturning = true;
                    return "No task description was provided — ask the user what they'd like done.";
                }
                wasDelegate = true;
                var task = StartTask(session, desc!.Trim());
                return $"Task #{task.Id} started in the background; its result will come back to you to relay.";
            }

            case "list_tasks":
            {
                dataReturning = true;
                var all = OrderedTasks(session);
                if (all.Count == 0) return "No tasks have been started yet.";
                return string.Join("\n", all.Select(t => $"#{t.Id} [{t.Status}] {t.Description}"));
            }

            case "task_status":
            {
                dataReturning = true;
                if (!TryResolve(session, call, out var t, out var miss)) return miss;
                var age = (DateTimeOffset.UtcNow - t.StartedAt).TotalSeconds;
                var progress = string.IsNullOrWhiteSpace(t.LatestThought) ? "(no detail yet)" : t.LatestThought;
                var line = $"#{t.Id} [{t.Status}] {t.Description}\nRunning {age:F0}s. Latest: {progress}";
                if (t.Status == "done" && !string.IsNullOrWhiteSpace(t.Result))
                    line += $"\nResult: {t.Result}";
                return line;
            }

            case "cancel_task":
            {
                dataReturning = true;
                if (!TryResolve(session, call, out var t, out var miss)) return miss;
                if (!t.IsRunning) return $"Task #{t.Id} is already {t.Status}.";
                t.Status = "cancelled";
                t.Cts.Cancel();
                return $"Task #{t.Id} ({t.Description}) has been cancelled.";
            }

            case "message_task":
            {
                dataReturning = true;
                if (!TryResolve(session, call, out var t, out var miss)) return miss;
                if (!t.IsRunning) return $"Task #{t.Id} is already {t.Status}; it can't take a message.";
                var msg = call.Arguments.GetStringOrNull("message");
                if (string.IsNullOrWhiteSpace(msg)) return "No message text was provided.";
                t.Inbox.Enqueue(msg!.Trim());
                return $"Message passed along to task #{t.Id}; it'll pick it up shortly.";
            }

            default:
                dataReturning = true;
                return $"Unknown tool '{call.Name}'.";
        }
    }

    /// <summary>Register a delegated task, announce it, and spawn its worker + status monitor.</summary>
    private TaskInfo StartTask(Session session, string description)
    {
        var task = new TaskInfo { Id = session.NextTaskId(), Description = description };
        session.Tasks[task.Id] = task;
        session.Append("working", Json(new { id = task.Id, task = description }));
        _ = Task.Run(() => RunWorkerAsync(session, task));
        _ = Task.Run(() => MonitorTaskAsync(session, task));
        return task;
    }

    /// <summary>Run a delegated task on a background worker, tracking its progress and any mid-flight
    /// steering, then re-voice the result (unless it was cancelled, which is acknowledged elsewhere).</summary>
    private async Task RunWorkerAsync(Session session, TaskInfo task)
    {
        string result = "(no result)";
        bool cancelled = false;
        try
        {
            var input = new AgentInput
            {
                SystemPrompt = _workerSystem(),
                Model = ModelSpec.Ollama(_model, _ollamaBaseUrl),
                Tools = { ShellTool.Create() },
                DrainInbox = () => DrainInbox(task),
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
                        break;
                    case AgentEvent.Completed done:
                        result = done.Answer;
                        break;
                }
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
                    "just give them the answer conversationally."),
            };

            string content = await RunOrchestratorTurnAsync(
                session, convo, Array.Empty<AgentTool>(), think: false, CancellationToken.None).ConfigureAwait(false);

            session.History.Add(Message.Assistant(string.IsNullOrWhiteSpace(content) ? result : content));
        }
        finally
        {
            session.TurnLock.Release();
        }
    }

    /// <summary>Watch a running task; if it runs long while the user is quiet, push a brief status line.</summary>
    private async Task MonitorTaskAsync(Session session, TaskInfo task)
    {
        while (task.IsRunning)
        {
            try
            {
                await Task.Delay(StatusInterval, task.Cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // task finished or was cancelled
            }

            if (!task.IsRunning) return;

            var now = DateTimeOffset.UtcNow;
            if (now - session.LastUserMessageAt < QuietWindow) continue; // don't interrupt an active chat
            if (now - task.LastStatusAt < StatusInterval) continue;       // don't nag

            await PushStatusUpdateAsync(session, task).ConfigureAwait(false);
        }
    }

    /// <summary>Peek the worker's latest progress and voice a short "still on it" line — if nothing else
    /// is mid-turn and the user is still quiet.</summary>
    private async Task PushStatusUpdateAsync(Session session, TaskInfo task)
    {
        // Don't queue behind an in-flight turn; if the chat is busy, skip and try again next tick.
        if (!await session.TurnLock.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (!task.IsRunning) return;
            if (DateTimeOffset.UtcNow - session.LastUserMessageAt < QuietWindow) return;

            task.LastStatusAt = DateTimeOffset.UtcNow;
            var peek = string.IsNullOrWhiteSpace(task.LatestThought)
                ? "still working on it, nothing's gone wrong so far"
                : task.LatestThought;

            var convo = new List<Message>(session.History)
            {
                Message.System(
                    $"You're still working on something for the user (\"{task.Description}\") and it's taking a " +
                    $"little while. Here's where it's at: {peek}. Send the user ONE short, friendly line so they " +
                    "know you're still on it. Don't mention tasks, workers, or internals, and don't ask a question."),
            };

            string content = await RunOrchestratorTurnAsync(
                session, convo, Array.Empty<AgentTool>(), think: false, CancellationToken.None).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(content))
                session.History.Add(Message.Assistant(content));
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
        session.Append("msg_end", Json(new { id }));
    }

    // The orchestrator's tools. Schemas only — calls are read off the response and handled against the
    // session's task registry in HandleToolCall, so the executor bodies are inert.
    private static readonly AgentTool[] OrchestratorTools =
    {
        new("delegate",
            "Hand a task to a capable background worker that has real tools (shell, internet, files). Provide " +
            "a clear, self-contained description of the work.",
            new[] { ToolParameter.String("task", "A clear, self-contained description of the work to do.", required: true) },
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
