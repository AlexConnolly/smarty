namespace Smarty.Agents;

/// <summary>
/// The result of running a tool: its textual output and whether it failed. A failed result lets
/// the agent nudge the model to recover instead of giving up. A bare string converts implicitly
/// to a successful result, so simple tools can just return text.
/// </summary>
public sealed record ToolOutput(string Content, bool IsError = false)
{
    public static ToolOutput Ok(string content) => new(content, false);
    public static ToolOutput Error(string content) => new(content, true);

    public static implicit operator ToolOutput(string content) => new(content);
}

/// <summary>
/// A tool the agent can call: a name, a description, a set of typed parameters, and a
/// delegate that executes it. Tools support multiple, typed input parameters.
/// </summary>
public sealed class AgentTool
{
    private readonly Func<ToolCallArguments, CancellationToken, Task<ToolOutput>> _execute;

    public AgentTool(
        string name,
        string description,
        IEnumerable<ToolParameter> parameters,
        Func<ToolCallArguments, CancellationToken, Task<ToolOutput>> execute)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tool name is required.", nameof(name));

        Name = name;
        Description = description ?? "";
        Parameters = parameters?.ToArray() ?? Array.Empty<ToolParameter>();
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    /// <summary>Convenience overload for tool bodies that return plain text.</summary>
    public AgentTool(
        string name,
        string description,
        IEnumerable<ToolParameter> parameters,
        Func<ToolCallArguments, CancellationToken, Task<string>> execute)
        : this(name, description, parameters,
            async (args, ct) => (ToolOutput)await execute(args, ct).ConfigureAwait(false))
    {
    }

    /// <summary>Convenience overload for synchronous tool bodies.</summary>
    public AgentTool(
        string name,
        string description,
        IEnumerable<ToolParameter> parameters,
        Func<ToolCallArguments, string> execute)
        : this(name, description, parameters, (args, _) => Task.FromResult<ToolOutput>(execute(args)))
    {
    }

    public string Name { get; }

    public string Description { get; }

    public IReadOnlyList<ToolParameter> Parameters { get; }

    public Task<ToolOutput> InvokeAsync(ToolCallArguments arguments, CancellationToken ct = default)
        => _execute(arguments, ct);
}
