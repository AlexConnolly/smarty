namespace Smarty.Plugins;

/// <summary>
/// Everything Smarty needs from a plugin: what it is, how it gets set up, and what it can do. Implement this
/// in a class library, drop the built DLL (with any libraries it needs) into a zip, and upload it from the
/// control centre — nothing else is registered anywhere.
/// </summary>
/// <remarks>
/// The host builds a capability from the commands and a persona from the name and description, so the plugin
/// arrives as a specialist Smarty can delegate to, not just a bag of extra tools.
/// <para>
/// Setup and commands are deliberately separate worlds. Setup is between the plugin and the PERSON, one stage
/// at a time, through the control centre; whatever it learns it keeps in <see cref="IPluginState"/>. Commands
/// are between the plugin and the MODEL. Nothing a person types during setup is ever a command parameter, which
/// is what stops a credential — a password, an emailed sign-in code — from being something a model holds,
/// passes around, or fills in a plausible-looking value for.
/// </para>
/// </remarks>
public interface IPlugin
{
    /// <summary>What this plugin is called, in the control centre and as the persona it creates ("Weather",
    /// "Roborock"). Its slug also namespaces every command name.</summary>
    string Name { get; }

    /// <summary>
    /// One line on what this plugin is FOR — its overall goal, written as the role: "Reports current conditions
    /// and forecasts for anywhere in the world." Required, and not decoration: it becomes the description of the
    /// persona Smarty delegates to, and it is what the worker is told the plugin is for. A plugin that doesn't
    /// say what it's for is one nothing will ever choose to use, so a blank description is refused at upload.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// The next thing setup needs from the person, or null when there is nothing left to ask and the plugin is
    /// ready to work. Asynchronous because a step's work — sending a code, exchanging one — is usually a
    /// network call, and the host is waiting on an HTTP request while it happens.
    /// </summary>
    /// <param name="previous">The id of the stage just submitted, or null when setup is being (re)started —
    /// which is the host asking "where do we begin".</param>
    /// <param name="submitted">What was typed into <paramref name="previous"/>. Empty when it is null.</param>
    /// <param name="state">Everything kept so far, and where anything worth keeping goes. This IS the plugin's
    /// settings: there is no separate configuration.</param>
    /// <remarks>
    /// This is where the work of a step happens, not just the asking — sending the code, exchanging it, testing
    /// a connection — because a step's side effect is exactly what makes the next question answerable. Throw
    /// <see cref="PluginSetupException"/> when what was submitted can't be used; the host shows the reason and
    /// offers the same stage again. Called on every boot with a null <paramref name="previous"/>, so it must be
    /// cheap and side-effect-free on that path: read state, and either return the first unanswered stage or null.
    /// </remarks>
    Task<PluginStage?> GetNextStageAsync(
        string? previous, PluginValues submitted, IPluginState state, CancellationToken ct);

    /// <summary>
    /// The commands this plugin exposes, built from what setup left in <paramref name="state"/>. Return an
    /// empty list when it isn't set up far enough to do anything; the plugin then simply contributes nothing
    /// rather than offering tools that can only fail.
    /// </summary>
    /// <remarks>
    /// Asked again whenever state changes, so a plugin's surface can follow its setup with no restart. A plugin
    /// that needs a long-lived client (a session, a socket) builds it here and captures it in the commands it
    /// returns, so it is built once rather than per call.
    /// </remarks>
    IReadOnlyList<PluginCommand> GetCommands(IPluginState state);
}
