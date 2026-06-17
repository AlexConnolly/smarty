namespace Smarty.Agents;

/// <summary>
/// An event emitted while a <see cref="SmartyAgent"/> answers. The output stream is
/// <see cref="ContentDelta"/>; reasoning is surfaced separately as <see cref="ReasoningDelta"/>
/// so consumers can stream the answer <em>without</em> the chain-of-thought. Tool activity and
/// the final answer are also reported.
/// </summary>
public abstract record AgentEvent
{
    /// <summary>A chunk of the agent's answer text.</summary>
    public sealed record ContentDelta(string Text) : AgentEvent;

    /// <summary>A chunk of the agent's reasoning (off the main output stream).</summary>
    public sealed record ReasoningDelta(string Text) : AgentEvent;

    /// <summary>
    /// Discard any answer text streamed for the current turn — it turned out to be a tool call,
    /// not the answer (e.g. a model that emitted the call as inline text). The real answer follows.
    /// </summary>
    public sealed record ContentCleared() : AgentEvent;

    /// <summary>The agent has decided to call a tool.</summary>
    public sealed record ToolStarted(string ToolName, string Arguments) : AgentEvent;

    /// <summary>A tool finished and returned a result.</summary>
    public sealed record ToolCompleted(string ToolName, string Result) : AgentEvent;

    /// <summary>Terminal event carrying the complete final answer.</summary>
    public sealed record Completed(string Answer) : AgentEvent;
}
