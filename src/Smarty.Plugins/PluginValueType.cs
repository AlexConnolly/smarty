namespace Smarty.Plugins;

/// <summary>
/// The type of a value a plugin asks for — a field in a setup stage, or a command parameter. Everything
/// crosses the boundary as a string, so this is what tells the host how to ask for it (a number field, a
/// checkbox, a masked box) and what <see cref="PluginValues"/> will convert it back to.
/// </summary>
/// <remarks>
/// Deliberately a closed set: the host has to render an input for every member, and a plugin that could ask
/// for a type the control centre can't draw would be a plugin nobody can set up.
/// </remarks>
public enum PluginValueType
{
    /// <summary>Free text.</summary>
    Text,

    /// <summary>Text that must never be displayed back — an API key, a password, a sign-in code.</summary>
    Secret,

    /// <summary>A whole number.</summary>
    Integer,

    /// <summary>A number that may have a fractional part.</summary>
    Number,

    /// <summary>True or false.</summary>
    Boolean,
}
