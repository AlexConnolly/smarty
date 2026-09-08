namespace Smarty.Brain;

/// <summary>
/// What sort of thing a node is — the one dimension a question is allowed to filter on.
/// </summary>
/// <remarks>
/// <para>
/// The rule this exists for: <b>filter on the kind of thing you get back, never on the name of a relationship.</b> Labels
/// are free prose that drifts the moment a model is writing them — the same idea arrives three different ways in a week —
/// so filtering on them means every question needs a synonym list and silently misses whatever the list forgot. Asking
/// for a KIND and reading the labels that come back has neither problem.
/// </para>
/// <para>
/// The five below are the system kinds and always exist. The list is not closed, though, because a closed list has a
/// junk drawer: anything that fits none of them lands in <c>topic</c>, and <c>topic</c> then fills with the very things
/// hardest to categorise, which makes the one reliable filter useless for exactly the awkward cases. A club is not a
/// place; an illness is not a topic in the sense a hobby is.
/// </para>
/// <para>
/// So a new kind can be coined when nothing fits — and the drift that argument usually loses to is held off the same way
/// it is for labels: whoever is writing is shown the kinds THIS graph already uses and reuses one unless it genuinely
/// cannot. Nothing is remembered from a schema written months ago, so the vocabulary converges on what the data actually
/// contains. Coined kinds are normalised, and a graph can report its own kinds with counts, so a near-duplicate shows up
/// as a kind with two nodes in it rather than hiding.
/// </para>
/// </remarks>
public static class Kinds
{
    public const string Person = "person";
    public const string Project = "project";
    public const string Place = "place";
    public const string Topic = "topic";

    /// <summary>An object: something owned, tracked, sold or lost.</summary>
    public const string Thing = "thing";

    /// <summary>The kinds that always exist, whatever any particular graph happens to contain.</summary>
    public static readonly string[] System = { Person, Project, Place, Topic, Thing };

    /// <summary>Whether this is one of the built-in kinds. A coined one is no less valid — just not guaranteed.</summary>
    public static bool Known(string? kind) =>
        kind is { Length: > 0 } && System.Contains(kind, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A kind in the form it is stored in: one lowercase word, hyphenated.
    /// </summary>
    /// <remarks>
    /// The whole defence against drift, so it is applied on the way in and never skipped. Without it "Organisation" and
    /// "organisation" are two kinds, and the filter everything depends on quietly splits in half.
    /// </remarks>
    public static string Clean(string? kind)
    {
        var slug = Graph.Slug(kind);
        return slug.Length == 0 ? Topic : slug;
    }

    /// <summary>
    /// Whether two nodes of this kind may share a name.
    ///
    /// <para>
    /// People may: two friends called Emma is ordinary, and which one is meant comes from who is in the conversation.
    /// Nothing else may — two places called Palma, or two projects with the same title, is always a mistake and always
    /// the beginning of the rot.
    /// </para>
    /// </summary>
    public static bool NamesMayRepeat(string kind) =>
        string.Equals(kind, Person, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One thing the graph knows about.
/// </summary>
/// <remarks>
/// <para>
/// The id carries no kind. That looks like a detail and is the difference between a graph that survives being wrong
/// about something and one that does not: a node first recorded as a topic and later understood to be a project keeps
/// its id, so every edge pointing at it stays intact. An id of <c>project:holiday</c> would break all of them.
/// </para>
/// <para>
/// Ids are readable slugs rather than opaque handles, on purpose. Every file in this system gets diagnosed by eye
/// eventually, and <c>n_7f3a involves n_92bc</c> is unreadable at exactly the moment you need to read it.
/// </para>
/// </remarks>
public sealed class Node
{
    public string Id { get; set; } = "";

    /// <summary>One of <see cref="Kinds"/>. The only thing a query may filter on.</summary>
    public string Kind { get; set; } = Kinds.Topic;

    /// <summary>What it is called, for reading.</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Every other way this thing gets referred to, lower-cased.
    ///
    /// <para>
    /// The actual identity mechanism. Matched EXACTLY and never fuzzily: a near-match would cheerfully wire "cycling
    /// in France" to the cycling topic, and a confidently wrong edge is worse than a missing one — you never see the
    /// edge, only the conclusion it produces.
    /// </para>
    /// </summary>
    public List<string> Aliases { get; set; } = new();

    /// <summary>
    /// Set when this node turned out to be another one. Resolution follows it.
    /// </summary>
    /// <remarks>
    /// Duplicates are certain, so merging has to be a first-class operation — and a merge that deletes the loser
    /// breaks everything still holding its id: a panel, a task, an old conversation. The tombstone keeps those working.
    /// </remarks>
    public string? MergedInto { get; set; }

    public DateTimeOffset Created { get; set; }

    /// <summary>
    /// True when nothing has confirmed this node is a real, distinct thing.
    /// </summary>
    /// <remarks>
    /// A name that resolved to nothing has to become something or the fact cannot be saved, but it may equally be a
    /// second name for something already here. Flagging it is the honest middle: the fact lands, and the node shows up
    /// in the duplicate review rather than quietly becoming permanent.
    /// </remarks>
    public bool Provisional { get; set; }

    /// <summary>
    /// The node this one might turn out to BE — an open question about identity, not a fact about the world.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "My brother is Matthew", and a week later "Matt says he can drive". Matt is a name nothing answers to, so the
    /// only two options were both wrong: invent a second person and split every question about him in half, or guess
    /// that they are one and record facts against somebody who may not exist. Neither is recoverable, because neither
    /// leaves a trace.
    /// </para>
    /// <para>
    /// So the fact lands against a new node and the node carries the doubt. It is deliberately NOT an edge: an edge is
    /// something true about the world, and this is the graph admitting it does not know something about itself. Held
    /// here, it can be asked about, answered once, and then it is gone — where an edge would live on in every walk and
    /// eventually be read back as though somebody had said it.
    /// </para>
    /// </remarks>
    public string? MaybeSameAs { get; set; }

    /// <summary>
    /// The nodes this one has been confirmed NOT to be.
    /// </summary>
    /// <remarks>
    /// The half that makes asking bearable. Without a record of "no", every later mention of Matt raises the same
    /// question again — and a system that asks a question it has already had answered is one nobody answers twice.
    /// </remarks>
    public List<string> NotSameAs { get; set; } = new();

    /// <summary>Every string this node answers to, for matching.</summary>
    public IEnumerable<string> AllNames()
    {
        if (Name.Length > 0) yield return Name;
        foreach (var alias in Aliases) yield return alias;
        yield return Id;
    }
}
