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
        DumpPrompt(payload);

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
            int cachedCount = 0;      // input tokens served from the provider's prompt cache
            int reasoningCount = 0;   // output tokens spent thinking rather than answering

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

                            // Cache hits and thinking tokens, which the billing actually turns on: cached input is
                            // ~4x cheaper, and reasoning tokens are charged as output even when the user never
                            // sees them.
                            if (usageEl.TryGetProperty("prompt_tokens_details", out var pd) && pd.ValueKind == JsonValueKind.Object
                                && pd.TryGetProperty("cached_tokens", out var cachedEl) && cachedEl.ValueKind == JsonValueKind.Number)
                                cachedCount = cachedEl.GetInt32();
                            if (usageEl.TryGetProperty("completion_tokens_details", out var cd) && cd.ValueKind == JsonValueKind.Object
                                && cd.TryGetProperty("reasoning_tokens", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.Number)
                                reasoningCount = reasonEl.GetInt32();
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
                CachedInputTokens = cachedCount,
                ReasoningTokens = reasoningCount,
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

    /// <summary>
    /// Models that answered 400 to <c>reasoning_effort</c>. Remembered per model so the round trip is paid once,
    /// the same way the Ollama provider remembers which models reject <c>think</c>.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> NoEffortModels = new();

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
            // 500 belongs here as much as 503 does. It was missing, and a plain "Internal server error" from
            // Together — which it returns often enough under load — killed the task outright on the first
            // attempt: a five-minute research job lost in one second to somebody else's bad minute. Their 5xx is
            // their server failing, and the same request usually works moments later. 502/504 are their edge
            // saying the same thing. A 4xx stays fatal: that's our request being wrong, and repeating it just
            // repeats the mistake.
            // A model that doesn't take reasoning_effort says so with a 400. Drop it, remember that, and go
            // again — a leg must not die because we asked a model to think less and it had never heard of the
            // idea. Same shape as the Ollama provider's handling of `think`.
            if (status == 400 && payload.ContainsKey("reasoning_effort"))
            {
                var complaint = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (complaint.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase))
                {
                    NoEffortModels[payload["model"]?.GetValue<string>() ?? ""] = true;
                    payload.Remove("reasoning_effort");
                    response.Dispose();
                    continue;
                }
            }

            bool transient = status is 429 or 500 or 502 or 503 or 504;
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

        // Greedy by default — deterministic, and what reliable tool-calling wants. Raised only by a retry that
        // is recovering from a detected loop, because re-running a collapsed context greedily reproduces the
        // collapse exactly.
        payload["temperature"] = request.Temperature is { } t && t > 0 ? t : 0.0;

        // Thinking, at last actually controlled here.
        //
        // Think was honoured only by the Ollama provider, so every `Think = false` in the system — the re-voice,
        // the one-word classifications, the "keep it fast" calls — was a comment rather than an instruction on
        // any model reached through Together, and reasoning ran at full length on all of them.
        //
        // Sent ONLY when thinking was declined. There is no off switch on these models, just a floor ("low"),
        // and staying silent when reasoning IS wanted leaves the deployment's own default alone — so this
        // changes exactly the calls that already said they didn't need it.
        if (!request.Think && !NoEffortModels.ContainsKey(request.Model))
            payload["reasoning_effort"] = "low";

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

    private static JsonObject SerializeTool(AgentTool tool) => ToolSchema.Function(tool);

    private static int _dumpSeq;

    /// <summary>
    /// Write each outgoing prompt to disk when <c>SMARTY_DUMP_PROMPTS</c> is set, so consecutive requests can be
    /// diffed to find where the cacheable prefix breaks.
    /// </summary>
    /// <remarks>
    /// Prompt caching is prefix matching: the provider reuses the longest identical head of the prompt, so a
    /// single volatile byte near the front costs the cache for everything behind it. That failure is invisible
    /// from the outside — you just see cached_tokens stay at zero — and impossible to reason about reliably in a
    /// prompt assembled from a dozen contributing parts. So: dump, diff, fix.
    /// </remarks>
    private static void DumpPrompt(JsonObject payload)
    {
        if (Environment.GetEnvironmentVariable("SMARTY_DUMP_PROMPTS") is not "1") return;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "smarty-prompts");
            Directory.CreateDirectory(dir);
            int seq = Interlocked.Increment(ref _dumpSeq);
            // A unique name per request: concurrent calls (the planner and the worker overlap) were colliding on
            // the same path and losing all but one dump.
            var file = $"req-{seq:D4}-{DateTime.Now:HHmmss-fff}-{Guid.NewGuid():N}".Substring(0, 34) + ".json";
            File.WriteAllText(Path.Combine(dir, file), payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* diagnostics must never break a request */ }
    }

    private sealed class ToolCallBuilder
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}
