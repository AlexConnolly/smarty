namespace Smarty.Brain;

/// <summary>
/// One thing known, either about a node or between two nodes.
/// </summary>
/// <remarks>
/// <para>
/// A single record does both jobs, and which one it is depends on whether <see cref="To"/> is set. A property has a
/// value and no target — a price, a postcode, a date. A relationship has a target and no value of its own. Forcing
/// every fact to be a relationship would fill the graph with junk nodes for things like "£150 Buy It Now"; forcing
/// every fact to be text is the state the old store was in, and why nothing could be walked.
/// </para>
/// <para>
/// <b>Nothing is ever edited.</b> A change appends a new edge and retires the old one, so the history of how something
/// came to be true survives — including the reason, which is the part that turns a graph into something you can
/// interrogate later. "Why didn't we book that hotel?" is only answerable if the rejection kept its note.
/// </para>
/// </remarks>
public sealed class Edge
{
    public string Id { get; set; } = "";

    /// <summary>The node this is stated about.</summary>
    public string From { get; set; } = "";

    /// <summary>
    /// What the connection is called, in whatever words were used: <c>destination</c>, <c>chosen hotel</c>,
    /// <c>going on</c>, <c>friend</c>. Prose, for reading. Never the thing a query filters on.
    /// </summary>
    public string Label { get; set; } = "";

    /// <summary>The other node, when this is a relationship. Null for a property.</summary>
    public string? To { get; set; }

    /// <summary>
    /// The value in words. Always populated: for a relationship it is what the sentence reads as, which keeps the file
    /// legible after a merge renames the far end.
    /// </summary>
    public string Value { get; set; } = "";

    /// <summary>The nuance that does not fit a label: "met at uni", "5★ adults-only", "no returns".</summary>
    public string? Note { get; set; }

    /// <summary>
    /// When the thing itself happens, for the facts that have a moment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="Asserted"/>, and the distinction is the whole point: asserted is when somebody said it,
    /// this is when it occurs. Without the difference there was nowhere to put a time at all, so "heading there next
    /// Friday at six" could only be smuggled into a value on some edge or dropped — and it was dropped, every time,
    /// leaving a booking on file with the one detail removed that made it worth having.
    /// </para>
    /// <para>
    /// Null for the overwhelming majority of facts, which have no moment: who somebody is to you, what a thing costs.
    /// Set on the ones that do, whether ahead or behind — a diary entry and a visit last summer are the same shape, and
    /// only the clock decides which is which.
    /// </para>
    /// </remarks>
    public DateTimeOffset? When { get; set; }

    public EdgeState State { get; set; } = EdgeState.Active;

    /// <summary>The edge that replaced this one, when a property took a new value.</summary>
    public string? SupersededBy { get; set; }

    /// <summary>
    /// Why this stopped being true.
    /// </summary>
    /// <remarks>
    /// The field the whole idea rests on, and the one a supersession pointer cannot supply: the reason belongs to
    /// neither the old value nor the new one. Required when retiring, because a tidy graph with no story is exactly the
    /// outcome worth avoiding.
    /// </remarks>
    public string? Because { get; set; }

    /// <summary>
    /// Who was party to this, as a key only this graph's host understands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately opaque. What counts as an audience — a set of people, a channel, everyone — is the host's model, and
    /// the graph has no business knowing it; it carries the key and asks a supplied predicate whether a reader may see
    /// the edge. That keeps a real privacy boundary out of the hands of the layer least equipped to reason about it.
    /// </para>
    /// <para>
    /// Null means nobody said, which is treated as visible. That is the honest default for a single-user graph and the
    /// wrong one for a shared workspace, so a host serving more than one person must stamp every write.
    /// </para>
    /// </remarks>
    public string? Audience { get; set; }

    public DateTimeOffset Asserted { get; set; }
    public DateTimeOffset? Ended { get; set; }

    /// <summary>Who or what said so, so any single edge can be traced back.</summary>
    public string? Source { get; set; }

    public bool IsRelationship => !string.IsNullOrWhiteSpace(To);

    /// <summary>
    /// What makes this edge the same slot as another — and it differs between the two sorts.
    /// </summary>
    /// <remarks>
    /// A property supersedes on <c>(from, label)</c>: a new price replaces the old one, which is what keeps the store
    /// deterministic. A relationship must ALSO key on its target, or the second one silently retires the first —
    /// "the trip involves Emma" followed by "the trip involves Dave" would leave only Dave, and "who is coming" would
    /// answer wrongly for ever. That single difference is the whole gap between a graph and a graph that eats itself.
    /// </remarks>
    public string Slot => IsRelationship
        ? $"{From}|{Label}|{To}".ToLowerInvariant()
        : $"{From}|{Label}".ToLowerInvariant();
}

public enum EdgeState
{
    /// <summary>True now.</summary>
    Active,

    /// <summary>A property whose value was replaced.</summary>
    Superseded,

    /// <summary>Was true, and stopped being true.</summary>
    Ended,
}
