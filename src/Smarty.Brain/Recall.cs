using System.Text;
using System.Text.Json.Nodes;

namespace Smarty.Brain;

/// <summary>
/// Turning a question into a walk, and the walk back into something readable.
/// </summary>
/// <remarks>
/// The question is never matched against the graph's words, because the graph's words are not the asker's words: "what
/// friends are coming" over a graph that says "wife", "guest" and "old friend from uni" matches nothing at all. So the
/// question becomes a SHAPE — start here, walk out, bring back this kind of thing — and the words come back with the
/// answer for the reader to judge.
/// </remarks>
public static class Recall
{
    public static string Brief(string question, string known, string? self, string kinds) =>
        "You look things up in somebody's memory, which is a graph of things joined to other things. Turn their " +
        "question into a lookup.\n\n" +
        "Everything on file — the id, the name, the kind, and any other names it answers to:\n" +
        known + "\n" +
        (self is { Length: > 0 } ? $"They are {self} — \"me\", \"I\" and \"my\" mean that node.\n\n" : "") +
        "Their question:\n" +
        $"  \"{question}\"\n\n" +
        "A lookup has two parts, and getting the first one right is most of the job:\n" +
        "- ABOUT: the one thing to start from. Pick it from the list above and use its id. It is whatever the question " +
        "  hangs off, which is usually the thing the question NAMES rather than the thing it asks for.\n" +
        "- WITH: a SECOND thing to start from, when the question is about two of them at once — what they both do, " +
        "  where they both go, who they both know. Any question saying \"together\", \"both\" or \"we\", or naming " +
        "  two people, needs this. Only what they share comes back; leave it out and you answer a different question " +
        "  and the reply reads as nothing being recorded.\n" +
        "- WANTING: the kind of thing to bring back. These are the kinds this memory actually contains, with how many " +
        "  things are filed under each:\n" +
        kinds + "\n" +
        "  Use one of those EXACTLY as written, or leave it out to get everything connected. A kind not on that list " +
        "  matches nothing at all, so a guessed one returns an empty answer that looks like an absence of facts rather " +
        "  than a bad filter. When unsure, leave it out: too much coming back is recoverable, nothing is not.\n\n" +
        "Never filter on what a connection is CALLED. The same idea gets written down in different words every time, so " +
        "asking for one wording finds some of the answer and silently loses the rest. Ask for the KIND of thing you " +
        "want and read the words that come back.\n\n" +
        "HOPS: 1 for what is directly joined to it, 2 to also reach what THOSE things are joined to. 2 is usually " +
        "right, because what a question is really after often sits one step beyond the thing it names.\n" +
        "ANCHORS: things to describe each result's relationship to. Anchoring on the person whose memory this is brings " +
        "every result back labelled with how they relate to them, in their own recorded words — which is how a question " +
        "using a loose or subjective term gets answered without anybody having to define that term.\n" +
        "EXCLUDE: things to leave out. Only when the question explicitly asks who or what ELSE — otherwise leave it " +
        "empty, because excluding the asker from a question about their own connection removes the answer.\n\n" +
        "Two mistakes to avoid, both of which return a confident nothing:\n" +
        "- Do not start at the ANSWER. If what you picked for ABOUT is the same kind of thing as WANTING, you have " +
        "  almost certainly started at what you were looking for. Start at what the question names instead.\n" +
        "- If the question is about how they relate to ONE named thing, start at that thing, anchor on them, exclude " +
        "  nothing, and leave WANTING out. The relationship comes back on its own.\n\n" +
        "If nothing above is what they are asking about, leave ABOUT empty. Saying so is right; picking the nearest " +
        "thing produces a confident answer about the wrong subject.";

    public static JsonNode Schema { get; } = new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["about"] = new JsonObject { ["type"] = "string" },
            ["wanting"] = new JsonObject { ["type"] = "string" },
            ["with"] = new JsonObject { ["type"] = "string" },
            ["hops"] = new JsonObject { ["type"] = "integer" },
            ["anchors"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["exclude"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        },
        ["required"] = new JsonArray("about"),
    };

    /// <summary>
    /// Up to this many results, the detail comes too.
    /// </summary>
    /// <remarks>
    /// It used to be a flag on the plan, and the model got it wrong in the obvious direction: asked what something was
    /// costing, it returned the thing and left the cost behind, because the cost is a property and nobody had ticked the
    /// box. It is not a judgement worth asking for — the code can see how big the answer is, and detail on a handful of
    /// results is free. So the field is gone, and one fewer thing can be got wrong.
    /// </remarks>
    public const int DetailUpTo = 8;

    /// <summary>
    /// The answer, written so it can be checked.
    /// </summary>
    /// <remarks>
    /// Every line carries why it is there. Handing back a bare list of names asks whoever reads it to trust the walk,
    /// and the one thing a graph has over a pile of facts is that its reasoning is legible — "the trip's guest is Dave"
    /// is checkable, "Dave" is not.
    /// </remarks>
    public static string Say(string question, string about, Answer answer, string? selfId = null,
        Func<string, IReadOnlyList<(Context Held, string? Text)>>? context = null, DateTimeOffset? now = null)
    {
        if (answer.Ambiguous.Count > 0)
            return $"\"{about}\" could mean {answer.Ambiguous.Count} different things " +
                   $"({string.Join(", ", answer.Ambiguous.Select(n => $"{n.Name} [{n.Id}]"))}). Ask which.";

        if (!answer.Resolved) return $"I have nothing on file about \"{about}\".";

        var subject = answer.About!;
        var sb = new StringBuilder();
        sb.Append(subject.Name).Append(" (").Append(subject.Kind).Append(Same(subject, selfId) ? ", you" : "").Append(')');

        // A few, not all. The owner's node answers to nine things ("me", "i", "the-user", an email) and printing the lot
        // buried the actual answer under plumbing.
        if (subject.Aliases.Count > 0)
            sb.Append(" — also called ").Append(string.Join(", ", subject.Aliases.Take(3)));

        foreach (var tie in answer.AboutAnchored)
        {
            sb.Append(" — ").Append(tie.Anchor).Append(": ").Append(tie.Relation);
            if (tie.Note is { Length: > 0 } note) sb.Append(" (").Append(note).Append(')');
        }

        sb.AppendLine(":");

        foreach (var property in answer.AboutProperties)
            sb.Append("  ").Append(property.Label).Append(": ").AppendLine(Detail(property));

        Contents(sb, "  ", context?.Invoke(subject.Id));

        if (answer.Found.Count == 0)
        {
            sb.AppendLine(answer.AboutProperties.Count > 0
                ? "  (nothing else is connected to it)"
                : "  I know of it, but nothing is connected to it and nothing is recorded about it.");
            return sb.ToString().TrimEnd();
        }

        foreach (var found in answer.Found)
        {
            sb.Append("  ").Append(found.Node.Name).Append(" (").Append(found.Node.Kind)
                .Append(Same(found.Node, selfId) ? ", you" : "").Append(')');
            sb.Append(" — ").Append(found.Because);

            // "anchor: relation" rather than "relation of anchor". The labels are free prose, so no phrasing reads well
            // for all of them — "wife of me" is fine and "going on of me" is not — and a reader gets more from the two
            // facts side by side than from a sentence that fights the words.
            foreach (var tie in found.Anchored)
            {
                sb.Append(" — ").Append(tie.Anchor).Append(": ").Append(tie.Relation);
                if (tie.Note is { Length: > 0 } note) sb.Append(" (").Append(note).Append(')');
            }

            sb.AppendLine();

            foreach (var property in found.Properties)
                sb.Append("    ").Append(property.Label).Append(": ").AppendLine(Detail(property));

            Contents(sb, "    ", context?.Invoke(found.Node.Id));
        }

        if (answer.Truncated)
            sb.AppendLine("  (there may be more further out — this is as far as it looked)");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Whether this node is the person being spoken to.
    /// </summary>
    /// <remarks>
    /// Marked in the answer because the owner appears in it by name like everything else, and a reader with no way to tell
    /// reports it in the third person — answering "Tom's brother is Alex" to somebody called Alex. Two words fix it.
    /// </remarks>
    private static bool Same(Node node, string? selfId) =>
        selfId is { Length: > 0 } && node.Id.Equals(selfId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What a node holds, written so it can be both answered from and handed over.
    /// </summary>
    /// <remarks>
    /// Both, because the two questions are different and equally ordinary: where is my ticket, and what time does the thing
    /// on it start. The first needs the file named and reachable; the second needs its words. So the name and id come with
    /// the text, and a reader can quote the answer or pass on the document — or say plainly that a file exists but nothing
    /// could be read out of it, which is the honest reply for a photograph of a ticket.
    /// </remarks>
    private static void Contents(StringBuilder sb, string indent, IReadOnlyList<(Context Held, string? Text)>? held)
    {
        if (held is null) return;

        foreach (var (item, text) in held)
        {
            sb.Append(indent).Append(item.Sort == Sorts.File ? "file: " : "note: ").Append(item.Name);
            if (item.Sort == Sorts.File) sb.Append(" [").Append(item.Id).Append(']');
            sb.AppendLine();

            if (text is { Length: > 0 })
            {
                sb.Append(indent).AppendLine("  it says:");
                foreach (var line in text.Split('\n'))
                    if (line.Trim().Length > 0) sb.Append(indent).Append("    ").AppendLine(line.Trim());
            }
            else if (item.Sort == Sorts.File)
            {
                sb.Append(indent).AppendLine("  (nothing readable in it — hand the file over rather than quoting it)");
            }
        }
    }

    private static string Detail(Edge edge)
    {
        var said = edge.Note is { Length: > 0 } note ? $"{edge.Value} ({note})" : edge.Value;

        return edge.When is not { } at
            ? said
            : at.TimeOfDay == TimeSpan.Zero
                ? $"{said} [{at:ddd d MMM yyyy}]"
                : $"{said} [{at:ddd d MMM yyyy HH:mm}]";
    }
}
