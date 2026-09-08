using System.Text;
using System.Text.Json;

namespace Smarty.Agents;

/// <summary>
/// Turns what an MCP server declares into what the agent loop runs: an <see cref="AgentTool"/> per MCP tool,
/// with the server's own JSON Schema carried through untouched and its result flattened to the text a tool
/// result is.
/// </summary>
public static class McpTools
{
    /// <summary>Build the agent-facing tool for one MCP tool. <paramref name="call"/> performs the actual
    /// <c>tools/call</c> — the connection owns that, so it can reconnect underneath a tool the model already holds.</summary>
    /// <summary>
    /// Somewhere to put an image a tool returned: given the bytes and mime type, store it and hand back a URL the
    /// chat can render. Null (or no sink) means images stay described rather than shown.
    /// </summary>
    public delegate string? ImageSink(byte[] bytes, string mimeType);

    public static AgentTool Build(
        McpToolDefinition definition,
        McpServerConfig config,
        Func<string, JsonElement, CancellationToken, Task<JsonElement>> call,
        ImageSink? images = null)
    {
        string name = QualifiedName(config.ResolvedPrefix, definition.Name);
        string description = definition.Description is { Length: > 0 } d
            ? d
            : $"The {definition.Name} tool, from the \"{config.Name}\" MCP server.";

        // Every MCP tool is treated as stateful. We can't know from a schema whether a call is a pure function
        // of its arguments, and the expensive mistake is the wrong way round: assuming statelessness makes the
        // loop refuse a legitimate re-read (read the page, navigate, read again — byte-identical arguments,
        // completely different page), which is what the browser server asks for on every navigation.
        return new AgentTool(name, description, Parameters(definition.InputSchema), async (args, ct) =>
        {
            try
            {
                // Arguments go through exactly as the model wrote them: the server declared the schema, so it —
                // not us — is the authority on what its own tool accepts.
                var result = await call(definition.Name, args.Raw, ct).ConfigureAwait(false);
                return Interpret(result, config.MaxResultChars, images);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (McpException ex)
            {
                return ex.IsPermanent
                    ? ToolOutput.DeadEnd($"{name} can't be called: {ex.Message}")
                    : ToolOutput.Error($"{name} failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                return ToolOutput.Error($"{name} failed: {ex.Message}");
            }
        })
        {
            Repeatable = true,
        };
    }

    /// <summary>A server's tool name, prefixed and reduced to the characters a function name may contain, so two
    /// servers can both offer a <c>navigate</c> and the model can still tell them apart.</summary>
    public static string QualifiedName(string prefix, string toolName)
    {
        var safe = new string((toolName ?? "").Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray())
            .Trim('_');
        if (safe.Length == 0) safe = "tool";
        return string.IsNullOrEmpty(prefix) ? safe : $"{prefix}_{safe}";
    }

    /// <summary>The parameters of an MCP <c>inputSchema</c>. Each property keeps its own schema fragment, so an
    /// enum, an array's items or a union of types reaches the model intact.</summary>
    public static IReadOnlyList<ToolParameter> Parameters(string inputSchema)
    {
        var parameters = new List<ToolParameter>();
        try
        {
            using var doc = JsonDocument.Parse(inputSchema);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return parameters;

            var required = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                foreach (var item in req.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } r) required.Add(r);

            if (root.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                foreach (var prop in props.EnumerateObject())
                    parameters.Add(ToolParameter.FromSchema(
                        prop.Name, prop.Value.GetRawText(), required.Contains(prop.Name)));
        }
        catch (JsonException)
        {
            // A schema we can't read means a tool called with no declared arguments, not a lost tool.
        }
        return parameters;
    }

    /// <summary>
    /// Flatten a <c>tools/call</c> result into a tool output. Text blocks are the result; a block Smarty has no
    /// channel for (an image, audio) is named rather than dropped, so the model knows something came back it
    /// can't see instead of concluding the call returned nothing.
    /// </summary>
    public static ToolOutput Interpret(JsonElement result, int maxResultChars = 20_000, ImageSink? images = null)
    {
        if (result.ValueKind != JsonValueKind.Object)
            return ToolOutput.Error("the MCP server returned no result.");

        bool isError = result.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True;
        var text = new StringBuilder();

        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                var piece = Describe(block, images);
                if (piece.Length == 0) continue;
                if (text.Length > 0) text.Append('\n');
                text.Append(piece);
            }

        // Newer servers may answer with structured data and no prose. It's the result either way.
        if (text.Length == 0 && result.TryGetProperty("structuredContent", out var structured) &&
            structured.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            text.Append(structured.GetRawText());

        if (text.Length == 0)
            return isError
                ? ToolOutput.Error("the MCP server reported an error but gave no detail.")
                : ToolOutput.Ok("(the tool returned no content.)");

        var flat = Cap(text.ToString(), maxResultChars);
        return isError ? ToolOutput.Error(flat) : ToolOutput.Ok(flat);
    }

    private static string Describe(JsonElement block, ImageSink? images = null)
    {
        string kind = String(block, "type") ?? "";
        switch (kind)
        {
            case "text":
                return String(block, "text") ?? "";

            case "image":
            {
                // Base64 in a transcript is worse than useless — it fills the context with something the model
                // can't decode. But an image is often the answer ("what does the page look like"), so hand it to
                // the sink and return a markdown image the chat renders. Only the URL enters the transcript.
                string mime = String(block, "mimeType") ?? "image/png";
                var data = String(block, "data");
                if (images is not null && data is { Length: > 0 })
                {
                    try
                    {
                        if (images(Convert.FromBase64String(data), mime) is { Length: > 0 } url)
                            // The URL is the useful part, so it is stated as a value and not only as markdown: a
                            // picture fetched to put in a document is data the model has to pass on, and a bare
                            // ![](…) leaves it guessing whether it may. What it still cannot do is see the thing.
                            return $"![image]({url})\n(Stored and shown to the user. Its URL is {url} — use that " +
                                   "wherever you need this picture, including in a presentation. You cannot see " +
                                   "it, so describe only what other tools told you, never what you assume it shows.)";
                    }
                    catch (FormatException) { /* not decodable — fall through to describing it */ }
                }
                return $"[image: {mime}, {data?.Length ?? 0} base64 characters — not viewable through this tool channel]";
            }
            case "audio":
            {
                int size = String(block, "data")?.Length ?? 0;
                string mime = String(block, "mimeType") ?? "unknown";
                return $"[{kind}: {mime}, {size} base64 characters — not viewable through this tool channel]";
            }

            case "resource":
            {
                if (!block.TryGetProperty("resource", out var resource) || resource.ValueKind != JsonValueKind.Object)
                    return "[resource]";
                if (String(resource, "text") is { Length: > 0 } embedded) return embedded;
                return $"[resource: {String(resource, "uri") ?? "unknown"} ({String(resource, "mimeType") ?? "unknown type"})]";
            }

            case "resource_link":
                return $"[resource: {String(block, "uri") ?? "unknown"}]";

            default:
                // An unknown block type is still information; hand over its JSON rather than swallowing it.
                return block.GetRawText();
        }
    }

    private static string Cap(string value, int max)
    {
        if (max <= 0 || value.Length <= max) return value;
        return value[..max] + $"\n\n[…truncated: {value.Length - max} more characters. Narrow the call — a filter, " +
               "a smaller budget or a pattern — if you need the rest.]";
    }

    private static string? String(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
