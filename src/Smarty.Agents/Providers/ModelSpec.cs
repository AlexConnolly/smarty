namespace Smarty.Agents;

/// <summary>
/// Selects which model provider and model an agent should use. Carried on
/// <see cref="AgentInput"/> so the choice of model lives with the input.
/// </summary>
/// <param name="Provider">Provider key: <c>"together"</c> or <c>"ollama"</c>.</param>
/// <param name="Model">Model name understood by the provider, e.g. <c>"deepseek-ai/DeepSeek-V4-Flash-0731"</c>
/// or <c>"qwen3.5:latest"</c>.</param>
/// <param name="BaseUrl">Optional override for the provider endpoint.</param>
public sealed record ModelSpec(string Provider, string Model, string? BaseUrl = null)
{
    /// <summary>
    /// The base model every host falls back to: DeepSeek V4 Flash (the 0731 release) on Together AI, which
    /// needs <c>TOGETHER_API_KEY</c>. The id carries the routing — see <see cref="ModelRouting"/>: an id with a
    /// <c>/</c> in it is a Together model, anything else is a tag on the Ollama gateway. So pointing
    /// <c>Ollama:Model</c> at a tag you've pulled locally is all it takes to run entirely on the machine.
    /// </summary>
    public const string DefaultModelName = "deepseek-ai/DeepSeek-V4-Flash-0731";

    /// <summary>A model on the local Ollama gateway.</summary>
    public static ModelSpec Ollama(string model, string? baseUrl = null)
        => new("ollama", model, baseUrl);

    /// <summary>A model on Together AI — the base model's home. <paramref name="baseUrl"/> defaults to
    /// Together's own endpoint.</summary>
    public static ModelSpec Together(string model = DefaultModelName, string? baseUrl = null)
        => new("together", model, baseUrl);

    private static ModelSpec _default = Together();

    /// <summary>The primary model specification default.</summary>
    public static ModelSpec Default
    {
        get => _default;
        set => _default = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The secondary/dumber model specification default.</summary>
    public static ModelSpec? SecondaryDefault { get; set; }
}
