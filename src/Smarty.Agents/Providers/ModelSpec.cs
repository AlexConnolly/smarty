namespace Smarty.Agents;

/// <summary>
/// Selects which model provider and model an agent should use. Carried on
/// <see cref="AgentInput"/> so the choice of model lives with the input.
/// </summary>
/// <param name="Provider">Provider key, e.g. <c>"ollama"</c>.</param>
/// <param name="Model">Model name understood by the provider, e.g. <c>"qwen3:4b"</c>.</param>
/// <param name="BaseUrl">Optional override for the provider endpoint.</param>
public sealed record ModelSpec(string Provider, string Model, string? BaseUrl = null)
{
    /// <summary>The local Ollama gateway running qwen3 — the framework default.</summary>
    public static ModelSpec Ollama(string model = "qwen3:4b", string? baseUrl = null)
        => new("ollama", model, baseUrl);

    public static ModelSpec Default => Ollama();
}
