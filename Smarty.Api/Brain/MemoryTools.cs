using Smarty.Agents;
using Smarty.Brain;

namespace Smarty.Api;

/// <summary>
/// The memory, as three tools: ask it, tell it, take something back.
/// </summary>
/// <remarks>
/// <para>
/// Every parameter is a plain sentence. That is the entire design of this surface, and it replaced seven tools that
/// asked the model to do the librarian's job: pick a subject, choose a kind, name a relation, decide whether a thing was
/// new. It got those wrong constantly, and it was never its fault — it was being asked to decide whether "the holiday"
/// already existed while being shown nothing about what existed.
/// </para>
/// <para>
/// So the resolving moved to where both halves are known: the sentence, and the graph. The model now says what happened
/// in the words it heard, and the reconciler works out what that means against everything on file.
/// </para>
/// </remarks>
public static class MemoryTools
{
    /// <summary>
    /// Ask the memory something. The only tool that goes and thinks before answering.
    /// </summary>
    public static AgentTool Search(Memory memory, Func<string?> audience) => new(
        "search_memory",
        "Look something up in what you know about this person and their life — people, projects, places, things. " +
        "Ask it as a QUESTION, in full, the way it was put to you. Not keywords — a whole question. It works out for " +
        "itself what to start from and what to bring back, including questions whose answer is a step or two away from " +
        "anything the question names.\n" +
        "Everything it returns says WHY it is there, so you can quote it with confidence. If it comes back with " +
        "nothing, you genuinely do not know — say so rather than filling the gap.",
        new[] { ToolParameter.String("question", "The question, in full.", required: true) },
        async (args, ct) =>
        {
            var question = args.GetStringOrNull("question") ?? args.GetStringOrNull("query") ?? "";
            if (question.Trim().Length == 0) return ToolOutput.Ok("Ask me a question and I'll look it up.");
            return ToolOutput.Ok(await memory.RecallAsync(question, audience(), ct).ConfigureAwait(false));
        });

    /// <summary>
    /// Tell the memory something. Returns instantly — the graph work happens behind the reply.
    /// </summary>
    /// <summary>
    /// Tell the memory something, optionally with a document that belongs to it.
    /// </summary>
    /// <remarks>
    /// One verb, not two. "Here is my flight ticket" is a thing being told AND a file being handed over, and splitting it
    /// into two calls means the file can arrive with nothing to attach it to — or the sentence can land and the document be
    /// forgotten, which is the worse half.
    /// </remarks>
    public static AgentTool Set(Memory memory, Func<string?> source, Func<string?> audience,
        Func<string, string?>? findFile = null) => new(
        "set_memory",
        "Record something you have just learned about this person or their world. Write it as a plain sentence, in " +
        "full, keeping the words it was told to you in.\n" +
        "Do NOT break it into fields, and do not worry about whether any of it is already known — nothing needs to " +
        "exist first. Say it once, in one sentence, and the people, places and things in it get found or created and " +
        "joined up afterwards. Keep the relationships the sentence gave you rather than simplifying them away; those " +
        "are the part that makes it findable later.\n" +
        "This returns immediately and costs you nothing, so record things as they come up rather than saving them " +
        "for later. Only record what was actually said — never your own guesses, and never the conversation itself.\n" +
        "If they attached a document that belongs with it — a ticket, a booking, a statement — name the file too. It " +
        "gets kept against whatever the sentence is about, so it can be handed back later and read from when a question " +
        "needs something that is only written inside it.",
        new[]
        {
            ToolParameter.String("statement", "What you learned, as one plain sentence.", required: true),
            ToolParameter.String("file", "A file attached to this conversation that belongs with it.", required: false),
        },
        args =>
        {
            var statement = args.GetStringOrNull("statement") ?? args.GetStringOrNull("value") ?? "";
            if (statement.Trim().Length == 0) return "Nothing to record — tell me what you learned.";

            var named = args.GetStringOrNull("file");
            if (named is { Length: > 0 } && findFile?.Invoke(named) is { Length: > 0 } path)
            {
                memory.Keep(statement, path, System.IO.Path.GetFileName(path), source(), audience());
                return $"Noted, and I've kept {System.IO.Path.GetFileName(path)} with it — carry on.";
            }

            if (named is { Length: > 0 })
                return $"I can't find a file called \"{named}\" in this conversation — recorded the rest.";

            memory.Note(statement, source(), audience());
            return "Noted. It'll be filed against everything already known — carry on.";
        });

    /// <summary>
    /// Something is no longer true. Ends the connection and keeps the reason.
    /// </summary>
    public static AgentTool Forget(Memory memory, Func<string?> source, Func<string?> audience) => new(
        "forget_memory",
        "Record that something you knew is no longer the case. Write it as a sentence, and SAY WHY if you were told " +
        "why.\n" +
        "The reason is the most valuable part, and the whole point of recording this rather than deleting it: a " +
        "question later about why something did not happen can only be answered if somebody wrote down why. Nothing is " +
        "erased — it is marked as ended, with the reason, and stays findable.\n" +
        "Use this only for things that have STOPPED being true. Something merely taking a new value is set_memory, " +
        "which replaces the old one on its own.",
        new[] { ToolParameter.String("statement", "What is no longer true, and why.", required: true) },
        args =>
        {
            var statement = args.GetStringOrNull("statement") ?? args.GetStringOrNull("what") ?? "";
            if (statement.Trim().Length == 0) return "Tell me what's no longer true.";

            memory.Forget(statement, source(), audience());
            return "Noted as no longer the case, with the reason kept.";
        });

    /// <summary>
    /// Settle whether two names are one thing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The graph can notice that "Matt" might be the Matthew already on file; it cannot know, and guessing either way is
    /// unrecoverable — invent a second person and every question about him finds half the answer, or merge them and
    /// facts get recorded against somebody who does not exist. So it asks, and this is the answer.
    /// </para>
    /// <para>
    /// Deliberately available in ordinary conversation rather than only on a settings page, because that is where the
    /// answer actually turns up. The open questions are named in the assistant's context, so when somebody says "yeah,
    /// Matt's my brother" the thing is already known and can simply be recorded.
    /// </para>
    /// </remarks>
    public static AgentTool Same(Graph graph) => new(
        "same_thing",
        "Settle whether two names in the memory are the same thing or two different ones. Any identity questions still " +
        "open are listed in your context — this answers one.\n" +
        "Yes merges them, and the name that was in doubt becomes another name for the survivor, so it resolves for " +
        "ever afterwards. No records that they are different, so it is never asked again.\n" +
        "Only answer when you actually know — from what the user just said, or from what the conversation makes plain. " +
        "If it is unclear, ASK them in one short line instead; a wrong yes attaches somebody's facts to a stranger.",
        new[]
        {
            ToolParameter.String("name", "The name in doubt, as listed in your context.", required: true),
            ToolParameter.String("other", "The thing it might be.", required: true),
            ToolParameter.Boolean("same", "True if they are one thing; false if they are genuinely different.",
                required: true),
        },
        args =>
        {
            var name = args.GetStringOrNull("name")?.Trim() ?? "";
            var other = args.GetStringOrNull("other")?.Trim() ?? "";

            var one = graph.Resolve(name).Node ?? graph.Get(name);
            var two = graph.Resolve(other).Node ?? graph.Get(other);
            if (one is null) return $"There's nothing in the memory called \"{name}\".";
            if (two is null) return $"There's nothing in the memory called \"{other}\".";
            if (one.Id == two.Id) return $"{one.Name} is already the one thing — nothing to settle.";

            var same = args.GetBoolOrNull("same") ?? true;
            if (!same)
                return graph.NotTheSame(one.Id, two.Id)
                    ? $"Kept apart: {one.Name} and {two.Name} are two different things, and I won't ask again."
                    : "Couldn't record that.";

            // Which one survives is the graph's decision, not the caller's: the older node is the one other things
            // already point at.
            var winner = one.Created <= two.Created ? one : two;
            var loser = winner.Id == one.Id ? two : one;
            return graph.TheSame(one.Id, two.Id)
                ? $"Merged: everything about {loser.Name} now hangs off {winner.Name}, and \"{loser.Name}\" finds " +
                  $"{winner.Name} from now on."
                : "Couldn't merge those.";
        });

    /// <summary>
    /// The identity questions still open, for the assistant's context.
    /// </summary>
    /// <remarks>
    /// Named rather than counted, because the answer usually arrives in passing: somebody says "Matt's coming too" in a
    /// conversation that has nothing to do with the memory, and that sentence settles it — but only if the question was
    /// in front of the model at the time.
    /// </remarks>
    public static string Asks(Graph graph)
    {
        var open = graph.Maybes();
        if (open.Count == 0) return "";

        var sb = new System.Text.StringBuilder("\n\nIdentity questions still open in the memory:\n");
        foreach (var (node, maybe) in open.Take(5))
            sb.Append($"- is \"{node.Name}\" the same {node.Kind} as \"{maybe.Name}\", or someone else?\n");
        sb.Append("Answer one with same_thing the moment the conversation makes it clear — and if it comes up " +
                  "naturally, ask. Until then facts about them are being kept apart.\n");
        return sb.ToString();
    }

    /// <summary>
    /// The three, bound to one conversation.    /// <summary>
    /// The three, bound to one conversation.
    /// </summary>
    /// <remarks>
    /// The audience is a callback rather than a value, so it is re-read on every call: somebody joining the channel
    /// narrows what the very next lookup may recall, with nothing to invalidate.
    /// </remarks>
    public static IReadOnlyList<AgentTool> All(Memory memory, Func<string?> source, Func<string?> audience,
        Func<string, string?>? findFile = null) =>
        new[] { Search(memory, audience), Set(memory, source, audience, findFile), Forget(memory, source, audience) };

    /// <summary>
    /// All four, when the caller can reach the graph as well as the memory.
    /// </summary>
    /// <remarks>
    /// Settling an identity question is a graph operation rather than a statement about the world, which is why it sits
    /// beside the other three rather than inside set_memory: "Matt is Matthew" is not a fact to record, it is two nodes
    /// becoming one.
    /// </remarks>
    public static IReadOnlyList<AgentTool> All(Memory memory, Graph graph, Func<string?> source,
        Func<string?> audience, Func<string, string?>? findFile = null) =>
        All(memory, source, audience, findFile).Append(Same(graph)).ToList();
}
