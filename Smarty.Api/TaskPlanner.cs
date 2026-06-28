using System.Text.Json;
using System.Text.Json.Nodes;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// The planning layer for delegated work. Before a worker runs, a cheap GATE sizes up the task: a quick
/// lookup or one-off action runs straight away (no overhead), but a complex, multi-step or long-horizon job
/// is handed to a PLANNER first — a focused agent (with read-only recon tools) that scopes the unknowns and
/// produces a concrete, ordered plan. That plan is seeded into the full-tooled executor, which carries it
/// out. Simple things stay snappy; hard, long-horizon tasks get the structure they need to not wander.
/// </summary>
public sealed class TaskPlanner
{
    private readonly IModelProvider _provider;
    private readonly ModelSpec _modelSpec;
    private readonly Func<IReadOnlyList<AgentTool>>? _reconTools;
    private readonly string _model;
    private readonly ModelProviderRegistry _registry;

    public TaskPlanner(ModelSpec modelSpec, ModelProviderRegistry? registry = null, Func<IReadOnlyList<AgentTool>>? reconTools = null)
    {
        _modelSpec = modelSpec;
        _registry = registry ?? ModelProviderRegistry.Default;
        _provider = _registry.Resolve(modelSpec);
        _model = modelSpec.Model;
        _reconTools = reconTools;
    }

    public TaskPlanner(string model, string ollamaBaseUrl, Func<IReadOnlyList<AgentTool>>? reconTools = null)
        : this(ResolveModelSpec(model, ollamaBaseUrl), null, reconTools)
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

    private static JsonNode ComplexitySchema() => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["complexity"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("simple", "complex") },
            ["reason"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("complexity"),
    };

    /// <summary>The gate: is this task complex enough to warrant a plan? Cheap, fast, schema-forced (so the
    /// verdict is a real field, not parsed from prose). On any error it returns false — treat as simple — so
    /// planning can never block getting on with the work.</summary>
    public async Task<bool> IsComplexAsync(string task, CancellationToken ct)
    {
        try
        {
            var convo = new List<Message>
            {
                Message.User(
                    "Classify how much planning this task needs.\n" +
                    "- \"simple\": one lookup or a quick action a single pass can finish (look up / check one thing, " +
                    "a short answer, a single booking).\n" +
                    "- \"complex\": multi-step or long-horizon work that benefits from a plan first — diagnosing or " +
                    "fixing a problem, building or organising something, comparing across many sources, anything " +
                    "with several dependent steps.\n\nTask:\n" + task),
            };
            var request = new ModelRequest
            {
                Model = _model,
                Messages = convo,
                Think = false,
                ResponseFormat = ComplexitySchema(),
                MaxOutputTokens = 80,
                TurnTimeout = TimeSpan.FromSeconds(25),
            };
            var response = await ((IModelProvider)_provider).CompleteAsync(request, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.Content)) return false;
            using var doc = JsonDocument.Parse(response.Content);
            return doc.RootElement.TryGetProperty("complexity", out var c)
                   && string.Equals(c.GetString(), "complex", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private const string PlannerSystem =
        "You are the PLANNING step for a capable executor agent that will carry out a task afterwards with full " +
        "tools. Your job is to produce a clear, concrete, ordered PLAN — NOT to do the task or write the final " +
        "deliverable.\n" +
        "If you have recon tools (web_search / get_page_answer), you MAY use a couple of quick lookups ONLY to " +
        "scope unknowns you need in order to plan well — do not carry out the task itself. Then output the plan as " +
        "3–7 short, concrete numbered steps the executor should follow, ending with a single \"Done when: …\" line " +
        "that defines success. Be specific and lean — no preamble, no fluff.";

    /// <summary>Produce a plan for a complex task. Runs a focused agent (with any recon tools) whose final
    /// answer IS the plan. Returns null if it couldn't produce one — the executor then just proceeds unplanned,
    /// exactly as today.</summary>
    public async Task<string?> PlanAsync(string task, CancellationToken ct)
    {
        try
        {
            var tools = _reconTools?.Invoke() ?? Array.Empty<AgentTool>();
            var input = new AgentInput
            {
                SystemPrompt = PlannerSystem,
                Model = _modelSpec,
                Tools = tools.ToList(),
                Think = true,
                MaxIterations = tools.Count > 0 ? 5 : 2, // room for a little recon, never the whole job
            };
            var plan = await new SmartyAgent(input, _registry).Answer(
                "Plan this task (do not carry it out):\n" + task, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(plan) ? null : plan.Trim();
        }
        catch { return null; }
    }
}
