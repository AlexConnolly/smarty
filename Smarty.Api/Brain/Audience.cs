using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smarty.Api;

/// <summary>
/// A person, canonically their email address — the one identity that survives crossing surfaces, so the same
/// human in Slack, in an email thread and in the web app is one person to the brain.
/// </summary>
/// <remarks>
/// Compared case-insensitively and stored lowercased, because <c>Dave@x.com</c> and <c>dave@x.com</c> being two
/// people would silently split a brain in half.
/// </remarks>
[JsonConverter(typeof(PersonIdJsonConverter))]
public readonly record struct PersonId : IComparable<PersonId>
{
    private PersonId(string value) => Value = value;

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <summary>Canonicalise a raw identity. Returns empty for anything unusable, which callers must treat as
    /// "unknown person" rather than as a participant.</summary>
    public static PersonId From(string? raw) =>
        new((raw ?? "").Trim().Trim('<', '>').ToLowerInvariant());

    public int CompareTo(PersonId other) => string.CompareOrdinal(Value, other.Value);

    public override string ToString() => Value;
}

internal sealed class PersonIdJsonConverter : JsonConverter<PersonId>
{
    public override PersonId Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        PersonId.From(reader.GetString());

    public override void Write(Utf8JsonWriter writer, PersonId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

/// <summary>
/// Who a piece of knowledge belongs to — and, using the same type, who is currently in the room. Either
/// <see cref="Public"/> (anyone; what a public channel produces and reads) or an explicit set of people.
/// </summary>
/// <remarks>
/// <para>
/// The whole isolation model is one predicate, <see cref="Covers"/>: knowledge with audience <c>S</c> may be
/// recalled in a room whose participants are <c>P</c> <b>iff every participant in P is in S</b> — everyone
/// present must already have been party to it. Not the other way around: a fact learnt alone with one person
/// must not surface the moment someone else joins, and that only falls out of P ⊆ S.
/// </para>
/// <para>
/// Two rooms are deliberately degenerate, because in both the honest answer is "we don't know who can hear
/// this", and a subset test against an unknown set is vacuously true — which would hand over everything:
/// </para>
/// <list type="bullet">
/// <item>A <b>public</b> room is the wildcard set, so no finite audience can contain it — only public
/// knowledge is recalled there. Which is also just correct: a public channel is not the place to surface what
/// three people said in private.</item>
/// <item>An <b>empty</b> room (we couldn't establish who is present) recalls public knowledge only.</item>
/// </list>
/// </remarks>
public sealed class Audience : IEquatable<Audience>
{
    /// <summary>The storage form of the wildcard audience.</summary>
    public const string PublicKey = "*";

    private readonly PersonId[] _people;

    private Audience(bool isPublic, PersonId[] people)
    {
        IsPublic = isPublic;
        _people = people;
    }

    /// <summary>Anyone. What a public channel writes, and what every room can read.</summary>
    public static Audience Public { get; } = new(true, Array.Empty<PersonId>());

    /// <summary>Nobody identified — an unknown room. Reads fall back to public knowledge only; writes to it are
    /// refused by the brain rather than being quietly saved somewhere nobody can reach.</summary>
    public static Audience Unknown { get; } = new(false, Array.Empty<PersonId>());

    public bool IsPublic { get; }

    /// <summary>The people, sorted and de-duplicated. Empty when public or unknown.</summary>
    public IReadOnlyList<PersonId> People => _people;

    public bool IsUnknown => !IsPublic && _people.Length == 0;

    /// <summary>An audience of specific people. Unusable ids are dropped; an empty result is
    /// <see cref="Unknown"/>, never a silently-public audience.</summary>
    public static Audience Of(IEnumerable<PersonId> people)
    {
        var set = people.Where(p => !p.IsEmpty).Distinct().OrderBy(p => p).ToArray();
        return set.Length == 0 ? Unknown : new Audience(false, set);
    }

    public static Audience Of(params PersonId[] people) => Of((IEnumerable<PersonId>)people);

    public static Audience Of(IEnumerable<string> emails) => Of(emails.Select(PersonId.From));

    /// <summary>The canonical storage key: <c>*</c> for public, otherwise the sorted addresses joined by
    /// <c>+</c>. Sorted so the same group always produces the same key regardless of who joined the room first.</summary>
    public string Key => IsPublic ? PublicKey : string.Join('+', _people.Select(p => p.Value));

    public static Audience Parse(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return Unknown;
        if (key.Trim() == PublicKey) return Public;
        return Of(key.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(PersonId.From));
    }

    /// <summary>
    /// May knowledge with THIS audience be recalled in <paramref name="room"/>? True iff every participant in
    /// the room was already party to it (P ⊆ S). Public knowledge is readable anywhere; a public or unknown
    /// room reads nothing but public knowledge.
    /// </summary>
    public bool Covers(Audience room)
    {
        if (IsPublic) return true;              // anyone may read it, including a public room
        if (room is null) return false;
        if (room.IsPublic || room.IsUnknown) return false;  // see the class remarks: never vacuously true
        if (IsUnknown) return false;            // knowledge nobody is attached to is unreachable, by design

        // P ⊆ S. Both sides are small (a channel's membership), so a linear scan beats allocating a set.
        foreach (var participant in room._people)
            if (Array.IndexOf(_people, participant) < 0) return false;

        return true;
    }

    /// <summary>Whether this person was party to the knowledge (public counts as everyone).</summary>
    public bool Includes(PersonId person) => IsPublic || Array.IndexOf(_people, person) >= 0;

    /// <summary>How specific this audience is, for ranking: a fact known to three people is more particular to
    /// the room than one the whole organisation shares.</summary>
    public int Size => IsPublic ? int.MaxValue : _people.Length;

    public bool Equals(Audience? other) => other is not null && Key == other.Key;

    public override bool Equals(object? obj) => Equals(obj as Audience);

    public override int GetHashCode() => Key.GetHashCode();

    public override string ToString() => IsPublic ? "public" : IsUnknown ? "unknown" : Key;
}
