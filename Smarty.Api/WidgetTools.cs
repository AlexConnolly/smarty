using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// Building a panel, and publishing it.
///
/// <para>
/// Two tools for two different jobs, held by two different things. The chat voice calls <c>widget_build</c>, which
/// starts a background task and returns immediately — because finding a data feed for a flight, checking it
/// actually answers, and writing a component around it is a job, not a form to fill in. The worker doing that job
/// calls <c>widget_publish</c> when it has both halves.
/// </para>
/// <para>
/// The brief is the interesting part. It is long because it has to be: it names the primitives available, insists
/// the feed be VERIFIED rather than guessed, and spells out that a scrape is a last resort. The previous version
/// asked a model to re-read a page every ten minutes and it failed on the first real request; the difference is
/// entirely in what the builder is told to go and find.
/// </para>
/// </summary>
public static class WidgetTools
{
    /// <summary>
    /// What a builder is told. Handed to the background task as its whole job.
    ///
    /// <para>
    /// The kit is listed by name because a component that invents its own primitives renders as a blank box, and
    /// the model has no way to discover them otherwise — there is no import to read.
    /// </para>
    /// </summary>
    public static string Brief(Widget w, string ask) =>
        $"Build a REUSABLE KIND of home-page panel, and light up the one that asked for it: \"{w.Title}\" " +
        $"(id {w.Id}).\n\nWhat this panel must show: {ask}\n\n" +
        "THE POINT — generic, with parameters.\n" +
        "You are not building a panel for this one flight, listing or city. You are building the KIND, which takes " +
        "the particular thing as a parameter, so that the next request is a lookup and a value instead of another " +
        "job like this one. \"Track BA2490\" means a flight-status kind taking a flight number; \"track my vinyl " +
        "record on eBay\" means an ebay-listing kind taking an item id.\n" +
        "A KIND IN USE IS A CONTRACT. If the slug you want already exists and panels are using it, you may fix its "
        + "component — that mends every panel of that kind at once and is the point of the library — but you may NOT "
        + "change its parameters. Panels hold values for those names and a feed url is built from them, so renaming "
        + "them orphans every existing panel silently. A different shape is a different kind: publish it under a "
        + "different slug (city-weather and city-weather-week are two kinds, not one), or reuse the existing "
        + "parameters exactly. Publishing will refuse the reshape, so decide before you write it.\n" +
        "So: pick a generic slug (flight-status, ebay-listing, city-weather — never ba2490 or palma-weather), put " +
        "{parameterName} in the feed url where the particular value goes, and declare each parameter with a " +
        "description and an example so a value can be recognised or gone and found later. Write the kind's " +
        "description in the words someone would USE asking for one — it is what gets searched next time, and a " +
        "kind nobody can find is a kind that gets built twice.\n\n" +
        "THEN, THREE PIECES OF WORK, IN THIS ORDER.\n\n" +
        "1. DESIGN IT, AND SHOW IT — before you go anywhere near a source.\n" +
        "   Decide what this panel shows and what shape the data has to be in for it to show that. Write the " +
        "component — JSX for this thing specifically, not a headline with a number in it; a flight should look like " +
        "a flight. Then invent a handful of plausible values in that shape and call widget_design with the panel id, " +
        "the code and the sample.\n" +
        "   The panel goes on the home page immediately, rendering your design against those values, dimmed, with " +
        "what you are doing written over it. Two things follow, and both are worth the one call: the user can SEE " +
        "what is being made for them while it is being made, and you find out whether the component renders NOW, " +
        "against data you control, instead of in eight minutes' time against a live source. A class outside the " +
        "palette or a stray fetch is refused here, where fixing it is one line.\n" +
        "   The shape you invent IS the model you declare when publishing, so choose the field names now and keep " +
        "them.\n\n" +
        "2. DECIDE WHO FETCHES, then prove it works.\n" +
        "   The question is not how hard it is — it is what the fetcher has to BE. In order:\n" +
        "   - client — THE COMPONENT DOES IT, in the page. For anything the browser can reach by itself: an "
        + "endpoint on the local network, a public API that allows cross-origin requests, and above all things that "
        + "are not \"data\" at all — an image that refreshes, a video or MJPEG stream, an embedded map. Bytes a "
        + "browser can already fetch have no reason to travel through a server. But CHECK that it can: a device on "
        + "the local network serving https with a self-signed certificate cannot be loaded by a page at all, and "
        + "neither can a protocol a browser doesn't speak — rtsp:// is not something an <img> or a <video> can open, "
        + "whatever the device's app manages to do with it. In this mode you declare NO model and NO url; "
        + "the component is handed its parameters and gets on with it, and it MAY use fetch, an <img> or a <video>.\n"
        + "   - http — a plain server-side GET. For an endpoint that needs a key (which must never reach the page), "
        + "one that refuses cross-origin requests, or one whose response wants shaping once rather than in every tab.\n"
        + "   - browser — loaded in the USER'S OWN Chrome. ONLY when the fetcher has to be THEM: a page behind a "
        + "login, their own listing's seller view, their own orders. It costs a tab and a page load every refresh, so "
        + "using it where a plain request would do is pure waste. Do not reach for it just because a site is awkward.\n"
        + "   - internal — what we already hold. THE SHAPE EACH ONE RETURNS, so you can write against it without\n"
        + "     going to look for the underlying data (a build asked to show this week's dinners spent 38 steps\n"
        + "     opening files in Chrome and scraping innerText, because it had only been told the NAMES):\n"
        + "       agenda   {today:[{item,list,project,done}], tomorrow:[same]}\n"
        + "       lists    {lists:[{id,title,project,checklist,total,done,items:[{text,done,date}]}]}\n"
        + "       schedule {tasks:[{task,at,repeat,runs,dueInMinutes}]}\n"
        + "       tasks    {running:[{task,status,note,waiting,startedAt}]}\n"
        + "       projects {projects:[{slug,title,description}]}\n"
        + "       plugin:<plugin>/<command>\n"
        + "                whatever an installed plugin's command returns as DATA \u2014 plugin:roborock/status\n"
        + "                is the vacuums. The one internal name whose shape is NOT fixed above, because it\n"
        + "                belongs to the plugin: declare it, load it once to see the fields, then map from\n"
        + "                what came back. Pair it with Action to make the panel a control, not a readout\n"
        + "     `date` on a list item is yyyy-MM-dd or null, which is what makes \"what is on for tonight\"\n"
        + "     answerable — filter the items by it rather than asking for a different feed. These need NO url, NO\n"
        + "     map and no model of your own: declare the internal name and read the shape above straight off `data`.\n"
        + "     THERE IS NOTHING TO PROVE for an internal feed and nothing to fetch. The shape above IS what your\n"
        + "     component is handed — it is this system's own data, produced in this process, and it cannot be\n"
        + "     unreachable. So do not go looking for a local server or a port: a previous build probed\n"
        + "     localhost:3000 for one because it had been told to prove its source and had nothing to call. If you\n"
        + "     want to see the REAL values rather than the shape, read them with your own list and project tools,\n"
        + "     which are the same data by another door.\n\n"
        + "   AND WHAT NONE OF THEM CAN DO, so you don't spend the build finding out: a page cannot read the user's "
        + "filesystem — there is no route from a panel to a local folder of photos or documents, and no server here "
        + "serves one. A device that speaks only a non-HTTP protocol, or https with a self-signed certificate, is "
        + "equally out of reach. If what was asked for needs one of those, it needs a new server-side capability "
        + "that does not exist: say exactly that in your result and stop. Do not go looking for a local port that "
        + "might be serving it, and do not read this repository's own source — a previous build spent four of its "
        + "eight minutes doing both, and neither could have worked.\n\n"
        + "   Whichever you pick, PROVE IT before publishing. Call the exact url with the real values and look at "
        + "what comes back; for a client-mode panel, check the thing is actually reachable and renderable from a "
        + "browser. A url you have not called is a guess, and a panel built on a guess is a blank box on someone's "
        + "home page that nobody can debug.\n"
        + "   RSS AND ATOM COUNT AS http. A feed is converted to JSON for you, so paths work normally: an RSS "
        + "document reads rss.channel.item (a list), with title, link, pubDate and description under each; an Atom one "
        + "reads feed.entry, and an attribute is a plain field, so <link href=\"…\"/> is entry.link.href. Reach for a "
        + "feed BEFORE giving up on a source: most sites publish one, it needs no key, and it keeps answering when the "
        + "JSON api beside it starts refusing server requests — Reddit's .json endpoints answer 403 with a block page "
        + "while /r/<sub>/.rss serves fine.\n"
        + "   For http, in order of preference: a key-less JSON API (open-meteo for weather, an operator's own JSON, "
        + "a public status endpoint); a site's own /api/ route that its front end calls, which you can find by "
        + "opening the page and watching the network requests. Do NOT give up because there is no official API — a "
        + "previous build was asked to track an eBay listing, found no key-less JSON feed and stopped, on a page "
        + "that has the price printed on it. Only report failure if there is genuinely no way to read the value at "
        + "all, and say which of the four routes you tried.\n\n"
        + "   IF ONE SOURCE CANNOT ANSWER THE WHOLE QUESTION, widget_publish takes further feeds in `more`, each with "
        + "its own map of the fields it supplies. They merge into one object and the model is checked once, so a feed "
        + "that fails only costs what it was carrying. What it exists for: a club's last result and its next fixture "
        + "are two different calls to the same api. Only for genuinely two questions.\n\n"
        + "3. PUBLISH IT. Call widget_publish with the panel id, the kind slug, title, description, the parameters " +
        "(each with a description and THIS panel's value), the code, the feed url template, and a refresh cadence " +
        "matched to how fast the answer really changes (a flight in the air: \"every 5 minutes\"; weather: \"every " +
        "2 hours\"; a bin collection: \"daily at 06:00\").\n" +
        "   The code goes IN THAT CALL. Do NOT write it to a file — a .jsx file is not a panel and nothing will " +
        "ever read it. A previous build spent four minutes finding a live feed, wrote a perfectly good component " +
        "to lgw_palma_panel.jsx, handed the user a download and finished; the home page was left showing an empty " +
        "box that said it was still being built. widget_publish is the only thing that puts a panel on the page, " +
        "and this job is not done until it has succeeded.\n\n" +
        Craft() +
        // Deliberately no worked example. There was one — a flight card — and every panel built afterwards was a
        // rearrangement of it: the same `justify-between` column, the same icon-and-badge heading, whatever the
        // subject was. An example is the strongest instruction in a brief, and what it instructs is "look like
        // this", which is the opposite of the point. The kit above names a component per shape; that is the
        // vocabulary, and a panel written from it looks like its own subject.
        $"The panel is {w.Size} ({Footprint(w.Size)})." + Already(w) +
        "\nDon't ask the user for anything that is above — least of all a value this panel already carries. Asking " +
        "for something only they can do (signing in to their own account, a credential) is legitimate; asking them " +
        "to re-type what you were handed is not.";

    /// <summary>
    /// How a panel is MADE: the shapes, the kit, the palette and the contract the code has to satisfy.
    /// </summary>
    /// <remarks>
    /// Its own block because only two jobs need it — drawing a panel and writing the component that ships —
    /// and it is the largest thing in any brief. A state that is finding a data source does not need the list
    /// of Tailwind classes, and every paragraph it does not need is a paragraph competing with the one it does.
    /// </remarks>
    internal static string Craft() =>
        "MAKE IT VISUAL, AND MAKE IT MOVE. Lead with the thing, not with a label for it.\n" +
        "There is a kit component for each shape a panel comes in, and reaching for it is not a shortcut — it is how " +
        "the proportions come out right, because the sizes live in the component and not in a number you had to " +
        "guess. Match the shape:\n" +
        "  one number            → Hero, and let it be big\n" +
        "  a proportion          → Ring\n" +
        "  a heading + contents  → Panel, with the contents inside it\n" +
        "  a list of things      → List, of Thing rows when they have pictures, of Row when they don't\n" +
        "  one picture           → Tile\n" +
        "  several pictures      → Pictures, which fills the box with them\n" +
        "An icon reads instantly where a word has to be read, so a status, a category or a direction should be an " +
        "Icon and a tone rather than a word — and a figure that matters should be big enough to see from across a " +
        "room. Rows inside List arrive one after another rather than all at once, which is the difference between a " +
        "list that looks pasted in and one that looks considered; panels already stagger against each other, so a " +
        "panel whose insides also stagger reads as part of the page assembling itself.\n\n" +
        "MAKE IT CLICKABLE. A panel about a thing should open the thing.\n" +
        "Every panel built so far has been read-only, and that was never a rule — no generated component had thought " +
        "of it. A listing you cannot open, a headline that is only a headline, a flight with no way through to the " +
        "flight: all worse than they need to be. So if the subject has a url, link to it with Link({href}) — the " +
        "title, the row, the headline, whichever IS the thing. Derive the url from a parameter when the feed doesn't " +
        "carry one (an eBay item id gives https://www.ebay.co.uk/itm/<itemId>). If a list has a url per row, every " +
        "row links.\n\n" +
        "AND IT HAS TO FIT — AND THE BOX IS THE SAME SIZE ON EVERY DEVICE. This page is read on a phone nearly " +
        "always, and a bigger screen does NOT give your panel more room: the grid gains columns and fits more panels " +
        "beside it. So the width above is the width you get, on a desktop exactly as on a phone, and anything wider " +
        "is clipped mid-word, silently.\n" +
        "What 170px actually holds, because it is less than it sounds: ONE column of rows, not two. About 22 " +
        "characters of body text per line. A row can hold a small icon, a short label and one number — nothing more. " +
        "Three or four such rows, not six. A weather panel that laid its hours out two-across at this width wrapped " +
        "\"22°\" onto two lines, splitting the degree sign off the digits, because each half was barely 80px. If you " +
        "have more to show than fits one column, show less of it — the panel is a glance, and the honest version of a " +
        "glance is fewer rows that are readable.\n" +
        "Long text must wrap or truncate — never leave both unset. `truncate` for one line, `line-clamp-2` or " +
        "`line-clamp-3` for a few. Give any flex child that holds text `min-w-0`, or it keeps its content's full " +
        "width and pushes past the edge: a news panel lost the end of every headline exactly that way, and a weather " +
        "panel lost half of \"London\".\n\n" +
        "THE CODE CONTRACT — read this carefully, there is no compiler to catch you:\n" +
        "- In http/browser/internal mode the component is handed `data` in exactly the shape your model declares, " +
        "plus `params`. In client mode there is no `data` — it gets `params` and does its own fetching or " +
        "embedding.\n" +
        "- Write the BODY of a function. It ends in `return (<jsx/>)`. No imports, no export, no `function` " +
        "wrapper, no `const Component =`.\n" +
        "- MEMORY, if the numbers are worth a history. Declare `track` when publishing — the field paths whose "
        + "numbers matter — and every load is kept. The component then also gets `history`, which is its OWN "
        + "top-level variable and NOT part of `data`: write `history.price`, never `data.history.price`. (The first "
        + "panel built against this reached for `data?.history?.price`, which is always undefined, so its trend "
        + "silently never appeared and nothing looked broken.) Per tracked field it holds "
        + "{values, at, first, last, change, percent, min, max, since}. This is what lets a panel say \"up 31 this "
        + "week\" or draw a line, which is usually the more interesting half: 247 views means little, 247 and flat "
        + "for three days means something. It accumulates in the background as the panel refreshes, so it is EMPTY "
        + "on a new panel — render the current value alone when there is no series yet, and never assume one.\n" +
        "- In scope: `data` — exactly what the feed returned, parsed. `params` — the parameter values for this "
        + "panel, so it can name what it is about. `React` and the hooks. `act(plugin, command, parameters)` — "
        + "runs one of an installed plugin's commands and returns {ok, text, data}; `Action` below is the ordinary "
        + "way to reach it and handles the waiting and the failure for you. A panel can DO things now, not only "
        + "show them: a vacuum panel with a Clean button, a lights panel with a switch. It never acts on its own "
        + "though — every one of these happens because somebody pressed it, so never call `act` from an effect or "
        + "on first render. And the kit:\n" +
        Kit() +
        "- Tailwind for layout, but ONLY from the list below. Tailwind compiles the stylesheet before your panel "
        + "exists, so a class outside it is not in the CSS at all and silently does nothing — the first panel built "
        + "this way used `tabular-nums` and `border-ink-faint`; the times didn't line up, the cards had no border, "
        + "and nothing anywhere said why. widget_publish REFUSES a class that isn't here, so use these:\n"
        + Classes()
        + "- The panel is a FIXED box, and YOU DO NOT KNOW HOW TALL IT IS. So anything that should be as big as the "
        + "box allows — a picture, a grid of them, a stream, a chart — takes the height rather than naming one: "
        + "`h-full w-full` with `object-cover`, inside a parent that has a height to give. `Panel` hands its body the "
        + "height left over after the heading, which is what makes that work; a `justify-between` column does the "
        + "opposite and gives the middle child nothing, which is how a panel of photographs ends up rendering them "
        + "the size of icons. Never pick a pixel height for a picture. For text the other way round: `truncate` and "
        + "`line-clamp-2` freely, and show fewer rows rather than overflowing.\n" +
        "- `data` may be null on the very first render. Handle it: `if (!data) return <Meta>Loading…</Meta>`.\n" +
        "- Defend every path into the feed's shape (`data?.a?.b?.[0]`). The feed you verified today is the feed " +
        "that returns something slightly different next Tuesday, and an exception here is a blank panel.\n" +
        "- In http/browser/internal mode: no fetch, no timers, no window, no document, no external libraries — the "
        + "data arrives as `data`, already shaped. In CLIENT mode the opposite: there is no `data`, and fetching or "
        + "embedding IS your job, so fetch, an <img src>, a <video src> and a refresh interval are all fair game. "
        + "Read what you need out of `params`.\n\n";

    /// <summary>
    /// Changing a panel that already works, WITHOUT rebuilding it.
    ///
    /// <para>
    /// A different job from <see cref="Brief"/>, and the difference matters more than anything else about it. A build
    /// starts from a description and goes looking for a source; an adjustment starts from a component that already
    /// renders real data and changes one thing about it. Handing an adjustment to the build brief threw away a working
    /// loader and a working component to re-derive them from a sentence — which is both slower and a coin flip,
    /// because the thing being replaced was already right.
    /// </para>
    /// <para>
    /// So the current code goes in verbatim, the loader is declared off limits unless the note is about the data, and
    /// the instruction is the smallest change that satisfies what was asked. Same kind, same parameters, same slug —
    /// which the library now enforces anyway.
    /// </para>
    /// </summary>
    public static string AdjustBrief(Widget w, WidgetKind? kind, string note) =>
        "ADJUST an existing home-page panel. Do NOT rebuild it.\n\n" +
        $"The panel: \"{w.Title}\" (id {w.Id}), an instance of the \"{kind?.Name ?? "one-off"}\" kind" +
        (w.Params.Count > 0
            ? $" with {string.Join(", ", w.Params.Select(kv => $"{kv.Key}={kv.Value}"))}"
            : "") + ".\n\n" +
        $"WHAT THE USER WANTS CHANGED — this is the whole job:\n{note}\n\n" +
        "THIS IS AN EDIT, NOT A BUILD.\n" +
        "The loader works and the data is arriving. Do not go looking for a new source, do not open a browser, do " +
        "not re-derive anything that already functions. Make the SMALLEST change to the component that satisfies " +
        "what was asked, leave everything else exactly as it is, and republish the same kind with the same slug and " +
        "the same parameters. If the note asks for a field the data genuinely does not carry, THEN the loader needs " +
        "changing — say so and change only that part of it, and prove it with widget_try before you publish it: " +
        "that is the same fetch publish judges, so a loader that passes there passes there.\n\n" +
        $"The panel is {w.Size} ({Footprint(w.Size)}).\n\n" +
        "--- the current data model ---\n" +
        ((kind?.Model.Count ?? 0) == 0
            ? "(none declared — this is a client-mode panel)\n"
            : string.Join("\n", kind!.Model.Select(f => $"{f.Name} ({f.Type}) — {f.Description}")) + "\n") +
        // EVERYTHING NEEDED TO REPUBLISH THE LOADER UNCHANGED, because republishing it is not optional — publish
        // takes the whole kind, so a part left out of this brief is a part the worker has to invent.
        //
        // That is not a theoretical risk. This printed the mode and the map and NOT the internal feed's name, so a
        // rename was handed "mode: internal" and a map of paths beginning "any." with no way to know what the feed
        // was called. It reconstructed the only token on the page and published "source:any" — which no folder was
        // ever granted, over a feed that had been working — and the panel failed every five minutes for nine hours.
        // The map was right all along. Telling it beats guarding against the guess.
        "\n--- how it loads (leave alone unless the note is about the data) ---\n" +
        $"mode: {kind?.Loader.Mode ?? "own component"}\n" +
        (kind?.Loader.Url is { Length: > 0 } url ? $"url: {url}\n" : "") +
        (kind?.Loader.Internal is { Length: > 0 } feed ? $"internal feed: {feed}\n" : "") +
        (kind?.Loader.Expression is { Length: > 0 } script
            ? $"expression (republish this EXACTLY unless the note is about the data):\n{script}\n"
            : "") +
        ((kind?.Loader.Map.Count ?? 0) > 0
            ? "map: " + string.Join(", ", kind!.Loader.Map.Select(kv => $"{kv.Key} <- {kv.Value}")) + "\n"
            : "") +
        ((kind?.More.Count ?? 0) > 0
            ? $"further feeds ({kind!.More.Count}), which republish with the panel and must be passed back in " +
              "`more`:\n" + string.Join("\n", kind.More.Select(f =>
                  $"  - {f.Mode}: {f.Url ?? f.Internal ?? "(own expression)"} supplying " +
                  string.Join(", ", f.Map.Keys))) + "\n"
            : "") +
        $"refresh: {kind?.Refresh}\n" +
        ((kind?.Track.Count ?? 0) > 0 ? $"remembered: {string.Join(", ", kind!.Track)}\n" : "") +
        "\n--- THE CURRENT COMPONENT, which you are editing ---\n```jsx\n" +
        (w.OwnCode ?? kind?.Code ?? "") + "\n```\n\n" +
        "The code contract is unchanged from when this component was written, so you can edit it in place. Call " +
        "widget_publish with the edited component, then say in one line what you CHANGED — not what the panel is.\n\n" +
        "THE KIT AS IT IS TODAY — not necessarily as it was when this panel was written, so read it rather than " +
        "assuming. Most complaints about how a panel looks are answered by using the component for the shape it is " +
        "(a picture grid, a list row with a picture, the panel scaffold) instead of hand-rolled markup, because the " +
        "proportions live in the component:\n" +
        Kit() +
        "\nAND YOU DO NOT KNOW HOW TALL THE BOX IS, so anything that should be as big as it allows takes " +
        "the height — `h-full w-full` with `object-cover`, inside a parent with a height to give. `Panel` " +
        "gives its body the height left after the heading. Never pick a pixel height for a picture.\n\n" +
        // The Tailwind palette used to be pasted in here: ~825 tokens on every adjust, for ever. It is a
        // closed list that widget_publish already ENFORCES, and the staged build path has never carried it
        // and does not need it — so the list now lives in the refusal, read by the one attempt that got it
        // wrong rather than by every attempt that did not.
        "Tailwind here is a CLOSED palette — the stylesheet is compiled before your panel exists, so a class " +
        "outside it is not in the CSS at all and silently does nothing. Stay with what the component you are " +
        "editing already uses; publishing tells you, and lists the whole palette, if you stray.";

    /// <summary>
    /// What this panel already is, when it is being rebuilt rather than built.
    ///
    /// <para>
    /// A rebuild used to arrive with no mention of the instance's own values, and the consequence was exact: asked to
    /// add views and watchers to an eBay panel, the worker opened eBay, hit a sign-in wall, and asked the user for
    /// "the item id of the vinyl listing you want tracked" — which was sitting on the panel it was rebuilding, as
    /// itemId=327313414035. It could not have known. The brief in the same breath told it not to ask the user
    /// anything, which is only fair if what it needs is actually in front of it.
    /// </para>
    /// </summary>
    private static string Already(Widget w)
    {
        if (w.Kind is not { Length: > 0 } kind) return "";

        var values = w.Params.Count > 0
            ? string.Join(", ", w.Params.Select(kv => $"{kv.Key}={kv.Value}"))
            : "(none)";

        return $"\n\nTHIS IS A REBUILD of an existing panel, not a new one. It is already an instance of the " +
               $"\"{kind}\" kind, with these parameter values: {values}. Use them — they are the thing this panel " +
               "is about. Keep the same kind slug and the same parameter names unless you are deliberately changing " +
               "the shape, and republish with those same values so the panel keeps pointing at the same thing.";
    }

    /// <summary>
    /// The kit, as a builder reads it.
    ///
    /// <para>
    /// Its own method because BOTH briefs need it and only one had it. An adjustment — "make the pictures bigger",
    /// which is the most common thing anyone ever asks of a panel — was told the kit was "unchanged from when this
    /// component was written" and never shown it. So the one job most likely to want a component the original build
    /// didn't use was the one job that could not see the list. That is the same shape as a tool advertised with no
    /// dispatch behind it: the capability exists, the model cannot reach it, and nothing reports a fault.
    /// </para>
    /// </summary>
    private static string Kit() =>
        "    Stat({label, value, unit, tone})            one big figure with a label\n" +
        "    Row({label, value, tone, icon})             a label/value line\n" +
        "    Progress({value, max, tone, label})         a progress bar\n" +
        "    Bars({values, tone})                        a tiny bar chart, for a series\n" +
        "    Badge({children, tone})                     a small pill\n" +
        "    Dot({tone})                                 a status dot\n" +
        "    Icon({name, className})                     plane, clock, sun, cloud, rain, check, alert, arrow,\n" +
        "                                                train, pin, money, chart, box, bell, drop, wind, star,\n" +
        "                                                heart, tag, calendar, mail, music, film, cart, flame,\n" +
        "                                                moon, wifi, battery, home, person\n" +
        "    Meta({children})                            small muted text, for footnotes\n" +
        "    Link({href, children, tone, title, fill})   opens in a new tab; plain text if href is missing. fill\n" +
        "                                                when the link IS the thing — a picture, a row, a tile\n" +
        "    Stagger({children, step})                   its children arrive one after another, not all at once\n" +
        "    Hero({label, value, unit, icon, tone,       ONE figure as the whole point of the panel: big, with its\n" +
        "          change, percent, series})             icon, its trend and its history under it\n" +
        "    Ring({value, max, label, caption, tone})    a proportion as a circle — reads as a fraction at a glance\n" +
        "    Tile({src, title, caption, href})           ONE picture as the panel, with the words over it\n" +
        "    Pictures({items, cols, lead})               SEVERAL pictures as the panel: [{src, href, caption}],\n" +
        "                                                a grid that fills the box — each one as big as the box\n" +
        "                                                affords, no heights to guess. lead makes the first one a\n" +
        "                                                2x2 block with the rest beside it\n" +
        "    Slides({items, every, caption})             SEVERAL pictures ONE AT A TIME, moving on by themselves:\n" +
        "                                                [{src, href, caption}], every is seconds, 5 by default.\n" +
        "                                                The timer is INSIDE it, so your component still may not\n" +
        "                                                have one. Reach for it when the ask says slideshow or\n" +
        "                                                rotating, which used to come out as a grid of thumbnails\n" +
        "    Button({label, onPress, tone, icon,         SOMETHING TO PRESS that stays inside the panel. onPress\n" +
        "            active})                            is an ordinary handler, so with useState it gives a\n" +
        "                                                second view of the same data: next and previous, a tab,\n" +
        "                                                \"show the other four\"\n" +
        "    Action({label, plugin, command,             SOMETHING TO PRESS THAT DOES IT. Runs one of an\n" +
        "            parameters, icon, tone})            installed plugin's commands — plugin is its id\n" +
        "                                                (\"roborock\"), command is the bare name (\"clean\",\n" +
        "                                                \"stop\", \"dock\"), parameters is whatever that command\n" +
        "                                                takes as {name: value}. It handles pressed-and-waiting,\n" +
        "                                                reports back whatever failed, and reloads the panel when\n" +
        "                                                it worked — so do NOT write that state yourself. Which\n" +
        "                                                button to show is your job: read the data and render\n" +
        "                                                Stop when it is running, Start when it is not\n" +
        "    Panel({title, icon, badge, tone, footer,    THE WHOLE PANEL: heading, a body that takes the height\n" +
        "           children})                           that is left, a footnote. Start here for anything with a\n" +
        "                                                heading and contents — it is what makes a picture grid or\n" +
        "                                                a list fill the box instead of being squeezed by it\n" +
        "    Header({title, icon, badge, tone, href})    just the top line, when Panel is more than you need\n" +
        "    List({children})                            rows, spaced and arriving in order (Stagger, built in)\n" +
        "    Thing({image, icon, title, meta, value,     ONE ROW OF A LIST WITH ITS PICTURE: a listing, a record,\n" +
        "           badge, tone, href})                  a fixture, a headline. Picture big enough to recognise,\n" +
        "                                                the name, a line of detail, the figure on the right, and\n" +
        "                                                the whole row opens the thing\n" +
        "    Avatar({src, name, size})                   someone, as a circle; initials when there is no photo\n" +
        "    Chips({items, tone})                        a wrapping row of small pills\n" +
        "    Sparkline({values, tone})                   a tiny line chart, for a series over time\n" +
        "    Trend({change, percent, tone, suffix})      an arrow with a delta, coloured by direction\n" +
        "    Kpi({label, value, unit, change, percent})  a whole KPI box: figure, label and trend, laid out\n" +
        "  tone is one of: good | warn | bad | accent | neutral.\n";

    /// <summary>Every component name the kit listing offers, read out of the listing itself.</summary>
    internal static IReadOnlyList<string> KitNames { get; } =
        System.Text.RegularExpressions.Regex.Matches(Kit(), @"(?m)^ {4}([A-Z]\w*)\(\{")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The Tailwind classes a generated panel may use.
    ///
    /// <para>
    /// This exists because the alternative failure is completely silent. Tailwind emits only the classes it finds in
    /// the source at build time, so one appearing for the first time inside a component written an hour ago is not
    /// in the stylesheet — the element simply renders unstyled, with nothing in any log. The first generated flight
    /// panel invented `border-ink-faint`, and the cards had no border.
    /// </para>
    /// <para>
    /// So the palette is closed, stated in the brief, and enforced at publish time. It is paired with the
    /// <c>widgetClasses</c> safelist in Smarty.Chat/tailwind.config.js — the two are halves of one contract, and
    /// <c>npm run check:classes</c> proves they still agree against the panels that actually exist.
    /// </para>
    /// </summary>
    private static readonly string[][] Palette =
    {
        new[]
        {
            "layout",
            "flex inline-flex grid hidden block flex-col flex-row flex-wrap flex-1 shrink-0 grow",
            "items-start items-center items-end items-baseline",
            "justify-start justify-center justify-end justify-between justify-around",
            "grid-cols-1 grid-cols-2 grid-cols-3 grid-cols-4 col-span-1 col-span-2 col-span-3 col-span-4",
            "grid-rows-1 grid-rows-2 grid-rows-3 auto-rows-fr row-span-1 row-span-2",
            "ml-auto mr-auto mt-auto mb-auto self-start self-center self-end",
            "relative absolute inset-0 right-0 top-0 bottom-0 left-0",
            "h-full w-full min-w-0 min-h-0 max-w-full max-h-full overflow-hidden",
        },
        new[]
        {
            "spacing",
            "gap-0.5 gap-1 gap-1.5 gap-2 gap-2.5 gap-3 gap-4 gap-x-2 gap-x-3 gap-y-1 gap-y-2",
            "p-0 p-1 p-1.5 p-2 p-3 px-1 px-1.5 px-2 px-2.5 px-3 py-0.5 py-1 py-1.5 py-2",
            "mt-0.5 mt-1 mt-1.5 mt-2 mt-3 mb-0.5 mb-1 mb-2 pt-1 pt-2 pl-2 pl-3 pl-4 pr-2",
            "space-y-0.5 space-y-1 space-y-1.5 space-y-2 space-x-1 space-x-2",
        },
        new[]
        {
            "type",
            "text-[0.625rem] text-[0.6875rem] text-xs text-sm text-base text-lg text-xl text-2xl text-[1.75rem]",
            "font-normal font-medium font-semibold font-bold font-mono",
            "leading-none leading-tight leading-relaxed tracking-tight tracking-wide uppercase capitalize",
            "tabular-nums truncate line-clamp-1 line-clamp-2 line-clamp-3 whitespace-nowrap break-words",
            "text-left text-center text-right",
        },
        new[]
        {
            "colour (the app's own tokens — invent nothing)",
            "text-ink text-ink-soft text-ink-mute text-accent text-danger text-on-accent",
            "text-emerald-600 text-amber-600",
            "bg-surface bg-surface-low bg-surface-mid bg-accent bg-accent-soft bg-transparent",
            "bg-emerald-50 bg-amber-50 bg-red-50",
            "border border-0 border-t border-b border-l border-line border-accent border-transparent",
            "rounded rounded-sm rounded-md rounded-lg rounded-xl rounded-full",
            "opacity-50 opacity-60 opacity-70",
        },
        new[]
        {
            "sizes — icons and swatches at the small end, PICTURES at the large end",
            "h-1.5 h-2 h-3 h-3.5 h-4 h-5 h-6 h-8 h-10 h-11 h-12 h-14 h-16 h-20 h-24",
            "w-1.5 w-2 w-3 w-3.5 w-4 w-5 w-6 w-8 w-10 w-11 w-12 w-14 w-16 w-20 w-24",
            "object-cover object-contain object-center object-top aspect-video aspect-square",
            "sm:block sm:flex sm:hidden",
        },
    };

    private static readonly HashSet<string> Allowed =
        new(Palette.SelectMany(group => group.Skip(1)).SelectMany(line => line.Split(' ')), StringComparer.Ordinal);

    /// <summary>
    /// The palette, for the test that proves it still matches the stylesheet's safelist. A class in one and not the
    /// other either renders unstyled or is refused for no reason, and both are silent.
    /// </summary>
    internal static IReadOnlySet<string> PaletteClasses => Allowed;

    /// <summary>The palette as the brief shows it.</summary>
    private static string Classes() =>
        string.Concat(Palette.Select(g => $"  {g[0]}: {string.Join(" ", g.Skip(1))}\n"));

    /// <summary>
    /// Classes in the code that aren't in the palette. Anything with a template hole in it is skipped — that is a
    /// value being interpolated, not a class name, and there is nothing to check until it runs.
    /// </summary>
    private static List<string> UnknownClasses(string code)
    {
        var found = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(code, @"className=(?:""([^""]*)""|\{`([^`]*)`\})"))
        {
            var text = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            foreach (var cls in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (cls.Contains('$') || cls.Contains('{') || cls.Contains('}')) continue;
                if (!Allowed.Contains(cls) && !found.Contains(cls)) found.Add(cls);
            }
        }
        return found;
    }

    /// <summary>
    /// What will fit, said in terms of content rather than pixels.
    ///
    /// <para>
    /// Blunt about the KPI box on purpose. Every clipped panel on the home page was a component putting a sentence, a
    /// list or three figures into a box two rows of text high, and the box was never going to hold it — a quote panel
    /// read "Non-violence and truth are inseparable and presuppose one another" and then stopped mid-thought. Told
    /// what fits, a component is written to fit.
    /// </para>
    /// </summary>
    internal static string Footprint(string size) => size switch
    {
        WidgetSizes.Tall => "2 grid columns × 2 rows — about 170px wide by 250px tall, and the SAME physical size on "
             + "every device: a bigger screen adds columns and fits more panels beside this one, it does not make this "
             + "one bigger. Your component gets about 185px of that height once the padding and the age line are out. "
             + "Room for a few short rows, a small chart, or a couple of lines of text",
        WidgetSizes.Wide => "4 grid columns × 2 rows — about 350px by 250px, the full width of a phone and the same "
             + "size on a laptop. The only footprint with room for a list, a table or a real chart. Your component "
             + "gets about 320px by 185px of it, once the padding and the age line underneath are out — WHICH IS THE "
             + "NUMBER THAT DECIDES HOW MANY ROWS: a heading is 20, a footnote is 15, a Row is 26, and a Thing (a row "
             + "with a picture in it) is 56. So a heading and three Things fills it exactly and a footnote under them "
             + "would be pushed out of sight; a heading, six Rows and a footnote also fits. Count it rather than "
             + "guessing, and if what you have will not fit, show less of it — a panel is a glance",
        _ => "a KPI box, 2 grid columns × 1 row: about 170px by 115px, and the same on every device. ONE number with "
             + "a label, and at most a small trend beside it. There is no room for a sentence, a list, or a second "
             + "figure. If what you have to show needs more than that, the size is wrong — say so in your result "
             + "rather than cramming it in, because it will be clipped and look broken",
    };

    /// <summary>The chat voice's tool: reserve a place on the page and send a worker to build it.</summary>
    public static AgentTool BuildTool(WidgetStore widgets, Func<string?> currentSession,
        Func<Widget, string, Task<string>> startBuild) => new(
        "widget_build",
        "Build the user a panel on their home page. Use this whenever they want to be KEPT informed rather than " +
        "told once — \"track this flight\", \"keep an eye on the share price\", \"show me when the bins go out\" — " +
        "and whenever you notice something they clearly check often.\n" +
        "A background worker goes and finds a live data feed for it, verifies the feed actually answers, and " +
        "writes a component around it. That takes a minute or two; the panel appears immediately and says it's " +
        "being built. You do NOT need to find the data yourself — describe what should be on the panel and let " +
        "the worker do it.\n" +
        "If the user asked for it, it goes straight on the page: asking IS the approval, so never come back to " +
        "check. If you thought of it yourself, set proposed=true and they'll be offered it once.",
        new[]
        {
            ToolParameter.String("title", "The panel's name, a few words — \"BA2490 to Palma\", \"Bin day\".", required: true),
            ToolParameter.String("shows",
                "What must be on it, concretely, naming the exact thing — the flight number and date, the ticker, " +
                "the postcode, the city. The worker sees only this, so anything vague here is a vague panel.", required: true),
            ToolParameter.String("size",
                "small (two or three lines), wide (one line, full width), tall (about six rows), large (a chart " +
                "or eight rows). Small unless it genuinely needs the room.", required: false),
            new ToolParameter("priority", "integer",
                "0-100, how near the top. Something they just asked about is 80+; useful background 40-60. Be " +
                "honest — everything at 90 is the same as everything at 10.", required: false),
            new ToolParameter("proposed", "boolean", "True only when the user did NOT ask for this.", required: false),
            ToolParameter.String("why", "One line on why it deserves a place. Shown when you're offering it.", required: false),
        },
        async (args, _) =>
        {
            var title = args.GetStringOrNull("title")?.Trim();
            var shows = args.GetStringOrNull("shows")?.Trim();
            if (string.IsNullOrWhiteSpace(title)) return ToolOutput.Error("A panel needs a title.");
            if (string.IsNullOrWhiteSpace(shows))
                return ToolOutput.Error("Say what the panel must show — the worker has nothing else to go on.");

            // Already there, already working, already an instance of something in the library. Building over it
            // would put a live panel back to "building" and publish a second kind for a shape that has one — which
            // is exactly what happened the first time a city was added twice: widget_add succeeded, the model made
            // a redundant second call, then "recovered" by building, and a working Lisbon panel went dark while a
            // duplicate city-weather kind was written.
            if (widgets.All().FirstOrDefault(p =>
                    string.Equals(p.Title, title, StringComparison.OrdinalIgnoreCase)) is
                { Status: WidgetStatus.Live, Kind: { Length: > 0 } existingKind } already)
                return ToolOutput.Error(
                    $"\"{already.Title}\" is already on the home page and working, from the {existingKind} kind. " +
                    "Nothing to do. If it needs to show something different, rebuild it explicitly rather than " +
                    "building a new one over the top.");

            bool proposed = args.GetBoolOrNull("proposed") == true;
            var w = widgets.Reserve(
                title!,
                args.GetStringOrNull("size") ?? WidgetSizes.Kpi,
                args.GetIntOrNull("priority") ?? (proposed ? 40 : 75),
                args.GetStringOrNull("why")?.Trim(),
                currentSession(),
                proposed);

            if (proposed)
                return ToolOutput.Ok(
                    $"Offered \"{w.Title}\" — it'll be built if the user says yes. Mention it in passing; don't press.");

            var taskId = await startBuild(w, shows!).ConfigureAwait(false);
            return ToolOutput.Ok(
                $"Building \"{w.Title}\" now (task {taskId}) — it's on the home page already, saying it's being " +
                "built, and it'll fill in when the worker has found a feed for it. Tell the user in one line " +
                "that it's on its way; don't describe the mechanics and don't wait for it.");
        })
    {
        Repeatable = true,
    };

    /// <summary>
    /// The mistakes that turn into a blank panel, caught while there is still something to tell.
    /// </summary>
    /// <param name="clientMode">
    /// A client-mode component fetches and embeds by design, so the guards that exist to stop a server-fed panel
    /// doing its own I/O would forbid the very thing it is for. The two that always hold — a return, and no import
    /// or export — still do.
    /// </param>
    internal static string? Rejected(string code, bool clientMode = false)
    {
        if (code.Contains("import ", StringComparison.Ordinal))
            return "No imports — React, the hooks and the kit are already in scope. Remove the import lines.";
        if (code.Contains("export ", StringComparison.Ordinal))
            return "No export — write the function BODY only, ending in a return.";
        if (!clientMode)
        {
            if (code.Contains("fetch(", StringComparison.Ordinal))
                return "No fetch — the data arrives as `data`, already loaded and shaped. If the page genuinely has " +
                       "to fetch for itself (a local endpoint, a stream), publish with loader_mode \"client\".";
            if (code.Contains("setInterval", StringComparison.Ordinal) ||
                code.Contains("setTimeout", StringComparison.Ordinal))
                return "No timers — refreshing is the loader's job, on the cadence you give widget_publish. A panel " +
                       "that has to poll for itself is a client-mode panel.";
            if (code.Contains("document.", StringComparison.Ordinal) ||
                code.Contains("window.", StringComparison.Ordinal))
                return "No document or window — return JSX and nothing else.";
        }
        if (!code.Contains("return", StringComparison.Ordinal))
            return "The body has to end in a `return (<jsx/>)`.";
        // A component that never mentions its data is a component showing a hardcoded answer, which is the exact
        // failure this design exists to prevent: it looks right today and is a lie tomorrow. In client mode there is
        // no `data` — the equivalent is that it must at least use its parameters, or it is a panel about nothing in
        // particular.
        if (!clientMode && !code.Contains("data", StringComparison.Ordinal))
            return "The component never uses `data`, so it would show the same thing forever. Read the values out " +
                   "of the model rather than writing them in.";
        if (clientMode && !code.Contains("params", StringComparison.Ordinal))
            return "A client-mode component has no `data`, so it must read `params` — otherwise it is hardcoded to " +
                   "one address and the kind cannot be reused.";

        // The silent one, and so the one most worth refusing: a class outside the palette isn't in the compiled
        // stylesheet, so the element renders unstyled and nothing anywhere says so.
        if (UnknownClasses(code) is { Count: > 0 } unknown)
            // The whole palette goes in the REFUSAL rather than into every brief. It was in the adjust brief
            // and in no other, so this message told a staged build to "use only what your brief listed" when
            // its brief had listed nothing — an unactionable refusal, which is as good as none.
            return $"These classes aren't in the panel palette, so they would do nothing at all: " +
                   $"{string.Join(" ", unknown)}. The stylesheet is compiled before your panel exists, so " +
                   $"there is nothing outside this list:\n{Classes()}";

        return null;
    }

    /// <summary>Re-ranking, resizing and removing. Cheap edits, no rebuild.</summary>
    public static IEnumerable<AgentTool> Manage(WidgetStore widgets) => new[]
    {
        new AgentTool(
            "widget_update",
            "Re-rank, retitle, resize, pause or re-time a panel already on the home page. This is what keeps the " +
            "page honest — a flight that has landed drops to nothing, a shopping list becomes urgent the day " +
            "before. It does NOT rebuild the panel; for a different feed or a different look, build it again.",
            new[]
            {
                ToolParameter.String("id", "The panel's id.", required: true),
                ToolParameter.String("title", "A new name.", required: false),
                ToolParameter.String("size", "small, wide, tall or large.", required: false),
                new ToolParameter("priority", "integer", "0-100.", required: false),
                new ToolParameter("pinned", "boolean", "Keep it at the top regardless. Only when the user has said so.", required: false),
                ToolParameter.String("status", "live or paused.", required: false),
                ToolParameter.String("refresh", "A new cadence, or empty to stop re-fetching.", required: false),
            },
            (args, _) =>
            {
                var id = args.GetStringOrNull("id")?.Trim();
                if (string.IsNullOrWhiteSpace(id) || widgets.Get(id!) is null)
                    return Task.FromResult(ToolOutput.DeadEnd($"There's no panel \"{id}\"."));

                return Task.FromResult(widgets.Edit(id!, args.GetStringOrNull("title"),
                    args.GetStringOrNull("size"), args.GetIntOrNull("priority"), args.GetBoolOrNull("pinned"),
                    args.GetStringOrNull("status")?.Trim().ToLowerInvariant(), args.GetStringOrNull("refresh"))
                    ? ToolOutput.Ok($"Updated \"{widgets.Get(id!)!.Title}\".")
                    : ToolOutput.Error("Couldn't read the refresh cadence. Nothing changed."));
            })
        {
            Repeatable = true,
        },

        new AgentTool(
            "widget_remove",
            "Take a panel off the home page. Do it the moment something has stopped mattering — a flight that has " +
            "landed, a delivery that arrived. A page nothing ever leaves stops being a page about now.",
            new[] { ToolParameter.String("id", "The panel's id.", required: true) },
            (args, _) =>
            {
                var id = args.GetStringOrNull("id")?.Trim();
                if (string.IsNullOrWhiteSpace(id)) return Task.FromResult(ToolOutput.Error("Which panel?"));
                return Task.FromResult(widgets.Remove(id!)
                    ? ToolOutput.Ok($"Removed panel {id}.")
                    : ToolOutput.DeadEnd($"There's no panel \"{id}\"."));
            }),
    };
}

/// <summary>
/// Every name a generated component is handed.
/// </summary>
/// <remarks>
/// <para>
/// One list, derived from the kit listing the builder is shown, because the copy that mattered was hand-kept and
/// stale: a guard meant to spot "a browser running an older bundle is complaining about a name that DOES exist" knew
/// eight of the twenty-three names, so it recognised a third of them. The two-thirds it missed each looked like a real
/// fault in the panel.
/// </para>
/// </remarks>
public static class WidgetScope
{
    /// <summary>What a component gets besides the kit: its data, its parameters, React, and the hooks.</summary>
    private static readonly string[] Given =
    {
        "React", "useState", "useMemo", "useEffect", "useRef", "data", "params", "history", "fetch",
        // Runs a plugin command. Named here or widget_publish refuses every panel that presses a button,
        // which is how the capability existed for a while with nothing able to reach it.
        "act",
    };

    public static IReadOnlyCollection<string> Names { get; } =
        Given.Concat(WidgetTools.KitNames).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>
/// Which tools survive the trim a panel build gets.
/// </summary>
/// <remarks>
/// <para>
/// The trim itself is worth having: given all forty tools, a build asked to show the user's own pictures spent four
/// minutes port-scanning localhost and then reading this repository's own source. Fewer tools is a shorter prompt on
/// every turn, and the turns are where the minutes go.
/// </para>
/// <para>
/// But a NAMED list of the panel tools is a trap, and it caught the design step: a tool added to the build's toolset
/// was silently trimmed back out, so the brief told the worker to show its design first and gave it nothing to do
/// that with. No error, no failed run — the panel just appeared the old way. So the panel tools are kept by their
/// prefix, and the named set is only for the things a build BORROWS: a browser, its own workspace, the guides.
/// </para>
/// </remarks>
internal static class PanelBuild
{
    /// <summary>The one call that finishes a state. Everything else it does is preparation for making it.</summary>
    internal static string? Ends(PanelStep? step) => step switch
    {
        PanelStep.Agree => "panel_shows",
        PanelStep.Design => "widget_design",
        PanelStep.Research => "panel_source",
        PanelStep.Prove => "panel_proof",
        PanelStep.Bind => "widget_publish",
        _ => null,
    };

    /// <summary>
    /// Whether this state has any business with a browser.
    /// </summary>
    /// <remarks>
    /// Agreeing what a panel shows and drawing it are decisions about the panel, not about the internet, and a
    /// browser in the room turns both into research: the state that was asked what the panel should show goes and
    /// finds out what an API happens to return, and answers that instead. Binding has the proven response in its
    /// brief already, so a browser there is a way to go and get a different one.
    /// </remarks>
    private static bool Browses(PanelStep step) => step is PanelStep.Research or PanelStep.Prove;

    /// <summary>
    /// Whether this state INVENTS rather than inspects.
    /// </summary>
    /// <remarks>
    /// Reasoning is on for a worker so it reads its tool output properly — notices a blank field, an error page
    /// served with a 200, a number that cannot be right — and recovers or says so, instead of writing down whatever
    /// it was handed. That is worth paying for on every state that looks at something real.
    /// <para>
    /// Agreeing what a panel shows and drawing it are not that. They call no source; they make the values up. There
    /// is no tool output to be sceptical about, so the deliberation buys nothing a person can see — it just runs
    /// for two minutes before the first character of the panel exists. An adjust is the same job as
    /// <see cref="PanelStep.Design"/>, one panel later, and the caller counts it as one.
    /// </para>
    /// </remarks>
    internal static bool Drafts(PanelStep? step) => step is PanelStep.Agree or PanelStep.Design;

    /// <summary>
    /// What an ADJUST keeps: every panel tool, the borrowed set, and no browser.
    /// </summary>
    /// <remarks>
    /// It differs from every build state in one way and it matters: an adjust may finish with any of the panel
    /// tools, because changing a live kind means publishing it. What it must not have is a way to go and find a
    /// different source — it was asked to change how something looks, and the source is already proven.
    /// </remarks>
    internal static bool KeepForAdjust(string name, IReadOnlySet<string> borrowed) =>
        name.StartsWith("widget_", StringComparison.OrdinalIgnoreCase)
        || (borrowed.Contains(name) && !name.StartsWith("chrome_", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Which tools survive, for a whole build (<paramref name="step"/> null) or for one state of one.
    /// </summary>
    /// <remarks>
    /// A state holding another state's tool is a state that can do another state's job badly. The research state
    /// with a publish tool publishes something it never proved; the design state with a browser goes shopping for a
    /// source instead of drawing. So a state keeps its own closing tool and nothing else that belongs to a state.
    /// </remarks>
    internal static bool Keep(string name, IReadOnlySet<string> borrowed, PanelStep? step = null)
    {
        if (step is null)
            return name.StartsWith("widget_", StringComparison.OrdinalIgnoreCase) || borrowed.Contains(name);

        if (string.Equals(name, Ends(step), StringComparison.OrdinalIgnoreCase)) return true;

        // Any other state's closing tool, or a panel tool this state was not given.
        if (name.StartsWith("widget_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("panel_", StringComparison.OrdinalIgnoreCase)) return false;

        if (!borrowed.Contains(name)) return false;

        return Browses(step.Value) || !name.StartsWith("chrome_", StringComparison.OrdinalIgnoreCase);
    }
}
