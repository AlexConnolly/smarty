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
    private readonly Smarty.Brain.Memory? _memory;

    public ControlCatalog(PersonaStore personas, CapabilityRegistry capabilities, IntegrationConfig config,
        string ollamaBaseUrl, string model, Smarty.Brain.Memory? memory = null)
    {
        _personas = personas;
        _capabilities = capabilities;
        _config = config;
        _ollamaBaseUrl = ollamaBaseUrl;
        _model = model;
        _memory = memory;
    }

    /// <summary>The memory's tools, for preview only — the catalogue exposes names, descriptions and parameters and
    /// never calls one, so the provenance callback here is never invoked.</summary>
    private IReadOnlyList<AgentTool> BrainToolSet() => _memory is null
        ? Array.Empty<AgentTool>()
        : MemoryTools.All(_memory, _memory.Graph, () => null, () => null);

    /// <summary>The base tools every worker gets regardless of persona (web, shell, files, memory).</summary>
    public IReadOnlyList<ToolMeta> BaseTools()
    {
        var provider = new OllamaModelProvider(_ollamaBaseUrl);
        var tools = new List<AgentTool>();
        void Try(Func<AgentTool> build) { try { tools.Add(build()); } catch { /* skip a tool we can't preview */ } }

        Try(() => ShellTool.Create());
        Try(() => FileTools.ReadFileTool());
        Try(() => FileTools.SummaryTool(provider, _model));
        Try(() => FileTools.WriteFileTool("(conversation files)"));
        Try(() => FileTools.ListFilesTool("(conversation files)"));
        Try(() => FileTools.SendFileTool("(conversation files)", (_, _) => true));
        tools.AddRange(BrainToolSet());
        // The web arrives as the connected browser's tools, so they belong in the always-present set.
        tools.AddRange(BrowserTools());

        return tools.Select(Meta).ToList();
    }

    /// <summary>The connected browser's tools — what "web research" resolves to now that there are no
    /// fetch-and-extract tools. Empty when no MCP server claims the <c>browser</c> function.</summary>
    private IReadOnlyList<AgentTool> BrowserTools()
    {
        var task = new TaskInfo { Id = "preview", Description = "preview" };
        try { return _capabilities.BuildFor(new[] { "browser" }, _config, task); }
        catch { return Array.Empty<AgentTool>(); }
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
        // A persona now gets ONLY the blocks it declares (mirrors Orchestrator.BuildBlock), so the catalogue must
        // resolve per-block — not prepend a universal base set that the worker won't actually have at runtime.
        var provider = new OllamaModelProvider(_ollamaBaseUrl);
        var task = new TaskInfo { Id = "preview", Description = "preview" };
        var tools = new List<AgentTool>();
        void Try(Func<AgentTool> b) { try { tools.Add(b()); } catch { } }
        foreach (var block in p.CapabilityIds)
            switch (block.Trim().ToLowerInvariant())
            {
                case "web": tools.AddRange(BrowserTools()); break; // web research = the connected browser
                case "read": Try(() => FileTools.ReadFileTool()); Try(() => FileTools.SummaryTool(provider, _model)); Try(() => FileTools.ListFilesTool("(conversation files)")); break;
                case "files":
                    Try(() => FileTools.ReadFileTool()); Try(() => FileTools.SummaryTool(provider, _model));
                    Try(() => FileTools.WriteFileTool("(conversation files)")); Try(() => FileTools.EditFileTool("(conversation files)"));
                    Try(() => FileTools.FindInFileTool("(conversation files)")); Try(() => FileTools.ListFilesTool("(conversation files)")); break;
                case "memory": tools.AddRange(BrainToolSet()); break;
                case "shell": Try(() => ShellTool.Create()); break;
                default: try { tools.AddRange(_capabilities.BuildFor(new[] { block }, _config, task)); } catch { } break;
            }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = tools.Where(t => seen.Add(t.Name)).Select(Meta).ToList();
        return new PersonaView(p.Id, p.Name, p.Description, p.Builtin, p.CapabilityIds, unique);
    }

    public IReadOnlyList<PersonaView> Personas() => _personas.All.Select(View).ToList();

    private static ToolMeta Meta(AgentTool t) => new(
        t.Name,
        t.Description,
        t.Parameters.Select(p => new ToolParamMeta(p.Name, p.Type, p.Description, p.Required)).ToList());


}
