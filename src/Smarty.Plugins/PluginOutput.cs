namespace Smarty.Plugins;

/// <summary>
/// What a command produces: a line for whoever asked, and optionally the same answer as data.
/// </summary>
/// <remarks>
/// Both halves come out of ONE execution because they are one answer. A model reads <see cref="Text"/>; a panel
/// on the home page reads <see cref="Data"/> and renders it. Producing them separately would mean two code
/// paths that can disagree about the same vacuum, and a panel scraping numbers back out of an English sentence
/// is exactly the fragility a declared data model exists to prevent.
/// <para>
/// A bare string converts implicitly, so a command with nothing to plot says nothing extra.
/// </para>
/// </remarks>
public sealed record PluginOutput(string Text, object? Data = null)
{
    public static implicit operator PluginOutput(string text) => new(text);
}
