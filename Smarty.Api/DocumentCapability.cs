using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// Structured document production, deliberately kept SEPARATE from the data-science Python engine. It exposes
/// one tool — <c>build_document</c> — that turns a structured spec (content blocks + optional brand tokens) into
/// a real .docx or .pdf via the deterministic <see cref="DocumentRenderer"/>. The model supplies WHAT goes in
/// the document; the rendering is fixed and tested, so it doesn't hand-write reportlab/python-docx every time.
///
/// This is the GREENFIELD path only. When the user supplies a template document to build inside (a letterhead
/// with its own headers/layout), that goes through the LLM's <c>run_python</c> + python-docx instead — the
/// template is authoritative and reconciling it with structured blocks needs judgement. <c>build_document</c>
/// deliberately has no <c>template</c> parameter, so its use IS the "no template" signal.
/// </summary>
public sealed class DocumentCapability : ICapability
{
    public string Id => "document";
    public string DisplayName => "Document Production (structured .docx / .pdf)";
    public IReadOnlyList<string> RequiredConfig => Array.Empty<string>();

    public string? PromptHint =>
        "Use build_document to produce a NEW document from scratch, reliably: give it a format (\"docx\" or " +
        "\"pdf\"), a filename, and an ordered `blocks` array of content. Block types: " +
        "{\"type\":\"heading\",\"level\":1-3,\"text\":\"\"}, {\"type\":\"paragraph\",\"text\":\"\"}, " +
        "{\"type\":\"bullets\",\"items\":[]}, {\"type\":\"numbered\",\"items\":[]}, " +
        "{\"type\":\"table\",\"headers\":[],\"rows\":[[]]}, {\"type\":\"image\",\"file\":\"logo.png\"} (a file in " +
        "this conversation), {\"type\":\"pagebreak\"}, {\"type\":\"spacer\"}. Optional `style` for a from-scratch " +
        "brand: {\"font\":\"Arial\",\"primaryColor\":\"#1A73E8\"} (used for headings). The finished file is saved " +
        "to the conversation and handed to the user. " +
        "IMPORTANT: build_document is for when you were NOT given a template. If the user provided a template or " +
        "branded document to produce the output INSIDE (it already has its own headers/letterhead/styles), do NOT " +
        "use build_document — use run_python with python-docx to open that file as the template and add your " +
        "content, so its existing styling stands.";

    public IReadOnlyList<AgentTool> BuildTools(IntegrationConfig config, TaskInfo task)
    {
        return new[]
        {
            new AgentTool(
                "build_document",
                "Builds a NEW .docx or .pdf from a structured content spec (headings, paragraphs, bullets, tables, " +
                "images, page breaks) with optional brand styling. Deterministic — you provide the content, not " +
                "rendering code. Use this when you were NOT given a template document to build inside. The file is " +
                "saved to the conversation and handed to the user.",
                new[]
                {
                    ToolParameter.String("format", "Output format: \"docx\" or \"pdf\".", required: true),
                    ToolParameter.String("filename", "Output file name, e.g. \"q3-playbook.docx\" (the extension is corrected to match format).", required: true),
                    ToolParameter.String("blocks", "A JSON array of content blocks, in order. See the block types in the tool guidance.", required: true),
                    ToolParameter.String("style", "Optional JSON brand tokens for a from-scratch document, e.g. {\"font\":\"Arial\",\"primaryColor\":\"#1A73E8\"}.", required: false),
                },
                (args, ct) => BuildAsync(args, task))
        };
    }

    private static Task<ToolOutput> BuildAsync(ToolCallArguments args, TaskInfo task)
    {
        DocumentSpec spec;
        try
        {
            spec = DocumentSpec.Parse(
                args.GetString("format"),
                args.GetString("filename"),
                args.GetString("blocks"),
                args.GetStringOrNull("style"));
        }
        catch (ArgumentException ex)
        {
            // A malformed spec is correctable — tell the model exactly what was wrong so it can retry.
            return Task.FromResult(ToolOutput.Error($"Couldn't build the document: {ex.Message}"));
        }

        // Deliverables land in the conversation's files area (same place run_python writes to), so the finish
        // pass hands them to the user. With no workspace (shouldn't happen for a delegated task) fall back to temp.
        string? filesDir = string.IsNullOrEmpty(task.WorkspaceDir)
            ? null
            : Path.Combine(Path.GetDirectoryName(task.WorkspaceDir)!, "files");
        string outDir = filesDir ?? Path.Combine(Path.GetTempPath(), "smarty-docs");
        Directory.CreateDirectory(outDir);
        string outPath = Path.Combine(outDir, spec.Filename);

        try
        {
            DocumentRenderer.Render(spec, outPath, name => ResolveImage(outDir, name));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolOutput.Error($"The document failed to render: {ex.Message}"));
        }

        return Task.FromResult(ToolOutput.Ok(
            $"Built {spec.Filename} ({spec.Blocks.Count} block(s)). It's saved to the conversation and will be " +
            "handed to the user when you finish."));
    }

    // Resolve an image block's file name to a path in the conversation's files area (where uploads and any
    // charts a worker generated already live). Null → the renderer notes it's missing rather than failing.
    private static string? ResolveImage(string filesDir, string name)
    {
        var safe = Path.GetFileName(name);
        var path = Path.Combine(filesDir, safe);
        return File.Exists(path) ? path : null;
    }

    // Pure managed libraries (OpenXML, MigraDoc) — nothing to install or verify on the host.
    public void ValidateSystemPrerequisites() { }
}
