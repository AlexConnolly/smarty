using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Smarty.Agents;

/// <summary>
/// <see cref="IModelProvider"/> backed by the local Ollama gateway (<c>POST /api/chat</c>),
/// streaming NDJSON chunks.
///
/// qwen3 is a thinking model: we request <c>think:true</c> so the chain-of-thought streams
/// in a separate <c>thinking</c> field (surfaced as <see cref="ModelStreamEvent.Reasoning"/>),
/// leaving <c>content</c> as the clean answer stream (<see cref="ModelStreamEvent.Content"/>).
/// Tool calls arrive in <c>message.tool_calls[].function</c> with a parsed argument object.
/// </summary>
public sealed class OllamaModelProvider : IModelProvider
{
    public const string DefaultBaseUrl = "http://localhost:11434";

    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public OllamaModelProvider(string? baseUrl = null, HttpClient? http = null)
    {
        _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _http = http ?? Shared;
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var payload = BuildPayload(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var httpResponse = await _http
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var error = await httpResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Ollama request failed ({(int)httpResponse.StatusCode} {httpResponse.ReasonPhrase}): {error}");
        }

        await using var stream = await httpResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var toolCalls = new List<ToolCall>();

        while (true)
        {
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (line.Length == 0) continue;

            // Parse the chunk synchronously, extract the deltas, then yield outside the using.
            string? contentDelta = null;
            string? reasoningDelta = null;

            using (var doc = JsonDocument.Parse(line))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("message", out var message))
                {
                    if (message.TryGetProperty("thinking", out var t) &&
                        t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } think)
                    {
                        reasoningDelta = think;
                        reasoning.Append(think);
                    }

                    if (message.TryGetProperty("content", out var c) &&
                        c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } chunk)
                    {
                        contentDelta = chunk;
                        content.Append(chunk);
                    }

                    if (message.TryGetProperty("tool_calls", out var calls) &&
                        calls.ValueKind == JsonValueKind.Array)
                    {
                        ExtractToolCalls(calls, toolCalls);
                    }
                }
            }

            if (reasoningDelta is not null)
                yield return new ModelStreamEvent.Reasoning(reasoningDelta);
            if (contentDelta is not null)
                yield return new ModelStreamEvent.Content(contentDelta);
        }

        var response = new ModelResponse
        {
            Content = content.Length > 0 ? content.ToString() : null,
            Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null,
            ToolCalls = toolCalls,
        };
        yield return new ModelStreamEvent.Completed(response);
    }

    private static void ExtractToolCalls(JsonElement calls, List<ToolCall> sink)
    {
        foreach (var call in calls.EnumerateArray())
        {
            if (!call.TryGetProperty("function", out var fn))
                continue;

            string name = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(name))
                continue;

            JsonElement arguments = fn.TryGetProperty("arguments", out var a) ? a.Clone() : default;

            string id = call.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()!
                : $"call_{sink.Count}";

            sink.Add(new ToolCall(id, name, arguments));
        }
    }

    private static JsonObject BuildPayload(ModelRequest request)
    {
        var messages = new JsonArray();

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt });

        foreach (var message in request.Messages)
            messages.Add(SerializeMessage(message));

        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["stream"] = true,
            ["think"] = true,
            ["messages"] = messages,
        };

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
                tools.Add(SerializeTool(tool));
            payload["tools"] = tools;
        }

        return payload;
    }

    private static JsonObject SerializeMessage(Message message)
    {
        var role = message.Role switch
        {
            Role.System => "system",
            Role.User => "user",
            Role.Assistant => "assistant",
            Role.Tool => "tool",
            _ => "user",
        };

        var obj = new JsonObject { ["role"] = role, ["content"] = message.Content ?? "" };

        if (message.Role == Role.Tool && message.ToolName is not null)
            obj["tool_name"] = message.ToolName;

        if (message.ToolCalls is { Count: > 0 })
        {
            var calls = new JsonArray();
            foreach (var call in message.ToolCalls)
            {
                calls.Add(new JsonObject
                {
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = JsonNode.Parse(call.Arguments.ToString()),
                    },
                });
            }
            obj["tool_calls"] = calls;
        }

        return obj;
    }

    private static JsonObject SerializeTool(AgentTool tool)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var p in tool.Parameters)
        {
            properties[p.Name] = new JsonObject
            {
                ["type"] = p.Type,
                ["description"] = p.Description,
            };
            if (p.Required)
                required.Add(p.Name);
        }

        var parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0)
            parameters["required"] = required;

        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = parameters,
            },
        };
    }
}
