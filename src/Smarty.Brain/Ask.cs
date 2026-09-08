namespace Smarty.Brain;

/// <summary>
/// A question put to the graph.
/// </summary>
/// <remarks>
/// <para>
/// Everything here that FILTERS is structural — the kind of thing you want back, and specific nodes or connections to
/// leave out. Everything semantic comes back as words for the reader to judge. That division is the whole design, and
/// it exists because the alternative was inventing a vocabulary of relationship types: a closed list of
/// partner/family/friend/colleague, chosen by whoever wrote the code, with no room for a sister-in-law or an ex or
/// somebody you met once at a wedding.
/// </para>
/// <para>
/// It is unnecessary as well as arbitrary. An enum exists so that a <c>WHERE tie = 'friend'</c> clause can work; the
/// consumer here reads English, and reads "wife" or "old friend from uni" perfectly well. So the graph answers the part
/// it can be certain about — who is connected to this, and how, in the words used — and leaves "which of these count as
/// friends" to something that can actually make that judgement.
/// </para>
/// </remarks>
public sealed class Ask
{
    /// <summary>
    /// What to start from: a name, an alias, or an id.
    /// </summary>
    /// <remarks>
    /// Resolved exactly. An unrecognised subject is reported as unrecognised rather than answered with an empty list —
    /// "I have never heard of this" and "I know of it and nothing is connected" are different answers, and a system that
    /// conflates them ends up confidently knowing nothing.
    /// </remarks>
    public string About { get; set; } = "";

    /// <summary>The kind of thing to return, or null for everything. The only filter that can be trusted.</summary>
    public string? Wanting { get; set; }

    /// <summary>
    /// A second thing the answer must ALSO be connected to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What two things have in common, which is a question the rest of this cannot express and a very ordinary one to
    /// ask: what do I do with my brother, where do the two of them both go, who do we both know. Without it the only
    /// available reading is "things connected to me" or "things connected to him", and neither is the question — so the
    /// honest answer is "nothing recorded", said over a graph that holds the answer plainly.
    /// </para>
    /// <para>
    /// An intersection, not a second walk stapled on: a result has to be within reach of both ends. It also annotates,
    /// because the interesting part is HOW each of them relates to the thing they share — one may have joined it and the
    /// other merely trained with it, and that difference is the actual answer.
    /// </para>
    /// </remarks>
    public string? With { get; set; }

    /// <summary>
    /// How far to walk. One for the immediate ring, two for what those in turn touch.
    /// </summary>
    /// <remarks>
    /// Two is the useful default because the interesting answer usually lies one step beyond the thing asked about —
    /// nobody attaches a hobby to a holiday, but the holiday has people on it and the people have hobbies. Three is
    /// almost always noise: everything in a well-used graph is connected eventually.
    /// </remarks>
    public int Hops { get; set; } = 2;

    /// <summary>
    /// Nodes to describe each result's relationship to, in the words recorded.
    /// </summary>
    /// <remarks>
    /// This is what makes "what friends are coming" answerable without a type system. Anchor on the user and every
    /// person comes back labelled "wife", "old friend from uni", "friend" — or with nothing, which is the honest answer
    /// for somebody whose connection was never recorded. A typed filter would have dropped that person silently.
    /// <para>A list rather than one, because "how do these relate to me AND to my wife" is a reasonable question.</para>
    /// </remarks>
    public List<string> Anchors { get; } = new();

    /// <summary>Subtractives: the things to take out. Structural only, so they cannot lie.</summary>
    public List<Exclusion> Exclude { get; } = new();

    /// <summary>
    /// The most to bring back. Nearest first, so what gets dropped is what was furthest away.
    /// </summary>
    /// <remarks>
    /// A cap has to exist — one badly-connected hub can otherwise pull in most of the graph — and hitting it is reported
    /// rather than hidden, because a truncated answer read as a complete one is worse than no answer.
    /// </remarks>
    public int Most { get; set; } = 60;

    /// <summary>
    /// Whether a reader is allowed to see a given fact, or null for no restriction.
    /// </summary>
    /// <remarks>
    /// Carried on the question rather than set on the graph, because one process answers for many conversations at once
    /// and a filter held on the graph would be a race with a privacy boundary on the line. Passed down every read this
    /// question makes, so an edge nobody may see is not merely hidden from the answer — it is never walked through, and
    /// cannot leak by being the reason something else showed up.
    /// </remarks>
    public Func<Edge, bool>? Visible { get; set; }

    /// <summary>
    /// Whether to bring each RESULT's own facts along.
    /// </summary>
    /// <remarks>
    /// Off by default, because this is the part that scales with the answer: ten results dragging a hundred properties
    /// behind them is unreadable, and the caller usually wants the shape of the neighbourhood first and the detail second.
    /// <para>
    /// The SUBJECT's properties are not covered by this and always come back. One node's facts are bounded however big the
    /// answer is, and they are the thing most likely to be what was actually asked for.
    /// </para>
    /// </remarks>
    public bool WithProperties { get; set; }
}

/// <summary>
/// Something to leave out of an answer.
/// </summary>
/// <remarks>
/// Both forms are structural — a specific node, or the absence of a connection. There is deliberately no "exclude by
/// relationship type", because that would need the invented vocabulary this design does without: excluding "family"
/// requires agreeing what family means, and getting it wrong removes people silently.
/// </remarks>
public sealed class Exclusion
{
    public string? NodeId { get; init; }
    public string? EdgeLabel { get; init; }
}

/// <summary>The subtractives, named so a call reads as a sentence.</summary>
public static class Without
{
    /// <summary>This particular node — most often the user themselves.</summary>
    public static Exclusion Node(string nameOrId) => new() { NodeId = nameOrId };

    /// <summary>
    /// Anything that has a connection with this label.
    /// </summary>
    /// <remarks>
    /// The subtractive a flat store cannot express at all: "who have I not asked yet" has no row to match on, because
    /// the thing being matched is an absence. In a graph it is one lookup.
    /// </remarks>
    public static Exclusion Edge(string label) => new() { EdgeLabel = label };
}

/// <summary>One thing found, with everything needed to judge it.</summary>
public sealed class Finding
{
    public required Node Node { get; init; }

    /// <summary>How many steps from the subject.</summary>
    public required int Hops { get; init; }

    /// <summary>
    /// Why this is in the answer, in the words that were written.
    /// </summary>
    /// <remarks>
    /// A result you cannot check is a result you have to take on trust. This is the sentence of the edge that produced
    /// it, read in the direction it was walked.
    /// </remarks>
    public required string Because { get; init; }

    /// <summary>How this relates to each anchor, in the recorded words. Empty where nothing was ever recorded.</summary>
    public IReadOnlyList<AnchorTie> Anchored { get; init; } = Array.Empty<AnchorTie>();

    /// <summary>Its own facts, when they were asked for.</summary>
    public IReadOnlyList<Edge> Properties { get; init; } = Array.Empty<Edge>();
}

/// <summary>How a result relates to an anchor — the words, never a category.</summary>
public readonly record struct AnchorTie(string Anchor, string Relation, string? Note);

/// <summary>What the graph came back with.</summary>
public sealed class Answer
{
    /// <summary>The node the question was about, or null when the subject was not recognised.</summary>
    public Node? About { get; init; }

    /// <summary>False when the subject could not be resolved — distinct from resolving to something with no neighbours.</summary>
    public bool Resolved => About is not null;

    /// <summary>Set when the name matched more than one thing. A question to put back, not an answer.</summary>
    public IReadOnlyList<Node> Ambiguous { get; init; } = Array.Empty<Node>();

    /// <summary>The subject's own facts, when properties were asked for.</summary>
    public IReadOnlyList<Edge> AboutProperties { get; init; } = Array.Empty<Edge>();

    /// <summary>
    /// How the SUBJECT relates to each anchor.
    /// </summary>
    /// <remarks>
    /// Because a whole class of question is about the subject itself rather than about what surrounds it. "How do I know
    /// Dave" wants the tie between me and Dave, and annotating only the findings answered it with Dave's neighbours —
    /// which is a list of people who are not the answer.
    /// </remarks>
    public IReadOnlyList<AnchorTie> AboutAnchored { get; init; } = Array.Empty<AnchorTie>();

    public IReadOnlyList<Finding> Found { get; init; } = Array.Empty<Finding>();

    /// <summary>
    /// The cap was reached, so there may be more further out.
    /// </summary>
    /// <remarks>
    /// Deliberately errs towards saying so: an answer of exactly <see cref="Ask.Most"/> items sets this even when it
    /// happened to be everything. "There may be more" is the harmless direction to be wrong in.
    /// </remarks>
    public bool Truncated { get; init; }
}
