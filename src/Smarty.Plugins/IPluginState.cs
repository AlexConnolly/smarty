namespace Smarty.Plugins;

/// <summary>
/// Somewhere a plugin can keep what it learns, kept for it by the host and still there after a restart.
/// </summary>
/// <remarks>
/// Distinct from configuration, and the distinction matters: configuration is what a PERSON typed and can
/// retype, state is what the PLUGIN obtained and usually cannot get again. A sign-in code emailed to you is
/// single-use, so the session token it buys has to outlive the process or every restart asks for another code.
/// <para>
/// State is never shown in the control centre and never offered as a field to fill in. Writing to it is
/// immediate and durable, and it tells the host the plugin's surface may have changed — a plugin that exposes
/// nothing until it is signed in gets its real commands the moment it stores the token, with no restart.
/// </para>
/// </remarks>
public interface IPluginState
{
    /// <summary>What was stored under this key, or null if nothing was.</summary>
    string? Get(string key);

    /// <summary>Store a value, or clear it by passing null. Persisted before this returns.</summary>
    void Set(string key, string? value);
}
