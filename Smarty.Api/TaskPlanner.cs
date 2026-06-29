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

    // ── Multi-discipline routing ────────────────────────────────────────────────────────────────────────
    // The gate above decides "does this need a plan?" for a SINGLE worker. The methods below answer a
    // different question: does the task span more than one DISCIPLINE (data → product → engineering → review)
    // that no single persona can cover? Triage is a quick ballpark — name the disciplines, in order. Only when
    // it finds more than one do we pay for a step breakdown. Everything fails open to "no plan" so routing can
    // never block the work.

    private static JsonNode StringArraySchema(string prop) => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            [prop] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        },
        ["required"] = new JsonArray(prop),
    };

    /// <summary>Quick gate: which specialist personas does this task need, IN ORDER? Most tasks return one (or
    /// none) — only a genuine cross-discipline job returns several. Cheap and schema-forced; returns an empty
    /// list on any error so the caller falls back to the ordinary single-worker path.</summary>
    public async Task<IReadOnlyList<string>> TriageDisciplinesAsync(string task, string roster, CancellationToken ct)
    {
        try
        {
            var convo = new List<Message>
            {
                Message.User(
                    "Route a task to specialist disciplines. From the roster, list the persona ids this task " +
                    "needs, IN THE ORDER they must run. Most tasks need exactly ONE (or none — leave empty for " +
                    "general work). Return MORE THAN ONE only when the task genuinely spans different disciplines " +
                    "that hand off to each other (e.g. analyse data, THEN write tickets, THEN change code). Do " +
                    "not pad — fewer is better.\n\nRoster:\n" + roster + "\n\nTask:\n" + task),
            };
            var request = new ModelRequest
            {
                Model = _model,
                Messages = convo,
                Think = false,
                ResponseFormat = StringArraySchema("personas"),
                MaxOutputTokens = 120,
                TurnTimeout = TimeSpan.FromSeconds(25),
            };
            var response = await _provider.CompleteAsync(request, ct).ConfigureAwait(false);
            return ParseStringArray(response.Content, "personas");
        }
        catch { return Array.Empty<string>(); }
    }

    private static JsonNode StepsSchema() => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["steps"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["persona"] = new JsonObject { ["type"] = "string" },
                        ["instruction"] = new JsonObject { ["type"] = "string" },
                        ["produces"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("persona", "instruction", "produces"),
                },
            },
        },
        ["required"] = new JsonArray("steps"),
    };

    /// <summary>Break a cross-discipline task into an ordered list of single-persona steps. Each step names the
    /// persona, what it must do (self-contained — it can read files earlier steps left in the shared area), and
    /// the concrete artifact it hands on. Returns null if it couldn't produce a usable plan.</summary>
    public async Task<WorkPlan?> PlanStepsAsync(string task, string roster, CancellationToken ct)
    {
        try
        {
            var convo = new List<Message>
            {
                Message.User(
                    "Break this task into an ordered list of steps, each handled by ONE persona from the roster. " +
                    "Keep it minimal — roughly one step per discipline hand-off, in dependency order. For each " +
                    "step give: persona (an id from the roster), instruction (what that specialist must do, " +
                    "self-contained; note it can use files earlier steps saved to the shared files area), " +
                    "produces (the concrete artifact or output it must hand to the next step). No preamble.\n\n" +
                    "Roster:\n" + roster + "\n\nTask:\n" + task),
            };
            var request = new ModelRequest
            {
                Model = _model,
                Messages = convo,
                Think = true,
                ResponseFormat = StepsSchema(),
                MaxOutputTokens = 900,
                TurnTimeout = TimeSpan.FromSeconds(45),
            };
            var response = await _provider.CompleteAsync(request, ct).ConfigureAwait(false);
            return ParsePlan(response.Content, task);
        }
        catch { return null; }
    }

    private static JsonNode VerifySchema() => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["ok"] = new JsonObject { ["type"] = "boolean" },
            ["reason"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("ok"),
    };

    /// <summary>Did a step actually produce what it was meant to? Lenient about format, strict about substance —
    /// the guard against a worker that claims success but handed on nothing usable. An empty result fails for
    /// free (no model call). The verifier never blocks progress on its OWN failure — it returns ok=true so a
    /// flaky verifier can't stall a plan.</summary>
    public async Task<(bool Ok, string Reason)> VerifyStepAsync(string instruction, string produces, string result, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(result) || string.Equals(result.Trim(), "(no result)", StringComparison.OrdinalIgnoreCase))
            return (false, "the step produced no usable result");
        try
        {
            var convo = new List<Message>
            {
                Message.User(
                    "A step in a plan has finished. Decide if its output satisfies what the step was meant to " +
                    "produce. Be lenient about format, strict about substance: only set ok=false if it clearly " +
                    "did NOT do the work or produced nothing the next step could use, and give a one-line reason.\n\n" +
                    "Step: " + instruction + "\nExpected output: " + produces + "\n\nActual output:\n" + result),
            };
            var request = new ModelRequest
            {
                Model = _model,
                Messages = convo,
                Think = false,
                ResponseFormat = VerifySchema(),
                MaxOutputTokens = 120,
                TurnTimeout = TimeSpan.FromSeconds(25),
            };
            var response = await _provider.CompleteAsync(request, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.Content)) return (true, "");
            using var doc = JsonDocument.Parse(response.Content);
            bool ok = !doc.RootElement.TryGetProperty("ok", out var o) || o.ValueKind != JsonValueKind.False;
            string reason = doc.RootElement.TryGetProperty("reason", out var r) ? (r.GetString() ?? "") : "";
            return (ok, reason);
        }
        catch { return (true, ""); }
    }

    private static JsonNode RouteSchema() => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["step"] = new JsonObject { ["type"] = "integer" },   // 1-based existing step, or 0 to append a new one
            ["persona"] = new JsonObject { ["type"] = "string" }, // when step == 0: the persona for the new step
        },
        ["required"] = new JsonArray("step"),
    };

    /// <summary>Route a user refinement onto a running/finished plan: which step does it touch? Returns a
    /// 0-based step index to re-enter at, or (-1, persona) to append a brand-new step. Defaults to the LAST
    /// step on any uncertainty (the most recent thing the user saw).</summary>
    public async Task<(int StepIndex, string? NewPersona)> RouteRefineAsync(WorkPlan plan, string roster, string message, CancellationToken ct)
    {
        int last = plan.Steps.Count - 1;
        try
        {
            var sb = new System.Text.StringBuilder("Plan steps:\n");
            for (int i = 0; i < plan.Steps.Count; i++)
                sb.Append($"{i + 1}. [{plan.Steps[i].Persona}] {plan.Steps[i].Instruction}\n");
            var convo = new List<Message>
            {
                Message.User(
                    "A plan has already run. The user now asks for a change. Decide where it belongs:\n" +
                    "- If it adjusts what an EXISTING step produced, return that step's number (1-based).\n" +
                    "- If it is genuinely new work needing a different discipline, return step = 0 and the " +
                    "persona id (from the roster) that should do it as a new appended step.\n\n" +
                    sb + "\nRoster:\n" + roster + "\n\nUser's change:\n" + message),
            };
            var request = new ModelRequest
            {
                Model = _model,
                Messages = convo,
                Think = false,
                ResponseFormat = RouteSchema(),
                MaxOutputTokens = 80,
                TurnTimeout = TimeSpan.FromSeconds(25),
            };
            var response = await _provider.CompleteAsync(request, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.Content)) return (last, null);
            using var doc = JsonDocument.Parse(response.Content);
            int step = doc.RootElement.TryGetProperty("step", out var s) && s.TryGetInt32(out var n) ? n : last + 1;
            if (step <= 0)
            {
                string? persona = doc.RootElement.TryGetProperty("persona", out var p) ? p.GetString() : null;
                return (-1, string.IsNullOrWhiteSpace(persona) ? null : persona!.Trim());
            }
            int idx = step - 1;
            return (idx >= 0 && idx < plan.Steps.Count ? idx : last, null);
        }
        catch { return (last, null); }
    }

    private static JsonNode BrandSchema() => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["brand"] = new JsonObject { ["type"] = "string" } },
        ["required"] = new JsonArray("brand"),
    };

    /// <summary>For branding work at a brand AGENCY: which client brand is this task for? Given the known brand
    /// slugs and the task, return the slug to use, or "house" for the agency's own brand / when none is named.
    /// Resolves from context; defaults to "house" on any doubt so it never blocks (the worker can flag if it's
    /// genuinely unsure). With no client brands known, it's always "house".</summary>
    public async Task<string> ResolveBrandAsync(string task, IReadOnlyList<string> brandSlugs, CancellationToken ct)
    {
        var clients = brandSlugs.Where(s => !string.Equals(s, "house", StringComparison.OrdinalIgnoreCase)).ToList();
        if (clients.Count == 0) return "house";
        try
        {
            var convo = new List<Message>
            {
                Message.User(
                    "This is branding work at an agency that has its own 'house' brand and separate brands for " +
                    "clients. Which brand is THIS task for? Return one slug from the list, or \"house\" for the " +
                    "agency's own brand or when no client is named. If unsure, return \"house\".\n\n" +
                    "Client brands: " + string.Join(", ", clients) + "\n\nTask:\n" + task),
            };
            var request = new ModelRequest
            {
                Model = _model,
                Messages = convo,
                Think = false,
                ResponseFormat = BrandSchema(),
                MaxOutputTokens = 40,
                TurnTimeout = TimeSpan.FromSeconds(25),
            };
            var response = await _provider.CompleteAsync(request, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.Content)) return "house";
            using var doc = JsonDocument.Parse(response.Content);
            var slug = doc.RootElement.TryGetProperty("brand", out var b) ? b.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(slug)) return "house";
            // Only trust a slug that actually exists; otherwise fall back rather than mount a phantom brand.
            return brandSlugs.FirstOrDefault(s => string.Equals(s, slug, StringComparison.OrdinalIgnoreCase)) ?? "house";
        }
        catch { return "house"; }
    }

    private static IReadOnlyList<string> ParseStringArray(string? content, string prop)
    {
        if (string.IsNullOrWhiteSpace(content)) return Array.Empty<string>();
        using var doc = JsonDocument.Parse(content);
        var list = new List<string>();
        if (doc.RootElement.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } v)
                    list.Add(v.Trim());
        return list;
    }

    private static WorkPlan? ParsePlan(string? content, string goal)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        using var doc = JsonDocument.Parse(content);
        if (!doc.RootElement.TryGetProperty("steps", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        var steps = new List<PlanStep>();
        foreach (var e in arr.EnumerateArray())
        {
            string? persona = e.TryGetProperty("persona", out var p) ? p.GetString() : null;
            string? instruction = e.TryGetProperty("instruction", out var ins) ? ins.GetString() : null;
            string produces = e.TryGetProperty("produces", out var pr) ? (pr.GetString() ?? "") : "";
            if (!string.IsNullOrWhiteSpace(persona) && !string.IsNullOrWhiteSpace(instruction))
                steps.Add(new PlanStep(persona!.Trim(), instruction!.Trim(), produces.Trim()));
        }
        return steps.Count > 0 ? new WorkPlan { Goal = goal, Steps = steps } : null;
    }
}
