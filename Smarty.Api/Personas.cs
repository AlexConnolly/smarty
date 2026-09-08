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
    IReadOnlyList<string> CapabilityIds)
{
    /// <summary>True for the shipped templates: their curated <see cref="SystemPrompt"/> is owned by code and
    /// can never be edited or deleted (the prompt is never shown or edited anywhere — only ever read by the
    /// orchestrator). User-created personas get a synthesised prompt, equally hidden.</summary>
    public bool Builtin { get; init; }
}

/// <summary>
/// The roster of personas. Ships built-in templates (defined in code) and persists user-created/edited ones to
/// <c>personas.json</c>, merging the two on load. Built-ins are always present and their curated system prompt
/// is owned by code; user personas store their own (synthesised) prompt. The system prompt is intentionally
/// never surfaced — the control centre edits a persona's name, description and capabilities only.
/// </summary>
public sealed class PersonaStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Persona> _personas;
    private readonly string? _path;
    private readonly JsonSerializerOptions _json;

    /// <summary>In-memory store seeded from a fixed roster (or the built-ins). No persistence — used by hosts
    /// that don't manage personas (e.g. Slack) and by tests.</summary>
    public PersonaStore(IEnumerable<Persona>? personas = null)
    {
        _personas = (personas ?? BuiltIns).ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        _json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    /// <summary>File-backed store: loads <paramref name="path"/> if present, then seeds any missing built-ins
    /// and re-asserts every built-in's curated prompt (so a stored copy can never override or expose it).</summary>
    public PersonaStore(string path, JsonSerializerOptions json)
    {
        _path = path;
        _json = json;
        _personas = new Dictionary<string, Persona>(StringComparer.OrdinalIgnoreCase);
        Load();
    }

    public Persona? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_lock) return _personas.TryGetValue(id.Trim(), out var p) ? p : null;
    }

    public IReadOnlyList<Persona> All
    {
        get { lock (_lock) return _personas.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToList(); }
    }

    /// <summary>Create or update a persona's name, description and capabilities. The system prompt is never
    /// taken from the caller: built-ins keep their curated prompt; user personas get a synthesised one. A new
    /// id is created from the name. Returns the resulting persona, or null if the inputs are unusable.</summary>
    public Persona? Upsert(string? id, string name, string description, IReadOnlyList<string> capabilityIds)
    {
        name = (name ?? "").Trim();
        description = (description ?? "").Trim();
        capabilityIds = (capabilityIds ?? Array.Empty<string>())
            .Select(c => c.Trim().ToLowerInvariant()).Where(c => c.Length > 0).Distinct().ToList();
        if (name.Length == 0) return null;

        lock (_lock)
        {
            string key = string.IsNullOrWhiteSpace(id) ? Slugify(name) : id!.Trim();
            if (key.Length == 0) return null;

            _personas.TryGetValue(key, out var existing);
            // A brand-new id must be unique; disambiguate a collision when creating from a name.
            if (existing is null && string.IsNullOrWhiteSpace(id))
            {
                string baseKey = key; int n = 2;
                while (_personas.ContainsKey(key)) key = $"{baseKey}_{n++}";
            }

            bool builtin = existing?.Builtin ?? false;
            string prompt = builtin ? existing!.SystemPrompt : SynthesisePrompt(name, description, capabilityIds);
            var persona = new Persona(key, name, description, prompt, capabilityIds) { Builtin = builtin };
            _personas[key] = persona;
            Save();
            return persona;
        }
    }

    /// <summary>Delete a user-created persona. Built-ins can't be deleted. Returns false if not found or built-in.</summary>
    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_lock)
        {
            if (!_personas.TryGetValue(id.Trim(), out var p) || p.Builtin) return false;
            _personas.Remove(id.Trim());
            Save();
            return true;
        }
    }

    // A functional, generic role framing for a user-created persona. Deliberately hidden from the UI — the
    // user manages WHAT a persona is (name/description/tools), not the exact wording the model is steered with.
    private static string SynthesisePrompt(string name, string description, IReadOnlyList<string> capabilityIds)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"You are the {name}. ");
        if (description.Length > 0) sb.Append(description.TrimEnd('.') + ". ");
        sb.Append("Do the task with your tools; base every claim only on what a tool actually returned — if the " +
                  "tools can't get it, say so plainly rather than inventing.");
        return sb.ToString();
    }

    private static string Slugify(string name)
    {
        var chars = name.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var slug = new string(chars);
        while (slug.Contains("__")) slug = slug.Replace("__", "_");
        return slug.Trim('_');
    }

    private void Load()
    {
        try
        {
            if (_path is not null && File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<List<Persona>>(File.ReadAllText(_path), _json);
                if (loaded is not null)
                    foreach (var p in loaded)
                        if (!string.IsNullOrWhiteSpace(p.Id)) _personas[p.Id] = p;
            }
        }
        catch { /* a corrupt personas file shouldn't crash startup */ }

        // Seed missing built-ins and ALWAYS re-assert each built-in's curated prompt + flag, so a stored copy
        // can neither override the prompt nor be edited into exposing it. Stored name/description/capabilities
        // for a built-in are preserved (the user may have tweaked them).
        // Built-ins are fully re-asserted from the template (name, description, capabilities, prompt) — a stored
        // copy can neither drift nor expose the prompt. Their definitions are structural (tool blocks), not
        // user-tunable. User-CREATED personas (not in BuiltIns) are left untouched.
        foreach (var t in BuiltIns)
            _personas[t.Id] = t with { Builtin = true };

        // Drop a stored built-in that no longer exists in code. Without this, retiring a built-in leaves it in
        // every existing personas.json forever — still offered to the router, and undeletable through the API
        // because built-ins can't be deleted. User-created personas (Builtin: false) are never touched.
        foreach (var stale in _personas.Values
                     .Where(p => p.Builtin && BuiltIns.All(b => !string.Equals(b.Id, p.Id, StringComparison.OrdinalIgnoreCase)))
                     .Select(p => p.Id).ToList())
            _personas.Remove(stale);

        Save();
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_personas.Values.ToList(), _json));
        }
        catch { /* best-effort persistence */ }
    }

    /// <summary>The built-in persona templates. Capability ids must exist in the <see cref="CapabilityRegistry"/>;
    /// a referenced capability that isn't configured simply contributes no tools (the persona still runs).</summary>
    public static IReadOnlyList<Persona> BuiltIns => new[]
    {
        new Persona(
            "software_engineer",
            "Software Engineer",
            "Reads and writes code to diagnose issues and propose reviewable fixes.",
            "Work from the real code: read it, quote the exact files/functions, and propose fixes as concrete " +
            "before/after changes — never invent. You propose changes; you don't deploy them.",
            new[] { "code", "files", "memory" }),

        new Persona(
            "proact",
            "Proact",
            "Works on the user's behalf without being asked: notices things, prepares work, proposes the rest.",
            "Nobody asked you for this, so the bar is higher than usual, not lower. Everything you produce has to be " +
            "about THIS person and drawn from something you actually read — a fact about their own week, work you " +
            "did and left ready, or something worth offering them. Never advice they did not ask for, never a " +
            "generality, never a greeting. You cannot send, buy, book or cancel anything and you have no tools that " +
            "could; anything of that kind is a proposal for them to tick. Finding nothing worth reporting is a good " +
            "outcome and you should say so plainly.",
            // The blocks it starts from. What it ENDS with is narrower — the browser arrives read-only and the
            // sending half of everything is removed by name. See Proact.Keep, which is where the boundary lives.
            new[] { "read", "files", "memory", "web" }),

        new Persona(
            "sre",
            "Site Reliability",
            "Searches logs to find and explain production issues and incidents.",
            "Answer from the logs: query them, quote the exact errors/counts/timestamps the tools return, and " +
            "separate what's shown from what you infer — never invent a metric or stack trace.",
            new[] { "observability", "memory" }),

        new Persona(
            "product_manager",
            "Project Manager",
            "Searches projects and does CRUD on tickets in the connected project-management tool.",
            "Answer about projects and tickets from the connected project-management tool — search and read real " +
            "items rather than guessing, and create or update tickets when asked. Be concise and decisive.",
            new[] { "project_management", "memory" }),

        new Persona(
            "data_scientist",
            "Data Analyst",
            "Analyses data files with Python and returns findings, charts (PNG) and processed data.",
            "Analyse data with run_python. Produce a short written analysis of the real findings (with the actual " +
            "figures — never invent one), plus supporting charts as PNGs and any cleaned data. Hand over raw " +
            "analysis, not polished or branded documents.",
            new[] { "data", "files", "memory" }),

        new Persona(
            "branding_designer",
            "Document Production",
            "Produces documents (.docx/.pdf): structured build for greenfield work, or fills a supplied template. Never invents a brand or design taste.",
            "You produce documents. Two paths, chosen by whether the user gave you a TEMPLATE to build inside:\n" +
            "• No template (just brand tokens/colours/a logo, or nothing to inherit) → use build_document: pass the " +
            "format, filename and an ordered `blocks` array (headings, paragraphs, bullets, tables, images). It's " +
            "deterministic and reliable; pull table rows straight from a supplied CSV.\n" +
            "• A template/branded document supplied (a letterhead/.docx with its own headers and layout) → the " +
            "template is authoritative: use run_python with python-docx to OPEN it as the template and add your " +
            "content, so its existing styles, fonts and logo stand. Don't rebuild it from blocks.\n" +
            "You do NOT invent brands, visual identities, or design taste: if asked to create one from scratch, say " +
            "that's not something you do and ask for the guidelines to apply.",
            new[] { "documents", "data", "files", "memory" }),

        // The role that was missing, and it is a finishing role.
        //
        // Everything else here either gathers material (research) or transforms one file into another (documents,
        // images). Nobody was responsible for what the user actually SEES — so a general-purpose worker did it in
        // passing, between reading pages, and produced bullet lists in a default typeface. Art direction is a
        // separate job from finding things out, which is why agencies staff it separately.
        new Persona(
            "designer",
            "Designer",
            "Turns gathered material — findings, photographs, numbers — into something worth looking at: a " +
            "presentation, a visual comparison, a page you would show someone. Composes; does not research.",
            "You are a designer. Someone else did the research; your job is what the user SEES.\n" +
            "Build with build_presentation. Choose the display font, the body font and the accent colour for THIS " +
            "subject — two decks on unrelated subjects should not look alike, and leaving them " +
            "unset gets a default, which is the only genuinely wrong answer. Where a slide deserves a layout of " +
            "its own, write that slide as HTML with Tailwind classes rather than markdown.\n" +
            "Pictures are the work, not decoration. Use chrome_images to find them — it gives size and alt text, " +
            "which is what separates a photograph of the place from the logo and the payment icons — and read the " +
            "alt before you use one. A slide describing somewhere with no picture of it has failed. Take images " +
            "from the subject's own site where you can; a reseller's stock shot of a different pool is worse than " +
            "none.\n" +
            "Work with the material you are given: the project's files, the facts and lists it holds, the images on " +
            "the pages you are pointed at. If something essential is missing — no photographs exist, a price was " +
            "never established — say so plainly rather than inventing it or padding round it. You are not here to " +
            "verify claims; you are here to present them well, and a placeholder is not presenting.",
            // "web" because the pictures are on the web: chrome_images and chrome_grab_image live there, and a
            // designer told to find photographs without a browser is a designer told to invent them.
            new[] { "web", "files", "images", "data", "memory" }),

        new Persona(
            "image_editor",
            "Image Editor",
            "Edits images deterministically: crop, resize, recolour, composite, add a logo or overlay.",
            "Edit the image(s) provided with the image tools — crop, resize, recolour, composite, place a logo. " +
            "You transform images you're given; you don't generate or invent new artwork.",
            new[] { "images", "files" }),

        new Persona(
            "file_converter",
            "File Converter",
            "Converts files between formats (e.g. docx↔pdf, csv↔xlsx, image formats).",
            "Convert the file(s) provided from one format to another with the conversion tools. Convert only — " +
            "don't edit or reinterpret the content.",
            new[] { "conversion", "files" }),
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
    private readonly object _lock = new();

    // Platform-agnostic FUNCTIONS → the integration(s) that provide them. Personas reference a function (e.g.
    // "project_management"), never a platform, so a Smarty instance connected to Trello instead of Jira lights up
    // the same persona. A reference that isn't a function key is treated as a direct integration id (back-compat).
    private static readonly IReadOnlyDictionary<string, string[]> BuiltInFunctions =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["project_management"] = new[] { "jira" },
            ["observability"] = new[] { "kibana" },
            ["code"] = new[] { "code", "github" },
            ["data"] = new[] { "datascience" },
            // Structured document production (build_document) is its OWN capability, kept separate from the data
            // engine so the data analyst can't make documents and the document persona owns that surface.
            ["documents"] = new[] { "document" },
            // Images and file conversion run on the same Python engine (Pillow / pandas / reportlab / python-docx) today; a
            // dedicated bounded toolset can slot in behind these functions later without touching the personas.
            ["images"] = new[] { "datascience" },
            ["conversion"] = new[] { "datascience" },
        };

    private readonly Dictionary<string, string[]> _functions;

    // Expand a persona's capability refs (function ids and/or direct integration ids) into integration ids.
    private IEnumerable<string> ResolveIntegrations(IReadOnlyList<string> refs)
    {
        lock (_lock)
            return refs.SelectMany(r => _functions.TryGetValue(r, out var ints) ? ints : new[] { r })
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Register a capability at runtime, with the functions it answers — how an MCP server added from the control
    /// centre starts contributing tools without restarting the host. Replaces one of the same id.
    /// </summary>
    /// <remarks>
    /// Safe to do live because worker toolsets are assembled per task: the next task sees the new capability, and
    /// a task already running keeps the toolset it started with.
    /// </remarks>
    public void Register(ICapability capability, IReadOnlyList<string>? functions = null)
    {
        lock (_lock)
        {
            _caps[capability.Id] = capability;
            foreach (var function in functions ?? Array.Empty<string>())
            {
                var ids = _functions.TryGetValue(function, out var existing)
                    ? existing.Where(i => !string.Equals(i, capability.Id, StringComparison.OrdinalIgnoreCase)).Append(capability.Id).ToArray()
                    : new[] { capability.Id };
                _functions[function] = ids;
            }
        }
    }

    /// <summary>Forget a capability and drop it from every function it answered.</summary>
    public bool Unregister(string id)
    {
        lock (_lock)
        {
            if (!_caps.Remove(id)) return false;
            foreach (var function in _functions.Keys.ToList())
            {
                var remaining = _functions[function]
                    .Where(i => !string.Equals(i, id, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (remaining.Length == 0) _functions.Remove(function);
                else _functions[function] = remaining;
            }
            return true;
        }
    }

    /// <param name="capabilities">The integrations available to this host.</param>
    /// <param name="extraFunctions">Function → integration ids discovered at runtime rather than declared in code,
    /// which is how an MCP server claims a function ("browser") without the framework knowing the server exists.
    /// Merged over the built-ins, so a configured server can also take over a built-in function.</param>
    public CapabilityRegistry(
        IEnumerable<ICapability> capabilities,
        IReadOnlyDictionary<string, string[]>? extraFunctions = null)
    {
        _caps = capabilities.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

        var functions = new Dictionary<string, string[]>(BuiltInFunctions, StringComparer.OrdinalIgnoreCase);
        foreach (var (function, ids) in extraFunctions ?? new Dictionary<string, string[]>())
            functions[function] = functions.TryGetValue(function, out var existing)
                ? existing.Concat(ids).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : ids;
        _functions = functions;
    }

    public ICapability? Get(string id) { lock (_lock) return _caps.TryGetValue(id, out var c) ? c : null; }

    /// <summary>All registered capabilities — for the control centre's "what tools can this call" view.</summary>
    public IReadOnlyList<ICapability> All { get { lock (_lock) return _caps.Values.OrderBy(c => c.Id, StringComparer.Ordinal).ToList(); } }

    /// <summary>Runs system prerequisite validation for all registered capabilities.</summary>
    public void ValidateAll()
    {
        foreach (var cap in All)
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
        foreach (var id in ResolveIntegrations(capabilityIds))
            if (Get(id) is { } cap)
                tools.AddRange(cap.BuildTools(config, task));
        return tools;
    }

    /// <summary>Prompt hints for the capabilities that actually produced tools (so the worker is told only about
    /// integrations it really has).</summary>
    public IReadOnlyList<string> ActiveHints(IReadOnlyList<string> capabilityIds, IntegrationConfig config, TaskInfo task)
    {
        var hints = new List<string>();
        foreach (var id in ResolveIntegrations(capabilityIds))
            if (Get(id) is { } cap && cap.PromptHint is { Length: > 0 } hint
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
