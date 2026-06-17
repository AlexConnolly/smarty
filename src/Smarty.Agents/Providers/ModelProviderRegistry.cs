namespace Smarty.Agents;

/// <summary>
/// Resolves a <see cref="ModelSpec"/> to a concrete <see cref="IModelProvider"/>.
/// The default registry knows the <c>"ollama"</c> provider; more can be registered.
/// </summary>
public sealed class ModelProviderRegistry
{
    private readonly Dictionary<string, Func<ModelSpec, IModelProvider>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A shared registry pre-populated with the built-in providers.</summary>
    public static ModelProviderRegistry Default { get; } = CreateDefault();

    public static ModelProviderRegistry CreateDefault()
    {
        var registry = new ModelProviderRegistry();
        registry.Register("ollama", spec => new OllamaModelProvider(spec.BaseUrl));
        return registry;
    }

    public void Register(string provider, Func<ModelSpec, IModelProvider> factory)
    {
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("Provider key is required.", nameof(provider));
        _factories[provider] = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public IModelProvider Resolve(ModelSpec spec)
    {
        if (!_factories.TryGetValue(spec.Provider, out var factory))
            throw new InvalidOperationException(
                $"No model provider registered for '{spec.Provider}'. " +
                $"Known providers: {string.Join(", ", _factories.Keys)}.");
        return factory(spec);
    }
}
