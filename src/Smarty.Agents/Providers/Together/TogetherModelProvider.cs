using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Smarty.Agents;

/// <summary>
/// <see cref="IModelProvider"/> backed by the Together AI chat completions API,
/// using an OpenAI-compatible endpoint.
/// </summary>
public sealed class TogetherModelProvider : IModelProvider
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    public TogetherModelProvider(string apiKey, string? baseUrl = null, HttpClient? http = null)
    {
        _apiKey = apiKey;
        _baseUrl = (baseUrl ?? "https://api.together.xyz/v1").TrimEnd('/');
        _http = http ?? Shared;
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException("Together API key is missing. Please set the TOGETHER_API_KEY environment variable or configuration.");
        }

        var payload = BuildPayload(request);

        // The throttle/outage arrives BEFORE any token streams, so retrying here is safe (nothing has been
        // yielded yet) and far cheaper than throwing — a thrown 429 makes the orchestrator restart the whole
        // multi-minute leg, which is exactly how one rate-limit turned a build into a 30-minute ordeal.
        var httpResponse = await SendWithRetryAsync(payload, ct).ConfigureAwait(false);

        try
        {
            await using var stream = await httpResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            using var timeoutCts = new CancellationTokenSource();
            if (request.TurnTimeout is { } timeout && timeout > TimeSpan.Zero)
                timeoutCts.CancelAfter(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var content = new StringBuilder();
            var reasoning = new StringBuilder();
            var toolCallBuilders = new Dictionary<int, ToolCallBuilder>();
            int promptEvalCount = 0;
            int evalCount = 0;

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
                    finish = FinishReason.Timeout;
                    break;
                }

                if (line is null) break;
                line = line.Trim();
                if (line.Length == 0) continue;

                if (!line.StartsWith("data: ")) continue;
                string data = line.Substring(6).Trim();
                if (data == "[DONE]") break;

                string? contentDelta = null;
                string? reasoningDelta = null;

                using (var doc = JsonDocument.Parse(data))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                        {
                            var choice = choices[0];
                            if (choice.ValueKind == JsonValueKind.Object)
                            {
                                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                                {
                                    if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                                    {
                                        string text = c.GetString() ?? "";
                                        if (text.Length > 0)
                                        {
                                            contentDelta = text;
                                            content.Append(text);
                                        }
                                    }

                                    if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                                    {
                                        string text = rc.GetString() ?? "";
                                        if (text.Length > 0)
                                        {
                                            reasoningDelta = text;
                                            reasoning.Append(text);
                                        }
                                    }
                                    else if (delta.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String)
                                    {
                                        string text = r.GetString() ?? "";
                                        if (text.Length > 0)
                                        {
                                            reasoningDelta = text;
                                            reasoning.Append(text);
                                        }
                                    }

                                    if (delta.TryGetProperty("tool_calls", out var toolCallsEl) && toolCallsEl.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var tc in toolCallsEl.EnumerateArray())
                                        {
                                            if (tc.ValueKind != JsonValueKind.Object) continue;
                                            int index = tc.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number ? idxEl.GetInt32() : 0;
                                            if (!toolCallBuilders.TryGetValue(index, out var builder))
                                            {
                                                builder = new ToolCallBuilder();
                                                toolCallBuilders[index] = builder;
                                            }
                                            if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                                            {
                                                builder.Id = idEl.GetString();
                                            }
                                            if (tc.TryGetProperty("function", out var fnEl) && fnEl.ValueKind == JsonValueKind.Object)
                                            {
                                                if (fnEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                                                {
                                                    builder.Name = nameEl.GetString();
                                                }
                                                if (fnEl.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
                                                {
                                                    builder.Arguments.Append(argsEl.GetString());
                                                }
                                            }
                                        }
                                    }
                                }

                                if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                                {
                                    string frStr = fr.GetString() ?? "";
                                    if (string.Equals(frStr, "length", StringComparison.OrdinalIgnoreCase))
                                    {
                                        finish = FinishReason.Length;
                                    }
                                }
                            }
                        }

                        if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                        {
                            if (usageEl.TryGetProperty("prompt_tokens", out var ptEl) && ptEl.ValueKind == JsonValueKind.Number)
                                promptEvalCount = ptEl.GetInt32();
                            if (usageEl.TryGetProperty("completion_tokens", out var ctEl) && ctEl.ValueKind == JsonValueKind.Number)
                                evalCount = ctEl.GetInt32();
                        }
                    }
                }

                if (reasoningDelta is not null)
                    yield return new ModelStreamEvent.Reasoning(reasoningDelta);
                if (contentDelta is not null)
                    yield return new ModelStreamEvent.Content(contentDelta);

                string? loopDelta = reasoningDelta ?? contentDelta;
                if (loopDelta is not null)
                {
                    recent.Append(loopDelta);
                    sinceCheck += loopDelta.Length;
                    if (recent.Length > LoopWindow)
                        recent.Remove(0, recent.Length - LoopWindow);

                    if (sinceCheck >= LoopCheckEvery && recent.Length >= LoopMinLength)
                    {
                        sinceCheck = 0;
                        if (LooksLikeLoop(recent))
                        {
                            finish = FinishReason.Loop;
                            break;
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

            var finalToolCalls = new List<ToolCall>();
            foreach (var kvp in toolCallBuilders.OrderBy(x => x.Key))
            {
                var b = kvp.Value;
                string name = b.Name ?? "";
                string id = b.Id ?? $"call_{finalToolCalls.Count}";
                string argsStr = b.Arguments.ToString().Trim();
                JsonElement argsJson = default;
                if (!string.IsNullOrEmpty(argsStr))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(argsStr);
                        argsJson = doc.RootElement.Clone();
                    }
                    catch
                    {
                    }
                }

                if (argsJson.ValueKind == JsonValueKind.Undefined)
                {
                    using var doc = JsonDocument.Parse("{}");
                    argsJson = doc.RootElement.Clone();
                }

                finalToolCalls.Add(new ToolCall(id, name, argsJson));
            }

            var response = new ModelResponse
            {
                Content = content.Length > 0 ? content.ToString() : null,
                Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null,
                ToolCalls = finalToolCalls,
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

    // Together throttles (429) and occasionally 503s under load. Retry the initial request a few times,
    // honouring the server's Retry-After when present and otherwise backing off exponentially, before giving
    // up so a real outage still surfaces. A fresh HttpRequestMessage is built per attempt (they can't be
    // re-sent), and only the pre-stream response is retried — never a stream that has already emitted tokens.
    private const int MaxSendAttempts = 4;

    private async Task<HttpResponseMessage> SendWithRetryAsync(JsonObject payload, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

            var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;

            int status = (int)response.StatusCode;
            bool transient = status is 429 or 503;
            if (!transient || attempt >= MaxSendAttempts)
            {
                var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                response.Dispose();
                throw new InvalidOperationException(
                    $"Together AI request failed ({status} {response.ReasonPhrase}): {error}");
            }

            var delay = RetryDelay(response, attempt);
            response.Dispose();
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        // Prefer the server's Retry-After (a delta in seconds or an HTTP-date); else exponential backoff
        // 2s, 4s, 8s … Either way capped at 60s so a throttle never stalls a leg for minutes.
        var cap = TimeSpan.FromSeconds(60);
        if (response.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta is { } d && d > TimeSpan.Zero) return d < cap ? d : cap;
            if (ra.Date is { } when && when - DateTimeOffset.UtcNow is { } until && until > TimeSpan.Zero)
                return until < cap ? until : cap;
        }
        return TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 60));
    }

    private JsonObject BuildPayload(ModelRequest request)
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

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
                tools.Add(SerializeTool(tool));
            payload["tools"] = tools;
        }

        if (request.ResponseFormat is { } format)
        {
            payload["response_format"] = new JsonObject
            {
                ["type"] = "json_object",
                ["schema"] = JsonNode.Parse(format.ToJsonString())
            };
        }

        if (request.MaxOutputTokens is { } maxTokens && maxTokens > 0)
            payload["max_tokens"] = maxTokens;

        if (request.RepeatPenalty is { } rp && rp > 0)
            payload["repetition_penalty"] = rp;

        payload["temperature"] = 0.0;

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

        var obj = new JsonObject 
        { 
            ["role"] = role,
            ["content"] = message.Content ?? "" 
        };

        if (message.Role == Role.Tool)
        {
            obj["tool_call_id"] = message.ToolCallId ?? "";
        }

        if (message.ToolCalls is { Count: > 0 })
        {
            var calls = new JsonArray();
            foreach (var call in message.ToolCalls)
            {
                calls.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.Arguments.ToString()
                    }
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

    private sealed class ToolCallBuilder
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}
