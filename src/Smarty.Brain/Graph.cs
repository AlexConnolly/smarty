using System.Text.Json;

namespace Smarty.Brain;

/// <summary>
/// The graph: nodes, the edges between them, and the walks that make it worth having.
/// </summary>
/// <remarks>
/// <para>
/// Storage is a flat list of edges; the adjacency list everyone pictures is built in memory on load. That is not a
/// compromise, it is the only arrangement that works both ways round: connections held on the node make "what does Alex
/// like" free and "who likes cycling" a scan of everything. Storing each edge twice would fix the scan and introduce
/// two copies of one truth that can disagree.
/// </para>
/// <para>
/// Both indexes are derived, never persisted, so a file edited by hand cannot desync them.
/// </para>
/// </remarks>
public sealed partial class Graph
{
    private readonly List<Node> _nodes = new();
    private readonly List<Edge> _edges = new();
    private readonly object _lock = new();
    private readonly string? _dir;
    private readonly JsonSerializerOptions _json;
    private int _nextEdge;

    /// <summary>How far a walk goes before "connected" stops meaning anything.</summary>
    public const int MaxHops = 3;

    public Graph(string? dir = null, JsonSerializerOptions? json = null)
    {
        _dir = dir;
        _json = json ?? new JsonSerializerOptions { WriteIndented = true };
        Load();
    }

    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Somewhere to say what the graph itself refused, for the log.
    /// </summary>
    /// <remarks>
    /// The refusals in here are silent by nature — an alias that is really a relationship is simply not added — and a
    /// silent refusal is indistinguishable from a write that never happened. Optional, because the graph works without
    /// anybody listening.
    /// </remarks>
    public Action<string>? Trace { get; set; }

    /// <summary>
    /// Verbs that carry no relationship of their own.
    /// </summary>
    /// <remarks>
    /// A closed list, deliberately tiny, and only ever consulted about a link to a PERSON. Every one of these is a fine
    /// label for owning a thing and a useless one for knowing somebody — and the sentence that produced it always said
    /// which relationship it was.
    /// </remarks>
    private static readonly HashSet<string> Empty = new(StringComparer.OrdinalIgnoreCase)
        { "has", "have", "had", "is", "was", "of", "with", "to", "for", "and" };

    /// <summary>
    /// The labels a link to a person is refused for, in the order they read.
    /// </summary>
    /// <remarks>
    /// Exposed so whoever writes a plan can be TOLD, rather than finding out by being refused. A guard the writer cannot
    /// see produces the same rejected edge every time and a question in place of a fact — which is what happened: a
    /// sister survived as a question because nothing had mentioned the rule.
    /// </remarks>
    public static IReadOnlyList<string> EmptyForPeople { get; } = new[] { "has", "have", "had", "is", "was", "of" };

    public IReadOnlyList<Node> Nodes
    {
        get { lock (_lock) return _nodes.Where(n => n.MergedInto is null).ToList(); }
    }

    public IReadOnlyList<Edge> Edges
    {
        get { lock (_lock) return _edges.ToList(); }
    }

    /// <summary>
    /// The kinds this graph actually contains, most used first, with the system kinds always present.
    /// </summary>
    /// <remarks>
    /// What makes an open vocabulary safe, and the same device as reporting the labels in use rather than declaring them:
    /// whoever is about to write or filter is shown what is already here, so they reuse it. The counts are the other half
    /// — a kind holding one node is either brand new or a near-duplicate of one holding forty, and both are worth seeing.
    /// </remarks>
    public IReadOnlyList<(string Kind, int Count)> KindsInUse()
    {
        var counts = Nodes
            .GroupBy(n => n.Kind, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var system in Kinds.System)
            counts.TryAdd(system, 0);

        return counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => (pair.Key, pair.Value))
            .ToList();
    }

    /// <summary>
    /// The labels this graph already uses, most used first.
    /// </summary>
    /// <remarks>
    /// The same device as reporting the kinds, for the same reason, against a worse failure. Labels are free prose and
    /// nothing constrains their length, so a model with no idea what wording already exists writes a whole clause as a
    /// label — turning one fact into a phrase no other fact will ever share, which is a fact that can never be found by
    /// its sort. Shown the words already in use, it reuses them, and the vocabulary converges on itself.
    /// </remarks>
    public IReadOnlyList<(string Label, int Count)> LabelsInUse(int most = 60)
    {
        lock (_lock)
            return _edges
                .Where(e => e.State == EdgeState.Active)
                .GroupBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, most))
                .Select(g => (g.Key, g.Count()))
                .ToList();
    }

    /// <summary>
    /// Change what a node is called, keeping its identity and everything joined to it.
    /// </summary>
    /// <remarks>
    /// The id never moves, so nothing pointing at it breaks, and the old name is kept as an alias — anything that used to
    /// find it still does. Which is what makes correcting a name safe: the alternative is a second node beside the first,
    /// and half the memory answering to each.
    /// </remarks>
    public bool Rename(string id, string name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return false;

        lock (_lock)
        {
            if (Get(id) is not { } node) return false;
            if (node.Name.Equals(trimmed, StringComparison.Ordinal)) return true;

            var was = Normalise(node.Name);
            node.Name = trimmed;
            if (was.Length > 0 && !node.Aliases.Contains(was)) node.Aliases.Add(was);

            Save();
            return true;
        }
    }

    // ---- identity ----

    /// <summary>
    /// The node a name refers to, or nothing.
    /// </summary>
    /// <remarks>
    /// Exact matching only, and ambiguity is reported rather than guessed at. Picking one of two Emmas is how a graph
    /// starts producing confident wrong answers, and the cost of asking is one question.
    /// </remarks>
    public Resolution Resolve(string? name, string? kind = null)
    {
        var wanted = Normalise(name);
        if (wanted.Length == 0) return Resolution.None;

        lock (_lock)
        {
            var hits = _nodes
                .Where(n => n.MergedInto is null)
                .Where(n => kind is null || n.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
                .Where(n => n.AllNames().Any(candidate => Normalise(candidate) == wanted))
                .ToList();

            return hits.Count switch
            {
                1 => new Resolution(hits[0], Ambiguous: Array.Empty<Node>()),
                0 => Resolution.None,
                _ => new Resolution(null, hits),
            };
        }
    }

    public Node? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        lock (_lock)
        {
            var node = _nodes.FirstOrDefault(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

            // Follow the tombstone, so anything still holding a merged-away id keeps working.
            var guard = 0;
            while (node?.MergedInto is { } into && guard++ < 8)
                node = _nodes.FirstOrDefault(n => n.Id.Equals(into, StringComparison.OrdinalIgnoreCase));

            return node;
        }
    }

    /// <summary>
    /// The node this name refers to, adding it if it is new.
    /// </summary>
    /// <remarks>
    /// A name already known always means the thing already known — mentioning Emma again is a mention, not a second
    /// Emma. Which is why a second one is <see cref="Add"/> and never inferred: the graph cannot tell "em" (a nickname
    /// for the Emma on file) from "emma@work.example" (a different Emma entirely). Both are strings it has not seen, and
    /// only the caller knows which it meant.
    /// </remarks>
    public Node Upsert(string name, string kind, IEnumerable<string>? aliases = null, bool provisional = false)
    {
        kind = Kinds.Clean(kind);

        var offered = Normalised(aliases);
        var existing = Resolve(name, kind);

        if (existing.Node is { } found)
        {
            if (offered.Count > 0) AddAliases(found.Id, offered);
            return found;
        }

        if (existing.IsAmbiguous)
        {
            // Several already answer to this. Something that names one of them settles it; with nothing to go on,
            // inventing a third is the confident-nonsense outcome this design exists to avoid, so it becomes a question.
            if (existing.Ambiguous.FirstOrDefault(n => Answers(n, offered)) is { } picked) return picked;
            throw new AmbiguousName(name, existing.Ambiguous);
        }

        return Add(name, kind, offered, provisional);
    }

    /// <summary>
    /// Another one, deliberately, even though something already answers to this name.
    /// </summary>
    /// <remarks>
    /// Two friends called Emma is ordinary and has to be sayable. Two places called Palma is always a mistake, so for
    /// kinds whose names may not repeat this is the same as <see cref="Upsert"/> — the refusal lives in one place rather
    /// than in the judgement of every caller.
    /// </remarks>
    public Node Add(string name, string kind, IEnumerable<string>? aliases = null, bool provisional = false)
    {
        kind = Kinds.Clean(kind);
        var offered = Normalised(aliases);

        lock (_lock)
        {
            if (!Kinds.NamesMayRepeat(kind))
            {
                var clash = _nodes.FirstOrDefault(n =>
                    n.MergedInto is null
                    && n.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)
                    && Normalise(n.Name) == Normalise(name));

                if (clash is not null)
                {
                    foreach (var alias in offered)
                        if (!clash.Aliases.Contains(alias)) clash.Aliases.Add(alias);
                    Save();
                    return clash;
                }
            }

            var node = new Node
            {
                Id = FreeSlug(name),
                Kind = kind,
                Name = name.Trim(),
                Aliases = offered.ToList(),
                Created = Now(),
                Provisional = provisional,
            };

            _nodes.Add(node);
            Save();

            // A new node makes sense of facts already recorded in words: every text edge whose value is exactly this
            // name becomes a real connection. That is how the graph gets denser through use rather than curation.
            RelinkTo(node);
            return node;
        }
    }

    /// <summary>
    /// A node this name might already be, when nothing answers to it exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only ONE shape of near-match is looked for: a name that is a whole word-prefix of an existing one, or the other
    /// way round. Matt / Matthew, Fonda / Fonda Fitzrovia, Niamh / Niamh Connolly. Everything else — Bob for Robert,
    /// Kate for Katherine, initials, misspellings — is deliberately missed, because the only alternative is a table of
    /// nicknames or a similarity score, and both of those eventually produce a CONFIDENT wrong pairing. A missed maybe
    /// costs a question nobody was asked; a wrong one attaches somebody's brother to a stranger.
    /// </para>
    /// <para>
    /// Never a merge, and never applied to anything already confirmed distinct. It answers "is this worth asking
    /// about?" and nothing else.
    /// </para>
    /// </remarks>
    public Node? MightBe(string? name, string? kind)
    {
        var wanted = Normalise(name);
        if (wanted.Length < 3) return null;

        lock (_lock)
        {
            var candidates = _nodes
                .Where(n => n.MergedInto is null)
                .Where(n => kind is null || n.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
                .Where(n => n.AllNames().Any(other => Covers(wanted, Normalise(other))))
                .ToList();

            // Two candidates is not a maybe, it is a coin toss — and a question offering a choice between two people
            // who might both be wrong is worse than a question about one.
            return candidates.Count == 1 ? candidates[0] : null;
        }
    }

    /// <summary>
    /// Is one of these a prefix of the other, at a boundary a person would recognise?
    /// </summary>
    /// <remarks>
    /// Normalised names have no spaces, so the boundary test is length: the shorter has to be at least three letters and
    /// the longer has to continue rather than merely start alike. "mat" against "matthew" passes; "sam" against "samantha"
    /// passes, which is exactly the sort of thing worth ASKING about rather than deciding.
    /// </remarks>
    /// <summary>Could these be one thing under a longer and a shorter name? The same test the maybe is built on.</summary>
    public static bool CouldBeSame(string? one, string? other) => Covers(Normalise(one), Normalise(other));

    private static bool Covers(string one, string other)
    {
        if (one.Length == 0 || other.Length == 0 || one == other) return false;
        var (shorter, longer) = one.Length < other.Length ? (one, other) : (other, one);
        return shorter.Length >= 3 && longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    /// <summary>
    /// Say that a node might be another one — an open question, recorded where it cannot be lost.
    /// </summary>
    public bool Might(string id, string otherId)
    {
        lock (_lock)
        {
            var node = Get(id);
            var other = Get(otherId);
            if (node is null || other is null || node.Id == other.Id) return false;
            if (node.NotSameAs.Contains(other.Id, StringComparer.OrdinalIgnoreCase)) return false;

            node.MaybeSameAs = other.Id;
            Save();
            return true;
        }
    }

    /// <summary>
    /// Every identity question still open, newest first.
    /// </summary>
    /// <remarks>
    /// Derived from the graph rather than kept in a queue, which is what makes it survivable: a question held in a list
    /// somewhere is lost by the next restart, and this one has to outlive several — nobody answers "is Matt your
    /// brother Matthew" within thirty seconds of it being asked.
    /// </remarks>
    public IReadOnlyList<(Node Node, Node Maybe)> Maybes()
    {
        lock (_lock)
            return _nodes
                .Where(n => n.MergedInto is null && n.MaybeSameAs is { Length: > 0 })
                .Select(n => (Node: n, Maybe: Get(n.MaybeSameAs)))
                .Where(pair => pair.Maybe is not null && pair.Maybe.Id != pair.Node.Id)
                .Select(pair => (pair.Node, Maybe: pair.Maybe!))
                .OrderByDescending(pair => pair.Node.Created)
                .ToList();
    }

    /// <summary>
    /// Yes, they are the same thing: merge, and keep the name that was in doubt as another name for the survivor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Which one survives is not the caller's business and not a coin toss: the node everything else already points at
    /// has to be the one left standing, or a merge quietly renames somebody. So the doubtful one loses — the node
    /// flagged provisional, failing that the one holding fewer facts, and only as a last resort the newer of the two.
    /// </para>
    /// <para>
    /// Timestamps alone were the first attempt and they are not enough: two nodes created in the same reading share one,
    /// and the tie then went to whichever was passed in first — so answering the same question the other way round
    /// renamed a brother. The loser's name becomes an alias in <see cref="Merge"/>, which is the whole point: after
    /// this, "Matt" finds Matthew for ever without anybody having to say so again.
    /// </para>
    /// </remarks>
    public bool TheSame(string id, string otherId)
    {
        lock (_lock)
        {
            var node = Get(id);
            var other = Get(otherId);
            if (node is null || other is null || node.Id == other.Id) return false;

            var (loser, winner) = Doubtful(node, other) ? (node, other) : (other, node);
            loser.MaybeSameAs = null;
            winner.MaybeSameAs = null;
            // Confirmed by a person, so it is no longer a guess at anything.
            winner.Provisional = false;
            return Merge(loser.Id, winner.Id);
        }
    }

    /// <summary>Which of two nodes about to become one is the one to give up.</summary>
    private bool Doubtful(Node node, Node other)
    {
        if (node.Provisional != other.Provisional) return node.Provisional;

        var mine = Around(node.Id).Count;
        var theirs = Around(other.Id).Count;
        if (mine != theirs) return mine < theirs;

        if (node.Created != other.Created) return node.Created > other.Created;

        // Nothing to choose between them: same standing, same facts, same instant. Decided on the id so that the answer
        // does not depend on which way round the question was asked — "is Matt Matthew" and "is Matthew Matt" have to
        // leave the same person standing, or answering it twice renames somebody.
        return string.CompareOrdinal(node.Id, other.Id) > 0;
    }

    /// <summary>
    /// No, they are two different things — recorded, so it is never asked again.
    /// </summary>
    public bool NotTheSame(string id, string otherId)
    {
        lock (_lock)
        {
            var node = Get(id);
            var other = Get(otherId);
            if (node is null || other is null || node.Id == other.Id) return false;

            if (!node.NotSameAs.Contains(other.Id, StringComparer.OrdinalIgnoreCase)) node.NotSameAs.Add(other.Id);
            if (!other.NotSameAs.Contains(node.Id, StringComparer.OrdinalIgnoreCase)) other.NotSameAs.Add(node.Id);
            if (string.Equals(node.MaybeSameAs, other.Id, StringComparison.OrdinalIgnoreCase)) node.MaybeSameAs = null;
            if (string.Equals(other.MaybeSameAs, node.Id, StringComparison.OrdinalIgnoreCase)) other.MaybeSameAs = null;

            // Two things genuinely distinct are both real, whatever created them.
            node.Provisional = false;
            other.Provisional = false;

            // And the name they were confused over stops being a name for whichever of them only BORROWED it.
            //
            // Without this the answer changes nothing. "Alex" was also on file as another name for Alex's wife, so
            // every question that resolved "me" found two candidates and refused — and it kept refusing after being
            // told they are different people, because the shared name was still there. The graph recorded "these are
            // not the same" and stayed unable to tell them apart, which is the one state worse than not having asked.
            //
            // Only aliases go. A node's own Name is what it IS, and two people genuinely called Matt is a real
            // ambiguity that a question is right to raise — this removes the borrowed name, never the real one.
            foreach (var shared in node.AllNames().Select(Normalise)
                         .Intersect(other.AllNames().Select(Normalise), StringComparer.OrdinalIgnoreCase)
                         .ToList())
            {
                if (!Normalise(node.Name).Equals(shared, StringComparison.OrdinalIgnoreCase))
                    node.Aliases.RemoveAll(a => Normalise(a).Equals(shared, StringComparison.OrdinalIgnoreCase));
                if (!Normalise(other.Name).Equals(shared, StringComparison.OrdinalIgnoreCase))
                    other.Aliases.RemoveAll(a => Normalise(a).Equals(shared, StringComparison.OrdinalIgnoreCase));
            }

            Save();
            return true;
        }
    }

    private static List<string> Normalised(IEnumerable<string>? names)
        => (names ?? Array.Empty<string>()).Select(Normalise).Where(a => a.Length > 0).Distinct().ToList();

    /// <summary>Whether any of these already-normalised names is one this node answers to.</summary>
    private static bool Answers(Node node, IReadOnlyList<string> normalised)
        => normalised.Count > 0 && node.AllNames().Select(Normalise).Intersect(normalised).Any();

    /// <summary>
    /// Add other names something answers to. Returns only the ones that were actually new.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A RELATIONSHIP is not a name, and the moment stating other names became possible the writer started putting them
    /// here: "Matthew — also known as — brother" and "Niamh — also known as — sister", from two sentences that had
    /// stated a relationship perfectly well. The damage is quiet and total — "brother" then resolves to Matthew, so the
    /// next person's brother is recorded as him.
    /// </para>
    /// <para>
    /// Refused by reading the graph rather than by a list of forbidden words: anything used as an edge label HERE is a
    /// relationship in this memory, so it cannot also be a name in it. That grows with the data and needs nothing
    /// maintained.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> AddAliases(string id, IEnumerable<string> aliases)
    {
        lock (_lock)
        {
            if (Get(id) is not { } node) return Array.Empty<string>();

            var relations = _edges
                .Select(e => Normalise(e.Label))
                .Where(label => label.Length > 0)
                .ToHashSet(StringComparer.Ordinal);

            var added = new List<string>();
            foreach (var alias in aliases.Select(Normalise).Where(a => a.Length > 0))
            {
                if (node.Aliases.Contains(alias) || Normalise(node.Name) == alias) continue;

                if (relations.Contains(alias))
                {
                    Trace?.Invoke($"[graph] \"{alias}\" is a relationship in this memory, not another name for " +
                                  $"{node.Name} — not adding it");
                    continue;
                }

                node.Aliases.Add(alias);
                added.Add(alias);
            }

            if (added.Count > 0) Save();
            return added;
        }
    }

    /// <summary>
    /// Two nodes were one thing. Everything pointing at the loser is repointed and the loser becomes a tombstone.
    /// </summary>
    public bool Merge(string loserId, string winnerId)
    {
        lock (_lock)
        {
            var loser = _nodes.FirstOrDefault(n => n.Id.Equals(loserId, StringComparison.OrdinalIgnoreCase));
            var winner = Get(winnerId);
            if (loser is null || winner is null || loser.Id == winner.Id) return false;

            foreach (var edge in _edges)
            {
                if (edge.From.Equals(loser.Id, StringComparison.OrdinalIgnoreCase)) edge.From = winner.Id;
                if (edge.To is { } to && to.Equals(loser.Id, StringComparison.OrdinalIgnoreCase)) edge.To = winner.Id;
            }

            // Two nodes becoming one turns two facts into the same fact twice. Seen the first time a real merge ran:
            // "Matthew — has bought — Camper Van" appeared on the panel twice, because the edge had been written once
            // against Matt and once against Matthew and repointing made them identical. The older one is kept, since it
            // is the one anything else already refers to.
            var kept = new List<Edge>();
            foreach (var edge in _edges.OrderBy(e => e.Asserted).ToList())
            {
                var already = kept.Any(k =>
                    k.From.Equals(edge.From, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(k.Label, edge.Label, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(k.To ?? "", edge.To ?? "", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(k.Value ?? "", edge.Value ?? "", StringComparison.OrdinalIgnoreCase)
                    && k.State == edge.State);

                if (already) _edges.Remove(edge);
                else kept.Add(edge);
            }

            // The loser's names come too, or every phrase that used to find it stops working.
            foreach (var alias in loser.AllNames().Select(Normalise).Where(a => a.Length > 0))
                if (!winner.Aliases.Contains(alias)) winner.Aliases.Add(alias);

            loser.MergedInto = winner.Id;
            Save();
            return true;
        }
    }

    // ---- writing ----

    /// <summary>State a property: a value in words, about one node.</summary>
    public Edge State(string fromId, string label, string value, string? note = null, string? source = null,
        string? audience = null, DateTimeOffset? when = null)
        => Write(fromId, label, value, to: null, note, source, audience, when);

    /// <summary>State a relationship between two nodes.</summary>
    /// <summary>
    /// State a relationship between two nodes.
    /// </summary>
    /// <remarks>
    /// A node cannot be related to itself. Such an edge carries no information — it says a thing is connected to the thing
    /// it already is — and it pollutes every walk that passes through, appearing as a neighbour of itself. One was written
    /// as soon as a model was given free rein over labels, so the refusal belongs here rather than in the judgement of
    /// whatever is writing.
    /// </remarks>
    public Edge Link(string fromId, string label, string toId, string? note = null, string? source = null,
        string? audience = null, DateTimeOffset? when = null)
    {
        var target = Get(toId) ?? throw new ArgumentException($"No node \"{toId}\".", nameof(toId));
        var from = Get(fromId) ?? throw new ArgumentException($"No node \"{fromId}\".", nameof(fromId));

        if (from.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"\"{from.Name}\" cannot be its own {label}.", nameof(toId));

        // Nobody HAS a person.
        //
        // "Alex has a flight ticket" is possession and a perfectly good fact. "Alex has Niamh" is not: a person stands
        // in some relation to another person, the sentence always says which one — sister, neighbour, wife — and this
        // label is the writer discarding the only part that mattered. It arrived as "my sister is called Niamh" and was
        // recorded as though she were an object he owned.
        //
        // Narrow on purpose: the closed list below, and only when the far end is a PERSON. That is a property of the two
        // nodes rather than a reading of the words, which is why it can live here — and why it cannot fire on the honest
        // uses of the same verb.
        if (Kinds.NamesMayRepeat(target.Kind) && Empty.Contains((label ?? "").Trim()))
            throw new ArgumentException(
                $"\"{label}\" says nothing about how {from.Name} and {target.Name} are related — use the words the " +
                "sentence used.", nameof(label));

        // The same label back the other way already exists, so this says nothing new.
        //
        // "Alex has the places list" and "the places list has Alex" are not two facts; they are one fact written twice
        // by a writer reaching for the emptiest verb available, and the pair is a reliable tell that the label carries
        // no direction and therefore no meaning. Refused here rather than judged upstream, for the same reason a
        // self-link is: it is a structural property of the edge, visible without reading a word of it.
        var wording = (label ?? "").Trim();
        lock (_lock)
        {
            var mirrored = _edges.Any(e =>
                e.State == EdgeState.Active
                && e.From.Equals(target.Id, StringComparison.OrdinalIgnoreCase)
                && e.To is { } back && back.Equals(from.Id, StringComparison.OrdinalIgnoreCase)
                && e.Label.Equals(wording, StringComparison.OrdinalIgnoreCase));

            if (mirrored)
                throw new ArgumentException(
                    $"\"{target.Name} — {wording} → {from.Name}\" is already on file; the reverse says nothing more.",
                    nameof(toId));
        }

        return Write(fromId, label, target.Name, target.Id, note, source, audience, when);
    }

    private Edge Write(string fromId, string label, string value, string? to, string? note, string? source,
        string? audience, DateTimeOffset? when = null)
    {
        var from = Get(fromId) ?? throw new ArgumentException($"No node \"{fromId}\".", nameof(fromId));
        label = (label ?? "").Trim();
        if (label.Length == 0) throw new ArgumentException("A fact needs a label.", nameof(label));

        lock (_lock)
        {
            var edge = new Edge
            {
                Id = $"e{++_nextEdge}",
                From = from.Id,
                Label = label,
                To = to,
                Value = (value ?? "").Trim(),
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                Asserted = Now(),
                When = when,
                Source = source,
                Audience = audience,
            };

            var previous = _edges.FirstOrDefault(e => e.State == EdgeState.Active && e.Slot == edge.Slot);
            if (previous is not null)
            {
                // The same thing said again is a reaffirmation, not a change — restamp it rather than growing history.
                //
                // The moment counts as part of "the same thing". Left out, moving a booking from six to eight looks
                // identical to being told about it twice, and the new time is dropped on the floor with the old one
                // still reading as current — which is worse than never having recorded a time at all.
                if (string.Equals(previous.Value, edge.Value, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(previous.Note, edge.Note, StringComparison.OrdinalIgnoreCase)
                    && previous.When == edge.When)
                {
                    previous.Asserted = edge.Asserted;
                    _nextEdge--;
                    Save();
                    return previous;
                }

                previous.State = EdgeState.Superseded;
                previous.SupersededBy = edge.Id;
                previous.Ended = edge.Asserted;
            }

            _edges.Add(edge);
            Save();
            return edge;
        }
    }

    /// <summary>
    /// End a relationship, with the reason.
    /// </summary>
    /// <remarks>
    /// The reason is required. A retirement without one leaves a tidy graph and no story, which loses the most useful
    /// thing here: being able to ask later why something is no longer the case.
    /// </remarks>
    public bool End(string fromId, string label, string toId, string because)
    {
        if (string.IsNullOrWhiteSpace(because))
            throw new ArgumentException("Ending something needs a reason.", nameof(because));

        var from = Get(fromId);
        var to = Get(toId);
        if (from is null || to is null) return false;

        lock (_lock)
        {
            var edge = _edges.FirstOrDefault(e =>
                e.State == EdgeState.Active
                && e.From.Equals(from.Id, StringComparison.OrdinalIgnoreCase)
                && e.To is { } t && t.Equals(to.Id, StringComparison.OrdinalIgnoreCase)
                && e.Label.Equals(label, StringComparison.OrdinalIgnoreCase));

            if (edge is null) return false;

            edge.State = EdgeState.Ended;
            edge.Because = because.Trim();
            edge.Ended = Now();
            Save();
            return true;
        }
    }

    /// <summary>
    /// Retire one edge by id, whatever kind it is.
    /// </summary>
    /// <remarks>
    /// For a fact removed by hand rather than by something being said. Still an ending rather than a deletion — a fact
    /// removed from an editor was once true, and the whole design here says the reason it went is worth as much as the
    /// fact was. It also means a hand-edit can be seen afterwards, which a delete cannot.
    /// </remarks>
    public bool Retire(string edgeId, string because)
    {
        lock (_lock)
        {
            var edge = _edges.FirstOrDefault(e => e.Id.Equals(edgeId, StringComparison.OrdinalIgnoreCase));
            if (edge is null || edge.State != EdgeState.Active) return false;

            edge.State = EdgeState.Ended;
            edge.Because = string.IsNullOrWhiteSpace(because) ? "removed" : because.Trim();
            edge.Ended = Now();
            Save();
            return true;
        }
    }

    // ---- walking ----

    /// <summary>
    /// Every connection touching a node, in both directions.
    /// </summary>
    /// <remarks>
    /// The inbound half is the reason edges are stored separately. Without it a topic is a dead end: three people can
    /// be recorded as cyclists and cycling still cannot tell you who they are.
    /// </remarks>
    public IReadOnlyList<Step> Around(string nodeId, bool includeEnded = false, Func<Edge, bool>? visible = null)
    {
        var node = Get(nodeId);
        if (node is null) return Array.Empty<Step>();

        lock (_lock)
        {
            var steps = new List<Step>();

            foreach (var edge in _edges)
            {
                if (!includeEnded && edge.State != EdgeState.Active) continue;
                if (!edge.IsRelationship) continue;
                if (visible is not null && !visible(edge)) continue;

                if (edge.From.Equals(node.Id, StringComparison.OrdinalIgnoreCase) && Get(edge.To) is { } forward)
                    steps.Add(new Step(edge, node, forward, Backwards: false));
                else if (edge.To!.Equals(node.Id, StringComparison.OrdinalIgnoreCase) && Get(edge.From) is { } back)
                    steps.Add(new Step(edge, node, back, Backwards: true));
            }

            return steps;
        }
    }

    /// <summary>
    /// Whether these exact words are already recorded as a value somewhere.
    /// </summary>
    /// <remarks>
    /// The signal that a description has earned nodehood. Said once it is a value; written down a second time by a
    /// different fact, it is something two facts have in common, and only a node can be shared. Which also makes the
    /// promotion free — creating the node relinks the earlier value to it, so nothing had to be guessed right first time.
    /// </remarks>
    public bool MentionedAsValue(string? words)
    {
        var wanted = Normalise(words);
        if (wanted.Length == 0) return false;

        lock (_lock)
            return _edges.Any(e => e.State == EdgeState.Active && !e.IsRelationship && Normalise(e.Value) == wanted);
    }

    /// <summary>The properties stated about a node — the facts with no other end.</summary>
    public IReadOnlyList<Edge> Properties(string nodeId, Func<Edge, bool>? visible = null)
    {
        var node = Get(nodeId);
        if (node is null) return Array.Empty<Edge>();

        lock (_lock)
            return _edges
                .Where(e => e.State == EdgeState.Active && !e.IsRelationship)
                .Where(e => e.From.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
                .Where(e => visible is null || visible(e))
                .ToList();
    }

    /// <summary>
    /// The nodes reachable from here, nearest first.
    /// </summary>
    /// <remarks>
    /// Breadth-first and capped. A graph in use for a year is one connected blob, so nearness is the only usable proxy
    /// for relevance — walk it depth-first and you arrive at somebody's dietary requirements by way of a holiday and a
    /// bicycle.
    /// </remarks>
    /// <param name="avoid">
    /// Nodes that may be REACHED but never walked through. Used when asking what two things have in common: each walk has
    /// to reach a shared thing on its own, and without a wall between them everything either one touches is two hops from
    /// the other by way of the other — so "what do we both do" answers with everything one of them does alone.
    /// </param>
    public IReadOnlyList<Reached> Neighbourhood(string nodeId, int hops = 2, int max = 60, string? keeping = null,
        Func<Edge, bool>? visible = null, IReadOnlyCollection<string>? avoid = null)
    {
        var start = Get(nodeId);
        if (start is null) return Array.Empty<Reached>();

        hops = Math.Clamp(hops, 1, MaxHops);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start.Id };
        var reached = new List<Reached>();
        var frontier = new List<Node> { start };

        for (var hop = 1; hop <= hops && reached.Count < max; hop++)
        {
            var next = new List<Node>();

            foreach (var node in frontier)
            foreach (var step in Around(node.Id, visible: visible))
            {
                if (reached.Count >= max) break;
                if (!seen.Add(step.To.Id)) continue;

                // Walking on through something not being kept is the whole point of the second hop: nobody records a
                // hobby against a holiday, so reaching one means passing THROUGH a person. Which is also why the cap has
                // to count what is kept — count everything walked and a crowd of hobbies quietly eats the answer to
                // "who is coming", leaving four people looking like the whole truth.
                if (keeping is null || step.To.Kind.Equals(keeping, StringComparison.OrdinalIgnoreCase))
                    reached.Add(new Reached(step.To, hop, step));

                // Reached, but not expanded from. The distinction is the whole point of a wall.
                if (avoid is null || !avoid.Contains(step.To.Id)) next.Add(step.To);
            }

            if (next.Count == 0) break;
            frontier = next;
        }

        return reached;
    }

    /// <summary>
    /// The shortest chain of facts joining two nodes, or nothing.
    /// </summary>
    /// <remarks>
    /// The answer a list of facts can never give, and the path IS the answer rather than a route to it: "the trip's
    /// hotel is the Llaut, the Llaut is in Palma" explains itself and can be checked. Never inferred from similarity —
    /// a synthesised path is a plausible fiction, and the whole value here is that the reasoning is readable.
    /// </remarks>
    public IReadOnlyList<Step> Path(string fromId, string toId, int maxHops = MaxHops, IEnumerable<string>? along = null)
    {
        var from = Get(fromId);
        var to = Get(toId);
        if (from is null || to is null || from.Id == to.Id) return Array.Empty<Step>();

        var only = along?.Where(l => !string.IsNullOrWhiteSpace(l)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cameFrom = new Dictionary<string, Step>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { from.Id };
        var frontier = new List<Node> { from };

        for (var hop = 0; hop < Math.Clamp(maxHops, 1, MaxHops); hop++)
        {
            var next = new List<Node>();

            foreach (var node in frontier)
            foreach (var step in Around(node.Id))
            {
                if (only is not null && !only.Contains(step.Edge.Label)) continue;
                if (!seen.Add(step.To.Id)) continue;
                cameFrom[step.To.Id] = step;

                if (step.To.Id.Equals(to.Id, StringComparison.OrdinalIgnoreCase))
                {
                    var path = new List<Step>();
                    var at = to.Id;
                    while (cameFrom.TryGetValue(at, out var back))
                    {
                        path.Add(back);
                        if (back.From.Id.Equals(from.Id, StringComparison.OrdinalIgnoreCase)) break;
                        at = back.From.Id;
                    }
                    path.Reverse();
                    return path;
                }

                next.Add(step.To);
            }

            if (next.Count == 0) break;
            frontier = next;
        }

        return Array.Empty<Step>();
    }

    /// <summary>
    /// Whether one node can be reached from another at all, however far.
    /// </summary>
    /// <remarks>
    /// Uncapped, unlike a walk. A walk is bounded because nearness is a proxy for relevance; this is asking a different
    /// question — is this thing part of the same world at all — and a limit there would call a genuinely connected thing
    /// unconnected just for being far away.
    /// </remarks>
    public bool Reaches(string fromId, string toId)
    {
        var from = Get(fromId);
        var to = Get(toId);
        if (from is null || to is null) return false;
        if (from.Id.Equals(to.Id, StringComparison.OrdinalIgnoreCase)) return true;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { from.Id };
        var queue = new Queue<string>();
        queue.Enqueue(from.Id);

        while (queue.Count > 0)
        {
            foreach (var step in Around(queue.Dequeue()))
            {
                if (step.To.Id.Equals(to.Id, StringComparison.OrdinalIgnoreCase)) return true;
                if (seen.Add(step.To.Id)) queue.Enqueue(step.To.Id);
            }
        }

        return false;
    }

    /// <summary>
    /// Remove a node and everything said about it.
    /// </summary>
    /// <remarks>
    /// The one destructive operation here, and it exists for exactly one job: undoing a node that should never have been
    /// created. Nothing else deletes — a fact that stopped being true is ENDED and keeps its reason, because the reason is
    /// the most valuable thing in the graph. But a thing recorded by mistake has no story worth keeping, and leaving it
    /// means it appears in the list shown on every write and every question for the rest of the graph's life.
    /// </remarks>
    public bool Forget(string id)
    {
        lock (_lock)
        {
            var node = _nodes.FirstOrDefault(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (node is null) return false;

            _edges.RemoveAll(e => e.From.Equals(node.Id, StringComparison.OrdinalIgnoreCase)
                                  || (e.To is { } to && to.Equals(node.Id, StringComparison.OrdinalIgnoreCase)));
            _nodes.Remove(node);

            // Anything merged INTO this one comes back rather than following it into the hole.
            //
            // A tombstone is a name kept alive on purpose: Matt was merged into Matthew, and the record stays so that
            // every later "Matt" still lands on the right person. Removing Matthew without touching it left a pointer
            // into nothing — Get followed it and returned null, the list of everything on file skips merged nodes, and
            // so the graph both HELD the name Matt and could not find it. The next mention of Matt was therefore new,
            // and a second Matt was created, stranded, with a fact on it that belonged to the first.
            //
            // Restored, not deleted with it. Forget is for a node that should never have been created — and if the
            // winner should never have existed, the merge into it should never have happened either.
            foreach (var orphan in _nodes.Where(n =>
                         n.MergedInto is { } into && into.Equals(node.Id, StringComparison.OrdinalIgnoreCase)))
                orphan.MergedInto = null;

            Save();
            return true;
        }
    }

    /// <summary>
    /// Remove one edge outright, for something that should never have been written.
    /// </summary>
    /// <remarks>
    /// Distinct from retiring it, and the distinction is the same one <see cref="Forget"/> rests on: retiring keeps a
    /// thing that was true and stopped being true, which is the whole point of the history. This is for an edge that was
    /// never a fact about this person at all — a description of the world a job read on its way past — and keeping that
    /// as history would mean the graph still answers with it.
    /// </remarks>
    public bool Erase(string edgeId)
    {
        lock (_lock)
        {
            var gone = _edges.RemoveAll(e => e.Id.Equals(edgeId, StringComparison.OrdinalIgnoreCase)) > 0;
            if (gone) Save();
            return gone;
        }
    }

    /// <summary>
    /// Empty it completely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one operation with no undo inside the graph, so the caller owns the consequences: everything goes, including
    /// the owner, which means whoever is holding this will find nobody recorded as "me" afterwards and should expect to
    /// be asked again. Edge history goes too — retirements, reasons, the lot — because a wipe that kept the history
    /// would leave a graph that reads as empty and answers as though it is not.
    /// </para>
    /// <para>
    /// Returns what it removed rather than a bare true, because the only useful thing to say after this is how much
    /// there was.
    /// </para>
    /// </remarks>
    public (int Nodes, int Edges) Wipe()
    {
        lock (_lock)
        {
            var counted = (_nodes.Count, _edges.Count);
            _nodes.Clear();
            _edges.Clear();
            _nextEdge = 0;
            Save();
            return counted;
        }
    }

    // ---- housekeeping ----

    /// <summary>
    /// Nodes that look like the same thing twice.
    /// </summary>
    /// <remarks>
    /// The check that would have caught the duplicate this design was drawn from: <c>project:holiday-with-my-wife</c>
    /// and <c>topic:holiday with my wife</c> are different strings, so no naming rule could refuse them. What gives
    /// them away is structural — two nodes pointing at the same neighbours are almost certainly one node.
    /// </remarks>
    public IReadOnlyList<(Node A, Node B, string Why)> Duplicates()
    {
        var found = new List<(Node, Node, string)>();
        var nodes = Nodes;

        for (var i = 0; i < nodes.Count; i++)
        for (var j = i + 1; j < nodes.Count; j++)
        {
            var (a, b) = (nodes[i], nodes[j]);

            var aNames = a.AllNames().Select(Normalise).ToHashSet();
            var bNames = b.AllNames().Select(Normalise).ToHashSet();
            if (aNames.Overlaps(bNames))
            {
                found.Add((a, b, "they answer to the same name"));
                continue;
            }

            var aRing = Around(a.Id).Select(s => s.To.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var bRing = Around(b.Id).Select(s => s.To.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (aRing.Count >= 2 && bRing.Count >= 2)
            {
                var shared = aRing.Intersect(bRing, StringComparer.OrdinalIgnoreCase).Count();
                if (shared >= 2 && shared >= Math.Min(aRing.Count, bRing.Count))
                    found.Add((a, b, $"they connect to the same {shared} things"));
            }
        }

        return found;
    }

    /// <summary>
    /// Nodes that were probably meant to be values.
    /// </summary>
    /// <remarks>
    /// A time, an amount or a description written as a thing rather than as a value: one connection into it, nothing
    /// recorded about it, and no prospect of anything ever being. Not deleted, because the judgement is a guess and the
    /// cost of being wrong is a lost fact — reported instead, so the pile is visible while it is still small. Left
    /// unreported it grows quietly and the names it adds crowd the list every write and every question is shown.
    /// </remarks>
    public IReadOnlyList<Node> Loose()
    {
        return Nodes
            .Where(n => n.Kind.Equals(Kinds.Topic, StringComparison.OrdinalIgnoreCase))
            .Where(n => Properties(n.Id).Count == 0)
            .Where(n => Around(n.Id).Count == 1)
            .ToList();
    }

    /// <summary>Edges pointing at a node that isn't there. Cheap, and the one cost of storing edges separately.</summary>
    public IReadOnlyList<Edge> Dangling()
    {
        lock (_lock)
            return _edges
                .Where(e => e.State == EdgeState.Active && e.IsRelationship)
                .Where(e => Get(e.To) is null || Get(e.From) is null)
                .ToList();
    }

    // ---- internals ----

    private void RelinkTo(Node node)
    {
        var names = node.AllNames().Select(Normalise).ToHashSet();

        foreach (var edge in _edges)
        {
            if (edge.State != EdgeState.Active || edge.IsRelationship) continue;
            if (edge.From.Equals(node.Id, StringComparison.OrdinalIgnoreCase)) continue;
            if (!names.Contains(Normalise(edge.Value))) continue;

            edge.To = node.Id;
        }

        Save();
    }

    private string FreeSlug(string name)
    {
        var baseSlug = Slug(name);
        if (baseSlug.Length == 0) baseSlug = "node";

        var slug = baseSlug;
        for (var n = 2; _nodes.Any(x => x.Id.Equals(slug, StringComparison.OrdinalIgnoreCase)); n++)
            slug = $"{baseSlug}-{n}";
        return slug;
    }

    public static string Slug(string? text)
    {
        var chars = (text ?? "").Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-');
        var slug = new string(chars.ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }

    /// <summary>Names match on their letters and digits, so punctuation and spacing cannot fork a node.</summary>
    public static string Normalise(string? text) => Slug(text);

    private string NodesPath => Path_(nameof(Nodes));
    private string EdgesPath => Path_(nameof(Edges));
    private string Path_(string what) => System.IO.Path.Combine(_dir!, $"graph-{what.ToLowerInvariant()}.json");

    private void Load()
    {
        if (_dir is null) return;

        try
        {
            if (File.Exists(NodesPath))
                _nodes.AddRange(JsonSerializer.Deserialize<List<Node>>(File.ReadAllText(NodesPath), _json) ?? new());
            if (File.Exists(EdgesPath))
                _edges.AddRange(JsonSerializer.Deserialize<List<Edge>>(File.ReadAllText(EdgesPath), _json) ?? new());

            _nextEdge = _edges
                .Select(e => int.TryParse(e.Id.TrimStart('e'), out var n) ? n : 0)
                .DefaultIfEmpty(0)
                .Max();
        }
        catch
        {
            _nodes.Clear();
            _edges.Clear();
        }
    }

    private void Save()
    {
        if (_dir is null) return;

        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(NodesPath, JsonSerializer.Serialize(_nodes, _json));
            File.WriteAllText(EdgesPath, JsonSerializer.Serialize(_edges, _json));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[graph] couldn't save: {ex.Message}");
        }
    }
}

/// <summary>
/// A name that several things answer to, raised rather than guessed past.
/// </summary>
/// <remarks>
/// Carries the candidates, because "Emma is ambiguous" is not usable and "two people answer to Emma — emma and emma-2 —
/// say which" is. A caller writing to the graph can put that straight back as a question.
/// </remarks>
public sealed class AmbiguousName : Exception
{
    public AmbiguousName(string name, IReadOnlyList<Node> candidates)
        : base($"\"{name}\" already means {candidates.Count} things ({string.Join(", ", candidates.Select(c => c.Id))}). "
               + "Say which, or give something that tells them apart.")
        => Candidates = candidates;

    public IReadOnlyList<Node> Candidates { get; }
}

/// <summary>What a name resolved to: one node, nothing, or several — which is a question, not an answer.</summary>
public readonly record struct Resolution(Node? Node, IReadOnlyList<Node> Ambiguous)
{
    public static Resolution None { get; } = new(null, Array.Empty<Node>());
    public bool IsAmbiguous => Node is null && Ambiguous.Count > 1;
    public bool Found => Node is not null;
}

/// <summary>One crossing of an edge, and which way it was crossed.</summary>
public readonly record struct Step(Edge Edge, Node From, Node To, bool Backwards)
{
    /// <summary>
    /// The step written out, always in the direction the fact was recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not English. It used to build a sentence — "Alex's hobby is cycling" forwards, "cycling is the hobby
    /// of Alex" backwards — and that works only for labels that happen to be nouns. The labels are free prose, so the
    /// first verb phrase broke it: the edge <c>Me — going on → Majorca trip</c>, walked from the trip, came out as
    /// "Majorca trip is the going on of Me", which states the opposite of what is recorded.
    /// </para>
    /// <para>
    /// No template can be right for every label, and one that is wrong is worse than one that is plain, because this is
    /// the line a reader checks the answer against. So the triple is shown as it stands, in its true direction, whichever
    /// way it was crossed — which is also why no inverse edge is stored.
    /// </para>
    /// </remarks>
    public string Sentence() => (Backwards
        ? $"{To.Name} — {Edge.Label} → {From.Name}"
        : $"{From.Name} — {Edge.Label} → {To.Name}") + Moment();

    /// <summary>
    /// The moment, when the fact has one.
    /// </summary>
    /// <remarks>
    /// Shown on the step rather than left for a caller to remember, because the step is what a reader is handed and the
    /// moment is usually the whole reason they asked. A relationship to a place with the time stripped off answers "where"
    /// and refuses "when", which is the wrong half of an arrangement to keep.
    /// </remarks>
    private string Moment() => Edge.When is not { } at
        ? ""
        : at.TimeOfDay == TimeSpan.Zero
            ? $" [{at:ddd d MMM yyyy}]"
            : $" [{at:ddd d MMM yyyy HH:mm}]";
}

/// <summary>A node found by walking: how far away, and the step that got there.</summary>
public readonly record struct Reached(Node Node, int Hops, Step Via);
