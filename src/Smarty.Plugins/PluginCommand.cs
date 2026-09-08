namespace Smarty.Plugins;

/// <summary>
/// Thrown by a command that has hit something no retry can fix — a device that isn't there, a place that
/// doesn't exist, an account without the permission. The host stops the model re-running it; an ordinary
/// exception is treated as a hiccup worth one more go.
/// </summary>
public sealed class PluginDeadEndException : Exception
{
    public PluginDeadEndException(string message) : base(message) { }
    public PluginDeadEndException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// One thing a plugin can do: a name, the parameters it takes keyed by name, and the function that runs it.
/// The host turns each of these into a tool the model can call, so the description is written for the model —
/// what it does and when to reach for it, not how it's implemented.
/// </summary>
/// <remarks>
/// The name is bare ("forecast", "start"); the host namespaces it with the plugin's own name, so two plugins
/// can both have a "status" command without colliding. Arguments arrive as
/// <see cref="PluginValues"/> — string keys and string values, with the conversions on the bag itself.
/// </remarks>
public sealed class PluginCommand
{
    private readonly Func<PluginValues, CancellationToken, Task<PluginOutput>> _execute;

    public PluginCommand(
        string name,
        string description,
        IReadOnlyDictionary<string, PluginParameter>? parameters,
        Func<PluginValues, CancellationToken, Task<PluginOutput>> execute)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A command needs a name.", nameof(name));

        Name = name.Trim();
        Description = description ?? "";
        Parameters = parameters is null
            ? new Dictionary<string, PluginParameter>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, PluginParameter>(parameters, StringComparer.OrdinalIgnoreCase);
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    /// <summary>For a command that only has text to give.</summary>
    public PluginCommand(
        string name,
        string description,
        IReadOnlyDictionary<string, PluginParameter>? parameters,
        Func<PluginValues, CancellationToken, Task<string>> execute)
        : this(name, description, parameters,
            async (values, ct) => (PluginOutput)await execute(values, ct).ConfigureAwait(false))
    {
    }

    /// <summary>For a command whose body is synchronous.</summary>
    public PluginCommand(
        string name,
        string description,
        IReadOnlyDictionary<string, PluginParameter>? parameters,
        Func<PluginValues, string> execute)
        : this(name, description, parameters, (values, _) => Task.FromResult<PluginOutput>(execute(values)))
    {
    }

    public string Name { get; }

    public string Description { get; }

    /// <summary>Parameter name → what it is. Empty for a command that takes nothing.</summary>
    public IReadOnlyDictionary<string, PluginParameter> Parameters { get; }

    public Task<PluginOutput> ExecuteAsync(PluginValues parameters, CancellationToken ct = default) =>
        _execute(parameters ?? PluginValues.Empty, ct);
}
