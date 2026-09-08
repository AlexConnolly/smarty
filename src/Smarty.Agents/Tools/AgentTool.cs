namespace Smarty.Agents;

/// <summary>
/// The result of running a tool: its textual output, whether it failed, and — when it failed — whether
/// retrying could plausibly help. <see cref="CanRetry"/> lets a tool distinguish a transient hiccup
/// ("go again") from a hard dead end the model must NOT keep hammering (a bot wall, a 404, a permanently
/// invalid argument). The agent loop uses this to retry transient failures but route around dead ends,
/// instead of pushing the model to "keep trying" against something that can never succeed.
/// A bare string converts implicitly to a successful result, so simple tools can just return text.
/// </summary>
public sealed record ToolOutput(string Content, bool IsError = false, bool CanRetry = true)
{
    public static ToolOutput Ok(string content) => new(content, false);

    /// <summary>A transient failure — retrying with corrected input could work.</summary>
    public static ToolOutput Error(string content) => new(content, IsError: true, CanRetry: true);

    /// <summary>A hard dead end — this exact call can't succeed (blocked, gone, fundamentally invalid).
    /// The model should not retry it; it should try a different approach or conclude with what it has.</summary>
    public static ToolOutput DeadEnd(string content) => new(content, IsError: true, CanRetry: false);

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

    /// <summary>
    /// True when the same arguments can legitimately return something different — because the answer depends on
    /// state outside the call, not just on the inputs.
    /// </summary>
    /// <remarks>
    /// The agent loop refuses an exact repeat of a call that already succeeded, which is right for a stateless
    /// tool (searching the same query twice is a wasted turn) and WRONG for a stateful one. Reading a browser
    /// page takes <c>{tabId}</c> and nothing else, so a read after navigating somewhere new looks byte-identical
    /// to the read before it — and gets refused, exactly when the tool's own description is telling the model to
    /// read again because the page changed. Such a tool sets this, and the repeat guards leave it alone; the
    /// per-tool call budget and the watchdog still bound it, so runaway loops are still caught.
    /// </remarks>
    public bool Repeatable { get; init; }

    public Task<ToolOutput> InvokeAsync(ToolCallArguments arguments, CancellationToken ct = default)
        => _execute(arguments, ct);
}
