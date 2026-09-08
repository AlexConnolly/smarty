using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// The library half of the panel tools: putting up a panel from a kind that already exists, and publishing a new
/// kind when one doesn't.
///
/// <para>
/// The distinction that makes the whole thing work. "Track this flight" is not a request for a flight tracker — it
/// is a request for an instance of one, with a flight number in it. Building the specific thing means the next
/// flight costs another four minutes of a worker finding the same feed again; building the KIND means it costs a
/// parameter. So <c>widget_add</c> is the fast path and the default, and <c>widget_build</c> is what happens only
/// when no existing shape fits.
/// </para>
/// </summary>
public static class WidgetLibraryTools
{
    /// <summary>
    /// Put up a panel from a kind in the library. Instant, no worker, no cost.
    /// </summary>
    /// <param name="resolve">
    /// Sends a worker to find a parameter value the user didn't give. "Track my latest eBay listing" names no item
    /// id, and the id is a fact about the world that has to be looked up — so the panel goes up saying it is being
    /// worked out, and a task fills it in. Returns the task id.
    /// </param>
    public static AgentTool AddTool(WidgetStore widgets, WidgetLibrary library, Func<string?> currentSession,
        Func<Widget, WidgetKind, IReadOnlyList<WidgetParam>, string, Task<string>> resolve) => new(
        "widget_add",
        "Put a panel on the user's home page from a kind already in the library. ALWAYS prefer this over " +
        "widget_build: it is instant, where a build is minutes of a worker's time. The library and each kind's " +
        "parameters are listed in your context.\n" +
        "A different flight, listing, city or ticker is the SAME kind with a different parameter — not a new kind.\n" +
        "If you don't have a parameter's value and the user didn't give it (\"track my latest eBay listing\" names " +
        "no item number), pass what you do have and describe the rest in `find`. A worker will go and establish it " +
        "and the panel fills in — don't ask the user for something that can be looked up.",
        new[]
        {
            ToolParameter.String("kind", "The kind's name, exactly as listed in the library.", required: true),
            ToolParameter.String("title", "What to call this panel — \"BA2490 to Palma\", \"Kingfishr LP\".", required: true),
            ToolParameter.FromSchema("params",
                "{\"type\":\"array\",\"description\":\"The kind's parameters and their values.\",\"items\":{" +
                "\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"value\":{\"type\":\"string\"}}," +
                "\"required\":[\"name\",\"value\"]}}"),
            ToolParameter.String("find",
                "What still has to be established, when a parameter's value isn't known — \"the item number of " +
                "the user's most recent eBay listing\". A worker does it in the background.", required: false),
            ToolParameter.String("size",
                "kpi (2×1 — one number and a label), tall (2×2 — a few rows or a small chart), or wide (4×2 — a " +
                "chart or a table). Omit to use the kind's own default.", required: false),
            new ToolParameter("priority", "integer", "0-100, how near the top. Something just asked about is 80+.", required: false),
        },
        async (args, _) =>
        {
            var kindName = args.GetStringOrNull("kind")?.Trim();
            if (library.Get(kindName) is not { } kind)
                return ToolOutput.DeadEnd(
                    $"There's no kind \"{kindName}\" in the library. The ones there are are listed in your " +
                    "context; if none of them fit, use widget_build.");

            var title = args.GetStringOrNull("title")?.Trim();
            if (string.IsNullOrWhiteSpace(title)) return ToolOutput.Error("The panel needs a title.");

            var values = ReadParams(args);
            var missing = kind.Missing(values);
            var find = args.GetStringOrNull("find")?.Trim();

            // Refused rather than put up half-working: a feed url with an unsubstituted {itemId} in it fetches
            // nothing, and the panel would sit there looking built and showing an error nobody can act on.
            if (missing.Count > 0 && string.IsNullOrWhiteSpace(find))
                return ToolOutput.Error(
                    $"\"{kind.Name}\" still needs {string.Join(", ", missing.Select(p => $"{p.Name} ({p.Description})"))}. " +
                    "Give the value, or say in `find` what has to be established and a worker will go and get it.");

            var panel = widgets.Reserve(title!, args.GetStringOrNull("size") ?? kind.DefaultSize,
                args.GetIntOrNull("priority") ?? 75, null, currentSession(), proposed: false);

            if (missing.Count > 0)
            {
                var taskId = await resolve(panel, kind, missing, find!).ConfigureAwait(false);
                return ToolOutput.Ok(
                    $"\"{panel.Title}\" is on the home page, working out " +
                    $"{string.Join(" and ", missing.Select(p => p.Name))} (task {taskId}). Tell the user in one " +
                    "line that it's on its way; don't wait for it.");
            }

            widgets.Attach(panel.Id, kind, values);
            library.Used(kind.Name);
            return ToolOutput.Ok(
                $"\"{panel.Title}\" is live on the home page now, from the {kind.Name} kind — no build needed. " +
                "Tell the user in one line what you put there.");
        })
    {
        Repeatable = true,
    };

    /// <summary>
    /// The builder's tool: publish a KIND as its three parts, and light up the panel that asked for it.
    ///
    /// <para>
    /// The three arguments that matter are the three parts, in order: the data model, the loader that produces it,
    /// and the component that renders it. Keeping them separate is what makes each independently fixable — a source
    /// that moves its fields is a loader fault, not a rendering one.
    /// </para>
    /// </summary>
    /// <param name="internalFault">
    /// Whether an internal feed name points at something that EXISTS. Null where nothing can be asked.
    /// </param>
    /// <param name="prove">
    /// Runs the finished kind's loader for real. The gate that was missing: every other check here reads the
    /// DECLARATION — model against map, holes against parameters, a name against its spelling — and all of them pass
    /// for a loader that fetches nothing. A browser-mode weather panel was published against a page that returned
    /// nothing at all, and it kept returning nothing every five minutes for nine hours; the feeds beside this already
    /// refuse a source that produces nothing at publish, for exactly this reason, and there was never an argument for
    /// panels being different. Null where there is nothing to run it with, which leaves publishing as it was.
    /// </param>
    public static AgentTool PublishTool(WidgetStore widgets, WidgetLibrary library,
        Func<string, string?>? internalFault = null,
        Func<WidgetKind, IReadOnlyDictionary<string, string>, CancellationToken, Task<LoadResult>>? prove = null) => new(
        "widget_publish",
        "Publish the panel kind you were asked to build, as its three parts, and light up the panel that asked for " +
        "it. What you publish is a REUSABLE KIND: the loader carries {parameters} rather than this request's " +
        "particular values, so the next flight or listing costs nothing.",
        new[]
        {
            ToolParameter.String("id", "The panel's id, from your brief.", required: true),
            ToolParameter.String("kind",
                "A short generic slug for this SHAPE of panel: flight-status, ebay-listing, city-weather. Never the " +
                "particular thing — not ba2490, not palma-weather.", required: true),
            ToolParameter.String("title", "The kind, for a person: \"Flight status\".", required: true),
            ToolParameter.String("description",
                "What this kind shows, generically, in the words someone would use asking for it. This is what gets " +
                "searched next time, so write it to be found.", required: true),

            ToolParameter.FromSchema("params",
                "{\"type\":\"array\",\"description\":\"The parameters the kind takes, and the values for THIS panel.\"," +
                "\"items\":{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}," +
                "\"description\":{\"type\":\"string\"},\"example\":{\"type\":\"string\"}," +
                "\"value\":{\"type\":\"string\"}},\"required\":[\"name\",\"description\",\"value\"]}}"),

            ToolParameter.FromSchema("track",
                "{\"type\":\"array\",\"description\":\"Field paths in the loaded data whose NUMBERS are worth " +
                "remembering over time, so this kind of panel can show change rather than only the current value " +
                "(e.g. [\\\"views\\\",\\\"watchers\\\",\\\"price\\\"]). Every reading is kept, so the " +
                "component gets `history` with the series already differenced. Track what someone would want a trend " +
                "of; leave empty for a readout nobody wants a graph of, like a weather forecast.\"," +
                "\"items\":{\"type\":\"string\"}}"),

            // PART ONE
            ToolParameter.FromSchema("model",
                "{\"type\":\"array\",\"description\":\"PART ONE — the data model. The fields the component is handed, " +
                "already shaped. Name them for what they MEAN, not for what the source calls them.\"," +
                "\"items\":{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}," +
                "\"type\":{\"type\":\"string\",\"enum\":[\"string\",\"number\",\"boolean\",\"list\"]}," +
                "\"description\":{\"type\":\"string\"},\"example\":{\"type\":\"string\"}," +
                "\"required\":{\"type\":\"boolean\"}},\"required\":[\"name\",\"type\",\"description\"]}}"),

            // PART TWO
            ToolParameter.String("loader_mode",
                "PART TWO — how the data is loaded. \"http\" for a plain server-side GET of a JSON endpoint (the " +
                "cheapest thing that works, and the default). \"browser\" loads the page in the USER'S OWN Chrome: " +
                "use it for a site that refuses a server request, and for anything only visible when signed in as " +
                "them — their own eBay listing, their own orders. \"internal\" reads what we already hold.", required: true),
            ToolParameter.String("url", "For http and browser: the address, with {parameterName} where a value goes.", required: false),
            ToolParameter.String("internal", "For internal: agenda, lists, schedule, tasks or projects.", required: false),
            ToolParameter.String("expression",
                "For browser ONLY: a JavaScript expression evaluated IN THE PAGE that returns the data model as an " +
                "object — e.g. JSON.stringify({price: …, sold: …}). The browser is already a JavaScript engine, so " +
                "this is where the shaping happens and no mapping is needed. Read the DOM, or the page's own JSON-LD " +
                "block, or any global its front end has already loaded.", required: false),
            ToolParameter.FromSchema("map",
                "{\"type\":\"array\",\"description\":\"For http and internal: how the source's response becomes the " +
                "model. One entry per field, with a dotted path — offers.price, hourly.temperature_2m.0, or " +
                "results.*.number to take a field from every element. Omit entirely if the response IS already the " +
                "model.\",\"items\":{\"type\":\"object\",\"properties\":{\"field\":{\"type\":\"string\"}," +
                "\"path\":{\"type\":\"string\"}},\"required\":[\"field\",\"path\"]}}"),
            ToolParameter.FromSchema("headers",
                "{\"type\":\"array\",\"description\":\"Headers an http load needs, if any.\",\"items\":{" +
                "\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"value\":{\"type\":\"string\"}}," +
                "\"required\":[\"name\",\"value\"]}}"),

            ToolParameter.FromSchema("more",
                "{\"type\":\"array\",\"description\":\"FURTHER FEEDS, only when one source genuinely cannot " +
                "answer the whole question. Plenty of sources split what HAPPENED from what is COMING across two " +
                "calls: ESPN returns a team's finished match at /schedule and its upcoming fixtures at the same url " +
                "with ?fixture=true, and nothing returns both. Each feed maps only the fields IT supplies, they " +
                "merge into one object, and the model is checked once at the end — so a feed that fails only breaks " +
                "the panel if what it carried was required. Do NOT reach for this to avoid writing one good " +
                "loader.\",\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"url\":{\"type\":\"string\",\"description\":\"the address, with {parameterName} holes\"}," +
                "\"mode\":{\"type\":\"string\",\"description\":\"http (default), browser or internal\"}," +
                "\"internal\":{\"type\":\"string\"},\"expression\":{\"type\":\"string\"}," +
                "\"map\":{\"type\":\"array\",\"description\":\"which model fields this feed supplies, and " +
                "where they live in ITS response\",\"items\":{\"type\":\"object\",\"properties\":{" +
                "\"field\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"}}," +
                "\"required\":[\"field\",\"path\"]}}},\"required\":[\"map\"]}}"),

            // PART THREE
            ToolParameter.String("code",
                "PART THREE — the component body, JSX ending in a return. It is handed `data` in exactly the shape " +
                "declared in `model`, so read the fields by their model names and nothing else.", required: true),

            ToolParameter.String("refresh", "Default cadence: \"every 5 minutes\", \"every 2 hours\", \"daily at 06:00\".", required: true),
        },
        (args, ct) =>
        {
            var id = args.GetStringOrNull("id")?.Trim();
            if (string.IsNullOrWhiteSpace(id) || widgets.Get(id!) is not { } panel)
                return Task.FromResult(ToolOutput.DeadEnd(
                    $"There's no panel \"{id}\". Use the id from your brief exactly."));

            var kindName = args.GetStringOrNull("kind")?.Trim();
            if (string.IsNullOrWhiteSpace(kindName))
                return Task.FromResult(ToolOutput.Error("Give the kind a short generic slug — flight-status, ebay-listing."));

            var description = args.GetStringOrNull("description")?.Trim();
            if (string.IsNullOrWhiteSpace(description))
                return Task.FromResult(ToolOutput.Error(
                    "The kind needs a description — it is what gets searched when the next request arrives, and " +
                    "without one this kind will never be found and the work will be done again."));

            // PART TWO first, because it decides whether PART ONE applies at all.
            var mode = (args.GetStringOrNull("loader_mode") ?? "").Trim().ToLowerInvariant();
            if (!LoaderModes.Known(mode))
                return Task.FromResult(ToolOutput.Error(
                    $"\"{mode}\" isn't a loader mode. Use one of: {string.Join(", ", LoaderModes.All)}."));

            bool clientMode = mode == LoaderModes.Client;

            // PART ONE. Not in client mode: the server produces nothing, so there is nothing to declare a shape for
            // — the component is handed its parameters and fetches or embeds for itself.
            var model = ReadModel(args);
            if (!clientMode && model.Count == 0)
                return Task.FromResult(ToolOutput.Error(
                    "Declare the data model: the fields the component is handed. It is the contract between the " +
                    "loader and the component, and without it a source changing shape becomes a rendering bug."));

            // A field name the component cannot read is the worst kind of fault: everything works. The loader fetches,
            // the shaping succeeds, the panel goes live, every check reports healthy — and a weather panel shows "—°"
            // because it declared "current.temp" and the component's `data.current.temp` looks inside `current` for a
            // `temp` that is not there. The name is a PROPERTY, so it has to be readable as one.
            var unreadable = model.Select(f => f.Name)
                .Where(name => !IsReadable(name))
                .ToList();
            if (unreadable.Count > 0)
                return Task.FromResult(ToolOutput.Error(
                    $"{string.Join(", ", unreadable)} {(unreadable.Count == 1 ? "isn't" : "aren't")} usable as field " +
                    "name(s): the component reads them as properties, so a dot, a space or a dash makes it look " +
                    "INSIDE something instead. \"current.temp\" becomes data.current.temp, which reads a temp that " +
                    "isn't there and renders nothing, with no error anywhere. Name them flat — temp, high, low — and " +
                    "map each one to wherever it lives in the source."));

            var url = args.GetStringOrNull("url")?.Trim();
            var expression = args.GetStringOrNull("expression")?.Trim();
            var internalFeed = args.GetStringOrNull("internal")?.Trim();

            if (mode is LoaderModes.Http or LoaderModes.Browser && string.IsNullOrWhiteSpace(url))
                return Task.FromResult(ToolOutput.Error($"A {mode} loader needs a url."));
            if (mode == LoaderModes.Browser && string.IsNullOrWhiteSpace(expression))
                return Task.FromResult(ToolOutput.Error(
                    "A browser loader needs an expression — the JavaScript that reads the page and returns the " +
                    "data model. That is where the shaping happens in browser mode."));
            // Spelled like a feed, AND naming one that is actually there. The second half is what was missing: a
            // panel was published against "source:any" on a machine with no granted folders, and the only thing that
            // ever noticed was the load failing every five minutes from then on.
            if (mode == LoaderModes.Internal)
            {
                if (!WidgetInternals.Known(internalFeed))
                    return Task.FromResult(ToolOutput.Error(
                        $"An internal loader needs one of: {WidgetInternals.Options}."));
                if (internalFault?.Invoke(internalFeed!) is { Length: > 0 } noSuchFeed)
                    return Task.FromResult(ToolOutput.Error(noSuchFeed));
            }

            if (url is { Length: > 0 })
            {
                // Checked with the holes stripped, because the template is what gets stored.
                var probe = url.Replace("{", "").Replace("}", "");
                if (!(Uri.TryCreate(probe, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https"))
                    return Task.FromResult(ToolOutput.Error($"\"{url}\" isn't an http(s) url."));
            }

            var refresh = (args.GetStringOrNull("refresh") ?? "").Trim();
            if (refresh.Length > 0 && !ScheduleStore.TryParseRepeat(refresh, DateTimeOffset.Now, out var _n))
                return Task.FromResult(ToolOutput.Error(
                    $"Couldn't read the refresh \"{refresh}\". Use \"every N minutes/hours\", \"hourly\", " +
                    "\"daily at 07:00\"."));

            // PART THREE
            var code = args.GetStringOrNull("code");
            if (string.IsNullOrWhiteSpace(code))
                return Task.FromResult(ToolOutput.Error("The kind needs its component code."));
            if (WidgetTools.Rejected(code!, clientMode) is { } complaint)
                return Task.FromResult(ToolOutput.Error(complaint));

            var loader = new WidgetLoader
            {
                Mode = mode,
                Url = string.IsNullOrWhiteSpace(url) ? null : url,
                Internal = WidgetInternals.Store(internalFeed),
                Expression = string.IsNullOrWhiteSpace(expression) ? null : expression,
                Headers = ReadPairs(args, "headers", "name", "value"),
                Map = ReadPairs(args, "map", "field", "path"),
            };

            // A PANEL THAT CAN NEVER CHANGE, refused rather than published looking correct.
            //
            // Client mode says the component fetches for itself in the page. A component that declares a model and
            // then fetches nothing has no way to be handed one, so every value it renders comes from its stored
            // parameters — which, at the end of a build, are the example values the design invented. The result
            // passes every check there is: it renders, it looks right, it photographs well, and it shows the same
            // battery percentage until somebody notices months later that it has never once moved.
            //
            // That is what happened here, and it happened as a FALLBACK: the feed this panel wanted could not be
            // named (see WidgetInternals.Known), so the build put the numbers where it could. Both halves are
            // fixed; this is the half that refuses to ship the picture.
            if (model.Count > 0 && !CanFill(code!, clientMode))
                return Task.FromResult(ToolOutput.Error(
                    $"This declares a data model ({string.Join(", ", model.Select(f => f.Name))}) but nothing ever " +
                    "fills it: the loader is \"client\", which means the COMPONENT fetches, and the component never " +
                    "calls fetch. Every value would come from the stored parameters and never change again. Either " +
                    $"give it a loader that fetches \u2014 {WidgetInternals.Options} \u2014 or, if the panel really is " +
                    "static, declare no model and take the values as parameters."));

            var more = ReadMore(args);

            // A further feed with no map cannot be merged: nothing says which of the model's fields are its. And a
            // field mapped by nobody is a field the component renders as a dash.
            if (more.FirstOrDefault(f => f.Map.Count == 0) is not null)
                return Task.FromResult(ToolOutput.Error(
                    "Every feed in \"more\" needs its own map — which model fields it supplies, and where they are " +
                    "in that feed's response. Without one there is no way to merge it with the others."));

            // A further feed is a feed, so it is held to the same test. A broken one here is worse than a broken
            // primary, not better: it fails quietly and takes only the fields it was carrying with it.
            foreach (var extra in more.Where(f => f.Mode == LoaderModes.Internal))
                if (internalFault?.Invoke(extra.Internal ?? "") is { Length: > 0 } noSuchExtra)
                    return Task.FromResult(ToolOutput.Error(noSuchExtra));

            var (declared, values) = ReadKindParams(args);

            // A hole with no parameter behind it can never be filled, so the loader can never work. Caught here
            // because the symptom — a url with a literal {itemId} in it — appears minutes later as a 404.
            var holes = loader.Holes()
                .Where(h => !declared.Any(pp => string.Equals(pp.Name, h, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (holes.Count > 0)
                return Task.FromResult(ToolOutput.Error(
                    $"The loader has {{{string.Join("}, {", holes)}}} in it but you haven't declared " +
                    $"{(holes.Count == 1 ? "that parameter" : "those parameters")}. Every hole needs a param with a " +
                    "name, a description and this panel's value."));

            // A mapping for a field that isn't in the model produces a value nothing renders; a required field with
            // no mapping produces a panel showing dashes. Both are cheap to catch and expensive to find later.
            var unknown = clientMode ? new List<string>() : loader.Map.Keys
                .Where(f => !model.Any(m => string.Equals(m.Name, f, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (unknown.Count > 0)
                return Task.FromResult(ToolOutput.Error(
                    $"The map has {string.Join(", ", unknown)}, which {(unknown.Count == 1 ? "isn't" : "aren't")} " +
                    "in the model. Map only declared fields, or declare them."));

            if (!clientMode && loader.Map.Count > 0)
            {
                // A DOTTED FIELD NAME CANNOT BE READ, so it is refused here rather than explained in the brief.
                //
                // The component writes data.homeTeam. A field declared "team.name" is a property whose name
                // contains a dot — `data.team.name` looks for `team` and finds nothing — so it renders as
                // nothing at all, with no throw, no failed load and no fault anywhere. The brief warned about
                // it in prose, which costs tokens on every build forever and still relies on being read; this
                // costs nothing until somebody does it.
                if (model.Select(f => f.Name).Where(n => n.Contains('.')).ToList() is { Count: > 0 } dotted)
                    return Task.FromResult(ToolOutput.Error(
                        $"{string.Join(", ", dotted)} can't be a field name — the component reads data.<name>, so a " +
                        "dot in it silently reads as nothing. Flatten the name (team.name becomes teamName) and " +
                        "put the dotted path in the loader's map, which is what paths are for."));

                var unmapped = model
                    .Where(f => f.Required && !loader.Map.ContainsKey(f.Name))
                    .Select(f => f.Name)
                    .ToList();
                if (unmapped.Count > 0)
                    return Task.FromResult(ToolOutput.Error(
                        $"The model declares {string.Join(", ", unmapped)} but the map doesn't say where " +
                        $"{(unmapped.Count == 1 ? "it comes" : "they come")} from. Map every required field."));
            }

            // A kind that is already in use cannot be RESHAPED — only fixed.
            //
            // Publishing over a live kind with different parameters orphans every panel made from it: their stored
            // values no longer fill the url the kind now expects, and they fail on a timer for ever. So a genuinely
            // different shape has to be a genuinely different kind, and the builder is told to pick another slug.
            // Rewriting the COMPONENT of a live kind stays allowed — that is a repair, and it mends every instance
            // at once, which is the whole reason a library exists.
            var proposed = new WidgetKind { Name = kindName!, Params = declared };
            if (library.ShapeChange(proposed) is { Count: > 0 } changed)
            {
                // Only where the harm is real: a parameter orphans an instance by no longer filling the url. A
                // stored kind whose loader has no holes in it consumes its parameters nowhere, so changing them
                // costs nothing — and refusing there blocks the one repair that matters, because a panel frozen
                // on its parameters is repaired by TAKING THEM AWAY and giving it a feed instead. The component
                // is replaced in this same call either way.
                var consumed = library.Get(kindName!)?.Loader.Holes().Any() == true;
                var instances = consumed ? widgets.Instances(kindName!).Count : 0;
                if (instances > 0)
                {
                    // Say what the existing parameters ARE, not just which ones differ.
                    //
                    // "Keep its existing parameters exactly" is unactionable when nothing has said what they are,
                    // and the commonest case is the worst one: a kind that takes NONE, where "keep them" reads as
                    // keep something invisible. A restyle of a parameterless panel was refused twice over exactly
                    // this, and the worker gave up and asked the user what the stored parameters were — there were
                    // none, and it had no way to find that out.
                    var existing = library.Get(kindName!)?.Params.Select(p => p.Name).ToList() ?? new List<string>();
                    var keep = existing.Count == 0
                        ? "It takes NO parameters — publish it with none"
                        : $"It takes exactly: {string.Join(", ", existing)} — publish it with those";

                    return Task.FromResult(ToolOutput.DeadEnd(
                        $"\"{kindName}\" already exists and {instances} panel(s) are using it. {keep}, and change " +
                        $"only the component and the loader details. ({string.Join(", ", changed)} differ from what " +
                        "is stored, and republishing with different parameters would orphan every panel made from " +
                        $"it.) If this is genuinely a different shape, publish it under a new slug instead."));
                }
            }

            // Nothing is written yet. Everything above read the DECLARATION; the loader has still never been run,
            // and until it has, this is a description of a panel rather than a panel.
            return ProveThenPublishAsync(widgets, library, panel, new WidgetKind
            {
                Name = kindName!,
                Title = args.GetStringOrNull("title")?.Trim() ?? kindName!,
                Description = description!,
                Params = declared,
                Model = model,
                Loader = loader,
                More = more,
                Code = code!,
                Track = ReadTrack(args),
                Refresh = refresh,
                DefaultSize = panel.Size,
                LearnedFrom = panel.Id,
            }, values, clientMode ? null : prove, ct);
        });

    /// <summary>
    /// Run a loader and show what it actually returns, without publishing anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate refuses a loader that does not work, which is right, and on its own it is only half a capability. An
    /// ADJUST is handed six tools — the file tools, the designer and publish — and not one of them can call a source.
    /// So a worker whose loader is wrong has no way to find out what is right: it can only guess, publish, read the
    /// refusal, and guess again. The vacuum took three attempts that way; the weather panel reached for a shell tool
    /// that does not exist, because fetching a url was the obvious thing to do and there was nothing to do it with.
    /// </para>
    /// <para>
    /// This is deliberately NOT the thing an adjust is kept away from. It cannot search, open a page, or go shopping
    /// for a different source — it runs the loader it is given, by exactly the code path the panel will use, and
    /// prints the result. So "the source is already proven" stops being an assumption an adjust has to take on trust
    /// and becomes something it can check in one call.
    /// </para>
    /// <para>
    /// The arguments are the same as <c>widget_publish</c>'s loader half on purpose: try it, then publish the
    /// identical thing. What this prints is what publish will judge.
    /// </para>
    /// </remarks>
    public static AgentTool TryTool(WidgetStore widgets,
        Func<WidgetKind, IReadOnlyDictionary<string, string>, CancellationToken, Task<LoadResult>> prove,
        Func<string, string?>? internalFault = null) => new(
        "widget_try",
        "Run a loader and see exactly what comes back, WITHOUT publishing. Use it before widget_publish whenever " +
        "you are changing how a panel gets its data, or when a publish was refused because the loader didn't work — " +
        "it is the same fetch, judged the same way, so what this prints is what publish will accept or reject.\n" +
        "Takes the same loader arguments as widget_publish. Pass the model too and it will tell you whether the " +
        "response actually fills it, which is the check that refuses a publish.\n" +
        "Nothing is saved and nothing is shown to the user, so it costs nothing to call twice.",
        new[]
        {
            ToolParameter.String("id", "The panel's id, from your brief — for its parameter values.", required: true),
            ToolParameter.String("loader_mode", "http, browser or internal.", required: true),
            ToolParameter.String("url", "For http and browser.", required: false),
            ToolParameter.String("internal", "For internal.", required: false),
            ToolParameter.String("expression", "For browser: the JavaScript that reads the page.", required: false),
            ToolParameter.FromSchema("map",
                "{\"type\":\"array\",\"description\":\"How the response becomes the model, as field/path pairs.\"," +
                "\"items\":{\"type\":\"object\",\"properties\":{\"field\":{\"type\":\"string\"}," +
                "\"path\":{\"type\":\"string\"}},\"required\":[\"field\",\"path\"]}}"),
            ToolParameter.FromSchema("headers",
                "{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}," +
                "\"value\":{\"type\":\"string\"}},\"required\":[\"name\",\"value\"]}}"),
            ToolParameter.FromSchema("model",
                "{\"type\":\"array\",\"description\":\"The data model to judge the response against. Optional, but " +
                "without it this only shows what came back and cannot tell you whether it is enough.\",\"items\":{" +
                "\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"type\":{\"type\":\"string\"}," +
                "\"description\":{\"type\":\"string\"},\"required\":{\"type\":\"boolean\"}},\"required\":[\"name\"]}}"),
            ToolParameter.FromSchema("params",
                "{\"type\":\"array\",\"description\":\"Parameter values to use instead of the panel's stored ones.\"," +
                "\"items\":{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}," +
                "\"value\":{\"type\":\"string\"}},\"required\":[\"name\",\"value\"]}}"),
        },
        async (args, ct) =>
        {
            if (widgets.Get(args.GetStringOrNull("id")?.Trim()) is not { } panel)
                return ToolOutput.Error("That isn't a panel id — use the one from your brief.");

            var mode = (args.GetStringOrNull("loader_mode") ?? "").Trim().ToLowerInvariant();
            if (!LoaderModes.Known(mode))
                return ToolOutput.Error($"\"{mode}\" isn't a loader mode. Use one of: {string.Join(", ", LoaderModes.All)}.");
            if (mode == LoaderModes.Client)
                return ToolOutput.Error(
                    "A client loader fetches in the page, so there is nothing here to run. Check it in the browser.");

            var internalFeed = args.GetStringOrNull("internal")?.Trim();
            if (mode == LoaderModes.Internal)
            {
                if (!WidgetInternals.Known(internalFeed))
                    return ToolOutput.Error($"An internal loader needs one of: {WidgetInternals.Options}.");
                // Answered here as well as at publish, because the whole point is to find this out cheaply.
                if (internalFault?.Invoke(internalFeed!) is { Length: > 0 } noSuchFeed)
                    return ToolOutput.Error(noSuchFeed);
            }

            var url = args.GetStringOrNull("url")?.Trim();
            var expression = args.GetStringOrNull("expression")?.Trim();
            var model = ReadModel(args);

            // The panel's own values, so {holes} fill exactly as they will in service. Overridable for a build that
            // has not settled its parameters yet.
            var values = new Dictionary<string, string>(panel.Params, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in ReadKindParams(args).Values) values[name] = value;

            var kind = new WidgetKind
            {
                Name = panel.Kind is { Length: > 0 } k ? k : "trial",
                Model = model,
                Code = null!,
                Loader = new WidgetLoader
                {
                    Mode = mode,
                    Url = string.IsNullOrWhiteSpace(url) ? null : url,
                    Internal = WidgetInternals.Store(internalFeed),
                    Expression = string.IsNullOrWhiteSpace(expression) ? null : expression,
                    Headers = ReadPairs(args, "headers", "name", "value"),
                    Map = ReadPairs(args, "map", "field", "path"),
                },
                More = ReadMore(args),
            };

            LoadResult result;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(60));
                result = await prove(kind, values, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ToolOutput.Error("The loader didn't answer within a minute. A source that slow is a source " +
                                        "that times out on every refresh — find a faster one.");
            }
            catch (Exception ex) { return ToolOutput.Error($"Running it threw: {ex.Message}"); }

            if (result.Error is { Length: > 0 } wrong)
                // Not a tool failure: the answer to the question asked. Said as what happened, so the next attempt
                // changes the loader rather than retrying this call.
                return ToolOutput.Ok(
                    $"That loader DOESN'T work, and publish would refuse it for this reason:\n{wrong}\n\n" +
                    "Change the url, the mode or the map and try again. Nothing has been saved.");

            var json = result.Json ?? "";
            return ToolOutput.Ok(
                "That loader works. This is what it returns, and what your component will be handed as `data`:\n\n" +
                (json.Length > 2000 ? json[..2000] + "\n…(truncated)" : json) + "\n\n" +
                (model.Count > 0
                    ? "It fills the model you gave, so publish will accept it. Publish the SAME loader."
                    : "No model was given, so nothing has checked whether this is enough — pass `model` to have " +
                      "that answered, since it is what publish will test."));
        })
    {
        Repeatable = true,
    };

    /// <summary>
    /// Run the loader, and only publish what answered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole of the gate. A kind is built in memory, fetched with for real, and written to the library only if
    /// something came back that fills the model — which is the one question none of the static checks above can
    /// answer, and the one that mattered: the map can be perfectly consistent with a model and point at a page that
    /// serves nothing.
    /// </para>
    /// <para>
    /// The reading is KEPT rather than thrown away. Proving a panel and then leaving it blank until the next tick
    /// would pay for the fetch twice and show the user a panel that is live and empty for five minutes — which is
    /// indistinguishable, on a glance at a home page, from the failure this is here to prevent.
    /// </para>
    /// <para>
    /// Client mode is exempt and is passed a null prover by the caller: the component fetches for itself, in the
    /// page, so there is nothing here that could run it. That hole is covered by the check that refuses a client-mode
    /// panel which declares a model and never calls fetch.
    /// </para>
    /// </remarks>
    private static async Task<ToolOutput> ProveThenPublishAsync(
        WidgetStore widgets, WidgetLibrary library, Widget panel, WidgetKind candidate,
        IReadOnlyDictionary<string, string> values,
        Func<WidgetKind, IReadOnlyDictionary<string, string>, CancellationToken, Task<LoadResult>>? prove,
        CancellationToken ct)
    {
        LoadResult? proven = null;
        if (prove is not null)
        {
            try
            {
                // Bounded, because a source that hangs must not hang the build with it — a refusal a worker can act
                // on beats a tool call that never comes back.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(60));
                proven = await prove(candidate, values, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ToolOutput.Error(
                    "The loader didn't answer within a minute, so this panel can't be published yet. A source that " +
                    "slow on a build is a source that times out on every refresh afterwards — find a faster one, or " +
                    "a plain JSON endpoint instead of a page.");
            }
            catch (Exception ex)
            {
                return ToolOutput.Error($"Running this panel's loader threw: {ex.Message}");
            }

            if (proven.Error is { Length: > 0 } wrong)
                // The refusal names the loader, because that is the part at fault and the part to change. Said as
                // the thing that actually happened rather than as "validation failed", so it can be acted on.
                return ToolOutput.Error(
                    $"This panel isn't published, because its loader doesn't work: {wrong}\n" +
                    "That is what would have happened on the home page every refresh, silently. Go and look at what " +
                    "the source really returns, fix the loader — the url, the mode or the map — and publish again. " +
                    "Do not publish this until the loader answers.");
        }

        var kind = library.Upsert(candidate);
        widgets.Attach(panel.Id, kind, values);
        library.Used(kind.Name);

        // What the proof produced, so the panel is right the moment it appears rather than at the next tick.
        if (proven?.Json is { Length: > 0 } first)
            widgets.Loaded(panel.Id, first, null, kind.Track);

        // Siblings take the revision too. Fixing the flight kind and leaving the other flight panels on the old
        // broken component is the opposite of the point of having a library.
        var also = widgets.Requalify(kind) - 1;
        return ToolOutput.Ok(
            $"Published \"{kind.Name}\" to the library and \"{panel.Title}\" is live" +
            (also > 0 ? $"; {also} other panel(s) of this kind picked up the new version" : "") +
            (proven?.Json is { Length: > 0 } ? " with its first reading already in it" : "") +
            ". You're done — say in one line what you put there.");
    }

    /// <summary>
    /// Put the design on the page before there is anything real to put in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A build takes minutes, and for all of them the panel was a thin grey line and the word "Building". Which is
    /// accurate and tells the user nothing: they cannot see what is coming, cannot tell a build that is going well
    /// from one that is lost, and have nothing to react to until it is finished and too late to say "not like that".
    /// </para>
    /// <para>
    /// So the component and a handful of made-up values go up FIRST, dimmed, with what is happening written over them.
    /// The panel is watchable while it is made. And the second effect is the one that saves the time: a component that
    /// cannot render is caught in the first minute against invented data, rather than in the last minute against real
    /// data, after the source has already been found.
    /// </para>
    /// </remarks>
    public static AgentTool DesignTool(WidgetStore widgets) => new(
        "widget_design",
        "Show the user your design, before you go looking for a source. Write the component and a few made-up " +
        "values in the shape you intend it to receive, and the panel appears on the home page rendering them — " +
        "dimmed, with what you are doing written over it — while you build the rest.\n" +
        "This is the FIRST thing to do, not the last: the invented data proves the component renders while it is " +
        "still cheap to change, and the user can see what is being made for them.",
        new[]
        {
            ToolParameter.String("id", "The panel's id, from your brief.", required: true),
            ToolParameter.String("code",
                "The component body, JSX ending in a return — the same contract as widget_publish. It reads `data` " +
                "in the shape of the sample below.", required: true),
            ToolParameter.FromSchema("sample",
                "{\"type\":\"object\",\"description\":\"Made-up data in exactly the shape you intend the " +
                "component to be handed — the same object widget_publish will declare as the model. Plausible " +
                "values, not placeholders: the design is being SHOWN, so a real airport and a real temperature tell " +
                "the user what this panel will be, where the word string and a zero tell them nothing.\"}"),
            ToolParameter.String("mode",
                "Only if the component fetches or embeds for itself in the page: pass \"client\". Then there is no " +
                "sample to give — it has no `data`.", required: false),
        },
        (args, _) =>
        {
            var id = args.GetStringOrNull("id")?.Trim();
            if (string.IsNullOrWhiteSpace(id) || widgets.Get(id!) is not { } panel)
                return Task.FromResult(ToolOutput.DeadEnd(
                    $"There's no panel \"{id}\". Use the id from your brief exactly."));

            var code = args.GetStringOrNull("code");
            if (string.IsNullOrWhiteSpace(code))
                return Task.FromResult(ToolOutput.Error("Nothing to show — the design is the component code."));

            string? sample = null;
            if (args.Raw.TryGetProperty("sample", out var raw) && raw.ValueKind == JsonValueKind.Object)
                sample = raw.GetRawText();

            var clientMode = string.Equals(args.GetStringOrNull("mode")?.Trim(), LoaderModes.Client,
                                 StringComparison.OrdinalIgnoreCase)
                             || sample is null;

            // The same contract publishing enforces, applied now rather than in eight minutes' time. This is most of
            // the value of designing first: an unknown Tailwind class or a stray fetch costs a fix here, and a
            // rebuild there.
            if (WidgetTools.Rejected(code!, clientMode) is { } complaint)
                return Task.FromResult(ToolOutput.Error(complaint));

            if (sample is null && !clientMode)
                return Task.FromResult(ToolOutput.Error(
                    "Give the sample data too — a component with nothing in it shows the user an empty box, which is " +
                    "what they were already looking at."));

            if (!widgets.Designed(panel.Id, code!, sample))
                return Task.FromResult(ToolOutput.DeadEnd($"Couldn't show \"{id}\" — it isn't there any more."));

            return Task.FromResult(ToolOutput.Ok(
                $"\"{panel.Title}\" is on the home page now, showing your design against that sample and saying it " +
                "is building its data feed. It is NOT finished — the numbers on it are invented. Go and find the " +
                "real source, prove it, and call widget_publish with the same component (fixed if the design showed " +
                "you something) and the model matching your sample."));
        });

    /// <summary>
    /// Can a component read this name off <c>data</c> as a property?
    /// </summary>
    /// <remarks>
    /// Letters, digits and underscores, not starting with a digit — which is what JSX can write as
    /// <c>data.something</c>. Anything else has to be reached with brackets, and no generated component ever does, so
    /// the value silently never appears.
    /// </remarks>
    private static bool IsReadable(string? name) =>
        name is { Length: > 0 }
        && !char.IsDigit(name[0])
        && name.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>
    /// The field paths whose numbers this kind remembers.
    ///
    /// <para>
    /// Only meaningful for a mode that loads server-side: a client-mode panel fetches in the page and the server
    /// never sees the numbers, so nothing can accumulate. Kept anyway rather than rejected, because a kind that later
    /// moves to an http loader should not silently lose the declaration.
    /// </para>
    /// </summary>
    private static List<string> ReadTrack(ToolCallArguments args)
    {
        var track = new List<string>();
        if (!args.Raw.TryGetProperty("track", out var items) || items.ValueKind != JsonValueKind.Array) return track;

        foreach (var item in items.EnumerateArray())
        {
            var path = item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null;
            if (!string.IsNullOrWhiteSpace(path) && !track.Contains(path!, StringComparer.OrdinalIgnoreCase))
                track.Add(path!);
        }
        return track;
    }

    /// <summary>The declared data model.</summary>
    private static List<WidgetField> ReadModel(ToolCallArguments args)
    {
        var fields = new List<WidgetField>();
        if (!args.Raw.TryGetProperty("model", out var ms) || ms.ValueKind != JsonValueKind.Array) return fields;

        foreach (var f in ms.EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object) continue;
            string Read(string k) => f.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()?.Trim() ?? "" : "";

            var name = Read("name");
            if (name.Length == 0) continue;

            var type = Read("type");
            fields.Add(new WidgetField
            {
                Name = name,
                Type = type.Length > 0 ? type : "string",
                Description = Read("description"),
                Example = Read("example") is { Length: > 0 } e ? e : null,
                Required = !f.TryGetProperty("required", out var r) || r.ValueKind != JsonValueKind.False,
            });
        }
        return fields;
    }

    /// <summary>An array of two-key objects, as a dictionary. Used for headers and for the field map.</summary>
    /// <summary>
    /// The further feeds, when one source cannot answer the whole question.
    /// </summary>
    /// <remarks>
    /// Each carries its own map and nothing else is required of it: mode defaults to http, exactly as the primary
    /// loader's does, because a second feed is nearly always another url beside the first.
    /// </remarks>
    private static List<WidgetLoader> ReadMore(ToolCallArguments args)
    {
        var feeds = new List<WidgetLoader>();
        if (!args.Raw.TryGetProperty("more", out var arr) || arr.ValueKind != JsonValueKind.Array) return feeds;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            string? Text(string name) =>
                item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()?.Trim()
                    : null;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (item.TryGetProperty("map", out var pairs) && pairs.ValueKind == JsonValueKind.Array)
                foreach (var pair in pairs.EnumerateArray())
                {
                    if (pair.ValueKind != JsonValueKind.Object) continue;
                    var field = pair.TryGetProperty("field", out var f) ? f.GetString()?.Trim() ?? "" : "";
                    var path = pair.TryGetProperty("path", out var pth) ? pth.GetString()?.Trim() ?? "" : "";
                    if (field.Length > 0 && path.Length > 0) map[field] = path;
                }

            var mode = (Text("mode") ?? LoaderModes.Http).ToLowerInvariant();
            var internalFeed = Text("internal");
            feeds.Add(new WidgetLoader
            {
                Mode = LoaderModes.Known(mode) ? mode : LoaderModes.Http,
                Url = Text("url") is { Length: > 0 } u ? u : null,
                Internal = WidgetInternals.Store(internalFeed),
                Expression = Text("expression") is { Length: > 0 } e ? e : null,
                Map = map,
            });
        }
        return feeds;
    }

    /// <summary>
    /// Whether anything will ever put values into the declared model.
    /// </summary>
    /// <remarks>
    /// In a server mode the loader does, and the map check has already made sure it says where from. In CLIENT
    /// mode the claim is that the component fetches for itself in the page — so a component that never calls
    /// fetch has made a claim it does not honour, and the model it declared can only be filled by the panel's
    /// stored parameters. Which, at the end of a build, are the example values the design made up.
    /// </remarks>
    internal static bool CanFill(string code, bool clientMode) =>
        !clientMode || code.Contains("fetch(", StringComparison.Ordinal);

    private static Dictionary<string, string> ReadPairs(ToolCallArguments args, string property, string keyName,
        string valueName)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!args.Raw.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array) return pairs;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var k = item.TryGetProperty(keyName, out var kv) ? kv.GetString()?.Trim() ?? "" : "";
            var v = item.TryGetProperty(valueName, out var vv) ? vv.GetString()?.Trim() ?? "" : "";
            if (k.Length > 0 && v.Length > 0) pairs[k] = v;
        }
        return pairs;
    }

    /// <summary>
    /// The filler's tool: supply the parameter values a panel was waiting on.
    ///
    /// <para>
    /// Deliberately not widget_publish. A worker sent to find an item number has no business writing a component —
    /// given the publish tool it would write one, and the library would grow a second entry for a shape that
    /// already had one. This does the one thing that job is for.
    /// </para>
    /// </summary>
    public static AgentTool FillTool(WidgetStore widgets, WidgetLibrary library) => new(
        "widget_fill",
        "Supply the parameter values the panel was waiting on. The component and the feed already exist — this " +
        "only fills in the values you were sent to establish, and the panel goes live.",
        new[]
        {
            ToolParameter.String("id", "The panel's id, from your brief.", required: true),
            ToolParameter.FromSchema("params",
                "{\"type\":\"array\",\"description\":\"The parameter names and the values you found.\",\"items\":{" +
                "\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"value\":{\"type\":\"string\"}}," +
                "\"required\":[\"name\",\"value\"]}}"),
        },
        (args, _) =>
        {
            var id = args.GetStringOrNull("id")?.Trim();
            if (string.IsNullOrWhiteSpace(id) || widgets.Get(id!) is not { } panel)
                return Task.FromResult(ToolOutput.DeadEnd($"There's no panel \"{id}\"."));
            if (library.Get(panel.Kind) is not { } kind)
                return Task.FromResult(ToolOutput.DeadEnd(
                    $"Panel \"{id}\" isn't attached to a library kind, so there is nothing to fill in."));

            var found = ReadParams(args);
            // Whatever it already had, plus what was just established.
            var values = new Dictionary<string, string>(panel.Params, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in found) values[k] = v;

            var missing = kind.Missing(values);
            if (missing.Count > 0)
                return Task.FromResult(ToolOutput.Error(
                    $"Still missing {string.Join(", ", missing.Select(p => p.Name))}. Give a value for each, or " +
                    "say plainly that it can't be established."));

            widgets.Attach(panel.Id, kind, values);
            library.Used(kind.Name);
            return Task.FromResult(ToolOutput.Ok(
                $"\"{panel.Title}\" is live with " +
                string.Join(", ", found.Select(kv => $"{kv.Key}={kv.Value}")) +
                ". You're done — say in one line what you found."));
        });

    /// <summary>Read name/value pairs off a tool call.</summary>
    private static Dictionary<string, string> ReadParams(ToolCallArguments args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!args.Raw.TryGetProperty("params", out var ps) || ps.ValueKind != JsonValueKind.Array) return values;

        foreach (var p in ps.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object) continue;
            var name = p.TryGetProperty("name", out var n) ? n.GetString()?.Trim() ?? "" : "";
            var value = p.TryGetProperty("value", out var v) ? v.GetString()?.Trim() ?? "" : "";
            if (name.Length > 0 && value.Length > 0) values[name] = value;
        }
        return values;
    }

    /// <summary>The same array, read as both the kind's declarations and this instance's values.</summary>
    private static (List<WidgetParam> Declared, Dictionary<string, string> Values) ReadKindParams(ToolCallArguments args)
    {
        var declared = new List<WidgetParam>();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!args.Raw.TryGetProperty("params", out var ps) || ps.ValueKind != JsonValueKind.Array)
            return (declared, values);

        foreach (var p in ps.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object) continue;
            string Read(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()?.Trim() ?? "" : "";

            var name = Read("name");
            if (name.Length == 0) continue;

            declared.Add(new WidgetParam
            {
                Name = name,
                Description = Read("description"),
                Example = Read("example") is { Length: > 0 } e ? e : null,
            });
            if (Read("value") is { Length: > 0 } value) values[name] = value;
        }
        return (declared, values);
    }
}
