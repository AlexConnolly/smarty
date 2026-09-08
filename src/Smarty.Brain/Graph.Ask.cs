namespace Smarty.Brain;

/// <summary>
/// Asking the graph a question, and the two reads that support it.
/// </summary>
public sealed partial class Graph
{
    /// <summary>
    /// Answer a question: resolve the subject, walk out, filter structurally, annotate semantically.
    /// </summary>
    public Answer Ask(Ask ask)
    {
        var resolved = Resolve(ask.About);

        if (resolved.IsAmbiguous)
            return new Answer { Ambiguous = resolved.Ambiguous };

        // A subject the graph has never heard of is not an empty answer. Saying so is the difference between "nothing is
        // connected to your Reykjavik trip" and "you have never mentioned a Reykjavik trip".
        if ((resolved.Node ?? Get(ask.About)) is not { } about) return new Answer();

        // The second subject, when the question is about what two things share. Named as an anchor too, so every result
        // comes back saying how the other one relates to it — which is the half of the answer worth reading.
        var other = ask.With is { Length: > 0 } ? Named(ask.With) : null;
        var wanted = new List<string>(ask.Anchors);
        if (other is not null && !wanted.Any(a => Named(a)?.Id == other.Id)) wanted.Add(other.Name);

        // Each anchor's own ring, read once. An annotation is only ever direct, so this is all of it.
        var anchors = wanted
            .Select(a => (Name: a, Node: Named(a)))
            .Where(a => a.Node is not null)
            .Select(a => (a.Name, a.Node!.Id, Ring: Around(a.Node!.Id, visible: ask.Visible)))
            .ToList();

        // Subtractives name things the same way the question does — "me" is an alias, not an id, and a caller should
        // not have to know which is which to leave themselves out.
        var excludedNodes = ask.Exclude
            .Where(x => x.NodeId is { Length: > 0 })
            .Select(x => Named(x.NodeId!)?.Id)
            .Where(id => id is not null).Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var excludedByEdge = ask.Exclude
            .Where(x => x.EdgeLabel is { Length: > 0 })
            .Select(x => x.EdgeLabel!)
            .ToList();

        var found = new List<Finding>();
        var most = Math.Max(1, ask.Most);

        // The kind filter goes INTO the walk, so the cap bounds the answer rather than the search. Filtering afterwards
        // means a dense graph can fill the cap with things nobody asked for and hand back a short answer that reads as
        // a complete one.
        // Each walk treats the other subject as a wall, so a shared thing has to be reached on its own merits rather
        // than by stepping through the person you are comparing against.
        var wall = other is null ? null : new[] { other.Id };
        var walked = Neighbourhood(about.Id, ask.Hops, most, ask.Wanting, ask.Visible, wall);

        // Reachable from the OTHER end as well. Computed as its own walk and intersected, rather than filtered by a
        // direct edge, because two people share a club through the club — not by being joined to each other.
        var shared = other is null
            ? null
            : Neighbourhood(other.Id, ask.Hops, most, ask.Wanting, ask.Visible, new[] { about.Id })
                .Select(r => r.Node.Id)
                // Plus the other subject itself. A walk never includes its own starting point, so without this the second
                // subject is the one thing the intersection can never return — and "who is Tom's brother", planned as a
                // question about Tom shared with me, answered that it knew of nothing at all while holding the answer.
                .Append(other.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var reached in walked)
        {
            if (shared is not null && !shared.Contains(reached.Node.Id)) continue;
            if (excludedNodes.Contains(reached.Node.Id)) continue;
            if (excludedByEdge.Any(label => HasEdgeLabelled(reached.Node.Id, label, ask.Visible))) continue;

            found.Add(new Finding
            {
                Node = reached.Node,
                Hops = reached.Hops,
                Because = reached.Via.Sentence(),
                Anchored = anchors
                    .Where(anchor => !anchor.Id.Equals(reached.Node.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(anchor => (anchor.Name, Step: anchor.Ring.FirstOrDefault(step =>
                        step.To.Id.Equals(reached.Node.Id, StringComparison.OrdinalIgnoreCase))))
                    .Where(tie => tie.Step.Edge is not null)
                    .Select(tie => new AnchorTie(tie.Name, tie.Step.Edge.Label, tie.Step.Edge.Note))
                    .ToList(),
                Properties = ask.WithProperties ? Properties(reached.Node.Id, ask.Visible) : Array.Empty<Edge>(),
            });
        }

        return new Answer
        {
            About = about,
            // ALWAYS. The subject's own facts are one node's worth — bounded, cheap, and the most on-point content any
            // answer can carry. Gating them behind the same flag as the findings' meant a well-connected thing lost its
            // own detail: asked about a flight with nine things around it, the reply named the airports and dropped the
            // seat, the date and the departure time. The more it knew, the less it said.
            AboutProperties = Properties(about.Id, ask.Visible),

            // The subject gets the same annotation the findings do — "how do I know Dave" is a question about Dave, not
            // about what is near him.
            AboutAnchored = anchors
                .Where(anchor => !anchor.Id.Equals(about.Id, StringComparison.OrdinalIgnoreCase))
                .Select(anchor => (anchor.Name, Step: anchor.Ring.FirstOrDefault(step =>
                    step.To.Id.Equals(about.Id, StringComparison.OrdinalIgnoreCase))))
                .Where(tie => tie.Step.Edge is not null)
                .Select(tie => new AnchorTie(tie.Name, tie.Step.Edge.Label, tie.Step.Edge.Note))
                .ToList(),

            Found = found,

            // Read off the WALK, not off what survived. Subtractives can drop the count back under the cap, and a
            // truncated answer that reports itself complete is the one failure mode here that cannot be recovered from.
            Truncated = walked.Count >= most,
        };
    }

    /// <summary>A name, an alias or an id — whichever the caller happened to have.</summary>
    private Node? Named(string nameOrId) => Resolve(nameOrId).Node ?? Get(nameOrId);

    private bool HasEdgeLabelled(string nodeId, string label, Func<Edge, bool>? visible)
    {
        var node = Get(nodeId);
        if (node is null) return false;

        lock (_lock)
            return _edges.Any(e =>
                e.State == EdgeState.Active
                && (visible is null || visible(e))
                && e.Label.Equals(label, StringComparison.OrdinalIgnoreCase)
                && (e.From.Equals(node.Id, StringComparison.OrdinalIgnoreCase)
                    || (e.To is { } to && to.Equals(node.Id, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>
    /// Everything ever said about a node, including what has been superseded or ended.
    /// </summary>
    /// <remarks>
    /// Where "why did we not book that hotel?" is answered. Kept out of ordinary walks — every hop would otherwise drag
    /// the dead along — so history is an explicit ask.
    /// </remarks>
    public IReadOnlyList<Edge> History(string nodeId, Func<Edge, bool>? visible = null)
    {
        var node = Get(nodeId);
        if (node is null) return Array.Empty<Edge>();

        lock (_lock)
            return _edges
                .Where(e => visible is null || visible(e))
                .Where(e => e.From.Equals(node.Id, StringComparison.OrdinalIgnoreCase)
                            || (e.To is { } to && to.Equals(node.Id, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(e => e.Asserted)
                .ToList();
    }

    /// <summary>
    /// The words actually used for connections between two kinds of thing.
    /// </summary>
    /// <remarks>
    /// The alternative to declaring a vocabulary. Rather than a fixed list of relationship types chosen in advance, a
    /// caller — or a tool description shown to a model — can be handed the labels genuinely present in this graph, and
    /// filter using words that exist in this data rather than words somebody guessed at. Nothing to migrate, nothing to
    /// keep in sync, and it grows as the graph does.
    /// </remarks>
    public IReadOnlyList<string> LabelsBetween(string fromKind, string toKind)
    {
        lock (_lock)
            return _edges
                .Where(e => e.State == EdgeState.Active && e.IsRelationship)
                .Where(e => Get(e.From) is { } from && from.Kind.Equals(fromKind, StringComparison.OrdinalIgnoreCase))
                .Where(e => Get(e.To) is { } to && to.Kind.Equals(toKind, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Label)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }
}
