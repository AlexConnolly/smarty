namespace Smarty.Api;

/// <summary>
/// Who is in the conversation, and who is talking.
/// </summary>
/// <remarks>
/// <para>
/// What survived the memory being replaced, because it was never really about memory: a Slack thread, a group DM and the
/// local app all need to know their audience, and several things besides recall depend on it — who a task is answering
/// to, whose name goes on a reply.
/// </para>
/// <para>
/// It used to carry a list of subjects the conversation was "about", which the old store used to weight relevance. That
/// went with the store. Relevance is now decided by walking the graph from a subject the model names, so there is nothing
/// left for a pre-computed hint to do.
/// </para>
/// <para>
/// It still filters recall. The graph itself has no notion of an audience — it carries an opaque key on every fact and
/// asks <see cref="Sees"/> whether a reader may have it — so the rule lives here, in the one place that knows what a
/// room is.
/// </para>
/// </remarks>
public readonly record struct BrainContext(Audience Room, PersonId Speaker)
{
    /// <summary>
    /// Whether a fact recorded for one audience may be read by another.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The test runs in the direction that is safe rather than the one that feels natural: every person in the READING
    /// room must have been party to the fact. So something three people discussed is recallable when one of them asks
    /// alone, and something one person said is NOT recallable once somebody else is in the room. Reverse it — ask merely
    /// whether the room and the fact overlap — and adding a person to a channel hands them everything already said in it.
    /// </para>
    /// <para>
    /// A room whose membership is unknown gets nothing. That reads as unhelpful and is the only defensible answer: not
    /// knowing who is listening is not a reason to assume it is fine.
    /// </para>
    /// </remarks>
    public static bool Sees(string? fact, string? reader)
    {
        // Nothing was said about who was party to it. Correct for a single-person host, which stamps nothing at all.
        if (string.IsNullOrEmpty(fact)) return true;

        var known = Audience.Parse(fact);
        if (known.IsPublic) return true;
        if (known.IsUnknown) return true;

        var room = Audience.Parse(reader);

        // A room anybody can read may not be told something only some people were party to.
        if (room.IsPublic || room.IsUnknown) return false;

        return room.People.All(person => known.People.Contains(person));
    }

    /// <summary>Nothing known about the room. The honest default.</summary>
    public static BrainContext Unknown { get; } = new(Audience.Unknown, default);

    /// <summary>One person on their own — the local app's usual case, an audience of one.</summary>
    public static BrainContext Solo(PersonId person) => new(Audience.Of(person), person);

    /// <summary>A named group: everyone in it, and whichever of them is speaking.</summary>
    public static BrainContext Group(IReadOnlyList<PersonId> participants, PersonId speaker) =>
        new(Audience.Of(participants), speaker);

    /// <summary>A room anybody can read.</summary>
    public static BrainContext PublicRoom(PersonId speaker) => new(Audience.Public, speaker);
}
