using System.Text;
using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// Doing the same thing to many things — the shape almost every long job actually has.
/// <para>
/// A shopping run spent its entire 320-call budget driving one browser tab through a 49-item list and got one
/// item into the basket. The waste wasn't the browsing; it was that the SECOND item cost as much as the first.
/// Nothing carried over. Everything a worker learns on item one — which button adds to basket, where the search
/// box is, what a confirmed add looks like — is exactly what items two through forty-nine need, and it was being
/// rediscovered every time inside one ever-growing context.
/// </para>
/// <para>
/// So the shape is RECON, then FAN OUT. One worker explores until it knows the recipe, then hands the recipe and
/// the list here, and each item is run by its own child worker with a short context of its own. The parent stays
/// the orchestrator: it doesn't fire and forget, it gets every item back named and individually CHECKED by the
/// same verifier that gates a plan step, so "done" means a judge looked at the result and agreed — not merely
/// that a worker stopped talking.
/// </para>
/// <para>
/// This generalises well past browsing. "Read these thirty emails and tell me which need a reply" is the same
/// shape: work out the list, then run the list.
/// </para>
/// </summary>
public sealed partial class Orchestrator
{
    /// <summary>Above this the ask is almost certainly a misuse (a whole inbox, a scraped page of links) and the
    /// right move is to narrow it first. Sized so the biggest sane case — a weekly shop — fits.</summary>
    private const int MaxFanOutItems = 60;

    /// <summary>How many children run at once by default. Kept low deliberately: these are full workers with
    /// real tools, several may be driving the same browser, and the point is throughput per unit of budget, not
    /// raw parallelism.</summary>
    private const int DefaultFanOutBatch = 4;

    private const int MaxFanOutBatch = 8;

    /// <summary>One item's fate. Every item ends as exactly one of these — verified, failed, or never started —
    /// so the parent's picture has no gaps in it.</summary>
    internal sealed record ItemOutcome(int Number, string Item, bool Ok, string Detail)
    {
        public string Line => $"  {(Ok ? "✓" : "✗")} {Number}. {Item} — {Detail}";
    }

    /// <summary>
    /// The batching and stop rule, with the running of an actual batch left to the caller so it can be driven
    /// without a model behind it.
    /// <para>
    /// A failure stops the NEXT batch, not the one it happened in. The children already in flight are left to
    /// finish on purpose: killing a worker halfway through a mutation ("did that reach the basket or not?")
    /// turns a clean failure into an unknown, and an unknown is the one thing that makes working out where we
    /// got to harder. So the budget stops burning at the batch boundary, and every item still ends with a
    /// definite answer.
    /// </para>
    /// </summary>
    internal static async Task<(List<ItemOutcome> Outcomes, bool Halted, string HaltReason)> DriveBatchesAsync(
        IReadOnlyList<string> items,
        int batchSize,
        Func<IReadOnlyList<(string Item, int Number)>, int, Task<IReadOnlyList<ItemOutcome>>> runBatch,
        Func<bool>? stopped = null)
    {
        var outcomes = new List<ItemOutcome>();

        /*
         * The first item goes alone.
         *
         * A recipe written after exploring is a hypothesis, and the whole point of fanning out is that the
         * hypothesis is about to be applied thirty-four more times. Sending the first batch of four means a
         * wrong recipe costs four workers before anything is learned; sending one means it costs one, and the
         * report comes back naming exactly what went wrong while there is still a budget to fix it with.
         *
         * It costs nothing when the recipe is right — the pilot's result counts like any other, and the rest
         * follow at full width immediately after.
         */
        var plan = new List<int>();
        if (items.Count > 1) plan.Add(1);
        for (int taken = plan.Sum(); taken < items.Count; taken += batchSize)
            plan.Add(Math.Min(batchSize, items.Count - taken));
        if (plan.Count == 0) plan.Add(items.Count);

        int offset = 0;
        for (int b = 0; b < plan.Count; b++)
        {
            if (stopped?.Invoke() == true) break;

            var slice = items.Skip(offset).Take(plan[b])
                .Select((item, i) => (Item: item, Number: offset + i + 1)).ToList();
            offset += plan[b];

            var ran = await runBatch(slice, b).ConfigureAwait(false);
            outcomes.AddRange(ran.OrderBy(o => o.Number));

            if (ran.OrderBy(o => o.Number).FirstOrDefault(o => !o.Ok) is { } bad)
            {
                var reason = $"item {bad.Number} ({bad.Item}) — {bad.Detail}";
                // Failing the pilot says something different from failing later: the recipe never worked at
                // all, so there is nothing to resume and no point retrying the same words.
                if (b == 0 && plan[0] == 1 && items.Count > 1)
                    reason = $"the trial item — {reason}";
                return (outcomes, true, reason);
            }
        }

        return (outcomes, false, "");
    }

    private AgentTool FanOutToolFor(Session session, TaskInfo task) => new(
        "fan_out",
        "Run the SAME piece of work over many items at once, each in its own worker, and get every result back " +
        "checked. Use this the moment you notice you're repeating yourself: you've worked out how to do one of " +
        "something (added one item, read one email, checked one listing) and there are more to go. Explore FIRST " +
        "on a single item until you genuinely know the recipe, then call this with the rest — the recipe you " +
        "write is all each worker gets, so it must be complete enough to follow without you. Each item's result " +
        "is verified against 'produces' before it counts as done. If any item in a batch fails, the run stops " +
        "there and hands you back exactly what worked, what failed and why, and what was never attempted, so you " +
        "can fix the recipe and call it again for the rest rather than losing the whole run.\n" +
        "THE WORKERS SHARE YOUR BROWSER. A fresh context is not a fresh browser: they drive the SAME Chrome, " +
        "signed into the same accounts, with the same cookies and the SAME BASKET. Separate contexts, one " +
        "browser. So filling a single shopping cart, working through one inbox, or acting inside one logged-in " +
        "account is exactly what this is for — each worker takes its own tab, and their adds all land in the one " +
        "cart. You do NOT have to grind through a long list yourself to keep it in one session.",
        new[]
        {
            new ToolParameter("goal", "string",
                "The recipe: the complete instruction for doing ONE item, written so a worker who wasn't with " +
                "you can follow it. Include what you learned exploring — the exact page, the button, the search " +
                "box, the gotcha. Do not name a specific item here; the item is supplied separately.", required: true),
            new ToolParameter("produces", "string",
                "What ONE item's success looks like, concretely enough to be judged — 'the item is confirmed in " +
                "the basket with its price', 'a one-line verdict on whether the email needs a reply'. This is " +
                "the test each result is held to.", required: true),
            new ToolParameter("items", "array",
                "The things to run the recipe over, one per entry — the shopping list, the email subjects, the " +
                "URLs.", required: true,
                schema: """{"type":"array","items":{"type":"string"},"description":"The things to run the recipe over."}"""),
            new ToolParameter("batch_size", "integer",
                $"How many run at once. Defaults to {DefaultFanOutBatch}, max {MaxFanOutBatch}. Lower it when the " +
                "work is heavy or the items touch the same thing.", required: false),
        },
        async (args, ct) => await RunFanOutAsync(session, task, args, ct).ConfigureAwait(false))
    {
        // The list differs every time, and a second fan_out over the REMAINING items after a halt is the
        // designed recovery path — not a repeat to be refused.
        Repeatable = true,
    };

    /// <summary>Items arrive as a JSON array normally, but a model that has decided to write a list as text
    /// shouldn't lose the whole call over it — a newline- or comma-separated string is read too.</summary>
    internal static List<string> ReadItems(ToolCallArguments args)
    {
        var items = new List<string>();
        if (args.Raw.ValueKind == JsonValueKind.Object
            && args.Raw.TryGetProperty("items", out var raw))
        {
            if (raw.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in raw.EnumerateArray())
                {
                    var v = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
                    if (!string.IsNullOrWhiteSpace(v)) items.Add(v!.Trim());
                }
            }
            else if (raw.ValueKind == JsonValueKind.String)
            {
                var text = raw.GetString() ?? "";
                var parts = text.Contains('\n') ? text.Split('\n') : text.Split(',');
                foreach (var p in parts)
                {
                    // Tolerate a pasted markdown list.
                    var v = p.Trim().TrimStart('-', '*', '•', ' ').Trim();
                    if (!string.IsNullOrWhiteSpace(v)) items.Add(v);
                }
            }
        }

        // The same item twice is a duplicated effort, not two jobs.
        return items.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<ToolOutput> RunFanOutAsync(
        Session session, TaskInfo task, ToolCallArguments args, CancellationToken ct)
    {
        var goal = (args.GetStringOrNull("goal") ?? "").Trim();
        var produces = (args.GetStringOrNull("produces") ?? "").Trim();
        var items = ReadItems(args);

        if (goal.Length == 0 || produces.Length == 0)
            return ToolOutput.Error("fan_out needs both a 'goal' (the recipe for one item) and a 'produces' " +
                                    "(what one item's success looks like).");
        if (items.Count == 0)
            return ToolOutput.Error("fan_out needs a non-empty 'items' list.");
        if (items.Count == 1)
            return ToolOutput.DeadEnd("There's only one item — just do it yourself; fan_out is for repeating " +
                                      "work you already know how to do.");
        if (items.Count > MaxFanOutItems)
            return ToolOutput.DeadEnd(
                $"{items.Count} items is more than fan_out will run at once (max {MaxFanOutItems}). Narrow it " +
                "down, or run the most important ones first and come back for the rest.");
        if (_planner is null)
            return ToolOutput.DeadEnd("fan_out can't check its workers' results here, so it won't run them.");

        int batchSize = Math.Clamp(args.GetInt("batch_size", DefaultFanOutBatch), 1, MaxFanOutBatch);
        Trace($"[fan-out #{task.Id}] {items.Count} items, {batchSize} at a time: {Snip(goal, 100)}");

        var (outcomes, halted, haltReason) = await DriveBatchesAsync(
            items, batchSize,
            async (slice, b) =>
            {
                // The user watches the batches go by, and so does the parent's own thought line — this is the
                // part that makes it read as supervision rather than a long silence.
                var label = b == 0 && slice.Count == 1 && items.Count > 1
                    ? $"trying the recipe on one item first: {Head(slice[0].Item, 60)}"
                    : $"{slice.Count} at once: {Head(string.Join(", ", slice.Select(s => s.Item)), 70)}";
                task.LatestThought = label;
                session.Append("working", Json(new { id = task.Id, task = label, msgId = task.OriginMessageId, at = DateTimeOffset.UtcNow }));

                var ran = await Task.WhenAll(
                    slice.Select(s => RunFanOutItemAsync(session, task, goal, produces, s.Item, s.Number)))
                    .ConfigureAwait(false);
                return ran;
            },
            stopped: () => task.Cts.IsCancellationRequested || ct.IsCancellationRequested).ConfigureAwait(false);

        if (halted) Trace($"[fan-out #{task.Id}] HALTED: {Snip(haltReason, 140)}");

        var attempted = outcomes.Select(o => o.Item).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var untouched = items.Where(i => !attempted.Contains(i)).ToList();
        return ToolOutput.Ok(FanOutReport(goal, items.Count, outcomes, untouched, halted, haltReason));
    }

    /// <summary>Run ONE item as its own child worker and judge what came back. The child is a normal hidden child
    /// task, which is what keeps it cheap and safe: a child skips the clarify and assess gates (so four of them
    /// can't each stop to ask the user the same question), and it shares the conversation's file area, so what it
    /// writes is visible to its siblings and to the parent.</summary>
    private async Task<ItemOutcome> RunFanOutItemAsync(
        Session session, TaskInfo parent, string goal, string produces, string item, int number)
    {
        var child = new TaskInfo
        {
            Id = session.NextTaskId(),
            Description = $"{goal} — {item}",
            Persona = parent.Persona,
            ParentTaskId = parent.Id,
            IsFanOutChild = true,
            UserScope = parent.UserScope,
            Room = parent.Room,
            UserName = parent.UserName,
            // A fan-out child of an unattended run is still unattended. Proact holds no fan_out tool, so it should
            // never get here — inherited anyway, for the same reason the plan children are.
            Proact = parent.Proact,
        };
        child.WorkspaceDir = CreateWorkspace(session, child, null);
        session.Tasks[child.Id] = child;

        var brief = new StringBuilder(goal)
            .Append("\n\nYOUR ITEM: ").Append(item)
            .Append("\n\nYou are one of several workers each doing this for a different item, at the same time. " +
                    "Do YOUR item only — don't do anyone else's, and don't do the whole list.")
            .Append("\n\nWhat done looks like: ").Append(produces)
            .Append("\n\nIf you're working in a browser: open your OWN tab and work in that, because your " +
                    "siblings are using theirs. It is the SAME browser they and the person who sent you are " +
                    "using — same sign-ins, same cookies, same basket — so your tab is a separate window onto " +
                    "one shared session, not a separate account. Whatever you add lands in the same place " +
                    "theirs does. Close your tab when you're finished.")
            .Append("\n\nReport back in one or two lines: what happened to your item, and the details that " +
                    "prove it (the price, the confirmation, the answer). If you couldn't do it, say plainly " +
                    "what stopped you — a clear failure is more useful here than an optimistic summary.")
            .ToString();

        try
        {
            await DriveWorker(session, child, brief).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ItemOutcome(number, item, false, $"the worker died: {Head(ex.Message, 80)}");
        }

        if (child.Status == "cancelled")
            return new ItemOutcome(number, item, false, "cancelled");

        var result = child.Result ?? "";
        var (ok, reason) = await _planner!
            .VerifyStepAsync($"{goal} — for this item specifically: {item}", produces, result, parent.Cts.Token)
            .ConfigureAwait(false);

        return ok
            ? new ItemOutcome(number, item, true, $"\"{Head(result, 90)}\"")
            : new ItemOutcome(number, item, false,
                $"not verified: {Head(string.IsNullOrWhiteSpace(reason) ? "the result didn't meet the goal" : reason, 90)}" +
                $" (it said: \"{Head(result, 70)}\")");
    }

    /// <summary>What the parent reads. Every item accounted for, the failure named, and — the part that decides
    /// whether this is worth having — an explicit next move, because a halt is a handover, not an ending.</summary>
    internal static string FanOutReport(
        string goal, int total, List<ItemOutcome> outcomes, List<string> untouched, bool halted, string haltReason)
    {
        var done = outcomes.Where(o => o.Ok).ToList();
        var failed = outcomes.Where(o => !o.Ok).ToList();

        var sb = new StringBuilder($"fan_out — \"{Head(goal, 80)}\" over {total} items.\n\n");
        foreach (var o in outcomes.OrderBy(o => o.Number)) sb.AppendLine(o.Line);

        sb.AppendLine();
        sb.Append($"{done.Count} of {total} verified");
        if (failed.Count > 0) sb.Append($", {failed.Count} failed");
        if (untouched.Count > 0) sb.Append($", {untouched.Count} not attempted");
        sb.AppendLine(".");

        if (untouched.Count > 0)
            sb.AppendLine($"Not attempted: {string.Join(", ", untouched.Take(15))}{(untouched.Count > 15 ? $" (+{untouched.Count - 15} more)" : "")}");

        if (halted)
        {
            bool pilot = haltReason.StartsWith("the trial item", StringComparison.Ordinal);
            sb.AppendLine();
            sb.AppendLine($"STOPPED on {haltReason}");
            sb.AppendLine(
                pilot
                    // Nothing was proven, so "try the rest" is not the advice — the recipe itself is wrong.
                    ? "The recipe was tried on ONE item before committing the rest, and it didn't work, so " +
                      "nothing else was attempted and almost no budget was spent finding that out. The recipe " +
                      "is the problem, not the items: go and do this one item yourself, see where it actually " +
                      "differs from what you wrote, then call fan_out again with the corrected recipe. Sending " +
                      "the same words again will fail the same way."
                    : "Nothing further was started, so no budget went into repeating a mistake. Work out what " +
                      "went wrong — usually the recipe is missing a step you did by hand while exploring — then " +
                      "call fan_out again with the items that are left. If it's the same failure twice, stop " +
                      "and tell the user what you got and what blocked the rest; don't grind.");
        }
        else if (failed.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("All items verified. Report what was done — you don't need to re-check them.");
        }

        return sb.ToString();
    }
}
