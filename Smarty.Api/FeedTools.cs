using System.Text;
using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// Setting up something to watch: the ask, the job, and the two things the job publishes.
///
/// <para>
/// The same division as the panels, for the same reason. "Tell me when the printers reply about the invoice" is a
/// sentence; turning it into a feed is work — finding something that actually keeps producing, proving it answers,
/// working out where the items are and what counts as a topic. That work happens ONCE, in the background, by a
/// worker. After it, being told when something happens costs an HTTP GET on a timer.
/// </para>
/// <para>
/// So the chat voice gets <c>watch_for</c>, which starts the job and returns immediately, and the worker gets
/// <c>feed_publish</c> and <c>watch_publish</c>. Publishing a feed READS it first — a feed nobody has read is a
/// guess, and a guess on a timer is a watcher that waits politely for ever.
/// </para>
/// </summary>
public static class FeedTools
{
    /// <summary>What the worker setting up a watch is told.</summary>
    public static string Brief(string what, string act, string knownFeeds) =>
        "Set up something to WATCH, and say what to do when it happens.\n\n" +
        $"What to watch: {what}\n" +
        $"What to do when it happens: {act}\n\n" +
        "TWO PIECES OF WORK, IN THIS ORDER.\n\n" +
        "1. FIND SOMETHING THAT KEEPS PRODUCING — and read it before you believe it.\n" +
        "   In order of preference:\n" +
        "   - AN RSS OR ATOM FEED. Most sites still publish one and it needs no key, answers a plain server request, " +
        "and keeps answering long after the JSON api beside it starts refusing. Look for it: /feed, /rss, " +
        "/atom.xml, the <link rel=\"alternate\"> in the page head. A subreddit is /r/<name>/.rss.\n" +
        "   - A JSON ENDPOINT. A public api, or the route the site's own front end calls — open the page and watch " +
        "the network requests.\n" +
        "   - A PAGE AND A SCRIPT, if there is genuinely no feed. The browser is the user's own and is already " +
        "signed in, so a page only they can see is reachable this way and nothing else is. You write JavaScript " +
        "that runs IN the page and returns an ARRAY of items.\n" +
        "   PROVE IT. Call the address and look at what comes back before publishing. An address you have not read " +
        "is a guess, and a guess here produces a watcher that never fires and never says why.\n\n" +
        "2. PUBLISH THE FEED, THEN THE WATCHER.\n" +
        "   feed_publish takes the address, where the items are, and which part of an item is which. It READS the " +
        "feed as you publish it and refuses if nothing comes out, so the failure lands here, in front of you, " +
        "instead of silently in three days' time.\n" +
        "   Then watch_publish says which of those items matter and what to do about one.\n\n" +
        "TOPICS ARE THE PART THAT MATTERS. An item's topic is what it is ABOUT, and a watcher is nearly always " +
        "scoped to one — because \"watch my inbox\" is never what anybody means. Pick the split honestly:\n" +
        $"   {FeedSplits.Field} — the source labels its own items (a category, a list, a channel). Map the field.\n" +
        $"   {FeedSplits.Thread} — items come in conversations. Map the thread id, and the thread is named ONCE by " +
        "the first thing in it, so a reply titled \"Re: Re: FW:\" stays under the topic it started as.\n" +
        $"   {FeedSplits.One} — everything from here is the same subject. Name it.\n\n" +
        "THE WATCHER'S FILTER: FREE FIRST. A topic and a field test cost nothing and run for ever. Only reach for " +
        "`about` — a judgement, read by a model — for something a test genuinely cannot express, and even then the " +
        "topic and the tests still narrow what it has to read. A watcher whose filter is precise costs nothing " +
        "until the thing it is waiting for happens.\n\n" +
        "CADENCE: match how fast the answer really changes. An inbox: every 5 minutes. A news feed: every 30 " +
        "minutes. A council bin schedule: daily. Nothing is gained by asking a slow source quickly.\n\n" +
        (knownFeeds is { Length: > 0 }
            ? $"--- feeds that already exist, which you can watch instead of building another ---\n{knownFeeds}\n" +
              "A second feed pointed at the same address is pure waste: it polls twice and produces the same items. " +
              "If one of these already carries what you need, skip straight to watch_publish and name it.\n\n"
            : "") +
        "If there is genuinely nothing that keeps producing — the answer only exists on a page that has to be read " +
        "fresh each time, or behind something a browser cannot reach — say exactly that and stop. Do not publish a " +
        "feed you could not read.";

    /// <summary>
    /// The chat voice's tool: start the job, and say so in one line.
    /// </summary>
    public static AgentTool WatchForTool(Func<string, string, Task<string>> start) => new(
        "watch_for",
        "Set up something to be told about, in the background: an inbox thread, a price, a status page, a feed. " +
        "Use this whenever the user wants to KNOW WHEN something happens rather than know it now — \"tell me when " +
        "they reply\", \"let me know if it drops below 30\", \"watch for anything about the roadworks\".\n" +
        "A worker finds something that keeps producing, proves it, and sets up the watch; you return immediately. " +
        "When it fires, the assistant starts a NEW conversation with the user about it — so what to do can be " +
        "anything from telling them to getting on with a job.\n" +
        "Not for something to do at a set time — that is schedule_task. This is for something to do WHEN " +
        "SOMETHING HAPPENS.",
        new[]
        {
            ToolParameter.String("what",
                "What to watch, in the user's terms and with everything you know that narrows it: which site, which " +
                "account, which thread, which item. \"Replies from the printers about the invoice\", \"the price of " +
                "the Kingfishr LP on eBay\".", required: true),
            ToolParameter.String("act",
                "What to do when it happens, as an instruction. \"Tell me\" is a legitimate answer; so is \"draft a " +
                "reply and show it to me\" or \"cancel the other order\". This becomes the opening of the " +
                "conversation the assistant starts.", required: true),
        },
        async (args, _) =>
        {
            var what = args.GetStringOrNull("what")?.Trim();
            var act = args.GetStringOrNull("act")?.Trim();

            if (string.IsNullOrWhiteSpace(what))
                return ToolOutput.Error("Say what to watch.");
            if (string.IsNullOrWhiteSpace(act))
                return ToolOutput.Error(
                    "Say what to do when it happens. A watch with no instruction fires into a conversation with " +
                    "nothing to say.");

            var taskId = await start(what!, act!).ConfigureAwait(false);
            return ToolOutput.Ok(
                $"Setting it up in the background (task {taskId}). Tell the user in one line what you'll be " +
                "watching and what you'll do about it; don't wait for it.");
        })
    {
        Repeatable = true,
    };

    /// <summary>
    /// The worker's tool: publish a feed, having proved it reads.
    /// </summary>
    /// <param name="read">Reads the feed as it is published. The refusal that makes the rest of this trustworthy.</param>
    public static AgentTool PublishTool(FeedStore feeds,
        Func<Feed, CancellationToken, Task<FeedRead>> read) => new(
        "feed_publish",
        "Publish the feed you found. It is READ as part of publishing: if nothing comes out, this refuses and tells " +
        "you what came back instead, so a wrong address or a wrong items path is caught here rather than in three " +
        "days of silence.",
        new[]
        {
            ToolParameter.String("name",
                "What it is, in a few words: \"BBC tech news\", \"my Gmail\", \"Kingfishr LP on eBay\".", required: true),
            ToolParameter.String("source",
                $"rss (an RSS or Atom document — no configuring needed), json (a JSON endpoint), or browser (a page " +
                $"read by your script, in the user's own Chrome).", required: true),
            ToolParameter.String("url", "The address, or for a browser feed the page to open.", required: true),
            ToolParameter.String("expression",
                "For a browser feed ONLY: JavaScript evaluated in the page that returns an ARRAY of items. Read the " +
                "DOM, or the page's own JSON, or a global its front end already loaded.", required: false),
            ToolParameter.String("items",
                "For json: the dotted path to the array of items — results.orders, data.messages. Leave empty for " +
                "rss (it is known) and for a browser script that returns the array itself.", required: false),
            ToolParameter.FromSchema("map",
                "{\"type\":\"array\",\"description\":\"Which part of one item is which, as dotted paths inside a " +
                "single item. The names are fixed: key (something stable and unique — a guid, an id, a link; this is " +
                "what makes an item new only once), topic, thread, title, body, url, ref (what you would need to act " +
                "back on it later), at. RSS fills in the obvious ones itself.\",\"items\":{\"type\":\"object\"," +
                "\"properties\":{\"field\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"}}," +
                "\"required\":[\"field\",\"path\"]}}"),
            ToolParameter.FromSchema("headers",
                "{\"type\":\"array\",\"description\":\"Headers the request needs, if any.\",\"items\":{" +
                "\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"value\":{\"type\":\"string\"}}," +
                "\"required\":[\"name\",\"value\"]}}"),
            ToolParameter.String("every",
                "How often to look: \"every 5 minutes\", \"every 30 minutes\", \"hourly\", \"daily at 07:00\".", required: true),
            ToolParameter.String("split",
                $"How an item's topic is decided: {string.Join(", ", FeedSplits.All)}.", required: true),
            ToolParameter.String("topic", $"For {FeedSplits.One}: the single subject everything here belongs to.", required: false),
        },
        async (args, ct) =>
        {
            var name = args.GetStringOrNull("name")?.Trim();
            if (string.IsNullOrWhiteSpace(name)) return ToolOutput.Error("The feed needs a name.");

            var source = (args.GetStringOrNull("source") ?? "").Trim().ToLowerInvariant();
            if (!FeedSources.Known(source))
                return ToolOutput.Error($"\"{source}\" isn't a source. Use one of: {string.Join(", ", FeedSources.All)}.");

            var url = args.GetStringOrNull("url")?.Trim();
            if (string.IsNullOrWhiteSpace(url)) return ToolOutput.Error("The feed needs an address.");
            if (!(Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https"))
                return ToolOutput.Error($"\"{url}\" isn't an http(s) address.");

            var expression = args.GetStringOrNull("expression")?.Trim();
            if (source == FeedSources.Browser && string.IsNullOrWhiteSpace(expression))
                return ToolOutput.Error(
                    "A browser feed needs the script that reads the page and returns the items — that is where the " +
                    "shaping happens in browser mode.");

            var split = (args.GetStringOrNull("split") ?? "").Trim().ToLowerInvariant();
            if (!FeedSplits.Known(split))
                return ToolOutput.Error(
                    $"\"{split}\" isn't a split. Use one of: {string.Join(", ", FeedSplits.All)} — it decides what a " +
                    "watcher can be scoped to, which is the difference between watching one thread and watching an " +
                    "entire inbox.");

            var topic = args.GetStringOrNull("topic")?.Trim();
            if (split == FeedSplits.One && string.IsNullOrWhiteSpace(topic))
                return ToolOutput.Error($"A {FeedSplits.One} feed needs its topic named.");

            var every = (args.GetStringOrNull("every") ?? "").Trim();
            if (every.Length == 0 || !ScheduleStore.TryParseRepeat(every, DateTimeOffset.Now, out _))
                return ToolOutput.Error(
                    $"Couldn't read the cadence \"{every}\". Use \"every N minutes/hours\", \"hourly\", " +
                    "\"daily at 07:00\".");

            var map = Pairs(args, "map", "field", "path");
            var unknown = map.Keys.Where(f => !FeedFields.All.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unknown.Count > 0)
                return ToolOutput.Error(
                    $"The map has {string.Join(", ", unknown)}, which {(unknown.Count == 1 ? "isn't" : "aren't")} " +
                    $"one of the item's parts. Use only: {string.Join(", ", FeedFields.All)}. Anything else the " +
                    "source carries is kept anyway and can still be watched by name.");

            if (split == FeedSplits.Thread && !map.ContainsKey(FeedFields.Thread))
                return ToolOutput.Error(
                    $"A {FeedSplits.Thread} split needs the thread mapped — without it there is nothing to name once, " +
                    "and every reply would arrive as its own topic.");
            if (split == FeedSplits.Field && !map.ContainsKey(FeedFields.Topic))
                return ToolOutput.Error(
                    $"A {FeedSplits.Field} split needs the topic mapped — that is the field you are splitting on.");

            var feed = new Feed
            {
                Name = name!,
                Source = source,
                Url = url,
                Expression = string.IsNullOrWhiteSpace(expression) ? null : expression,
                Items = args.GetStringOrNull("items")?.Trim() ?? "",
                Map = map,
                Headers = Pairs(args, "headers", "name", "value"),
                Every = every,
                Split = split,
                Topic = topic,
            };

            // The refusal that makes a feed worth having. Read now, in front of whoever is publishing it.
            var proof = await read(feed, ct).ConfigureAwait(false);
            if (proof.Error is { Length: > 0 } || proof.Items.Count == 0)
                return ToolOutput.Error(
                    $"Reading it produced nothing: {proof.Error ?? "no items came out"}. Fix the address, the items path or " +
                    "the key mapping and publish again — a feed that reads nothing now will read nothing for ever.");

            // The split has to actually split. A field split whose field is not on the items reads as configured and
            // does nothing: every item falls back to the feed's own name, so every topic is the same string and a
            // watcher can only ever be scoped to "everything". Caught here because the proof read above is the only
            // moment anybody can see what the source really carries.
            if (split == FeedSplits.Field && proof.Items.All(i => string.IsNullOrWhiteSpace(i.Topic)))
                return ToolOutput.Error(
                    $"Nothing on these items carries a topic at \"{map.GetValueOrDefault(FeedFields.Topic)}\" — " +
                    $"{proof.Items.Count} item(s) came back and not one had a value there. So every item would end up " +
                    $"filed under the feed's own name and a watcher could only ever watch all of it. Map a field the " +
                    $"source really has, or publish it as {FeedSplits.One} with the subject named, or as " +
                    $"{FeedSplits.Thread} if these come in conversations.");

            var saved = feeds.Add(feed);
            var first = proof.Items[0];
            return ToolOutput.Ok(
                $"\"{saved.Name}\" is live as feed {saved.Id} — {proof.Items.Count} item(s) on the first read, " +
                $"looking {saved.Every}. The newest is \"{first.Title}\"" +
                (first.Topic is { Length: > 0 } t ? $" under topic \"{t}\"" : "") +
                ". Now call watch_publish to say which of these matter and what to do about one.");
        });

    /// <summary>The worker's other tool: what to watch for in that feed, and what to do about it.</summary>
    public static AgentTool WatchTool(WatcherStore watchers, FeedStore feeds) => new(
        "watch_publish",
        "Say which arriving items matter and what to do when one does. The filter runs for ever, so put as much of " +
        "it as possible in the topic and the tests, which are free.",
        new[]
        {
            ToolParameter.String("name",
                "What this is watching for, in a few words — it is what a person sees in the list: \"a reply about " +
                "the invoice\", \"the LP under £30\".", required: true),
            ToolParameter.String("feed", "The feed's id or name. Leave empty to watch every feed, which is rarely right.", required: false),
            ToolParameter.String("topic",
                "Only items filed under this topic. The cheapest and most useful narrowing there is — use it whenever " +
                "the feed has topics at all.", required: false),
            ToolParameter.FromSchema("when",
                "{\"type\":\"array\",\"description\":\"Free tests on an item, all of which must hold. field is one " +
                "of title, body, topic, url, or any field the source carried; op is has, is, not, above or below.\"," +
                "\"items\":{\"type\":\"object\",\"properties\":{\"field\":{\"type\":\"string\"}," +
                "\"op\":{\"type\":\"string\"},\"value\":{\"type\":\"string\"}}," +
                "\"required\":[\"field\",\"op\",\"value\"]}}"),
            ToolParameter.String("about",
                "A judgement, ONLY for what a test cannot express — \"anything that reads like a delivery going " +
                "wrong\". It costs a model call per item that already passed the tests above, so narrow those first.",
                required: false),
            ToolParameter.String("act",
                "What to do when it fires, as an instruction to yourself. This becomes the opening of a new " +
                "conversation with the user.", required: true),
            new ToolParameter("limit",
                "integer",
                "How many times an hour this may fire, at most. Default 6. A busy feed with a loose filter is a " +
                "machine for starting conversations.", required: false),
        },
        (args, _) =>
        {
            var name = args.GetStringOrNull("name")?.Trim();
            if (string.IsNullOrWhiteSpace(name)) return Task.FromResult(ToolOutput.Error("The watcher needs a name."));

            var act = args.GetStringOrNull("act")?.Trim();
            if (string.IsNullOrWhiteSpace(act))
                return Task.FromResult(ToolOutput.Error(
                    "Say what to do when it fires. Without it, it wakes up with nothing to say."));

            var named = args.GetStringOrNull("feed")?.Trim();
            Feed? feed = null;
            if (named is { Length: > 0 })
            {
                feed = feeds.Get(named);
                if (feed is null)
                    return Task.FromResult(ToolOutput.DeadEnd(
                        $"There's no feed \"{named}\". Publish it first, or leave the feed empty to watch everything."));
            }

            var topic = args.GetStringOrNull("topic")?.Trim();
            // A topic the feed has never produced is a watcher that can never fire, and the mistake is almost always a
            // guessed name. Named here, with what the feed actually calls things.
            if (feed is not null && topic is { Length: > 0 } && feed.Topics.Count > 0
                && !feed.Topics.Contains(topic, StringComparer.OrdinalIgnoreCase))
                return Task.FromResult(ToolOutput.Error(
                    $"\"{feed.Name}\" has no topic called \"{topic}\". It has produced: " +
                    $"{string.Join(", ", feed.Topics.Take(12))}. Use one of those, or leave the topic off and narrow " +
                    "with a test instead."));

            var tests = new List<WatchTest>();
            if (args.Raw.TryGetProperty("when", out var when) && when.ValueKind == JsonValueKind.Array)
            {
                foreach (var test in when.EnumerateArray())
                {
                    if (test.ValueKind != JsonValueKind.Object) continue;
                    string Read(string key) => test.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                        ? v.GetString()?.Trim() ?? "" : "";

                    var value = Read("value");
                    if (value.Length == 0) continue;

                    var op = Read("op").ToLowerInvariant();
                    if (op.Length > 0 && !WatchOps.Known(op))
                        return Task.FromResult(ToolOutput.Error(
                            $"\"{op}\" isn't a test. Use one of: {string.Join(", ", WatchOps.All)}."));

                    tests.Add(new WatchTest
                    {
                        Field = Read("field"),
                        Op = op.Length > 0 ? op : WatchOps.Has,
                        Value = value,
                    });
                }
            }

            var about = args.GetStringOrNull("about")?.Trim();
            if (feed is null && tests.Count == 0 && string.IsNullOrWhiteSpace(topic) && string.IsNullOrWhiteSpace(about))
                return Task.FromResult(ToolOutput.Error(
                    "That watches everything that ever arrives from anywhere, and would start a conversation about " +
                    "each one. Name the feed, or a topic, or a test."));

            var watcher = watchers.Add(new Watcher
            {
                Name = name!,
                Feed = feed?.Id,
                Topic = topic,
                When = tests,
                About = string.IsNullOrWhiteSpace(about) ? null : about,
                Do = act!,
                Limit = args.GetIntOrNull("limit") is { } cap && cap > 0 ? cap : 6,
            });

            return Task.FromResult(ToolOutput.Ok(
                $"Watching for \"{watcher.Name}\"" +
                (feed is not null ? $" in {feed.Name}" : " across every feed") +
                (topic is { Length: > 0 } ? $", topic \"{topic}\"" : "") +
                (tests.Count > 0 ? $", {tests.Count} test(s)" : "") +
                (about is { Length: > 0 } ? ", plus a judgement" : "") +
                ". You're done — say in one line what you set up and what will happen when it fires."));
        });

    /// <summary>
    /// What is already being watched, for the chat voice's context.
    /// </summary>
    /// <remarks>
    /// The note that stops the same watch being set up twice. Without it, "let me know if they reply" a week later
    /// starts a second job that finds the same feed, publishes a duplicate, and doubles the polling.
    /// </remarks>
    public static string Note(FeedStore feeds, WatcherStore watchers)
    {
        var live = watchers.All();
        if (live.Count == 0 && feeds.All().Count == 0) return "";

        var sb = new StringBuilder("\n\nAlready being watched (don't set up a second one for the same thing):\n");
        foreach (var w in live)
            sb.Append($"- {w.Name}" +
                      (feeds.Get(w.Feed) is { } f ? $" — from {f.Name}" : "") +
                      (w.Topic is { Length: > 0 } ? $", topic \"{w.Topic}\"" : "") +
                      (w.Paused ? " (paused)" : "") +
                      $" → {Snip(w.Do, 60)}" +
                      (w.Fired > 0 ? $" [fired {w.Fired}×]" : "") + "\n");

        var quiet = feeds.All().Where(f => live.All(w => w.Feed != f.Id)).ToList();
        if (quiet.Count > 0)
            sb.Append("Feeds already set up that nothing is watching, which can be watched without new work: " +
                      string.Join(", ", quiet.Select(f => $"{f.Name} ({f.Id})")) + "\n");

        return sb.ToString();
    }

    private static string Snip(string? text, int max) =>
        text is null ? "" : text.Length <= max ? text : text[..max].TrimEnd() + "…";

    private static Dictionary<string, string> Pairs(ToolCallArguments args, string property, string keyName,
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
}
