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
    private readonly Dictionary<string, AgentTool> _tools;

    public SmartyAgent(AgentInput input, ModelProviderRegistry? registry = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _provider = (registry ?? ModelProviderRegistry.Default).Resolve(input.Model);
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

        for (int iteration = 0; iteration < _input.MaxIterations; iteration++)
        {
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
            if (noAnswer && !final.HasToolCalls && _input.RecoverFromLoops && iteration < _input.MaxIterations - 1)
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
                yield return new AgentEvent.Completed(answerText);
                yield break;
            }

            // This turn is a tool-call turn — discard any answer text we optimistically streamed
            // (it was the inline tool call / preamble, not the final answer).
            if (streamedContent)
                yield return new AgentEvent.ContentCleared();

            bool anyToolError = false;
            foreach (var call in toolCalls)
            {
                yield return new AgentEvent.ToolStarted(call.Name, call.Arguments.ToString());

                ToolOutput output = await ExecuteToolAsync(call, ct).ConfigureAwait(false);
                run.ToolInvocations.Add(new ToolInvocation(call.Name, call.Arguments.ToString(), output.Content, output.IsError));

                var toolMessage = Message.ToolResult(call.Id, call.Name, output.Content);
                conversation.Add(toolMessage);
                run.Messages.Add(toolMessage);
                anyToolError |= output.IsError;

                yield return new AgentEvent.ToolCompleted(call.Name, output.Content);
            }

            // Kick the model when a tool failed, so it recovers instead of surrendering.
            if (anyToolError && _input.NudgeOnToolError)
            {
                var nudge = Message.System(_input.ToolErrorNudge);
                conversation.Add(nudge);
                run.Messages.Add(nudge);
            }
        }

        // Iterations exhausted — emit whatever text we last produced, or a graceful message if we
        // never got a clean answer (e.g. the model kept looping).
        var lastText = run.Messages.LastOrDefault(m => m.Role == Role.Assistant)?.Content;
        yield return new AgentEvent.Completed(
            string.IsNullOrWhiteSpace(lastText)
                ? "I got stuck and couldn't finish that — please try rephrasing your request."
                : lastText);
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
            return ToolOutput.Error($"Error: no tool named '{call.Name}' is available.");

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
    /// Detect tool call(s) a model emitted as inline JSON text (shape <c>{"name": ..., "arguments": {...}}</c>)
    /// where the name matches an available tool. Returns the recovered calls and the content with the
    /// tool-call JSON (and any wrapping code fences) stripped out.
    /// </summary>
    private bool TryExtractInlineToolCalls(string content, out List<ToolCall> calls, out string cleaned)
    {
        calls = new List<ToolCall>();
        cleaned = content;
        if (string.IsNullOrWhiteSpace(content) || !content.Contains("\"name\""))
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
}

/// <summary>A record of one tool call and its textual result.</summary>
public sealed record ToolInvocation(string ToolName, string Arguments, string Result, bool IsError = false);

/// <summary>Inspectable transcript of a single agent run.</summary>
public sealed class AgentRun
{
    public List<Message> Messages { get; } = new();

    public List<ToolInvocation> ToolInvocations { get; } = new();
}
