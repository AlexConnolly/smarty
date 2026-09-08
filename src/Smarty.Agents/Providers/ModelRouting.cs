namespace Smarty.Agents;

/// <summary>
/// Which provider a model name belongs to. The rule the hosts have always used, in one place: a model id with a
/// <c>/</c> in it (<c>deepseek-ai/DeepSeek-V4-Flash-0731</c>) is a Together AI model, and so is anything pointed
/// at a Together endpoint; everything else is a tag on an Ollama gateway (<c>qwen3.5:latest</c>).
/// </summary>
/// <remarks>
/// Inferring the provider from the id keeps one config key doing one job — switching between Together and a
/// local model is just a different model name. The cost is that it's implicit, so it lives here, named, rather
/// than being re-expressed at each call site.
/// </remarks>
public static class ModelRouting
{
    public static bool IsTogether(string model, string? baseUrl) =>
        model.Contains('/') ||
        (baseUrl is not null && baseUrl.Contains("together", StringComparison.OrdinalIgnoreCase));

    public static ModelSpec Spec(string model, string? baseUrl) =>
        IsTogether(model, baseUrl) ? ModelSpec.Together(model, Remote(baseUrl)) : ModelSpec.Ollama(model, baseUrl);

    public static IModelProvider Provider(string model, string? baseUrl) =>
        IsTogether(model, baseUrl)
            ? new TogetherModelProvider(ApiKey(), Remote(baseUrl))
            : new OllamaModelProvider(baseUrl);

    /// <summary>The key for the remote provider. <c>TOGETHER_API_KEY</c> first; the others are honoured because
    /// they predate it and hosts in the wild are set up with them.</summary>
    public static string ApiKey() =>
        Environment.GetEnvironmentVariable("TOGETHER_API_KEY")
        ?? Environment.GetEnvironmentVariable("OLLAMA_API_KEY")
        ?? Environment.GetEnvironmentVariable("SMARTY_API_KEY")
        ?? "";

    // A gateway URL of localhost means "the local Ollama", which is meaningless to Together — it uses its own.
    private static string? Remote(string? baseUrl) =>
        baseUrl is null || baseUrl.Contains("localhost") || baseUrl.Contains("127.0.0.1") ? null : baseUrl;
}
