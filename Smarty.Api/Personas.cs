using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// A named specialist role the orchestrator can delegate to: an expertise framing (system prompt) plus the
/// set of capability ids it's allowed to use. A persona is deliberately THIN — it doesn't own tools, it
/// references <see cref="ICapability"/> ids in the registry. So "give the PM Jira too" is just adding an id,
/// and a user-defined persona draws from the same capabilities as the built-ins. Base tools (web/files/memory)
/// are always present; a persona's capabilities stack ON TOP, so a specialist never loses general competence.
/// </summary>
public sealed record Persona(
    string Id,
    string Name,
    string Description,
    string SystemPrompt,
    IReadOnlyList<string> CapabilityIds);

/// <summary>
/// The roster of personas. v1 ships built-in templates (defined in code); shaped like <see cref="ProjectStore"/>
/// so loading/merging a user-editable <c>personas.json</c> later is a small change, not a rewrite.
/// </summary>
public sealed class PersonaStore
{
    private readonly Dictionary<string, Persona> _personas;

    public PersonaStore(IEnumerable<Persona>? personas = null) =>
        _personas = (personas ?? BuiltIns).ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

    public Persona? Get(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : _personas.TryGetValue(id.Trim(), out var p) ? p : null;

    public IReadOnlyList<Persona> All => _personas.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();

    /// <summary>The built-in persona templates. Capability ids must exist in the <see cref="CapabilityRegistry"/>;
    /// a referenced capability that isn't configured simply contributes no tools (the persona still runs).</summary>
    public static IReadOnlyList<Persona> BuiltIns => new[]
    {
        new Persona(
            "software_engineer",
            "Software Engineer",
            "Diagnoses bugs/exceptions/incidents from logs AND the code, then proposes a concrete, reviewable fix.",
            "You are acting as a SENIOR SOFTWARE ENGINEER. Diagnose problems from EVIDENCE — query the logs, " +
            "read what's actually there, and reason from it. State the most likely cause and the signal that " +
            "points to it. Be precise and technical: quote EXACT error types, counts, services, timestamps and " +
            "figures from the tools — never round, embellish, or invent a number, stack trace, or metric the " +
            "tools didn't return. Clearly separate what the evidence SHOWS from what you INFER.\n" +
            "Then PROPOSE A FIX, grounded in the real code: use code_search/code_read to find the exact file and " +
            "method involved, identify the root cause in the source, and write a concrete proposed change — name " +
            "the file and method, show the before/after, and explain why it addresses the cause. Prefer writing " +
            "the proposed patch to a file with write_file and sending it with send_file. You are PROPOSING only: " +
            "you cannot and must not modify the repository or claim you have applied anything — leave the decision " +
            "to a human.",
            new[] { "kibana", "code", "github" }),

        new Persona(
            "product_manager",
            "Product Manager",
            "Frames product questions, weighs trade-offs, scopes work and writes crisp summaries.",
            "You are acting as a PRODUCT MANAGER. Think in terms of users, outcomes and trade-offs. Be concise " +
            "and decisive: clarify the goal, lay out options with their pros/cons, and recommend one. Ground " +
            "claims about current work in real Jira issues (jira_search / jira_get_issue) rather than guessing, " +
            "and when the user actually wants a ticket, create it with jira_create_issue. Don't pad with platitudes.",
            new[] { "jira" }),

        new Persona(
            "data_scientist",
            "Data Scientist",
            "Analyzes data files (CSVs, Excel, PDFs, etc.), writes Python to process them, and produces the findings — written analysis, charts, and data — for others to act on or present.",
            "You are acting as a DATA SCIENTIST. Your role is ANALYSIS, not presentation: find the patterns and " +
            "tell the story in the numbers. When asked to analyze a file, understand its structure first, then " +
            "write and execute Python with run_python to do the heavy lifting — statistical analysis and plotting.\n" +
            "WHAT YOU PRODUCE (this is important):\n" +
            "- A clear written analysis as a MARKDOWN file (e.g. findings.md) written with write_file: the key " +
            "findings, ranked, with the concrete figures behind each — best/worst performers, segments, outliers, " +
            "correlations. Be specific and quantitative; never round away or invent a number.\n" +
            "- The supporting CHARTS as image files (PNG) saved with matplotlib/seaborn, and any processed DATA " +
            "(e.g. a cleaned CSV) the work produced.\n" +
            "- Do NOT build a PDF or any polished/branded document — that is someone else's job. Hand on the raw " +
            "picture: markdown + images + data. Send the markdown and images back with send_file.\n" +
            "In your final text response, give a 1–2 sentence high-level summary and point to the files you " +
            "produced. Don't paste long tables into the chat.",
            new[] { "datascience" }),

        new Persona(
            "branding_designer",
            "Branding & Design",
            "Designs and edits visuals, and produces on-brand docs and comms. Applies a brand the user provides rather than inventing one.",
            "You are acting as a BRANDING & DESIGN specialist. You design and edit by writing code — run_python is " +
            "your workhorse — working from the files in this conversation and sending what you make with send_file.\n" +
            "When the work should be on-brand, call list_files first: a brand kit the user provided may be mounted " +
            "read-only — use it, and don't invent a brand. Only ask for one when the work genuinely needs it.",
            new[] { "datascience", "figma" }),
    };
}

/// <summary>
/// An integration: a self-contained bundle of tools for one external system (Kibana, Jira, GitHub…), plus the
/// config keys it needs. A capability NEVER receives secrets through the model — it reads them from
/// <see cref="IntegrationConfig"/> at <see cref="BuildTools"/> time and hands back tools with the credentials
/// already baked in (deterministic carry, like file paths). Not configured → returns no tools, never throws.
/// </summary>
public interface ICapability
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>Config keys (under this capability's id) it needs to function, for diagnostics/help.</summary>
    IReadOnlyList<string> RequiredConfig { get; }

    /// <summary>An optional line woven into the worker's prompt when this capability is active (how/when to use it).</summary>
    string? PromptHint { get; }

    /// <summary>Build this capability's tools for a task, reading any credentials from <paramref name="config"/>.
    /// Returns an empty list when the required config is absent — the persona then simply runs without it.</summary>
    IReadOnlyList<AgentTool> BuildTools(IntegrationConfig config, TaskInfo task);

    /// <summary>Checks if any prerequisites on the host machine (e.g. CLI tools, local packages) are met.
    /// Throws an exception if critical requirements are missing, preventing application startup.</summary>
    void ValidateSystemPrerequisites();
}

/// <summary>The set of known capabilities, by id. Resolves a persona's capability ids into a flattened toolset
/// (and the prompt hints to go with them).</summary>
public sealed class CapabilityRegistry
{
    private readonly Dictionary<string, ICapability> _caps;

    public CapabilityRegistry(IEnumerable<ICapability> capabilities) =>
        _caps = capabilities.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

    public ICapability? Get(string id) => _caps.TryGetValue(id, out var c) ? c : null;

    /// <summary>Runs system prerequisite validation for all registered capabilities.</summary>
    public void ValidateAll()
    {
        foreach (var cap in _caps.Values)
        {
            Console.WriteLine($"[startup] Validating capability: {cap.DisplayName} ({cap.Id})...");
            cap.ValidateSystemPrerequisites();
        }
    }

    /// <summary>Every tool contributed by the given capability ids (each built with its config). Unknown or
    /// unconfigured ids contribute nothing.</summary>
    public IReadOnlyList<AgentTool> BuildFor(IReadOnlyList<string> capabilityIds, IntegrationConfig config, TaskInfo task)
    {
        var tools = new List<AgentTool>();
        foreach (var id in capabilityIds)
            if (_caps.TryGetValue(id, out var cap))
                tools.AddRange(cap.BuildTools(config, task));
        return tools;
    }

    /// <summary>Prompt hints for the capabilities that actually produced tools (so the worker is told only about
    /// integrations it really has).</summary>
    public IReadOnlyList<string> ActiveHints(IReadOnlyList<string> capabilityIds, IntegrationConfig config, TaskInfo task)
    {
        var hints = new List<string>();
        foreach (var id in capabilityIds)
            if (_caps.TryGetValue(id, out var cap) && cap.PromptHint is { Length: > 0 } hint
                && cap.BuildTools(config, task).Count > 0)
                hints.Add(hint);
        return hints;
    }
}

/// <summary>
/// Integration credentials/config, read from <c>&lt;dataDir&gt;/integrations.json</c> (a flat
/// <c>"capabilityId.key": "value"</c> map) with an environment-variable fallback
/// (<c>SMARTY_&lt;CAP&gt;_&lt;KEY&gt;</c>) so secrets needn't be committed to a file. NEVER serialized into a
/// model prompt — capabilities read it directly to construct authenticated tools.
/// </summary>
public sealed class IntegrationConfig
{
    private readonly Dictionary<string, string> _values;

    public IntegrationConfig(IDictionary<string, string>? values = null) =>
        _values = values is null ? new() : new(values, StringComparer.OrdinalIgnoreCase);

    public static IntegrationConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new IntegrationConfig();
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return new IntegrationConfig(map);
        }
        catch { return new IntegrationConfig(); /* a bad config file shouldn't crash startup */ }
    }

    /// <summary>A config value for a capability key — file first, then a SMARTY_CAP_KEY env var. Null if unset.</summary>
    public string? Get(string capabilityId, string key)
    {
        if (_values.TryGetValue($"{capabilityId}.{key}", out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        var envName = $"SMARTY_{capabilityId}_{key}".ToUpperInvariant().Replace('.', '_').Replace('-', '_');
        var env = Environment.GetEnvironmentVariable(envName);
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }

    public bool Has(string capabilityId, IReadOnlyList<string> keys) =>
        keys.All(k => !string.IsNullOrWhiteSpace(Get(capabilityId, k)));
}
