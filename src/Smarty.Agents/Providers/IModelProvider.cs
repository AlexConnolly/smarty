namespace Smarty.Agents;

/// <summary>What the agent sends to a model provider for one completion.</summary>
public sealed class ModelRequest
{
    public required string Model { get; init; }

    public string? SystemPrompt { get; init; }

    public required IReadOnlyList<Message> Messages { get; init; }

    public IReadOnlyList<AgentTool> Tools { get; init; } = Array.Empty<AgentTool>();
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
