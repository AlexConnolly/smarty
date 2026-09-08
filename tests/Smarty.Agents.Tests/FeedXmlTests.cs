using System.Text.Json;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Reading a feed.
///
/// <para>
/// Exists because a news panel had nowhere to go when its JSON endpoint began answering 403 with a block page, while
/// the site's RSS served perfectly — and no path in this system could walk XML. The conversion has to be predictable
/// enough that a builder can guess the paths from looking at the feed in a browser, so that is what is tested.
/// </para>
/// </summary>
public class FeedXmlTests
{
    private const string Rss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0"><channel>
          <title>r/artificial</title>
          <item><title>First story</title><link>https://example.com/1</link><pubDate>Mon, 17 Aug 2026</pubDate></item>
          <item><title>Second story</title><link>https://example.com/2</link><pubDate>Mon, 17 Aug 2026</pubDate></item>
        </channel></rss>
        """;

    private const string Atom = """
        <?xml version="1.0" encoding="utf-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <title>Example</title>
          <entry><title>An entry</title><link href="https://example.com/a"/></entry>
          <entry><title>Another</title><link href="https://example.com/b"/></entry>
        </feed>
        """;

    private static JsonElement Parse(string xml)
    {
        var json = FeedXml.ToJson(xml);
        Assert.NotNull(json);
        return JsonDocument.Parse(json!).RootElement;
    }

    [Fact]
    public void An_rss_document_is_recognised_and_reads_at_the_path_you_would_guess()
    {
        Assert.True(FeedXml.Looks(Rss));

        var root = Parse(Rss);
        var items = root.GetProperty("rss").GetProperty("channel").GetProperty("item");

        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("First story", items[0].GetProperty("title").GetString());
        Assert.Equal("https://example.com/2", items[1].GetProperty("link").GetString());
    }

    [Fact]
    public void The_wildcard_path_language_reaches_a_feeds_items()
    {
        // The whole point: the existing map language must work on a feed with no special cases.
        var root = Parse(Rss);
        var titles = WidgetShape.Walk(root, "rss.channel.item.*.title");

        Assert.NotNull(titles);
        Assert.Contains("First story", titles!.ToJsonString());
        Assert.Contains("Second story", titles!.ToJsonString());
    }

    [Fact]
    public void An_atom_entrys_link_lives_in_an_attribute_and_is_still_reachable()
    {
        // Atom puts the url in an attribute — <link href="…"/> — so an entry's link is unreachable unless attributes
        // become ordinary fields.
        var root = Parse(Atom);
        var entries = root.GetProperty("feed").GetProperty("entry");

        Assert.Equal(2, entries.GetArrayLength());
        Assert.Equal("https://example.com/a", entries[0].GetProperty("link").GetProperty("href").GetString());
        Assert.Equal("Another", entries[1].GetProperty("title").GetString());
    }

    [Fact]
    public void A_namespace_prefix_is_dropped_so_the_path_is_writable()
    {
        // dc:creator in one feed and creator in the next, and a path containing a brace-wrapped namespace uri is
        // unusable by hand.
        var xml = """
            <?xml version="1.0"?>
            <rss xmlns:dc="http://purl.org/dc/elements/1.1/"><channel>
              <item><title>Post</title><dc:creator>someone</dc:creator></item>
            </channel></rss>
            """;

        var root = Parse(xml);
        var item = root.GetProperty("rss").GetProperty("channel").GetProperty("item");
        Assert.Equal("someone", item.GetProperty("creator").GetString());
    }

    [Fact]
    public void A_single_repeated_element_stays_an_object_rather_than_a_one_item_array()
    {
        // Otherwise every path would need a "0" in it, which reads horribly and differs from the multi-item case.
        var xml = """
            <?xml version="1.0"?>
            <rss><channel><item><title>Only one</title></item></channel></rss>
            """;

        var root = Parse(xml);
        var item = root.GetProperty("rss").GetProperty("channel").GetProperty("item");

        Assert.Equal(JsonValueKind.Object, item.ValueKind);
        Assert.Equal("Only one", item.GetProperty("title").GetString());
    }

    [Fact]
    public void Reddits_real_feed_parses_and_its_posts_are_reachable()
    {
        // The feed that started all of this, saved verbatim: Atom, namespaced, with CDATA-wrapped html in every
        // entry. Synthetic xml proves the conversion; this proves the conversion against what a real site serves.
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "reddit-artificial.rss");
        var xml = File.ReadAllText(path);

        Assert.True(FeedXml.Looks(xml));
        var root = Parse(xml);

        var entries = root.GetProperty("feed").GetProperty("entry");
        Assert.Equal(JsonValueKind.Array, entries.ValueKind);
        Assert.True(entries.GetArrayLength() >= 5, "a live subreddit feed carries more than a handful of posts");

        // A title and a link on every entry is exactly what a panel needs, and the link is an attribute.
        foreach (var entry in entries.EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("title").GetString()));
            Assert.StartsWith("https://", entry.GetProperty("link").GetProperty("href").GetString());
        }

        // And the map language reaches them, which is the whole reason for the conversion.
        var titles = WidgetShape.Walk(root, "feed.entry.*.title");
        Assert.NotNull(titles);
        Assert.True(titles!.AsArray().Count >= 5);
    }

    [Fact]
    public void Json_is_left_alone_and_nonsense_is_refused()
    {
        Assert.False(FeedXml.Looks("{\"hits\":[]}"));
        Assert.Null(FeedXml.ToJson("<rss><unclosed>"));
    }
}
