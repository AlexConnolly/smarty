using System.Text;

namespace Smarty.Api;

/// <summary>
/// What each state is told, and nothing else.
///
/// <para>
/// The build used to be one brief of twenty-one thousand characters: how to pick a slug, the whole component kit,
/// every Tailwind class in the palette, the four loader modes, how tall a row is, and six cautionary tales — handed
/// to a worker whose first job was to decide what the panel would show. Almost none of it applied to that decision,
/// and all of it was in the way of it. Worse, it had to be re-read on every turn of every state for four minutes.
/// </para>
/// <para>
/// So each state gets its own. The palette goes to the two states that write code. The loader taxonomy goes to the
/// two that go looking for data. What earlier states decided arrives as ARTIFACTS — the agreed fields, the proven
/// response — rather than as a transcript, because a transcript is where a state learns the previous state's bad
/// habits.
/// </para>
/// </summary>
public static class PanelBuildBriefs
{
    public static string For(PanelStep step, Widget panel, PanelBuildRecord record, string note)
    {
        var b = new StringBuilder();

        b.Append(Headline(step)).Append("\n\n")
         .Append($"The panel: \"{panel.Title}\" (id {panel.Id}).\n")
         .Append($"What the user asked for: {record.Shows}\n\n");

        b.Append(Body(step, panel, record));

        if (Known(record, step) is { Length: > 0 } known)
            b.Append("\n\nWHAT IS ALREADY SETTLED — these are decisions, not suggestions:\n").Append(known);

        if (note is { Length: > 0 })
            b.Append("\n\nWHY YOU ARE HERE: ").Append(note);

        b.Append("\n\n").Append(Ends(step));
        return b.ToString();
    }

    private static string Headline(PanelStep step) => step switch
    {
        PanelStep.Agree =>
            "STEP 1 OF 6 — AGREE WHAT IT SHOWS. Not how it looks, not where the data comes from. Only what is on it.",
        PanelStep.Design =>
            "STEP 2 OF 6 — DRAW IT, AND PUT IT IN FRONT OF THE USER. They are watching this panel being made.",
        PanelStep.Research =>
            "STEP 3 OF 6 — WORK OUT WHO CAN ANSWER. Identify the subject and choose a source. Do not publish anything.",
        PanelStep.Prove =>
            "STEP 4 OF 6 — CALL IT FOR REAL. One source, the real values, and whatever comes back.",
        PanelStep.Bind =>
            "STEP 5 OF 6 — TIE THEM TOGETHER. Map the proven response onto the agreed fields and publish the kind.",
        _ => "CHECK THE PANEL.",
    };

    private static string Body(PanelStep step, Widget panel, PanelBuildRecord record) => step switch
    {
        PanelStep.Agree =>
            "Decide the FIELDS. What does a person get from a glance at this panel, and what would the component " +
            "have to be handed to show it? Name each one, say what it is, and give an example value.\n\n" +
            "Keep it to what fits — this panel is " + panel.Size + " (" + WidgetTools.Footprint(panel.Size) + ").\n\n" +
            "One thing people forget here, and it costs a rebuild:\n" +
            "- THE EMPTY DAY. No game on, no flight today, nothing sold. Every panel has them, and a panel that goes " +
            "blank looks broken. Say what it shows instead — that is part of what it shows, not an edge case.\n" +
            // Flat names were the second warning here. widget_publish REFUSES a dotted field name now, so
            // the rule is enforced where it cannot be skim-read past, and this brief is shorter on every
            // build from now on.
            "You are not choosing a source. Do not open a browser, do not search, do not wonder whether an API " +
            "exists — a field nobody can supply is a problem for step 3 to find, and deciding what the panel is FOR " +
            "against what happens to be easy to fetch is how panels end up showing whatever an API returned.",

        PanelStep.Design =>
            "Write the component, invent plausible values in the agreed shape, and call widget_design with both. " +
            "The panel appears on the home page immediately, rendering your design against those values, dimmed, " +
            "with what is happening written over it.\n\n" +
            "THIS IS THE STEP THE USER SEES. Everything before it is invisible to them and everything after it takes " +
            "minutes. So the design goes up EVEN IF you are unsure about it — a design on screen is something they " +
            "can react to, and it stays there whatever happens next, including if no source can be found at all. A " +
            "build that ends without one leaves them with an empty box, which is what this step exists to prevent.\n\n" +
            "Make the values plausible, not placeholders: a real ground, a real scoreline, a real time. The design is " +
            "being SHOWN — \"string\" and 0 tell the user nothing about what they are getting.\n\n" +
            WidgetTools.Craft() +
            $"The panel is {panel.Size} ({WidgetTools.Footprint(panel.Size)}).\n\n" +
            "Do not go looking for a source. Do not publish. One call, and this step is done.",

        PanelStep.Research =>
            "Two questions, in this order.\n\n" +
            "1. WHO IS THE SUBJECT, AND WHAT IS IT CALLED THERE? A team id, a ticker, an ICAO code, a seller name. " +
            "Establish it — do not assume it. A guessed identifier is not refused: it is answered, in full, about " +
            "something else. A build asked for Leicester City's result guessed ESPN team 338, got a valid 200 with a " +
            "complete schedule, and it was Reading's. Whatever you decide, name the string that will appear in the " +
            "response ONLY if the answer is about the right subject, because that is what gets checked. An id you " +
            "passed in yourself appears in the response either way and proves nothing.\n\n" +
            "2. WHO CAN ANSWER? Pick the fetcher by what it has to BE:\n" +
            "   - client — the component fetches or embeds in the page itself. For anything a browser can already " +
            "reach: a public API that allows cross-origin requests, an image that refreshes, a video or MJPEG " +
            "stream, an embedded map. No url and no model are declared; it gets its parameters and gets on with it.\n" +
            "   - http — a plain server-side GET. For a key (which must never reach the page), an endpoint that " +
            "refuses cross-origin requests, or a response worth shaping once rather than in every tab. RSS AND ATOM " +
            "COUNT AS http and are converted to JSON for you: rss.channel.item (a list) with title, link, pubDate, " +
            "description; Atom reads feed.entry, and <link href=\"…\"/> is entry.link.href. Reach for a feed before " +
            "giving up on a site — Reddit's .json answers 403 to a server while /r/<sub>/.rss serves fine.\n" +
            "   - browser — loaded in the USER'S OWN Chrome. ONLY when the fetcher has to be them: a page behind " +
            "their login, their own seller view, their own orders. It costs a tab and a page load every refresh.\n" +
            "   - internal — what we already hold, and there is nothing to find or prove:\n" +
            "       agenda   {today:[{item,list,project,done}], tomorrow:[same]}\n" +
            "       lists    {lists:[{id,title,project,checklist,total,done,items:[{text,done,date}]}]}\n" +
            "       schedule {tasks:[{task,at,repeat,runs,dueInMinutes}]}\n" +
            "       tasks    {running:[{task,status,note,waiting,startedAt}]}\n" +
            "       projects {projects:[{slug,title,description}]}\n\n" +
            "NAME THE ALTERNATIVES TOO, best first. If the one you pick doesn't answer, the next step comes straight " +
            "back here and your own list is what it works from — so a build never grinds on a single dead url.\n\n" +
            "IF ONE SOURCE CANNOT ANSWER THE WHOLE QUESTION, say so here and name both. A panel asked for the last " +
            "result AND a countdown to the next match needs two calls, because ESPN answers the finished game at " +
            "/schedule and the fixtures at the same url with ?fixture=true, and nothing returns both. That is " +
            "supported — the binding step can declare further feeds, each mapping the fields it supplies — " +
            "but only when it is genuinely two questions. It is not a way round writing one good loader.\n\n" +
            "AND WHAT NOTHING CAN DO, so you don't spend the step finding out: a page cannot read the user's " +
            "filesystem, and a device speaking a non-HTTP protocol or https with a self-signed certificate cannot be " +
            "loaded by a browser at all. If this panel needs one of those, say so plainly and stop — there is no " +
            "local port to go looking for, and this repository's own source has never once been the answer.\n\n" +
            "Probing is fine — open pages, watch what their front end calls, search. Publishing is not this step.",

        PanelStep.Prove =>
            "Call the source with the real values and record what comes back with panel_proof. Paste the response " +
            "itself, from the start — the next step writes the mapping against it, so a paraphrase would be a " +
            "mapping onto fields nobody has seen.\n\n" +
            "ONE SOURCE. If it doesn't answer, that is the answer for this step: record what happened and stop. The " +
            "source gets struck off and the build goes back to find another, which is a decision made for you and " +
            "not a thing to work around here. Re-issuing a call that has already failed cannot change what it " +
            "returns — a build spent thirty-eight consecutive calls on that and finished with nothing.\n\n" +
            "For a client-mode panel there is nothing to call server-side: check the thing is actually reachable and " +
            "renderable from a browser, and record what you saw.",

        PanelStep.Bind =>
            "You have an agreed shape and a response that is known to answer. Write the loader that turns one into " +
            "the other, and publish.\n\n" +
            "Call widget_publish with: the panel id, a generic kind slug (flight-status, city-weather — never " +
            "ba2490 or palma-weather), a title, a description written in the words someone would USE asking for one, " +
            "the parameters with THIS panel's values, the component, the url template with {parameterName} where the " +
            "particular value goes, and a refresh cadence matched to how fast the answer really changes.\n\n" +
            "A KIND IN USE IS A CONTRACT. If the slug exists and panels use it, you may fix its component — that " +
            "mends every panel of that kind at once — but you may NOT change its parameters, because panels hold " +
            "values for those names. A different shape is a different kind, under a different slug.\n\n" +
            "The component goes IN THAT CALL. A .jsx file on disk is not a panel and nothing will ever read it: a " +
            "build once wrote a perfect component to a file, handed over a download, and left the home page showing " +
            "an empty box.\n\n" +
            Proven(record) +
            WidgetTools.Craft() +
            $"The panel is {panel.Size} ({WidgetTools.Footprint(panel.Size)}).",

        _ => "",
    };

    /// <summary>The response that was proved, so the mapping is written against what actually arrived.</summary>
    private static string Proven(PanelBuildRecord record)
    {
        if (record.Proof is not { } proof) return "";
        var b = new StringBuilder("WHAT THE SOURCE RETURNED when it was called at ").Append(proof.Url);
        if (proof.Status is { } status) b.Append(" (HTTP ").Append(status).Append(')');
        b.Append(":\n").Append(proof.Body).Append("\n\n");
        return b.ToString();
    }

    /// <summary>
    /// What earlier states decided, carried forward as fact.
    /// </summary>
    /// <remarks>
    /// The whole channel between states, and deliberately narrow. Handing the next state the previous one's
    /// transcript would hand it the previous one's dead ends too — the second state of a build would inherit four
    /// minutes of a browser session it has no use for, and pay for it on every turn.
    /// </remarks>
    private static string? Known(PanelBuildRecord record, PanelStep step)
    {
        var b = new StringBuilder();

        if (record.Contract is { Fields.Count: > 0 } contract && step != PanelStep.Agree)
        {
            b.Append("The panel shows these fields, agreed in step 1 and now fixed:\n");
            foreach (var f in contract.Fields)
                b.Append("  ").Append(f.Name).Append(" (").Append(f.Type).Append(')')
                 .Append(f.Required ? "" : ", optional")
                 .Append(f.Description is { Length: > 0 } d ? " — " + d : "")
                 .Append(f.Example is { Length: > 0 } e ? $" e.g. {e}" : "")
                 .Append('\n');
            if (contract.WhenEmpty is { Length: > 0 } empty)
                b.Append("  When there is nothing to report: ").Append(empty).Append('\n');
        }

        if (record.Source is { } source && step is PanelStep.Prove or PanelStep.Bind)
        {
            b.Append("\nThe source, chosen in step 3: ").Append(source.Subject);
            if (source.Identifier is { Length: > 0 } ident) b.Append(", as ").Append(ident);
            b.Append("\n  ").Append(source.Mode).Append(' ').Append(source.Url).Append('\n');
            if (source.Expect is { Length: > 0 } expect)
                b.Append("  The response must contain \"").Append(expect).Append("\" or it is about something else.\n");
        }

        if (record.Struck.Count > 0 && step is PanelStep.Research or PanelStep.Prove)
            b.Append("\nAlready tried in this build and dead — they cannot be the answer: ")
             .Append(string.Join(", ", record.Struck)).Append('\n');

        if (record.Source is { Alternatives.Count: > 0 } alt && step == PanelStep.Research)
            b.Append("\nWhat the last pass thought of as alternatives: ")
             .Append(string.Join(", ", alt.Alternatives)).Append('\n');

        return b.Length == 0 ? null : b.ToString();
    }

    /// <summary>How the state ends. One call, named, so there is no question about what finishing means.</summary>
    private static string Ends(PanelStep step) => step switch
    {
        PanelStep.Agree => "FINISH BY CALLING panel_shows. That call is this step; nothing else counts as doing it.",
        PanelStep.Design => "FINISH BY CALLING widget_design. Until you do, the user is looking at a blank box.",
        PanelStep.Research => "FINISH BY CALLING panel_source. Nothing is published or fetched from this step.",
        PanelStep.Prove => "FINISH BY CALLING panel_proof, whether it answered or not.",
        PanelStep.Bind => "FINISH BY CALLING widget_publish. The panel is not built until that call has succeeded.",
        _ => "",
    };
}
