namespace Smarty.Plugins;

/// <summary>
/// One input a command takes: what type it is, what it's for, and whether the command can run without it. The
/// parameter's NAME is the key it sits under in the command's parameter dictionary, so it isn't repeated here.
/// </summary>
public sealed record PluginParameter(PluginValueType Type, string Description, bool Required = false)
{
    public static PluginParameter Text(string description, bool required = false) =>
        new(PluginValueType.Text, description, required);

    public static PluginParameter Integer(string description, bool required = false) =>
        new(PluginValueType.Integer, description, required);

    public static PluginParameter Number(string description, bool required = false) =>
        new(PluginValueType.Number, description, required);

    public static PluginParameter Boolean(string description, bool required = false) =>
        new(PluginValueType.Boolean, description, required);

    public static PluginParameter Secret(string description, bool required = false) =>
        new(PluginValueType.Secret, description, required);
}
