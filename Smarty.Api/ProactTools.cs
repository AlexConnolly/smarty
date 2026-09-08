using System.Text;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// The only three things Proact can do with what it finds.
///
/// <para>
/// Three tools rather than one, because the difference between them is the difference between safe and unsafe. A
/// notice costs the user nothing. Prepared work costs them a glance. A proposal costs them a decision, and is the
/// only one of the three that can lead to something irreversible happening — so it is the only one with a gate on it.
/// </para>
/// <para>
/// What is NOT here is the whole point: there is no send, no book, no buy, no cancel. Not because the brief asks it
/// not to, but because the tool does not exist. See <see cref="Proact"/>.
/// </para>
/// </summary>
public static class ProactTools
{
    /// <summary>Anything shorter than this is a placeholder rather than a sentence.</summary>
    private const int Least = 12;

    /// <summary>
    /// The most a TITLE may be.
    /// </summary>
    /// <remarks>
    /// The timeline shows it on one line and truncates, so a title longer than this arrives as an ellipsis with its
    /// useful half hidden. Refused rather than trimmed: cutting it silently would produce exactly the sentence
    /// fragment the cap exists to prevent, and the detail has somewhere proper to go.
    /// </remarks>
    private const int MostTitle = 110;

    /// <summary>Enough to be useful, few enough to read. A wall of sources is as unhelpful as none.</summary>
    private const int MostLinks = 4;

    private const int MostImages = 3;

    private static readonly ToolParameter Links = ToolParameter.FromSchema("links",
        "{\"type\":\"array\",\"description\":\"Up to 4 things worth CLICKING: the operator's own page, the article " +
        "that dated it, the menu, the listing. What you actually read — the next thing they will do is go and look, " +
        "and a finding they have to search for again has made work rather than saved it. Never a search results " +
        "page, never a link you did not open.\",\"items\":{\"type\":\"object\",\"properties\":{" +
        "\"label\":{\"type\":\"string\",\"description\":\"What it is, in a few words. Not 'click here'.\"}," +
        "\"url\":{\"type\":\"string\"}},\"required\":[\"label\",\"url\"]}}");

    private static readonly ToolParameter Pictures = ToolParameter.FromSchema("images",
        "{\"type\":\"array\",\"description\":\"Up to 3 pictures, ONLY where a picture is genuinely the answer — what " +
        "a place looks like, what a thing is. Must be image urls you actually saw on a page you opened; each one is " +
        "fetched before it is kept and a url that does not load is dropped, so a guess is simply wasted.\"," +
        "\"items\":{\"type\":\"object\",\"properties\":{\"url\":{\"type\":\"string\"}," +
        "\"caption\":{\"type\":\"string\"}},\"required\":[\"url\"]}}");

    private static List<ProactLink> ReadLinks(ToolCallArguments args)
    {
        var found = new List<ProactLink>();
        if (!args.Raw.TryGetProperty("links", out var rows)
            || rows.ValueKind != System.Text.Json.JsonValueKind.Array) return found;

        foreach (var row in rows.EnumerateArray())
        {
            var url = Text(row, "url");
            var label = Text(row, "label");
            // http(s) only. A worker with no shell should not be able to put a file:// or a javascript: url in front
            // of somebody, and the check costs nothing.
            if (url is not { Length: > 0 } || !Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                || parsed.Scheme is not ("http" or "https")) continue;

            found.Add(new ProactLink { Url = url, Label = label is { Length: > 0 } ? label : parsed.Host });
            if (found.Count >= MostLinks) break;
        }
        return found;
    }

    /// <summary>
    /// The pictures, keeping only the ones that actually load.
    /// </summary>
    /// <remarks>
    /// The same bargain as everything else here — feed_publish reads the feed, widget_publish runs the loader, and a
    /// proposal has to say what it established. An image is the sharpest version of it because it renders inline: a
    /// url that 404s is not a missing feature, it is a broken-image icon on the page, and this system has already
    /// lost a day to one of those sitting there while every check reported the panel healthy.
    /// </remarks>
    private static async Task<List<ProactImage>> ReadImagesAsync(ToolCallArguments args,
        Func<string, CancellationToken, Task<bool>>? loads, CancellationToken ct)
    {
        var found = new List<ProactImage>();
        if (!args.Raw.TryGetProperty("images", out var rows)
            || rows.ValueKind != System.Text.Json.JsonValueKind.Array) return found;

        foreach (var row in rows.EnumerateArray())
        {
            var url = Text(row, "url");
            if (url is not { Length: > 0 } || !Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                || parsed.Scheme is not ("http" or "https")) continue;

            // No checker wired means no picture, not an unchecked picture. Failing closed here costs a timeline
            // without images; failing open costs one with broken ones on it.
            if (loads is null || !await loads(url, ct).ConfigureAwait(false)) continue;

            found.Add(new ProactImage { Url = url, Caption = Text(row, "caption") });
            if (found.Count >= MostImages) break;
        }
        return found;
    }

    /// <summary>
    /// Does this text offer to do something?
    /// </summary>
    /// <remarks>
    /// The tell that a proposal has been written as prose. Deliberately a plain phrase match rather than anything
    /// clever: these are the exact forms it uses, a false positive costs one nudge it can ignore, and a false
    /// negative costs an offer the user cannot click.
    /// </remarks>
    private static bool Offers(string? text)
    {
        if (text is not { Length: > 0 }) return false;
        var t = text.ToLowerInvariant();
        return t.Contains("if you'd like") || t.Contains("if you would like")
            || t.Contains("shall i") || t.Contains("i can start") || t.Contains("i could start")
            || t.Contains("want me to") || t.Contains("would you like me")
            || t.Contains("i can run") || t.Contains("i could run") || t.Contains("just say the word");
    }

    private static string Subject(ToolCallArguments args) =>
        args.GetStringOrNull("subject")?.Trim() ?? "";

    /// <summary>What got kept, said back so the worker knows a bad image was dropped rather than silently ignored.</summary>
    private static string Kept(ProactAction a)
    {
        var parts = new List<string>();
        if (a.Links.Count > 0) parts.Add($"{a.Links.Count} link(s)");
        if (a.Images.Count > 0) parts.Add($"{a.Images.Count} picture(s)");
        return parts.Count == 0 ? "" : $", with {string.Join(" and ", parts)}";
    }

    private static string? Text(System.Text.Json.JsonElement row, string name) =>
        row.ValueKind == System.Text.Json.JsonValueKind.Object
        && row.TryGetProperty(name, out var v)
        && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString()?.Trim()
            : null;

    public static AgentTool NoticedTool(ProactStore store, string mode,
        Func<string, CancellationToken, Task<bool>>? imageLoads = null) => new(
        "proact_noticed",
        "Record something about their situation that they would want to know, and that they do not know. " +
        "A collision in their week, a deadline that is closer than it looks, a consequence of two things they have " +
        "arranged separately.\n" +
        "This is the most valuable thing you can produce and it has no artefact — you are not doing a job, you are " +
        "telling them a fact about their own life that they would thank you for.\n" +
        "Only for something you have actually established from what you read. Not a guess, not a generality, and " +
        "never advice they did not ask for dressed up as an observation.",
        new[]
        {
            ToolParameter.String("what",
                "The TITLE: one short line, as you would say it to them. Concrete — names, days, numbers — and short enough to read at a glance. The detail goes in `body`, not here.", required: true),
            ToolParameter.String("why",
                "What in their own situation makes this worth saying — the thing you read that prompted it. " +
                "Written TO THEM, in the second person — \"you keep a list of places to eat around here\". This is shown on their own page, so never \"they\" or \"their\": a note about somebody that the somebody can read is a note that sounds like being discussed behind their back.", required: true),
            ToolParameter.String("body",
                "The whole of it, as markdown — as long as it needs to be. Put the detail here: what you found, " +
                "where it is, what is worth knowing, what you could not confirm. Links and pictures render inline, " +
                "so ![a caption](https://…) and [a label](https://…) both work. Leave it out only when the title " +
                "genuinely says everything.", required: false),
            Links,
            Pictures,
            ToolParameter.String("subject",
                // Examples restored, and chosen NOT to resemble anything this person actually keeps.
                //
                // Removing them entirely was an overcorrection. An example teaches the SHAPE of a good answer and a
                // model generalises from it — shown "the Lisbon trip" with no Lisbon trip in sight, it reaches for
                // the trip they do have. What went wrong before was an example that matched their real data: it
                // stopped teaching form and became a pointer at a specific thing, which is the one way an example
                // does harm.
                "What this is ABOUT, in two or three words — \"the Lisbon trip\", \"the boiler\", \"the car " +
                "insurance\". Used to keep you from returning to the same corner of their life, so be coarse: the " +
                "same subject must get the same words every time, not a fresh phrasing.", required: true),
        },
        async (args, ct) =>
        {
            var what = args.GetStringOrNull("what")?.Trim() ?? "";
            var why = args.GetStringOrNull("why")?.Trim() ?? "";
            if (Vague(what, why) is { } complaint) return ToolOutput.Error(complaint);

            var action = store.Add(new ProactAction
            {
                Kind = ProactKinds.Noticed, Mode = mode, What = what, Why = why, Subject = Subject(args),
                Body = args.GetStringOrNull("body")?.Trim() ?? "",
                Links = ReadLinks(args),
                Images = await ReadImagesAsync(args, imageLoads, ct).ConfigureAwait(false),
            });
            // The reply used to end "do not also propose something about it", which was written to stop double
            // posting and instead suppressed the one thing that mattered: a note ending "if you'd like, I can start
            // a full clean" was recorded, told it was finished, and the offer stayed as prose nobody could click.
            // Now the offer is looked for, and where it is found the next step is named.
            return ToolOutput.Ok(
                $"Recorded. It will appear on their timeline as \"{action.What}\"" + Kept(action) + "." +
                (Offers(action.What) || Offers(action.Body)
                    ? "\n\nBUT YOU HAVE OFFERED TO DO SOMETHING IN THE TEXT — \"if you'd like\", \"I can\", " +
                      "\"shall I\". That is not something they can answer here: it is a sentence, and answering it " +
                      "means typing a reply. Put it through proact_propose NOW, as its own thing, so it becomes a " +
                      "tick and a cross. The note stays as it is."
                    : " You are done."));
        });

    public static AgentTool PreparedTool(ProactStore store, string mode,
        Func<string, CancellationToken, Task<bool>>? imageLoads = null) => new(
        "proact_prepared",
        "Record work you have DONE and left ready for them: a document you wrote, a shortlist you gathered, a draft " +
        "message sitting unsent, numbers you pulled together, a panel you built.\n" +
        "The work has to exist before you call this. A note saying you could write something is not prepared work.",
        new[]
        {
            ToolParameter.String("what", "The TITLE: one short line, in their terms. Detail goes in `body`.", required: true),
            ToolParameter.String("why",
                "What about their situation made it worth doing. " + "Written TO THEM, in the second person — \"you keep a list of places to eat around here\". This is shown on their own page, so never \"they\" or \"their\": a note about somebody that the somebody can read is a note that sounds like being discussed behind their back.", required: true),
            ToolParameter.String("body",
                "The whole of it, as markdown — as long as it needs to be. Put the detail here: what you found, " +
                "where it is, what is worth knowing, what you could not confirm. Links and pictures render inline, " +
                "so ![a caption](https://…) and [a label](https://…) both work. Leave it out only when the title " +
                "genuinely says everything.", required: false),
            ToolParameter.String("produced",
                "The artefact: the file name you wrote, the panel you published, the draft you left. " +
                "Something they can actually go and look at.", required: true),
            Links,
            Pictures,
            ToolParameter.String("subject",
                // Examples restored, and chosen NOT to resemble anything this person actually keeps.
                //
                // Removing them entirely was an overcorrection. An example teaches the SHAPE of a good answer and a
                // model generalises from it — shown "the Lisbon trip" with no Lisbon trip in sight, it reaches for
                // the trip they do have. What went wrong before was an example that matched their real data: it
                // stopped teaching form and became a pointer at a specific thing, which is the one way an example
                // does harm.
                "What this is ABOUT, in two or three words — \"the Lisbon trip\", \"the boiler\", \"the car " +
                "insurance\". Used to keep you from returning to the same corner of their life, so be coarse: the " +
                "same subject must get the same words every time, not a fresh phrasing.", required: true),
        },
        async (args, ct) =>
        {
            var what = args.GetStringOrNull("what")?.Trim() ?? "";
            var why = args.GetStringOrNull("why")?.Trim() ?? "";
            var produced = args.GetStringOrNull("produced")?.Trim() ?? "";
            if (Vague(what, why) is { } complaint) return ToolOutput.Error(complaint);
            if (produced.Length == 0)
                return ToolOutput.Error(
                    "Prepared work needs something to show for it. Name the file, the panel or the draft — if there " +
                    "isn't one, this is a notice rather than prepared work.");


            var action = store.Add(new ProactAction
            {
                Kind = ProactKinds.Prepared, Mode = mode, What = what, Why = why, Produced = produced,
                Body = args.GetStringOrNull("body")?.Trim() ?? "",
                Subject = Subject(args),
                Links = ReadLinks(args),
                Images = await ReadImagesAsync(args, imageLoads, ct).ConfigureAwait(false),
            });
            return ToolOutput.Ok($"Recorded, with \"{action.Produced}\" attached to it{Kept(action)}. You are done.");
        });

    /// <summary>
    /// The tick-or-cross tool, and the only one that can lead anywhere irreversible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything a proposal must carry is a required parameter, because every one of them is load-bearing. The plan
    /// is what makes ticking safe. The catch is what stops a one-click authorisation of something that cannot be
    /// taken back. The evidence is what stops a proposal that cannot actually run.
    /// </para>
    /// <para>
    /// That last one is the same bargain the panels and the feeds both make, in a third place: publish reads the feed
    /// before it will save it, because a guess on a timer is a watcher that waits politely for ever. A proposal is
    /// the same shape of promise — except the user has now spent a click and some trust on it, so an unrunnable one
    /// is worse than a missing one.
    /// </para>
    /// </remarks>
    public static AgentTool ProposeTool(ProactStore store, string mode,
        Func<string, CancellationToken, Task<bool>>? imageLoads = null) => new(
        "proact_propose",
        "Offer them something you will NOT do on your own — anything that spends money, books or reserves, sends " +
        "anything to anybody, or cannot be undone. They answer with a tick or a cross and nothing else, so " +
        "everything they need in order to decide has to be in what you write here.\n" +
        "ESTABLISH IT FIRST. If you propose a table at 8pm on Friday, you have already checked there is one. If you " +
        "name a price, you read the price. If you cannot establish it, you have a notice, not a proposal.\n" +
        "There is no edit button. A nearly-right plan gets a cross, so get it right or do not offer it.",
        new[]
        {
            ToolParameter.String("what",
                "The outcome, one line, in their terms: \"Book Trullo, 8pm Friday, table for two\".", required: true),
            ToolParameter.String("why",
                "What about their situation prompted it — this is what stops it reading as a cold suggestion. " +
                "Written TO THEM, in the second person — \"you keep a list of places to eat around here\". This is shown on their own page, so never \"they\" or \"their\": a note about somebody that the somebody can read is a note that sounds like being discussed behind their back.", required: true),
            ToolParameter.String("body",
                "The whole of it, as markdown — as long as it needs to be. Put the detail here: what you found, " +
                "where it is, what is worth knowing, what you could not confirm. Links and pictures render inline, " +
                "so ![a caption](https://…) and [a label](https://…) both work. Leave it out only when the title " +
                "genuinely says everything.", required: false),
            ToolParameter.String("plan",
                "The steps that will actually run when they tick it — the real sequence, short enough to read in " +
                "five seconds. NOT an explanation of how you work: they are authorising this, so it has to say " +
                "what will happen.", required: true),
            ToolParameter.String("checked",
                "What you established that makes this runnable, and how. \"Their booking page shows 8pm free for 2 " +
                "on Friday\". This is not shown to them — it is the evidence that this is a real offer.",
                required: true),
            ToolParameter.String("catch",
                "What cannot be undone, and by when they have to decide. If nothing is irreversible, say so plainly.",
                required: true),
            new ToolParameter("hours",
                "integer",
                "How many hours until this stops being worth answering. A table for tonight is not worth answering " +
                "tomorrow.", required: true),
            Links,
            Pictures,
            ToolParameter.String("subject",
                // Examples restored, and chosen NOT to resemble anything this person actually keeps.
                //
                // Removing them entirely was an overcorrection. An example teaches the SHAPE of a good answer and a
                // model generalises from it — shown "the Lisbon trip" with no Lisbon trip in sight, it reaches for
                // the trip they do have. What went wrong before was an example that matched their real data: it
                // stopped teaching form and became a pointer at a specific thing, which is the one way an example
                // does harm.
                "What this is ABOUT, in two or three words — \"the Lisbon trip\", \"the boiler\", \"the car " +
                "insurance\". Used to keep you from returning to the same corner of their life, so be coarse: the " +
                "same subject must get the same words every time, not a fresh phrasing.", required: true),
        },
        async (args, ct) =>
        {
            // Refused before anything else, because the alternative is a worker doing the research and then being
            // told it was wasted. A pile of unanswered decisions makes the whole surface feel like a chore.
            if (!store.RoomToPropose())
                return ToolOutput.DeadEnd(
                    $"They already have {ProactStore.OutstandingProposals} proposal(s) waiting for an answer, so " +
                    "this is not the moment for another. Record a notice if the fact behind it is worth knowing, " +
                    "otherwise do nothing.");

            var what = args.GetStringOrNull("what")?.Trim() ?? "";
            var why = args.GetStringOrNull("why")?.Trim() ?? "";
            var plan = args.GetStringOrNull("plan")?.Trim() ?? "";
            var proved = args.GetStringOrNull("checked")?.Trim() ?? "";
            var risk = args.GetStringOrNull("catch")?.Trim() ?? "";
            var hours = args.GetIntOrNull("hours") ?? 0;

            if (Vague(what, why) is { } complaint) return ToolOutput.Error(complaint);

            if (plan.Length < Least)
                return ToolOutput.Error(
                    "The plan has to say what will actually happen when they tick it. One click authorises this, so " +
                    "\"do the thing\" is not enough — write the real steps.");

            if (proved.Length < Least)
                return ToolOutput.Error(
                    "Say what you ESTABLISHED that makes this runnable. If the honest answer is that you haven't " +
                    "checked, then this isn't a proposal yet: go and check, or record what you know as a notice.");

            if (risk.Length < Least)
                return ToolOutput.Error(
                    "Say what can't be undone and by when they have to decide. If genuinely nothing is " +
                    "irreversible, say that — but it has to be said, because a tick is a commitment and they need " +
                    "to see what they are committing to.");

            if (hours <= 0)
                return ToolOutput.Error(
                    "Give it a life in hours. A proposal that never expires sits on their timeline long after it " +
                    "stopped meaning anything, and a list of dead offers is a list nobody opens.");

            var action = store.Add(new ProactAction
            {
                Kind = ProactKinds.Proposed, Mode = mode, Subject = Subject(args),
                Body = args.GetStringOrNull("body")?.Trim() ?? "",
                What = what, Why = why, Plan = plan, Catch = risk,
                Produced = proved,
                Expires = DateTimeOffset.UtcNow.AddHours(Math.Min(hours, 24 * 14)),
                Links = ReadLinks(args),
                Images = await ReadImagesAsync(args, imageLoads, ct).ConfigureAwait(false),
            });

            return ToolOutput.Ok(
                $"Offered. They will see it as a tick or a cross on \"{action.What}\", and if they tick it the plan " +
                "you wrote is what gets carried out. You are done — do not act on it yourself.");
        });

    /// <summary>
    /// The commonest bad action, caught cheaply: something that would suit anybody.
    /// </summary>
    /// <remarks>
    /// The panel suggester learned this and says so in its own prompt: a placeholder title once reached a live home
    /// page reading "Build ... for your home page?". The test that works is not length, it is whether the thing is
    /// about THIS person — so the reason is required and a reason that could be said to a stranger is refused.
    /// </remarks>
    private static string? Vague(string what, string why)
    {
        if (what.Length < Least)
            return "Say what it is concretely — names, days, numbers. If you cannot name it in real words, it is " +
                   "not worth recording.";

        if (why.Length < Least)
            return "Say what about THEIR situation prompted this. If the reason would be true of anybody, this is a " +
                   "generality rather than an observation about them, and it should not be recorded at all.";

        if (what.Contains("...") || what.Contains('…') || what.Trim('.', ' ').Length < Least)
            return "That reads as placeholder text. Write the real thing or record nothing.";

        if (what.Length > MostTitle)
            return $"That is {what.Length} characters, which is a paragraph rather than a title — on their timeline " +
                   "it shows on ONE line and everything past the first clause is hidden. Put a short line in `what` " +
                   "and move the detail to `body`, where it can be as long as it needs to be.";

        return null;
    }

    /// <summary>
    /// What the person already keeps — their lists and projects, with what is actually on them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gap the first real run walked straight into. It was sent to find something new for a "Dinner spots"
    /// project, went out, found a place — and reported that it could not check whether any of their SAVED places had
    /// closed, because "there's no data file for it in this workspace". Its file tools are scoped to the
    /// conversation's own folder, so it could see the project's TITLE in its brief and never a single item on it.
    /// </para>
    /// <para>
    /// Which is the difference between a useful recommendation and a useless one: without this it cannot avoid
    /// suggesting something already on the list, and "have you tried the place you go to every week" is exactly the
    /// output that makes an assistant look like it is not paying attention.
    /// </para>
    /// <para>
    /// Read-only, and it has to be — every list tool that exists is a write (list_write, list_update, list_check,
    /// list_delete), so there was nothing to hand over that did not also let an unattended run edit the user's own
    /// data. This is the read half that never existed.
    /// </para>
    /// </remarks>
    public static AgentTool TheirListsTool(ProjectListStore lists, ProjectStore projects) => new(
        "read_their_lists",
        "Read what they already keep: their lists, with the items actually on them, and the projects they have on. " +
        "Read-only — you cannot change any of it.\n" +
        "CALL THIS BEFORE RECOMMENDING ANYTHING. What is already on a list is the thing not to bring them again, and " +
        "what is on it tells you what they actually like far better than the list's title does.",
        Array.Empty<ToolParameter>(),
        (_, _) =>
        {
            var text = new StringBuilder();

            if (projects.ActiveProjects() is { Count: > 0 } active)
            {
                text.Append("--- projects they have on ---\n");
                foreach (var p in active.Take(20))
                    text.Append($"{p.Title}{(p.Description is { Length: > 0 } d ? $" — {d}" : "")}\n");
                text.Append('\n');
            }

            var all = lists.All();
            if (all.Count == 0)
            {
                text.Append("They keep no lists yet.\n");
                return Task.FromResult(ToolOutput.Ok(text.ToString()));
            }

            foreach (var l in all.Take(30))
            {
                text.Append($"--- {l.Title}");
                if (l.Project is { Length: > 0 } owner) text.Append($" (in {owner})");
                if (l.Checklist) text.Append($" — {l.DoneCount} of {l.Items.Count} done");
                text.Append(" ---\n");

                // Capped per list rather than overall: a long list should be truncated in place, not push the
                // shorter ones out of the answer entirely.
                foreach (var item in l.Items.Take(40))
                {
                    text.Append($"  {item}");
                    if (l.IsDone(item)) text.Append(" [done]");
                    if (l.DateOf(item) is { } on) text.Append($" [{on:ddd d MMM}]");
                    text.Append('\n');
                }
                if (l.Items.Count > 40) text.Append($"  …and {l.Items.Count - 40} more\n");
                text.Append('\n');
            }

            return Task.FromResult(ToolOutput.Ok(text.ToString()));
        })
    {
        Repeatable = true,
    };

    /// <summary>What a Proact worker is told before it goes and does something.</summary>
    /// <remarks>
    /// Deliberately short on rules about safety, because the safety is in the toolset — there is no sending tool to
    /// be told not to use. What the brief has to carry instead is TASTE: who this is for, what it already did today,
    /// and the standing instruction that doing nothing is a good outcome.
    /// </remarks>
    public static string Brief(string mode, string decided, string context, string history,
        string? subject = null, string? area = null) =>
        (mode == ProactModes.Discover
            ? "You are taking ONE thing further, on your own initiative, for someone whose assistant you are.\n" +
              // THE TEST IS WHETHER THEY CAN ALREADY SEE IT, not whether it came from outside.
              //
              // This used to say "go OUT and find something they do not have — handing their own notes back is worth
              // nothing". Written when a deep look only ever meant research, and it became a contradiction the moment
              // their devices were a legitimate target: sent to check a vacuum, the worker read that framing, decided
              // that querying their own machine was the worthless kind of work, went out instead, and came back with
              // an art installation. It obeyed the loudest instruction in the brief, which was mine and was wrong.
              //
              // Their NOTES they can already see. Their vacuum's state they cannot. Both are "their own material" and
              // only one of them is already known to them.
              "WHAT EARNS THIS IS SOMETHING THEY DO NOT ALREADY KNOW. That is the only test. It can come from " +
              "outside — what has opened, what has changed, what is coming up — or from something of theirs you can " +
              "interrogate and they have not, like a device's own state. Reading their notes back to them is worth " +
              "nothing because they can see their notes; telling them what their machine has been doing is worth a " +
              "great deal because they cannot.\n" +
              "DO THE THING YOU DECIDED TO DO. If it says check a device, check the device — do not substitute " +
              "research you find more interesting, and do not treat looking at something of theirs as the lesser " +
              "kind of work. If you get there and it is genuinely dull, say so and record nothing.\n" +
              "Be specific and check it. A famous name anybody could have guessed is not research — a particular " +
              "thing, recent, and verified by actually looking, is.\n\n"
            : "You are looking after someone, on your own initiative. Something has changed and you thought it was " +
              "worth acting on.\n\n") +
        $"WHAT YOU DECIDED TO DO: {decided}\n\n" +
        "--- who they are and what is going on ---\n" + context + "\n" +
        history + "\n" +
        "GIVE THEM SOMETHING TO LOOK AT, not just a paragraph. Where you read something on a page worth opening, " +
        "pass it as a link with a real label. Where a picture is genuinely the answer — what a place looks like, " +
        "what a thing is — pass the image url you actually saw; each one is fetched before it is kept, so a guessed " +
        "url is simply dropped.\n\n" +
        "FINISH BY RECORDING WHAT YOU DID: proact_noticed (a fact they would want to know), proact_prepared (work " +
        "you actually did and left ready), proact_propose (something you will not do unattended, as a tick or a " +
        "cross).\n" +
        // "One of" was wrong, and it cost the clearest case there has been. Sent to look at a vacuum, it found the
        // map had never been built, wrote that up as a notice — and put "if you'd like, I can start a full clean" in
        // the BODY, as prose, because it had already spent its one choice. The offer was the useful half and it
        // arrived as a sentence nobody can click.
        "USE MORE THAN ONE WHERE THERE IS MORE THAN ONE. A fact worth knowing and an offer to act on it are two " +
        "things, and they are the commonest pair: record the fact, then offer the action.\n" +
        "AND THIS IS THE TELL — if you find yourself writing \"if you'd like, I can…\", \"shall I…\", \"I could…\" " +
        "anywhere in what you write, STOP. That is a proposal, not a sentence. Take it out of the text and put it " +
        "through proact_propose, where it becomes a tick and a cross they can actually answer. An offer buried in a " +
        "paragraph is an offer they have to reply to in words, which is the thing this whole surface exists to " +
        "spare them.\n" +
        "If it turns out there is nothing worth telling them — you looked and the answer was dull, or you were " +
        "wrong that anything had changed — record nothing and say so. That is a good outcome and it happens often. " +
        "An assistant who reports something every time is one who gets ignored.\n\n" +
        // THIS LINE WAS FALSE, and it was the reason a device trip could only ever end in a report.
        //
        // It said: "you cannot send, buy, book, cancel or change anything of theirs, AND YOU HAVE NO TOOLS THAT
        // COULD." True when written, and untrue the moment their plugins were added to this toolset. So the worker
        // arrived holding roborock_clean, having just been told in the last line of its brief that it held nothing
        // capable of changing anything — and that starting a vacuum belonged in the same sentence as cancelling a
        // booking. It read the machine's state and wrote it up, which was the only move left to it.
        //
        // Split three ways instead, in order, so nothing here has to be weighed against anything else: what it
        // just does, what it offers, and what is never its to do at all.
        "WHAT IS SIMPLY YOURS TO DO: read anything of theirs, and ask any of their own machines what it is doing " +
        "and how it is set. No permission needed and no proposal — that is what they are for.\n" +
        "WHAT IS A TICK: making one of their machines actually DO something — start, stop, move, clean. This is " +
        "not forbidden and it is often the most useful thing available to you; what is not yours is the TIMING, " +
        "because a machine starting up in the room somebody is sitting in is not a decision you make for them. So " +
        "put it through proact_propose, where their tick is you starting it. An offer to get a chore done is not a " +
        "lesser result than a fact — it is usually the better one.\n" +
        "WHAT IS NEVER YOURS: sending, buying, booking, cancelling, or anything that reaches another person, spends " +
        "their money, or cannot be undone. Those are proposals too, and they are the ones to be careful with.\n" +
        "Everything you write goes in your own workspace.";
}
