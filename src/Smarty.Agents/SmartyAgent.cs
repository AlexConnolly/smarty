using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Smarty.Agents;

/// <summary>
/// A smart agent: takes an <see cref="AgentInput"/> (conversation + tools + system prompt +
/// model spec) and produces a textual answer, calling tools as needed along the way.
///
/// <code>
/// // Buffered:
/// string answer = await new SmartyAgent(input).Answer("What is the current system status?");
///
/// // Streamed (output only, no chain-of-thought):
/// await foreach (var ev in new SmartyAgent(input).AnswerStream("..."))
///     if (ev is AgentEvent.ContentDelta d) Console.Write(d.Text);
/// </code>
/// </summary>
public sealed class SmartyAgent
{
    private readonly AgentInput _input;
    private readonly IModelProvider _provider;
    private readonly IModelProvider _secondaryProvider;
    private readonly Dictionary<string, AgentTool> _tools;

    public SmartyAgent(AgentInput input, ModelProviderRegistry? registry = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        var reg = registry ?? ModelProviderRegistry.Default;
        _provider = reg.Resolve(input.Model);
        _secondaryProvider = reg.Resolve(input.SecondaryModel ?? input.Model);
        _tools = input.Tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The transcript (including tool calls/results) of the most recent run.</summary>
    public AgentRun? LastRun { get; private set; }

    /// <summary>Ask the agent a question and stream the answer as it is written.</summary>
    public async IAsyncEnumerable<AgentEvent> AnswerStream(
        string userMessage, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (userMessage is null) throw new ArgumentNullException(nameof(userMessage));

        var conversation = _input.Conversation;
        conversation.Add(Message.User(userMessage));

        var run = new AgentRun();
        LastRun = run;

        int toolFailures = 0; // cumulative failed tool calls this run — drives the failure budget
        var toolCallCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // per-tool call budget
        var successfulToolCalls = new HashSet<string>(); // exact (name+args) signatures that succeeded

        int currentMaxIterations = _input.MaxIterations;
        int currentMaxCallsPerTool = _input.MaxCallsPerTool;
        int currentMaxToolFailures = _input.MaxToolFailures;
        bool isDeadEndWrapUp = false;

        for (int iteration = 0; iteration < currentMaxIterations; iteration++)
        {
            var seenToolCallsInThisTurn = new HashSet<string>();
            // Pull in any out-of-band messages a caller queued while we were working, so a long task
            // can be steered or interrupted between turns rather than only before it starts.
            if (_input.DrainInbox is { } drain)
            {
                foreach (var injected in drain())
                {
                    conversation.Add(injected);
                    run.Messages.Add(injected);
                }
            }

            var request = new ModelRequest
            {
                Model = _input.Model.Model,
                SystemPrompt = _input.SystemPrompt,
                Messages = conversation,
                Tools = _input.Tools,
                MaxOutputTokens = _input.MaxOutputTokensPerTurn,
                RepeatPenalty = _input.RepeatPenalty,
                TurnTimeout = _input.TurnTimeout,
                Think = _input.Think,
            };

            ModelResponse? final = null;
            bool streamedContent = false;
            await foreach (var ev in _provider.StreamAsync(request, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                switch (ev)
                {
                    case ModelStreamEvent.Content content:
                        streamedContent = true;
                        yield return new AgentEvent.ContentDelta(content.Text);
                        break;
                    case ModelStreamEvent.Reasoning reasoning:
                        yield return new AgentEvent.ReasoningDelta(reasoning.Text);
                        break;
                    case ModelStreamEvent.Completed completed:
                        final = completed.Response;
                        break;
                }
            }

            final ??= new ModelResponse();

            // Loop/timeout recovery: if the model got stuck (degenerate repetition or ran past the time
            // limit) and produced no tool call, don't treat the runaway turn as the answer. Discard it,
            // nudge the model to conclude using what it already has, and try again. Everything before
            // this turn (prior tool calls/results) stays in the conversation.
            // A turn that produced NO answer and NO tool call is a dead end — it looped, timed out, hit
            // the token cap, or finished with its answer trapped in the thinking channel (no real
            // content). Nudge the model to actually answer the user, and retry. (final.Finish carries
            // the specific reason if needed; for recovery, "no answer + no tool" is what matters.)
            bool noAnswer = string.IsNullOrWhiteSpace(final.Content);
            if (noAnswer && !final.HasToolCalls && _input.RecoverFromLoops && iteration < currentMaxIterations - 1)
            {
                var recovery = Message.System(_input.LoopRecoveryNudge);
                conversation.Add(recovery);
                run.Messages.Add(recovery);
                continue;
            }

            // Some models (notably small qwen) sometimes "chat" the tool call — emitting it as JSON
            // text in the content instead of as a structured call. Recover it so we actually run the
            // command rather than printing it back to the user.
            IReadOnlyList<ToolCall> toolCalls = final.ToolCalls;
            string answerText = final.Content ?? "";
            if (toolCalls.Count == 0 && TryExtractInlineToolCalls(answerText, out var inlineCalls, out var cleaned))
            {
                toolCalls = inlineCalls;
                answerText = cleaned;
            }

            var assistantMessage = Message.Assistant(
                answerText, final.Reasoning, toolCalls.Count > 0 ? toolCalls : null);
            conversation.Add(assistantMessage);
            run.Messages.Add(assistantMessage);

            if (toolCalls.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(answerText))
                {
                    yield return new AgentEvent.Completed(answerText);
                    yield break;
                }
                // No tool call AND no answer text (the model reasoned but said nothing, on the final
                // iteration where loop-recovery no longer kicks in). Don't return an empty result — fall
                // through to the graceful "couldn't finish" message so the agent admits defeat rather than
                // handing the caller a blank to invent around.
                break;
            }

            // This turn is a tool-call turn — discard any answer text we optimistically streamed
            // (it was the inline tool call / preamble, not the final answer).
            if (streamedContent)
                yield return new AgentEvent.ContentCleared();

            bool anySuccess = false, anyRetryable = false, anyTerminal = false;
            foreach (var call in toolCalls)
            {
                yield return new AgentEvent.ToolStarted(call.Name, call.Arguments.ToString());

                // Structural blocks — BEFORE running the tool — so the model can't burn the run repeating a
                // call or hammering one tool forever. These catch the failure mode where every call "succeeds"
                // with useless results (so the failure budget never trips): a relentless, going-nowhere loop.
                string signature = call.Name + "|" + call.Arguments.ToString().ToLowerInvariant();
                int priorCalls = toolCallCounts.TryGetValue(call.Name, out var cc) ? cc : 0;
                ToolOutput output;
                if (!seenToolCallsInThisTurn.Add(signature))
                {
                    // Exact repeat in the same turn
                    output = ToolOutput.DeadEnd(
                        $"You already ran {call.Name} with these exact arguments in this turn. Running it " +
                        "again changes nothing.");
                }
                else if (successfulToolCalls.Contains(signature))
                {
                    // Exact repeat of a successful call from a prior turn
                    output = ToolOutput.DeadEnd(
                        $"You already successfully ran {call.Name} with these exact arguments — its result is above. Running it " +
                        "again changes nothing. Use what you already have, take a genuinely different approach, or " +
                        "stop and give your best answer (or say plainly you couldn't get it).");
                }
                else if (priorCalls >= currentMaxCallsPerTool)
                {
                    if (_input.EnableSupervisorGuard)
                    {
                        var decision = await RunSupervisorGuardAsync(run, ct).ConfigureAwait(false);
                        if (decision == SupervisorDecision.Progress)
                        {
                            currentMaxCallsPerTool += Math.Max(2, _input.MaxCallsPerTool / 2);
                            currentMaxToolFailures += Math.Max(2, _input.MaxToolFailures / 2);
                            if (currentMaxIterations - iteration <= 2)
                            {
                                currentMaxIterations += Math.Max(4, _input.MaxIterations / 2);
                                var turnNudge = Message.System(_input.SupervisorProgressNudge);
                                conversation.Add(turnNudge);
                                run.Messages.Add(turnNudge);
                            }

                            toolCallCounts[call.Name] = priorCalls + 1;
                            output = await ExecuteToolAsync(call, ct).ConfigureAwait(false);
                            if (!output.IsError)
                            {
                                successfulToolCalls.Add(signature);
                            }
                        }
                        else
                        {
                            output = ToolOutput.DeadEnd(
                                $"You've called {call.Name} {priorCalls} times this run and you're not getting there. STOP " +
                                $"calling {call.Name} now — work with what you've already got and either give your best " +
                                "answer or say plainly you couldn't find it. Don't call it again.");
                            
                            if (!isDeadEndWrapUp)
                            {
                                isDeadEndWrapUp = true;
                                currentMaxIterations = iteration + 3; // current + 2 more
                                var wrapUpNudge = Message.System(_input.SupervisorDeadEndNudge);
                                conversation.Add(wrapUpNudge);
                                run.Messages.Add(wrapUpNudge);
                            }
                        }
                    }
                    else
                    {
                        output = ToolOutput.DeadEnd(
                            $"You've called {call.Name} {priorCalls} times this run and you're not getting there. STOP " +
                            $"calling {call.Name} now — work with what you've already got and either give your best " +
                            "answer or say plainly you couldn't find it. Don't call it again.");
                    }
                }
                else
                {
                    toolCallCounts[call.Name] = priorCalls + 1;
                    output = await ExecuteToolAsync(call, ct).ConfigureAwait(false);
                    if (!output.IsError)
                    {
                        successfulToolCalls.Add(signature);
                    }
                }
                run.ToolInvocations.Add(new ToolInvocation(call.Name, call.Arguments.ToString(), output.Content, output.IsError));

                var toolMessage = Message.ToolResult(call.Id, call.Name, output.Content);
                conversation.Add(toolMessage);
                run.Messages.Add(toolMessage);

                if (output.IsError)
                {
                    toolFailures++;
                    if (output.CanRetry) anyRetryable = true; else anyTerminal = true;
                }
                else anySuccess = true;

                yield return new AgentEvent.ToolCompleted(call.Name, output.Content);
            }

            // Steer the model on failure — but only when this turn produced NOTHING useful. If anything
            // succeeded, say nothing: the model already has data and shouldn't be pushed to "keep trying"
            // (that's what made a news worker chase more sources after it already had the headlines). When
            // there's no success: out of budget → conclude with what you have; only dead ends → don't repeat
            // them, go a different way; otherwise (transient) → fix and retry.
            if (_input.NudgeOnToolError && !anySuccess && (anyTerminal || anyRetryable))
            {
                string nudgeText =
                    toolFailures >= currentMaxToolFailures
                        ? "You've hit several failures and still have no usable result. STOP calling tools now — " +
                          "answer the user with whatever you DID manage to find, or tell them plainly you couldn't " +
                          "get it. Do not invent anything."
                        : anyTerminal && !anyRetryable
                            ? "That source is a DEAD END (a hard block / not retrievable) — retrying the same call " +
                              "will never work, so don't call it again. Try a genuinely different source or " +
                              "approach; if none is likely to work, answer with what you have or say you couldn't."
                            : _input.ToolErrorNudge;
                var nudge = Message.System(nudgeText);
                conversation.Add(nudge);
                run.Messages.Add(nudge);
            }

            if (_input.EnableSupervisorGuard && iteration == currentMaxIterations - 1 && !isDeadEndWrapUp)
            {
                var decision = await RunSupervisorGuardAsync(run, ct).ConfigureAwait(false);
                if (decision == SupervisorDecision.Progress)
                {
                    currentMaxIterations += Math.Max(4, _input.MaxIterations / 2);
                    currentMaxCallsPerTool += Math.Max(2, _input.MaxCallsPerTool / 2);
                    currentMaxToolFailures += Math.Max(2, _input.MaxToolFailures / 2);

                    var turnNudge = Message.System(_input.SupervisorProgressNudge);
                    conversation.Add(turnNudge);
                    run.Messages.Add(turnNudge);
                }
                else
                {
                    isDeadEndWrapUp = true;
                    currentMaxIterations = iteration + 3; // current + 2 more

                    var wrapUpNudge = Message.System(_input.SupervisorDeadEndNudge);
                    conversation.Add(wrapUpNudge);
                    run.Messages.Add(wrapUpNudge);
                }
            }
        }

        // Iterations exhausted — emit whatever text we last produced, or a graceful message if we
        // never got a clean answer (e.g. the model kept looping).
        var lastInvocation = run.ToolInvocations.LastOrDefault();
        if (lastInvocation is not null && lastInvocation.IsError)
        {
            yield return new AgentEvent.Completed(
                $"The task could not be completed because the tool '{lastInvocation.ToolName}' failed:\n{lastInvocation.Result}");
        }
        else
        {
            var lastText = run.Messages.LastOrDefault(m => m.Role == Role.Assistant)?.Content;
            yield return new AgentEvent.Completed(
                string.IsNullOrWhiteSpace(lastText)
                    ? "I got stuck and couldn't finish that — please try rephrasing your request."
                    : lastText);
        }
    }

    /// <summary>Ask the agent a question and get its final answer text (buffered).</summary>
    public async Task<string> Answer(string userMessage, CancellationToken ct = default)
        => (await AnswerStream(userMessage, ct).ReadAllAsync(ct).ConfigureAwait(false)).Text;

    /// <summary>Ask the agent a question and get the full drained result (answer + reasoning + tools).</summary>
    public Task<AgentAnswer> AnswerAll(string userMessage, CancellationToken ct = default)
        => AnswerStream(userMessage, ct).ReadAllAsync(ct);

    private async Task<ToolOutput> ExecuteToolAsync(ToolCall call, CancellationToken ct)
    {
        if (!_tools.TryGetValue(call.Name, out var tool))
            return ToolOutput.Error($"Error: no tool named '{call.Name}' is available. Available tools: " +
                                    $"{string.Join(", ", _tools.Keys)}. Use one of those.");

        try
        {
            return await tool.InvokeAsync(call.Arguments, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ToolOutput.Error($"Error executing tool '{call.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Detect tool call(s) a model emitted as inline JSON text or XML tags
    /// where the name matches an available tool. Returns the recovered calls and the content with the
    /// tool-call text stripped out.
    /// </summary>
    private bool TryExtractInlineToolCalls(string content, out List<ToolCall> calls, out string cleaned)
    {
        calls = new List<ToolCall>();
        cleaned = content;
        if (string.IsNullOrWhiteSpace(content))
            return false;

        // Try XML first
        if (content.Contains("<tool_call>"))
        {
            if (TryExtractXmlToolCalls(content, _tools, out var xmlCalls, out var xmlCleaned))
            {
                calls = xmlCalls;
                cleaned = xmlCleaned;
                return true;
            }
        }

        // Fall back to JSON
        if (!content.Contains("\"name\""))
            return false;

        var removals = new List<(int start, int length)>();
        int index = 0;
        foreach (var (start, end, obj) in ScanJsonObjects(content))
        {
            if (!obj.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                continue;
            string name = nameEl.GetString() ?? "";
            if (!_tools.ContainsKey(name))
                continue;

            JsonElement args = default;
            if (obj.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object)
                args = a.Clone();
            else if (obj.TryGetProperty("parameters", out var p) && p.ValueKind == JsonValueKind.Object)
                args = p.Clone();

            calls.Add(new ToolCall($"call_inline_{index++}", name, args));
            removals.Add((start, end - start));
        }

        if (calls.Count == 0)
            return false;

        foreach (var (start, length) in removals.OrderByDescending(r => r.start))
            cleaned = cleaned.Remove(start, length);
        cleaned = Regex.Replace(cleaned, "```[a-zA-Z]*\\s*```", "").Trim();
        return true;
    }

    private static bool TryExtractXmlToolCalls(string content, Dictionary<string, AgentTool> availableTools, out List<ToolCall> calls, out string cleaned)
    {
        calls = new List<ToolCall>();
        cleaned = content;
        
        if (string.IsNullOrWhiteSpace(content) || !content.Contains("<tool_call>"))
            return false;

        var removals = new List<(int start, int length)>();
        int index = 0;
        int searchStart = 0;
        while (true)
        {
            int startIdx = content.IndexOf("<tool_call>", searchStart);
            if (startIdx < 0) break;
            
            int endIdx = content.IndexOf("</tool_call>", startIdx);
            if (endIdx < 0)
            {
                endIdx = content.Length;
            }
            else
            {
                endIdx += "</tool_call>".Length;
            }

            int innerStart = startIdx + "<tool_call>".Length;
            int innerLength = (endIdx == content.Length) ? (content.Length - innerStart) : (endIdx - "</tool_call>".Length - innerStart);
            if (innerLength < 0) innerLength = 0;
            string inner = content.Substring(innerStart, innerLength).Trim();
            
            if (TryParseXmlToolCall(inner, availableTools, index++, out var toolCall))
            {
                calls.Add(toolCall);
                removals.Add((startIdx, endIdx - startIdx));
            }
            
            searchStart = endIdx;
            if (searchStart >= content.Length) break;
        }

        if (calls.Count == 0)
            return false;

        foreach (var (start, length) in removals.OrderByDescending(r => r.start))
        {
            cleaned = cleaned.Remove(start, length);
        }
        
        cleaned = Regex.Replace(cleaned, @"```[a-zA-Z]*\s*```", "").Trim();
        return true;
    }

    private static bool TryParseXmlToolCall(string inner, Dictionary<string, AgentTool> availableTools, int index, out ToolCall toolCall)
    {
        toolCall = default;
        
        int firstBracket = inner.IndexOf('<');
        string toolName = firstBracket >= 0 ? inner.Substring(0, firstBracket).Trim() : inner.Trim();
        
        if (string.IsNullOrEmpty(toolName) || !availableTools.ContainsKey(toolName))
            return false;

        var argsObj = new System.Text.Json.Nodes.JsonObject();
        int pos = firstBracket;
        while (pos >= 0 && pos < inner.Length)
        {
            int keyStart = inner.IndexOf("<arg_key>", pos);
            if (keyStart < 0) break;
            
            int keyEnd = inner.IndexOf("</arg_key>", keyStart);
            string keyName;
            string valStr = "";
            int nextPos = -1;

            if (keyEnd >= 0)
            {
                keyName = inner.Substring(keyStart + "<arg_key>".Length, keyEnd - (keyStart + "<arg_key>".Length)).Trim();
                
                int valStart = inner.IndexOf("<arg_value>", keyEnd);
                if (valStart >= 0)
                {
                    int valEnd = inner.IndexOf("</arg_value>", valStart);
                    if (valEnd >= 0)
                    {
                        valStr = inner.Substring(valStart + "<arg_value>".Length, valEnd - (valStart + "<arg_value>".Length));
                        nextPos = valEnd + "</arg_value>".Length;
                    }
                    else
                    {
                        valStr = inner.Substring(valStart + "<arg_value>".Length);
                        nextPos = inner.Length;
                    }
                }
                else
                {
                    nextPos = keyEnd + "</arg_key>".Length;
                }
            }
            else
            {
                int valEnd = inner.IndexOf("</arg_value>", keyStart);
                string segment;
                if (valEnd >= 0)
                {
                    segment = inner.Substring(keyStart + "<arg_key>".Length, valEnd - (keyStart + "<arg_key>".Length));
                    nextPos = valEnd + "</arg_value>".Length;
                }
                else
                {
                    segment = inner.Substring(keyStart + "<arg_key>".Length);
                    nextPos = inner.Length;
                }

                int arrowIdx = segment.IndexOf('→');
                if (arrowIdx >= 0)
                {
                    keyName = segment.Substring(0, arrowIdx).Trim();
                    valStr = segment.Substring(arrowIdx + 1);
                }
                else
                {
                    keyName = segment.Trim();
                }
            }

            if (!string.IsNullOrEmpty(keyName))
            {
                argsObj[keyName] = valStr;
            }
            
            pos = nextPos;
        }

        using var doc = JsonDocument.Parse(argsObj.ToJsonString());
        toolCall = new ToolCall($"call_xml_{index}", toolName, doc.RootElement.Clone());
        return true;
    }

    /// <summary>Yield every top-level brace-balanced JSON object found in a string (start, endExclusive, parsed).</summary>
    private static IEnumerable<(int start, int end, JsonElement obj)> ScanJsonObjects(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '{') continue;

            int depth = 0;
            bool inString = false, escaped = false;
            int matchEnd = -1;
            for (int j = i; j < s.Length; j++)
            {
                char c = s[j];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                }
                else if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) { matchEnd = j; break; }
                }
            }

            if (matchEnd < 0)
                yield break; // unbalanced — nothing more to find

            JsonElement? parsed = null;
            try
            {
                using var doc = JsonDocument.Parse(s.Substring(i, matchEnd - i + 1));
                parsed = doc.RootElement.Clone();
            }
            catch
            {
                // not valid JSON — skip past this brace and keep scanning
            }

            if (parsed is { ValueKind: JsonValueKind.Object } obj)
                yield return (i, matchEnd + 1, obj);

            i = matchEnd; // continue scanning after this object
        }
    }

    private enum SupervisorDecision
    {
        Progress,
        DeadEnd
    }

    private async Task<SupervisorDecision> RunSupervisorGuardAsync(AgentRun run, CancellationToken ct)
    {
        var toolHistory = string.Join("\n", run.ToolInvocations.Select((t, i) =>
            $"- Call {i + 1}: {t.ToolName}({t.Arguments}) - {(t.IsError ? "failed: " + t.Result : "succeeded")}"));

        if (string.IsNullOrWhiteSpace(toolHistory))
        {
            toolHistory = "(No tools called yet)";
        }

        var systemPrompt =
            "You are a supervisor monitoring an AI coding agent. The agent is trying to solve a task but is approaching its turn limit. " +
            "Analyze the tool invocation history below. Decide if the agent is stuck in an unproductive loop (e.g., repeating the same actions/commands, " +
            "repeatedly failing on the same error without fixing it, or going in circles) or if it is making genuine progress toward the goal " +
            "(e.g., solving issues, installing packages, trying new files, modifying code successfully, moving forward, or performing necessary sequential steps).\n\n" +
            "Respond with exactly one of these two words:\n" +
            "PROGRESS - if the agent is making active, genuine progress and should be allowed to continue.\n" +
            "DEAD_END - if the agent is stuck in a loop, thrashing, repeating itself, or cannot proceed.";

        var request = new ModelRequest
        {
            Model = (_input.SecondaryModel ?? _input.Model).Model,
            SystemPrompt = systemPrompt,
            Messages = new[] { Message.User($"Tool Invocation History:\n{toolHistory}\n\nIs the agent making PROGRESS or at a DEAD_END?") },
            Think = false,
            MaxOutputTokens = 100
        };

        ModelResponse? final = null;
        await foreach (var ev in _secondaryProvider.StreamAsync(request, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            if (ev is ModelStreamEvent.Completed completed)
            {
                final = completed.Response;
            }
        }

        string resultText = final?.Content ?? "";
        if (resultText.Contains("PROGRESS", StringComparison.OrdinalIgnoreCase))
        {
            return SupervisorDecision.Progress;
        }

        return SupervisorDecision.DeadEnd;
    }
}

/// <summary>A record of one tool call and its textual result.</summary>
public sealed record ToolInvocation(string ToolName, string Arguments, string Result, bool IsError = false);

/// <summary>Inspectable transcript of a single agent run.</summary>
public sealed class AgentRun
{
    public List<Message> Messages { get; } = new();

    public List<ToolInvocation> ToolInvocations { get; } = new();
}
