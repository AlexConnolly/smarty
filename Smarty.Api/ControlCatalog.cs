using Smarty.Agents;

namespace Smarty.Api;

/// <summary>The display metadata for one tool — name, description and typed parameters. NEVER carries a
/// system prompt or any credential; it's purely "what the model can call and what it does".</summary>
public sealed record ToolMeta(string Name, string Description, IReadOnlyList<ToolParamMeta> Parameters);

public sealed record ToolParamMeta(string Name, string Type, string Description, bool Required);

/// <summary>One capability (integration) and the tools it contributes, with whether it's configured.</summary>
public sealed record CapabilityMeta(
    string Id,
    string DisplayName,
    bool Configured,
    IReadOnlyList<string> RequiredConfig,
    IReadOnlyList<ToolMeta> Tools);

/// <summary>A persona as the control centre shows it: identity + capabilities + the full toolset it can call.
/// The system prompt is deliberately absent — it is never exposed.</summary>
public sealed record PersonaView(
    string Id,
    string Name,
    string Description,
    bool Builtin,
    IReadOnlyList<string> CapabilityIds,
    IReadOnlyList<ToolMeta> Tools);

/// <summary>
/// Resolves the read-only catalogue the control centre shows for personas and capabilities: the concrete set
/// of tools each persona can call (the always-present base tools PLUS its capabilities' tools), and every
/// capability's tools and configured state. Mirrors how the orchestrator assembles a worker's toolset, so what
/// you see is what the model actually gets — but it only ever exposes tool NAMES, DESCRIPTIONS and PARAMETERS.
/// </summary>
public sealed class ControlCatalog
{
    private readonly PersonaStore _personas;
    private readonly CapabilityRegistry _capabilities;
    private readonly IntegrationConfig _config;
    private readonly string _ollamaBaseUrl;
    private readonly string _model;

    public ControlCatalog(PersonaStore personas, CapabilityRegistry capabilities, IntegrationConfig config,
        string ollamaBaseUrl, string model)
    {
        _personas = personas;
        _capabilities = capabilities;
        _config = config;
        _ollamaBaseUrl = ollamaBaseUrl;
        _model = model;
    }

    /// <summary>The base tools every worker gets regardless of persona (web, shell, files, memory).</summary>
    public IReadOnlyList<ToolMeta> BaseTools()
    {
        var provider = new OllamaModelProvider(_ollamaBaseUrl);
        var tools = new List<AgentTool>();
        void Try(Func<AgentTool> build) { try { tools.Add(build()); } catch { /* skip a tool we can't preview */ } }

        Try(() => ShellTool.Create());
        Try(() => WebResearch.SearchTool());
        Try(() => WebResearch.PageAnswerTool(provider, _model));
        Try(() => FileTools.ReadFileTool());
        Try(() => FileTools.SummaryTool(provider, _model));
        Try(() => FileTools.WriteFileTool("(conversation files)"));
        Try(() => FileTools.ListFilesTool("(conversation files)"));
        Try(() => FileTools.SendFileTool("(conversation files)", (_, _) => true));
        Try(() => MemoryTools.SearchTool(MemoryPlaceholder));
        Try(() => MemoryTools.SetTool(MemoryPlaceholder));

        return tools.Select(Meta).ToList();
    }

    public IReadOnlyList<CapabilityMeta> Capabilities()
    {
        var task = new TaskInfo { Id = "preview", Description = "preview" };
        return _capabilities.All.Select(c =>
        {
            IReadOnlyList<AgentTool> tools;
            try { tools = c.BuildTools(_config, task); } catch { tools = Array.Empty<AgentTool>(); }
            bool configured = c.RequiredConfig.Count == 0 ? tools.Count > 0 : _config.Has(c.Id, c.RequiredConfig);
            return new CapabilityMeta(c.Id, c.DisplayName, configured, c.RequiredConfig, tools.Select(Meta).ToList());
        }).ToList();
    }

    /// <summary>The capability tools a persona's ids resolve to (configured ones only contribute tools).</summary>
    public IReadOnlyList<ToolMeta> CapabilityToolsFor(IReadOnlyList<string> capabilityIds)
    {
        var task = new TaskInfo { Id = "preview", Description = "preview" };
        var tools = new List<ToolMeta>();
        foreach (var id in capabilityIds)
        {
            var cap = _capabilities.Get(id);
            if (cap is null) continue;
            try { tools.AddRange(cap.BuildTools(_config, task).Select(Meta)); } catch { /* unconfigured → nothing */ }
        }
        return tools;
    }

    public PersonaView View(Persona p)
    {
        var tools = new List<ToolMeta>(BaseTools());
        tools.AddRange(CapabilityToolsFor(p.CapabilityIds));
        // De-dupe by name (a persona capability could, in principle, re-add a base-named tool).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = tools.Where(t => seen.Add(t.Name)).ToList();
        return new PersonaView(p.Id, p.Name, p.Description, p.Builtin, p.CapabilityIds, unique);
    }

    public IReadOnlyList<PersonaView> Personas() => _personas.All.Select(View).ToList();

    private static ToolMeta Meta(AgentTool t) => new(
        t.Name,
        t.Description,
        t.Parameters.Select(p => new ToolParamMeta(p.Name, p.Type, p.Description, p.Required)).ToList());

    // A throwaway memory store only used to build the memory tools' metadata (never read or written here).
    private static readonly MemoryStore MemoryPlaceholder =
        new(Path.Combine(Path.GetTempPath(), "smarty-control-tool-preview.json"),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
}
