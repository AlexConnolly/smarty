using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// A supervisor that sits ON TOP of a running worker and steps in when it's thrashing — most often a
/// "relentless search" that keeps hitting blocked/failing sources and never concludes. It can't reach inside
/// a single generation (that's the per-turn timeout's job), but it watches the worker's LIVE transcript
/// between steps and, when cheap signals say something's off, makes ONE go/no-go judgement:
///   • continue — it's actually making progress, leave it.
///   • wrap_up  — it has enough or keeps failing; nudge it to STOP tool-calling and conclude with what it has.
///   • abort    — the task can't be done right now; stop it cleanly.
/// The nudge rides the worker's existing inbox (picked up at the next step); abort cancels the run.
/// </summary>
public sealed class TaskWatchdog
{
    private readonly IModelProvider _provider;
    private readonly ModelSpec _modelSpec;
    private readonly TimeSpan _checkEvery;
    private readonly string _model;

    public TaskWatchdog(ModelSpec modelSpec, ModelProviderRegistry? registry = null, TimeSpan? checkEvery = null)
    {
        _modelSpec = modelSpec;
        _provider = (registry ?? ModelProviderRegistry.Default).Resolve(modelSpec);
        _model = modelSpec.Model;
        _checkEvery = checkEvery ?? TimeSpan.FromSeconds(25);
    }

    public TaskWatchdog(string model, string ollamaBaseUrl, TimeSpan? checkEvery = null)
        : this(ResolveModelSpec(model, ollamaBaseUrl), null, checkEvery)
    {
    }

    private static ModelSpec ResolveModelSpec(string model, string ollamaBaseUrl)
    {
        string apiKey = Environment.GetEnvironmentVariable("TOGETHER_API_KEY") 
            ?? Environment.GetEnvironmentVariable("OLLAMA_API_KEY") 
            ?? Environment.GetEnvironmentVariable("SMARTY_API_KEY") ?? "";
        string? togetherBaseUrl = (ollamaBaseUrl.Contains("localhost") || ollamaBaseUrl.Contains("127.0.0.1")) ? null : ollamaBaseUrl;
        return model.Contains("/") || (ollamaBaseUrl != null && ollamaBaseUrl.Contains("together"))
            ? new ModelSpec("together", model, togetherBaseUrl)
            : ModelSpec.Ollama(model, ollamaBaseUrl);
    }

    // Failure markers our tools emit (bot-walls, fetch errors, empty results). Used for the cheap trip signal.
    private static readonly string[] FailureMarkers =
    {
        "blocks automated access", "could not fetch", "rate-limit", "bot-check", "no results",
        "dead end", "try a different", "couldn't", "not retrievable", "no readable text",
    };

    private static bool LooksFailed(string? s) =>
        s is not null && FailureMarkers.Any(m => s.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>Watch a running task until it stops (or <paramref name="ct"/> fires). Best-effort: any fault
    /// in here must never affect the worker.</summary>
    public async Task MonitorAsync(TaskInfo task, CancellationToken ct, Action<string>? trace = null)
    {
        int lastAssessedTools = 0;
        try
        {
            while (task.Status == "running")
            {
                try { await Task.Delay(_checkEvery, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (task.Status != "running") return;

                // Snapshot the live transcript (the worker appends to it as it runs). A concurrent append can
                // make the copy throw — just skip this round if so.
                List<Message> convo;
                try { convo = task.Conversation.ToList(); }
                catch { continue; }

                int toolCalls = convo.Count(m => m.Role == Role.Tool);
                int failures = convo.Count(m => m.Role == Role.Tool && LooksFailed(m.Content));
                double elapsed = (DateTimeOffset.UtcNow - task.StartedAt).TotalSeconds;

                // Cheap gate: only spend a model call when there's been real new tool activity since the last
                // look AND it's either failing, doing a LOT of calls (a relentless going-nowhere loop — even if
                // each call "succeeds" with junk), or just running very long.
                bool worthAssessing = elapsed > 45 && toolCalls >= 5 && toolCalls > lastAssessedTools
                                      && (failures >= 2 || toolCalls >= 8 || elapsed > 120);
                if (!worthAssessing) continue;
                lastAssessedTools = toolCalls;

                trace?.Invoke($"[watchdog #{task.Id}] assessing: {toolCalls} tool calls, {failures} failed, {elapsed:F0}s");
                var (action, note) = await AssessAsync(task, convo, ct).ConfigureAwait(false);
                if (action is null || task.Status != "running") continue;

                switch (action)
                {
                    case "wrap_up":
                        task.Inbox.Enqueue(
                            "[supervisor] " + (string.IsNullOrWhiteSpace(note)
                                ? "You've done enough and keep hitting blocks. STOP calling tools now — give your " +
                                  "answer from what you've ALREADY found, or say plainly you couldn't get it. Do not search again."
                                : note!));
                        trace?.Invoke($"[watchdog #{task.Id}] WRAP-UP nudge: {note}");
                        break;
                    case "abort":
                        trace?.Invoke($"[watchdog #{task.Id}] ABORT: {note}");
                        task.Status = "failed";
                        task.Cts.Cancel();
                        return;
                    default:
                        trace?.Invoke($"[watchdog #{task.Id}] continue ({note})");
                        break;
                }
            }
        }
        catch { /* supervision is best-effort — never let it disturb the worker */ }
    }

    private static JsonNode Schema() => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("continue", "wrap_up", "abort") },
            ["note"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("action"),
    };

    private async Task<(string? Action, string? Note)> AssessAsync(TaskInfo task, List<Message> convo, CancellationToken ct)
    {
        try
        {
            // Summarise the recent tool activity — names + short result snippets, flagging the failures. That's
            // what tells "making progress" from "relentlessly retrying dead ends".
            var sb = new StringBuilder();
            sb.Append("A background worker is doing this task:\n").Append(task.Description).Append("\n\n");
            sb.Append("Its recent tool calls and results (newest last):\n");
            foreach (var m in convo.Where(m => m.Role == Role.Tool).TakeLast(10))
            {
                var res = (m.Content ?? "").Replace("\n", " ");
                if (res.Length > 160) res = res[..160] + "…";
                sb.Append(LooksFailed(m.Content) ? "- [FAILED] " : "- [ok] ").Append(m.ToolName).Append(": ").Append(res).Append('\n');
            }
            sb.Append("\nDecide what should happen now:\n" +
                      "- \"continue\": it's genuinely making progress toward the task — leave it running.\n" +
                      "- \"wrap_up\": it already has enough to answer, OR it keeps hitting blocked/failing sources and " +
                      "more tries won't help — it should STOP calling tools and conclude with what it has. Put the " +
                      "instruction to give the worker in \"note\".\n" +
                      "- \"abort\": the task simply can't be completed right now — stop it. Briefly say why in \"note\".\n" +
                      "Bias towards letting real progress continue; only wrap_up/abort when it's clearly stuck or done.");

            var request = new ModelRequest
            {
                Model = _modelSpec.Model,
                Messages = new[] { Message.User(sb.ToString()) },
                Think = false,
                ResponseFormat = Schema(),
                MaxOutputTokens = 200,
                TurnTimeout = TimeSpan.FromSeconds(30),
            };
            var response = await ((IModelProvider)_provider).CompleteAsync(request, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.Content)) return (null, null);

            using var doc = JsonDocument.Parse(response.Content);
            var root = doc.RootElement;
            var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
            var note = root.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            return (action, note);
        }
        catch { return (null, null); }
    }
}
