namespace Smarty.Plugins;

/// <summary>
/// One step of a plugin's setup: what to ask for, and why. The host renders it on the plugin's card, takes
/// what was typed, and hands it back to the plugin to decide what comes next.
/// </summary>
/// <remarks>
/// Setup is a sequence rather than a form because some of it can't be a form. A sign-in code doesn't exist
/// until something asks for it to be sent, so "your email" and "the code we just emailed you" cannot be two
/// boxes on one screen — the second only becomes answerable because of the first. A single flat set of
/// configuration keys can only express questions that are all answerable at once.
/// </remarks>
public sealed record PluginStage(
    string Id,
    string Title,
    string? Instruction,
    IReadOnlyDictionary<string, PluginParameter> Fields)
{
    /// <summary>A stage that asks for nothing and only says something — a step whose work happened when the
    /// previous one was submitted, and which just needs acknowledging.</summary>
    public static PluginStage Message(string id, string title, string instruction) =>
        new(id, title, instruction, new Dictionary<string, PluginParameter>());
}

/// <summary>
/// Thrown by <see cref="IPlugin.GetNextStage"/> when what was submitted can't be used: a code that has
/// expired, an address with no account. The host shows the message and offers the same stage again, so the
/// answer can be corrected in place rather than the whole setup being started over.
/// </summary>
public sealed class PluginSetupException : Exception
{
    public PluginSetupException(string message) : base(message) { }
    public PluginSetupException(string message, Exception inner) : base(message, inner) { }
}
