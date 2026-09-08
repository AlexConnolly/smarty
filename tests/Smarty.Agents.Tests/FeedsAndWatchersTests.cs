using System.Text.Json;
using Smarty.Agents;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Things that arrive, and what to do about them.
///
/// <para>
/// Everything before this PULLED. A panel asks its source on a timer and puts the answer on a screen; a task asks and
/// stops. So nothing could ever be reacted to, and "tell me when the price drops" had no shape at all — the only
/// implementation was a job that woke every hour and asked a model to go and look, which costs a model call an hour
/// for ever, per thing anybody cares about.
/// </para>
/// <para>
/// A feed is therefore deliberately dumb: an address, a map, a cadence, no model. The only clever thing it does is
/// decide what each item is ABOUT, because "watch my inbox" is never what anybody means — they mean the thread about
/// the invoice. Topics are what make that a free string comparison instead of a model reading every message that
/// arrives for the rest of the year.
/// </para>
/// </summary>
public class FeedsAndWatchersTests : IDisposable
{
    private readonly List<string> _paths = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Tuesday = new(2026, 8, 18, 9, 0, 0, TimeSpan.Zero);

    private FeedStore NewFeeds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"feeds-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        return new FeedStore(path, Json);
    }

    private WatcherStore NewWatchers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"watchers-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        return new WatcherStore(path, Json);
    }

    private static async Task<ToolOutput> Call(AgentTool tool, object args) =>
        await tool.InvokeAsync(
            new ToolCallArguments(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement),
            CancellationToken.None);

    private const string Rss = """
        <?xml version="1.0"?>
        <rss version="2.0"><channel>
          <title>Example news</title>
          <item><title>Prices are falling</title><link>https://example.com/1</link><guid>a1</guid>
                <description>Everything is cheaper</description><category>money</category>
                <pubDate>Mon, 17 Aug 2026 08:00:00 GMT</pubDate></item>
          <item><title>Prices are rising</title><link>https://example.com/2</link><guid>a2</guid>
                <description>Everything costs more</description><category>money</category></item>
        </channel></rss>
        """;

    // ── reading a feed ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_rss_feed_needs_no_configuring_at_all()
    {
        // The commonest source there is, and the one where asking somebody to say where the items live would be an
        // invitation to get it wrong. There are exactly two answers and both are known.
        var feed = new Feed { Name = "Example news", Source = FeedSources.Rss };

        var read = FeedReader.Split(feed, Rss);

        Assert.Null(read.Error);
        Assert.Equal(2, read.Items.Count);
        Assert.Equal("a1", read.Items[0].Key);
        Assert.Equal("Prices are falling", read.Items[0].Title);
        Assert.Equal("Everything is cheaper", read.Items[0].Body);
        Assert.Equal("https://example.com/1", read.Items[0].Url);
        Assert.NotNull(read.Items[0].At);
    }

    [Fact]
    public void A_json_source_says_where_its_items_are_and_what_they_are_called()
    {
        var feed = new Feed
        {
            Name = "Orders",
            Source = FeedSources.Json,
            Items = "results.orders",
            Map =
            {
                [FeedFields.Key] = "reference",
                [FeedFields.Title] = "customer",
                [FeedFields.Topic] = "channel",
                [FeedFields.Ref] = "reference",
            },
        };

        var read = FeedReader.Split(feed, """
            {"results":{"orders":[
              {"reference":"ORD-1","customer":"Someone","channel":"ebay","total":42.5},
              {"reference":"ORD-2","customer":"Somebody","channel":"etsy","total":9.99}]}}
            """);

        Assert.Null(read.Error);
        Assert.Equal(2, read.Items.Count);
        Assert.Equal("ORD-1", read.Items[0].Key);
        Assert.Equal("Someone", read.Items[0].Title);
        Assert.Equal("ebay", read.Items[0].Topic);
        Assert.Equal("ORD-1", read.Items[0].Ref);
        // Everything else the source carried comes along, so a watcher can test a field nobody thought to map.
        Assert.Contains("42.5", read.Items[0].Data);
    }

    [Fact]
    public void A_source_serving_one_item_is_not_a_broken_source()
    {
        // Feeds that serve an array normally serve an OBJECT on the day there is exactly one entry, and a reader that
        // cannot cope looks broken precisely once in a while, which is the hardest kind of fault to catch.
        var feed = new Feed { Name = "Quiet", Source = FeedSources.Json, Items = "items", Map = { [FeedFields.Key] = "id" } };

        var read = FeedReader.Split(feed, "{\"items\":{\"id\":\"only\",\"title\":\"The only one\"}}");

        Assert.Null(read.Error);
        Assert.Single(read.Items);
    }

    [Fact]
    public void A_source_with_nothing_new_has_answered_correctly()
    {
        // Caught the first time this ran against something real. An empty list is an ANSWER — an inbox with nothing in
        // it, a schedule with nothing on it — and calling it a fault makes a working feed show an error for ever while
        // it waits for its first item. A MISSING path is the fault; an empty array is Tuesday.
        var feed = new Feed { Name = "My schedule", Source = FeedSources.Json, Items = "tasks" };

        var read = FeedReader.Split(feed, "{\"tasks\":[]}");

        Assert.Null(read.Error);
        Assert.Empty(read.Items);
    }

    [Fact]
    public void A_path_that_is_not_there_at_all_still_is_a_fault()
    {
        var feed = new Feed { Name = "Wrong path", Source = FeedSources.Json, Items = "results.orders" };

        var read = FeedReader.Split(feed, "{\"data\":{\"orders\":[{\"id\":\"1\"}]}}");

        Assert.Contains("results.orders", read.Error);
    }

    [Fact]
    public void An_item_with_nothing_stable_to_identify_it_is_not_an_item()
    {
        // Without a key there is no "already seen", so every poll would report it as new and a watcher would fire on
        // it for ever. Refused with a reason rather than passed through.
        var feed = new Feed { Name = "Keyless", Source = FeedSources.Json, Items = "items" };

        var read = FeedReader.Split(feed, "{\"items\":[{\"title\":\"No id anywhere\"}]}");

        Assert.Empty(read.Items);
        Assert.Contains("key", read.Error);
    }

    // ── what is new ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_first_look_learns_what_is_there_and_wakes_nobody()
    {
        // A feed's first poll is its whole back catalogue. Firing on all of it would mean twenty conversations about
        // things that happened last week, which is the single most likely way for this to be switched off in disgust.
        var feeds = NewFeeds();
        var feed = feeds.Add(new Feed { Name = "Example news", Source = FeedSources.Rss });

        var fresh = feeds.Polled(feed.Id, FeedReader.Split(feed, Rss).Items, null, Tuesday);

        Assert.Empty(fresh);
        Assert.Equal(2, feeds.Get(feed.Id)!.Seen);
        Assert.Equal(2, feeds.Get(feed.Id)!.Recent.Count);
    }

    [Fact]
    public void After_that_only_what_it_has_never_seen_counts()
    {
        var feeds = NewFeeds();
        var feed = feeds.Add(new Feed { Name = "Example news", Source = FeedSources.Rss });
        feeds.Polled(feed.Id, FeedReader.Split(feed, Rss).Items, null, Tuesday);

        // The same page again, with one addition — which is exactly what a feed serves every time.
        var again = Rss.Replace("</channel>",
            "<item><title>Something new</title><guid>a3</guid><category>money</category></item></channel>");
        var fresh = feeds.Polled(feed.Id, FeedReader.Split(feed, again).Items, null, Tuesday.AddMinutes(15));

        var one = Assert.Single(fresh);
        Assert.Equal("a3", one.Key);
    }

    [Fact]
    public void A_feed_that_started_empty_still_wakes_somebody_for_its_first_item()
    {
        // The second thing the first live run caught, and a nastier one than the empty-array error above, because the
        // feed looked perfectly healthy. "Has it any items yet" was standing in for "has it been read yet" — so a quiet
        // feed's first REAL arrival was mistaken for back catalogue and silently woke nobody. Which is the one job.
        var feeds = NewFeeds();
        var feed = feeds.Add(new Feed { Name = "My schedule", Source = FeedSources.Json, Items = "tasks", Map = { [FeedFields.Key] = "task" } });

        // Two reads with nothing on it, which is how most watched things spend most of their time.
        feeds.Polled(feed.Id, FeedReader.Split(feed, "{\"tasks\":[]}").Items, null, Tuesday);
        feeds.Polled(feed.Id, FeedReader.Split(feed, "{\"tasks\":[]}").Items, null, Tuesday.AddMinutes(5));

        var fresh = feeds.Polled(feed.Id,
            FeedReader.Split(feed, "{\"tasks\":[{\"task\":\"Something appeared\"}]}").Items, null,
            Tuesday.AddMinutes(10));

        Assert.Equal("Something appeared", Assert.Single(fresh).Key);
    }

    [Fact]
    public void A_feed_whose_first_read_failed_does_not_then_treat_its_whole_page_as_news()
    {
        // The other side of the same rule. Counting a failure as a read would mean a source that was down at setup
        // dumps its entire back catalogue into conversations the moment it comes back.
        var feeds = NewFeeds();
        var feed = feeds.Add(new Feed { Name = "Example news", Source = FeedSources.Rss });

        feeds.Polled(feed.Id, Array.Empty<FeedItem>(), "The source answered 503.", Tuesday);
        var fresh = feeds.Polled(feed.Id, FeedReader.Split(feed, Rss).Items, null, Tuesday.AddMinutes(15));

        Assert.Empty(fresh);
        Assert.Equal(2, feeds.Get(feed.Id)!.Seen);
    }

    [Fact]
    public void A_poll_that_failed_forgets_nothing()
    {
        // The dangerous confusion: a failed poll and an empty one look identical from here, and treating a failure as
        // "nothing there" would make the next successful poll report the whole page as news.
        var feeds = NewFeeds();
        var feed = feeds.Add(new Feed { Name = "Example news", Source = FeedSources.Rss });
        feeds.Polled(feed.Id, FeedReader.Split(feed, Rss).Items, null, Tuesday);

        feeds.Polled(feed.Id, Array.Empty<FeedItem>(), "The source answered 503.", Tuesday.AddMinutes(15));
        Assert.Equal("The source answered 503.", feeds.Get(feed.Id)!.Error);

        var fresh = feeds.Polled(feed.Id, FeedReader.Split(feed, Rss).Items, null, Tuesday.AddMinutes(30));
        Assert.Empty(fresh);
        Assert.Null(feeds.Get(feed.Id)!.Error);
    }

    [Fact]
    public void A_feed_books_its_next_look_from_its_own_cadence()
    {
        var feeds = NewFeeds();
        var feed = feeds.Add(new Feed { Name = "Example news", Source = FeedSources.Rss, Every = "every 5 minutes" });

        feeds.Polled(feed.Id, Array.Empty<FeedItem>(), null, Tuesday);
        var next = feeds.Get(feed.Id)!.NextPoll;

        Assert.NotNull(next);
        Assert.True(next > DateTimeOffset.UtcNow.AddMinutes(2), $"next poll was {next}");
    }

    // ── topics, which are the point ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void What_the_source_calls_it_becomes_the_topic()
    {
        var feeds = NewFeeds();
        var feed = feeds.Add(new Feed
        {
            Name = "Example news",
            Source = FeedSources.Rss,
            Split = FeedSplits.Field,
            Map = { [FeedFields.Topic] = "category" },
        });

        feeds.Polled(feed.Id, FeedReader.Split(feed, Rss).Items, null, Tuesday);

        Assert.Equal(new[] { "money" }, feeds.Get(feed.Id)!.Topics);
        Assert.All(feeds.Get(feed.Id)!.Recent, item => Assert.Equal("money", item.Topic));
    }

    [Fact]
    public void A_thread_is_named_once_and_keeps_that_name()
    {
        // The email case, and the reason topics exist. Name a thread from every message and it is renamed every time
        // somebody replies — so a watcher on "the invoice from the printers" stops matching at "Re: Re: FW:".
        var feeds = NewFeeds();
        var feed = feeds.Add(new Feed
        {
            Name = "Inbox",
            Source = FeedSources.Json,
            Items = "messages",
            Split = FeedSplits.Thread,
            Map = { [FeedFields.Key] = "id", [FeedFields.Thread] = "threadId", [FeedFields.Title] = "subject" },
        });

        var opening = FeedReader.Split(feed, """
            {"messages":[{"id":"m1","threadId":"t1","subject":"Invoice from the printers"}]}
            """).Items;
        feeds.Polled(feed.Id, opening, null, Tuesday);

        var reply = FeedReader.Split(feed, """
            {"messages":[{"id":"m2","threadId":"t1","subject":"Re: Re: FW: Invoice"}]}
            """).Items;
        var fresh = feeds.Polled(feed.Id, reply, null, Tuesday.AddMinutes(15));

        Assert.Equal("Invoice from the printers", Assert.Single(fresh).Topic);
        // And it has not learned a second topic for the same conversation.
        Assert.Single(feeds.Get(feed.Id)!.Topics);
    }

    [Fact]
    public void A_feed_about_one_thing_says_so_once()
    {
        var feeds = NewFeeds();
        var feed = feeds.Add(new Feed
        {
            Name = "Kingfishr LP",
            Source = FeedSources.Json,
            Items = "items",
            Split = FeedSplits.One,
            Topic = "the vinyl listing",
            Map = { [FeedFields.Key] = "id" },
        });

        feeds.Polled(feed.Id, FeedReader.Split(feed, "{\"items\":[{\"id\":\"x1\"}]}").Items, null, Tuesday);

        Assert.Equal("the vinyl listing", feeds.Get(feed.Id)!.Recent[0].Topic);
    }

    [Fact]
    public void Something_with_no_topic_anywhere_still_has_one()
    {
        // A watcher has to be able to name what it is watching, so "no topic" cannot be an outcome. The feed itself is
        // the subject when nothing else is.
        var feeds = NewFeeds();
        var feed = feeds.Add(new Feed { Name = "Example news", Source = FeedSources.Rss });

        feeds.Polled(feed.Id, FeedReader.Split(feed, Rss).Items, null, Tuesday);

        Assert.All(feeds.Get(feed.Id)!.Recent, item => Assert.Equal("Example news", item.Topic));
    }

    // ── watching ────────────────────────────────────────────────────────────────────────────────────────

    private static Feed Somewhere(string id = "f1", string name = "Example news") => new() { Id = id, Name = name };

    private static FeedItem Arrived(string topic = "money", string title = "Prices are falling",
        string body = "Everything is cheaper", string? data = null) => new()
    {
        Key = "a1", Topic = topic, Title = title, Body = body, Data = data,
    };

    [Fact]
    public void A_watcher_only_hears_from_the_feed_and_topic_it_asked_for()
    {
        var watcher = new Watcher { Name = "invoice thread", Feed = "f1", Topic = "money", Do = "tell me" };

        Assert.True(Watching.Matches(watcher, Somewhere(), Arrived()));
        Assert.False(Watching.Matches(watcher, Somewhere("f2", "Other"), Arrived()));
        Assert.False(Watching.Matches(watcher, Somewhere(), Arrived(topic: "sport")));
    }

    [Fact]
    public void A_watcher_on_no_particular_feed_hears_everything()
    {
        var watcher = new Watcher { Name = "anything at all", Do = "tell me" };

        Assert.True(Watching.Matches(watcher, Somewhere(), Arrived()));
        Assert.True(Watching.Matches(watcher, Somewhere("f9", "Elsewhere"), Arrived(topic: "sport")));
    }

    [Fact]
    public void The_tests_are_the_free_half_of_the_decision()
    {
        var has = new Watcher { Name = "falling", Do = "x", When = { new WatchTest { Field = "title", Op = WatchOps.Has, Value = "falling" } } };
        var not = new Watcher { Name = "not sport", Do = "x", When = { new WatchTest { Field = "topic", Op = WatchOps.Not, Value = "sport" } } };
        var exact = new Watcher { Name = "money", Do = "x", When = { new WatchTest { Field = "topic", Op = WatchOps.Is, Value = "MONEY" } } };

        Assert.True(Watching.Matches(has, Somewhere(), Arrived()));
        Assert.False(Watching.Matches(has, Somewhere(), Arrived(title: "Prices are rising")));
        Assert.True(Watching.Matches(not, Somewhere(), Arrived()));
        Assert.True(Watching.Matches(exact, Somewhere(), Arrived()));
    }

    [Fact]
    public void A_number_can_be_compared_even_when_it_arrives_with_a_pound_sign_on_it()
    {
        var under = new Watcher
        {
            Name = "cheap enough",
            Do = "x",
            When = { new WatchTest { Field = "price", Op = WatchOps.Below, Value = "30" } },
        };

        Assert.True(Watching.Matches(under, Somewhere(), Arrived(data: "{\"price\":\"£28.16\"}")));
        Assert.False(Watching.Matches(under, Somewhere(), Arrived(data: "{\"price\":\"£76.25\"}")));
    }

    [Fact]
    public void A_field_nobody_mapped_can_still_be_tested()
    {
        // Nobody knows what they will want to watch until they want it, so the fixed field names must not be a
        // ceiling. Anything the source carried is testable by name.
        var watcher = new Watcher
        {
            Name = "high priority",
            Do = "x",
            When = { new WatchTest { Field = "priority", Op = WatchOps.Is, Value = "high" } },
        };

        Assert.True(Watching.Matches(watcher, Somewhere(), Arrived(data: "{\"priority\":\"high\"}")));
        Assert.False(Watching.Matches(watcher, Somewhere(), Arrived(data: "{\"priority\":\"low\"}")));
    }

    [Fact]
    public void A_paused_watcher_hears_nothing()
    {
        var watcher = new Watcher { Name = "off for now", Do = "x", Paused = true };
        Assert.False(Watching.Matches(watcher, Somewhere(), Arrived()));
    }

    [Fact]
    public void It_may_not_start_fifty_conversations_in_an_hour()
    {
        // A loose filter on a busy feed is a machine for doing exactly that, and the first thing anybody would want is
        // for it to stop. Capped, and the cap says so — otherwise it reads as a watcher that broke.
        var watchers = NewWatchers();
        var watcher = watchers.Add(new Watcher { Name = "everything", Do = "tell me", Limit = 2 });

        Assert.True(watchers.Claim(watcher.Id, Tuesday));
        Assert.True(watchers.Claim(watcher.Id, Tuesday));
        Assert.False(watchers.Claim(watcher.Id, Tuesday));
        Assert.Contains("already fired", watchers.Get(watcher.Id)!.LastError);

        // An hour later it is allowed again: a cap that never forgets is a watcher that works for one afternoon.
        Assert.True(watchers.Claim(watcher.Id, Tuesday.AddHours(2)));
    }

    [Fact]
    public void A_fire_carries_the_conversation_it_started_until_somebody_reads_it()
    {
        // The other half of a watcher actually working. A conversation started in the background that nothing points
        // at is a conversation nobody reads.
        var watchers = NewWatchers();
        var watcher = watchers.Add(new Watcher { Name = "prices", Do = "tell me" });

        watchers.Fired(watcher.Id, "woke-1", "f1", Arrived());

        var waiting = Assert.Single(watchers.Unopened());
        Assert.Equal("woke-1", waiting.Session);
        Assert.Equal("prices", waiting.WatcherName);
        Assert.Equal(1, watchers.Get(watcher.Id)!.Fired);
        Assert.Equal("prices", watchers.Started("woke-1")?.WatcherName);

        Assert.True(watchers.Opened("woke-1"));
        Assert.Empty(watchers.Unopened());
    }

    [Fact]
    public void Deleting_a_watcher_takes_its_unread_nudges_with_it()
    {
        // Left behind, a nudge points at a conversation about something nobody is watching for any more — and after
        // the chat is deleted too, at nothing at all. A nudge that opens nothing is worse than no nudge.
        var watchers = NewWatchers();
        var watcher = watchers.Add(new Watcher { Name = "prices", Do = "tell me" });
        watchers.Fired(watcher.Id, "woke-1", "f1", Arrived());

        Assert.True(watchers.Remove(watcher.Id));

        Assert.Empty(watchers.Unopened());
        Assert.Empty(watchers.Fires());
    }

    [Fact]
    public void Feeds_and_watchers_survive_a_restart()
    {
        var feeds = NewFeeds();
        var feed = feeds.Add(new Feed { Name = "Example news", Source = FeedSources.Rss, Every = "hourly" });
        feeds.Polled(feed.Id, FeedReader.Split(feed, Rss).Items, null, Tuesday);

        var reopened = new FeedStore(_paths[^1], Json);
        var same = reopened.Get(feed.Id)!;

        Assert.Equal("Example news", same.Name);
        Assert.Equal(2, same.Seen);
        Assert.Equal(2, same.Keys.Count);
    }

    // ── publishing one, which is where the mistakes are catchable ───────────────────────────────────────

    [Fact]
    public async Task Publishing_a_feed_reads_it_first_and_refuses_one_that_produces_nothing()
    {
        // The refusal that makes the rest of this trustworthy. A feed nobody has read is a guess, and a guess on a
        // timer is a watcher that waits politely for ever and never says why.
        var feeds = NewFeeds();
        var tool = FeedTools.PublishTool(feeds, (_, _) => Task.FromResult(FeedRead.Failed("The source answered 404.")));

        var result = await Call(tool, new
        {
            name = "Somewhere", source = FeedSources.Rss, url = "https://example.com/feed",
            every = "hourly", split = FeedSplits.One, topic = "things",
        });

        Assert.True(result.IsError);
        Assert.Contains("404", result.Content);
        Assert.Empty(feeds.All());
    }

    [Fact]
    public async Task A_split_that_does_not_actually_split_is_refused()
    {
        // Caught live: a field split mapped to "category" on a source with no categories. It reads as configured and
        // does nothing — every item falls back to the feed's own name, so every topic is the same string and a watcher
        // can only ever be scoped to all of it. The proof read is the one moment this is visible.
        var feeds = NewFeeds();
        var read = new FeedRead(new[]
        {
            new FeedItem { Key = "a1", Title = "One", Topic = "" },
            new FeedItem { Key = "a2", Title = "Two", Topic = "" },
        }, null);
        var tool = FeedTools.PublishTool(feeds, (_, _) => Task.FromResult(read));

        var result = await Call(tool, new
        {
            name = "BBC technology news", source = FeedSources.Rss,
            url = "https://feeds.bbci.co.uk/news/technology/rss.xml",
            every = "every 30 minutes", split = FeedSplits.Field,
            map = new[] { new { field = FeedFields.Topic, path = "category" } },
        });

        Assert.True(result.IsError);
        Assert.Contains("category", result.Content);
        Assert.Contains(FeedSplits.One, result.Content);
        Assert.Empty(feeds.All());
    }

    [Fact]
    public async Task A_feed_that_reads_is_published_and_says_what_came_out()
    {
        var feeds = NewFeeds();
        var read = new FeedRead(new[] { new FeedItem { Key = "a1", Title = "Prices are falling" } }, null);
        var tool = FeedTools.PublishTool(feeds, (_, _) => Task.FromResult(read));

        var result = await Call(tool, new
        {
            name = "Example news", source = FeedSources.Rss, url = "https://example.com/feed",
            every = "every 30 minutes", split = FeedSplits.One, topic = "the news",
        });

        Assert.False(result.IsError);
        Assert.Contains("Prices are falling", result.Content);
        Assert.Single(feeds.All());
    }

    [Fact]
    public async Task A_watcher_cannot_be_scoped_to_a_topic_its_feed_has_never_produced()
    {
        // Almost always a guessed name, and it produces a watcher that can never fire — which looks exactly like one
        // waiting patiently. Answered with what the feed actually calls things.
        var feeds = NewFeeds();
        var watchers = NewWatchers();
        var feed = feeds.Add(new Feed { Name = "Inbox", Source = FeedSources.Json, Topics = { "the printers", "school" } });

        var result = await Call(FeedTools.WatchTool(watchers, feeds), new
        {
            name = "invoice replies", feed = feed.Id, topic = "invoices", act = "tell me",
        });

        Assert.True(result.IsError);
        Assert.Contains("the printers", result.Content);
        Assert.Empty(watchers.All());
    }

    [Fact]
    public async Task A_watcher_with_no_filter_at_all_is_refused()
    {
        // It would start a conversation about every single thing that ever arrives from anywhere.
        var result = await Call(FeedTools.WatchTool(NewWatchers(), NewFeeds()), new
        {
            name = "everything", act = "tell me",
        });

        Assert.True(result.IsError);
        Assert.Contains("everything that ever arrives", result.Content);
    }

    public void Dispose()
    {
        foreach (var p in _paths)
            if (File.Exists(p)) File.Delete(p);
    }
}
