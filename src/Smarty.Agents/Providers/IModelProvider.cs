using System.Text.Json.Nodes;

namespace Smarty.Agents;

/// <summary>Why a model turn stopped generating.</summary>
public enum FinishReason
{
    /// <summary>The model finished normally.</summary>
    Stop,

    /// <summary>Hit the output-token cap.</summary>
    Length,

    /// <summary>Detected degenerate repetition (the model got stuck in a loop) and was cut off.</summary>
    Loop,

    /// <summary>The per-turn time limit elapsed and the turn was cut off.</summary>
    Timeout,
}

/// <summary>What the agent sends to a model provider for one completion.</summary>
public sealed class ModelRequest
{
    public required string Model { get; init; }

    public string? SystemPrompt { get; init; }

    public required IReadOnlyList<Message> Messages { get; init; }

    public IReadOnlyList<AgentTool> Tools { get; init; } = Array.Empty<AgentTool>();

    /// <summary>Cap on tokens generated this turn (a hard backstop against runaway generation).</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Repetition penalty to discourage loops at the sampling level.</summary>
    public double? RepeatPenalty { get; init; }

    /// <summary>Abort the turn if it runs longer than this (cuts off a stuck generation).</summary>
    public TimeSpan? TurnTimeout { get; init; }

    /// <summary>Whether the model should produce a chain-of-thought. Off = instant response (used by
    /// the conversational orchestrator, which must reply immediately, not deliberate).</summary>
    public bool Think { get; init; } = true;

    /// <summary>Optional JSON Schema to constrain the model's output (Ollama structured outputs). When set,
    /// the provider passes it as <c>format</c>, so the response is GUARANTEED to be JSON matching the schema
    /// — used to force a reliable structured outcome rather than parsing it back out of prose.</summary>
    public JsonNode? ResponseFormat { get; init; }
}

/// <summary>What a model provider returns: a single assistant turn.</summary>
public sealed class ModelResponse
{
    /// <summary>Clean answer text (no chain-of-thought).</summary>
    public string? Content { get; init; }

    /// <summary>Reasoning text, when the provider exposes it separately.</summary>
    public string? Reasoning { get; init; }

    /// <summary>Any tools the model wants to invoke this turn.</summary>
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = Array.Empty<ToolCall>();

    /// <summary>Why generation stopped.</summary>
    public FinishReason Finish { get; init; } = FinishReason.Stop;

    public bool HasToolCalls => ToolCalls.Count > 0;

    public Message ToMessage() => Message.Assistant(Content, Reasoning, ToolCalls.Count > 0 ? ToolCalls : null);
}

/// <summary>
/// A streamed event from a model completion. Content and reasoning arrive as separate
/// deltas so callers can stream the <em>output</em> without the chain-of-thought; the
/// terminal <see cref="Completed"/> event carries the fully assembled response (including
/// any tool calls) for the agent loop to act on.
/// </summary>
public abstract record ModelStreamEvent
{
    /// <summary>A chunk of answer text.</summary>
    public sealed record Content(string Text) : ModelStreamEvent;

    /// <summary>A chunk of model reasoning (chain-of-thought), kept off the output stream.</summary>
    public sealed record Reasoning(string Text) : ModelStreamEvent;

    /// <summary>The final assembled response for this turn.</summary>
    public sealed record Completed(ModelResponse Response) : ModelStreamEvent;
}

/// <summary>Abstraction over a chat model that can call tools and stream its output.</summary>
public interface IModelProvider
{
    /// <summary>Stream a completion as a sequence of <see cref="ModelStreamEvent"/>s.</summary>
    IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken ct = default);

    /// <summary>Convenience non-streaming completion, assembled by draining the stream.</summary>
    async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        ModelResponse? final = null;
        await foreach (var ev in StreamAsync(request, ct).WithCancellation(ct).ConfigureAwait(false))
            if (ev is ModelStreamEvent.Completed completed)
                final = completed.Response;
        return final ?? new ModelResponse();
    }
}
