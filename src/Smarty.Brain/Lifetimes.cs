namespace Smarty.Brain;

/// <summary>
/// How long a fact lasts — the question that separates a map of somebody's world from a log of their week.
/// </summary>
/// <remarks>
/// <para>
/// The memory kept filling with things that were true and worthless: a tracker somebody asked for, a panel that got
/// published, the home page itself. Every guard written against them was a description of the last bad shape — an empty
/// verb, a property a run had looked up — and the next one arrived wearing a different one. The guards were a list of
/// wrong answers, and the list has no end.
/// </para>
/// <para>
/// This is the question underneath all of them, and it is answerable without judgement: a request ends when it is
/// served, a job ends when it is done, and a sister does not end. So the writer states the lifetime as a fact about the
/// fact, and the code keeps the two that outlast the work and drops the one that does not. Nobody has to decide what is
/// "relevant" — which was the impossible part, because a request somebody made is genuinely about them.
/// </para>
/// </remarks>
public static class Lifetimes
{
    /// <summary>It will not stop being true: who somebody is to them, where they are from, what they decided.</summary>
    public const string Forever = "forever";

    /// <summary>True until it changes: where they live, what they own, what they prefer.</summary>
    public const string WhileItHolds = "until-it-changes";

    /// <summary>It ends with a piece of work — a request, a need, a job. Not a memory.</summary>
    public const string UntilItIsDone = "until-the-job-is-done";

    public static readonly string[] All = { Forever, WhileItHolds, UntilItIsDone };

    /// <summary>
    /// Does a fact with this lifetime belong in the map?
    /// </summary>
    /// <remarks>
    /// An unstated lifetime is KEPT. The alternative — dropping anything unlabelled — makes a model that omits one
    /// field lose real facts silently, and the field is new: every plan written before it existed said nothing.
    /// </remarks>
    public static bool Keep(string? lasts) =>
        !string.Equals((lasts ?? "").Trim(), UntilItIsDone, StringComparison.OrdinalIgnoreCase);
}
