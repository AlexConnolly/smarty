using System.Text;

namespace Smarty.Agents;

/// <summary>The fully-drained result of an agent run: the answer plus everything that happened.</summary>
public sealed record AgentAnswer(
    string Text,
    string Reasoning,
    IReadOnlyList<ToolInvocation> Tools,
    IReadOnlyList<AgentEvent> Events);

/// <summary>Convenience helpers for consuming an <see cref="AgentEvent"/> stream.</summary>
public static class AgentStreamExtensions
{
    /// <summary>
    /// Drain an agent stream to completion, aggregating the output text, the reasoning, the tool
    /// invocations and the raw events into a single <see cref="AgentAnswer"/>. The stream is still
    /// consumed lazily, so tools execute and side effects happen as you'd expect — this just
    /// collects the results once instead of making every caller write the same <c>await foreach</c>.
    /// </summary>
    public static async Task<AgentAnswer> ReadAllAsync(
        this IAsyncEnumerable<AgentEvent> stream, CancellationToken ct = default)
    {
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var tools = new List<ToolInvocation>();
        var events = new List<AgentEvent>();
        string? finalAnswer = null;
        string? pendingToolName = null;
        string? pendingToolArgs = null;

        await foreach (var ev in stream.WithCancellation(ct).ConfigureAwait(false))
        {
            events.Add(ev);
            switch (ev)
            {
                case AgentEvent.ContentDelta delta:
                    content.Append(delta.Text);
                    break;
                case AgentEvent.ContentCleared:
                    content.Clear();
                    break;
                case AgentEvent.ReasoningDelta delta:
                    reasoning.Append(delta.Text);
                    break;
                case AgentEvent.ToolStarted started:
                    pendingToolName = started.ToolName;
                    pendingToolArgs = started.Arguments;
                    break;
                case AgentEvent.ToolCompleted completed:
                    tools.Add(new ToolInvocation(
                        completed.ToolName, pendingToolArgs ?? "", completed.Result));
                    pendingToolName = null;
                    pendingToolArgs = null;
                    break;
                case AgentEvent.Completed done:
                    finalAnswer = done.Answer;
                    break;
            }
        }

        return new AgentAnswer(
            finalAnswer ?? content.ToString(),
            reasoning.ToString(),
            tools,
            events);
    }

    /// <summary>Collect every event from the stream into a list (lossless, for inspection/testing).</summary>
    public static async Task<List<AgentEvent>> ToListAsync(
        this IAsyncEnumerable<AgentEvent> stream, CancellationToken ct = default)
    {
        var events = new List<AgentEvent>();
        await foreach (var ev in stream.WithCancellation(ct).ConfigureAwait(false))
            events.Add(ev);
        return events;
    }
}
