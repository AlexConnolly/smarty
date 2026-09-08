namespace Smarty.Api;

/// <summary>
/// The things the assistant made, which are not part of anybody's world.
/// </summary>
/// <remarks>
/// <para>
/// The memory is a map of the user's world: who is in it, what is theirs, how those connect. The assistant is not in
/// that world and neither is its furniture — a panel on the home page, a shape in its own library, the home page
/// itself. Those all turned up as things joined to the user ("Alex has Home Page", "Alex published Ebay Latest Sale"),
/// and each one is true, useless and permanent.
/// </para>
/// <para>
/// Deliberately a list of OURSELVES rather than a list of facts to forbid. Every previous attempt at this was the
/// second kind — no empty verbs, no properties a run looked up — and each described the last bad write while the next
/// one arrived in a shape nobody had thought of. What the assistant owns is knowable, finite, and ours to enumerate.
/// </para>
/// <para>
/// It is a backstop, not the rule. The rule is that a fact ending with a piece of work is not a memory, which is
/// answered per fact when the writing happens. This catches the leftovers: something of ours named directly, in a
/// sentence where the lifetime looked permanent because the panel really will sit there for ever.
/// </para>
/// </remarks>
public static class OurOwnThings
{
    /// <summary>
    /// The parts of the app itself, in the words somebody would use for them.
    /// </summary>
    /// <remarks>
    /// Short, and only the furniture: the screens and objects this application is MADE of. Anything longer starts being
    /// a list of subjects nobody may mention, which is not the same thing at all and is the trap this avoids.
    /// </remarks>
    private static readonly string[] Furniture =
    {
        "smarty", "pip", "the assistant", "home page", "homepage", "the home page", "dashboard",
        "panel", "the panel", "widget", "the widget", "chat", "the chat", "memory", "the memory", "brain",
    };

    /// <summary>
    /// A test for whether a name being recorded is one of ours. Reads the stores each time, so a panel put up a minute
    /// ago counts.
    /// </summary>
    public static Func<string, bool> Test(WidgetStore panels, WidgetLibrary kinds) => name =>
    {
        var asked = Same(name);
        if (asked.Length == 0) return false;

        if (Furniture.Any(f => Same(f) == asked)) return true;

        // A panel by its title, and a kind by either of its names. Compared as the same words in the same order rather
        // than loosely: two panels called "Flights" and "Flight to Palma" are not the same thing, and a place genuinely
        // called after one of ours would be lost by anything fuzzier.
        if (panels.All().Any(p => Same(p.Title) == asked)) return true;
        return kinds.All().Any(k => Same(k.Name) == asked || Same(k.Title) == asked);
    };

    /// <summary>Down to the letters, so "Home Page", "home page" and "home-page" are one name.</summary>
    private static string Same(string? text) =>
        new string((text ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
