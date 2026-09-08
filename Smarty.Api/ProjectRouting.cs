using System.Collections.Concurrent;
using Smarty.Brain;

namespace Smarty.Api;

/// <summary>
/// One thing known about a project, ready to be read out.
/// </summary>
/// <remarks>
/// A flat shape on purpose. A property stated about the project and a thing the project is joined to read the same way
/// in a status line — "budget: ~£1,200pp", "chosen hotel: the Llaut" — and every reader of these wants the sentence, not
/// the distinction.
/// </remarks>
public readonly record struct ProjectFact(string Relation, string Value, string? Context);

/// <summary>
/// Which project a passing remark belongs to.
/// </summary>
/// <remarks>
/// <para>
/// Not memory, which is why it survived the memory being replaced: it is a classifier. "What time are the flights next
/// week" has to land on the Lisbon holiday, and neither of those words is in its name — so the match is made against
/// everything recorded about each project as well as its title, by meaning rather than by shared words.
/// </para>
/// <para>
/// It reads the graph but never writes to it, and it deliberately reports scores rather than picking a winner. Deciding
/// how confident is confident enough belongs with the caller, who is the one who has to ask the user when it isn't.
/// </para>
/// </remarks>
public sealed class ProjectRouting
{
    /// <summary>How well a statement resolved to one project, and the recorded line that matched it.</summary>
    public readonly record struct ProjectMatch(string Slug, string Title, double Score, string Matched);

    private readonly Graph _graph;
    private readonly Func<string, CancellationToken, Task<float[]?>>? _embed;

    /// <summary>
    /// Vectors already computed, by the exact text.
    /// </summary>
    /// <remarks>
    /// In memory only, and keyed on the text rather than on a fact's identity — so a fact reworded gets re-embedded and
    /// a fact merely re-asserted does not. Nothing is persisted: a few dozen short strings cost less to recompute after a
    /// restart than they cost to keep correct on disk.
    /// </remarks>
    private readonly ConcurrentDictionary<string, float[]> _vectors = new();

    public ProjectRouting(Graph graph, Func<string, CancellationToken, Task<float[]?>>? embed)
    {
        _graph = graph;
        _embed = embed;
    }

    public async Task<IReadOnlyList<ProjectMatch>> RankAsync(
        string statement,
        IReadOnlyList<(string Slug, string Title, string Description)> projects,
        CancellationToken ct = default)
    {
        var results = new List<ProjectMatch>();
        if (_embed is null || projects.Count == 0 || string.IsNullOrWhiteSpace(statement)) return results;

        var query = await Vector("search_query: " + statement, ct).ConfigureAwait(false);
        if (query is null) return results;

        foreach (var (slug, title, description) in projects)
        {
            double best = 0;
            var matched = "";

            foreach (var line in Lines(title, description))
            {
                var vector = await Vector("search_document: " + line.Text, ct).ConfigureAwait(false);
                if (vector is null) continue;

                var score = Cosine(query, vector);
                if (score > best) (best, matched) = (score, line.Shown);
            }

            results.Add(new ProjectMatch(slug, title, best, matched));
        }

        return results.OrderByDescending(r => r.Score).ToList();
    }

    /// <summary>Everything worth matching a remark against: the project's own words, and what is recorded about it.</summary>
    private IEnumerable<(string Text, string Shown)> Lines(string title, string description)
    {
        var head = $"{title} {description}".Trim();
        if (head.Length > 0) yield return (head, title);

        if (_graph.Resolve(title).Node is not { } node) yield break;

        foreach (var property in _graph.Properties(node.Id))
            yield return ($"{property.Label} {property.Value} {property.Note}".Trim(),
                $"{property.Label}: {property.Value}");

        // The people and places on it count too — "the flights" resolving through a destination is the same trick as
        // resolving through a recorded date.
        foreach (var step in _graph.Around(node.Id))
            yield return ($"{step.Edge.Label} {step.To.Name}".Trim(), $"{step.Edge.Label}: {step.To.Name}");
    }

    private async Task<float[]?> Vector(string text, CancellationToken ct)
    {
        if (_vectors.TryGetValue(text, out var cached)) return cached;

        var vector = await _embed!(text, ct).ConfigureAwait(false);
        if (vector is not null) _vectors[text] = vector;
        return vector;
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;

        double dot = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            aa += a[i] * a[i];
            bb += b[i] * b[i];
        }

        return aa <= 0 || bb <= 0 ? 0 : dot / (Math.Sqrt(aa) * Math.Sqrt(bb));
    }
}
