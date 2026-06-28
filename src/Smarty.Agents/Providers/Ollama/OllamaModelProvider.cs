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

    // Models that rejected the `think` parameter (e.g. Gemma). Learned at runtime on the first 400 so we stop
    // sending `think` to them — making the provider work with non-thinking models, not just qwen.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> NoThinkModels = new();

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

        async Task<HttpResponseMessage> SendAsync(JsonObject body)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            return await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }

        var httpResponse = await SendAsync(payload).ConfigureAwait(false);

        // Some models (e.g. Gemma) reject the `think` parameter outright with a 400. Detect that, remember it
        // for this model so we never pay the round-trip again, and retry without thinking — keeping the provider
        // model-agnostic rather than hard-failing on any non-thinking model.
        if (httpResponse.StatusCode == System.Net.HttpStatusCode.BadRequest && payload.ContainsKey("think"))
        {
            var err = await httpResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (err.Contains("does not support thinking", StringComparison.OrdinalIgnoreCase))
            {
                NoThinkModels[request.Model] = true;
                payload.Remove("think");
                httpResponse.Dispose();
                httpResponse = await SendAsync(payload).ConfigureAwait(false);
            }
            else
            {
                httpResponse.Dispose();
                throw new InvalidOperationException($"Ollama request failed (400 Bad Request): {err}");
            }
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            var error = await httpResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            httpResponse.Dispose();
            throw new InvalidOperationException(
                $"Ollama request failed ({(int)httpResponse.StatusCode} {httpResponse.ReasonPhrase}): {error}");
        }

        try
        {
        await using var stream = await httpResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        // A per-turn time limit (so a stuck generation can't run forever).
        using var timeoutCts = new CancellationTokenSource();
        if (request.TurnTimeout is { } timeout && timeout > TimeSpan.Zero)
            timeoutCts.CancelAfter(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var toolCalls = new List<ToolCall>();
        int promptEvalCount = 0;
        int evalCount = 0;

        // Detection buffer over the live token stream (reasoning + content) to catch repetition loops.
        var recent = new StringBuilder();
        int sinceCheck = 0;
        var finish = FinishReason.Stop;

        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                finish = FinishReason.Timeout; // our time limit, not a caller abort
                break;
            }

            if (line is null) break;
            if (line.Length == 0) continue;

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

                if (root.TryGetProperty("prompt_eval_count", out var pec) && pec.ValueKind == JsonValueKind.Number)
                {
                    promptEvalCount = pec.GetInt32();
                }
                if (root.TryGetProperty("eval_count", out var ec) && ec.ValueKind == JsonValueKind.Number)
                {
                    evalCount = ec.GetInt32();
                }

                if (root.TryGetProperty("done_reason", out var dr) && dr.ValueKind == JsonValueKind.String &&
                    string.Equals(dr.GetString(), "length", StringComparison.OrdinalIgnoreCase))
                {
                    finish = FinishReason.Length;
                }
            }

            if (reasoningDelta is not null)
                yield return new ModelStreamEvent.Reasoning(reasoningDelta);
            if (contentDelta is not null)
                yield return new ModelStreamEvent.Content(contentDelta);

            // Watch for a degenerate repetition loop and cut the generation off early.
            string? delta = reasoningDelta ?? contentDelta;
            if (delta is not null)
            {
                recent.Append(delta);
                sinceCheck += delta.Length;
                if (recent.Length > LoopWindow)
                    recent.Remove(0, recent.Length - LoopWindow);

                if (sinceCheck >= LoopCheckEvery && recent.Length >= LoopMinLength)
                {
                    sinceCheck = 0;
                    if (LooksLikeLoop(recent))
                    {
                        finish = FinishReason.Loop;
                        break; // disposing the response cancels the Ollama generation
                    }
                }
            }
        }

        TokenTracker.Record(promptEvalCount, evalCount);
        try
        {
            File.WriteAllText("token_usage.json", $"{{\"Input\": {TokenTracker.TotalInputTokens}, \"Output\": {TokenTracker.TotalOutputTokens}}}");
        }
        catch {}

        var response = new ModelResponse
        {
            Content = content.Length > 0 ? content.ToString() : null,
            Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null,
            ToolCalls = toolCalls,
            Finish = finish,
            InputTokens = promptEvalCount,
            OutputTokens = evalCount,
        };
        yield return new ModelStreamEvent.Completed(response);
        }
        finally
        {
            httpResponse.Dispose();
        }
    }

    // Loop detection: treat the tail as degenerate if a short cycle (period 3..160) repeats
    // back-to-back many times — exactly the "X = 78, X = 78, X = 78…" failure mode.
    private const int LoopWindow = 1600;
    private const int LoopCheckEvery = 160;
    private const int LoopMinLength = 240;

    private static bool LooksLikeLoop(StringBuilder sb)
    {
        int n = sb.Length;
        const int repeats = 6;
        for (int p = 3; p <= 160; p++)
        {
            if (n < p * repeats) continue;
            bool same = true;
            for (int i = n - 1; i >= n - p * repeats; i--)
            {
                if (sb[i] != sb[i - p]) { same = false; break; }
            }
            if (same) return true;
        }
        return false;
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
            ["messages"] = messages,
        };
        // Only send `think` to models that accept it; once a model 400s on it, it's remembered and skipped.
        if (!NoThinkModels.ContainsKey(request.Model))
            payload["think"] = request.Think;

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
                tools.Add(SerializeTool(tool));
            payload["tools"] = tools;
        }

        // Structured outputs: when a schema is supplied, Ollama constrains generation to valid JSON
        // matching it. Round-trip so the caller's node isn't reparented into our payload.
        if (request.ResponseFormat is { } format)
            payload["format"] = JsonNode.Parse(format.ToJsonString());

        var options = new JsonObject { ["repeat_last_n"] = 256, ["temperature"] = 0.0, ["num_ctx"] = 12288 };
        if (request.RepeatPenalty is { } rp && rp > 0)
            options["repeat_penalty"] = rp;
        if (request.MaxOutputTokens is { } np && np > 0)
            options["num_predict"] = np;
        payload["options"] = options;

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
