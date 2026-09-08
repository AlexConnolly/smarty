using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Smarty.Api;

/// <summary>
/// XML — and therefore RSS and Atom — as JSON, so the loader's existing path language reaches it.
///
/// <para>
/// The gap this closes was found the hard way. A news panel ran on Reddit's json endpoints, which began answering 403
/// with a block page; the only thing Reddit still served was its RSS feed, and the loader could not read it, because
/// every path in this system walks a <see cref="JsonElement"/>. So the choice on offer was "find another JSON source"
/// — which is what happened — when the correct answer was sitting right there behind a content type nobody supported.
/// </para>
/// <para>
/// Worth having beyond that one panel: RSS and Atom are how most of the world publishes a list of things that changed,
/// they need no key, and they are far less aggressively rate-limited than the JSON APIs beside them. A feed reader is
/// the single most reusable data source a home page can have.
/// </para>
/// <para>
/// The conversion is deliberately dull and predictable, because a builder has to be able to guess the resulting paths
/// without running anything: an element becomes an object, repeated siblings become an array, attributes become plain
/// fields, and an element with only text becomes that text.
/// </para>
/// </summary>
public static class FeedXml
{
    /// <summary>Does this body look like XML rather than JSON?</summary>
    public static bool Looks(string raw)
    {
        var head = raw.AsSpan().TrimStart();
        return head.StartsWith("<?xml") || head.StartsWith("<rss") || head.StartsWith("<feed")
               || head.StartsWith("<RDF") || head.StartsWith("<rdf:RDF");
    }

    /// <summary>
    /// Convert XML to JSON, or null if it will not parse.
    /// </summary>
    public static string? ToJson(string raw)
    {
        try
        {
            var doc = XDocument.Parse(raw, LoadOptions.None);
            if (doc.Root is null) return null;

            // The root element's NAME becomes the outermost key, so an RSS document reads rss.channel.item and an
            // Atom one reads feed.entry — which is what someone looking at the feed in a browser would expect.
            var root = new JsonObject { [Local(doc.Root.Name)] = Convert(doc.Root) };
            return root.ToJsonString();
        }
        catch { return null; }
    }

    private static JsonNode? Convert(XElement element)
    {
        var children = element.Elements().ToList();
        var attributes = element.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList();

        // A leaf: just its text. This is what makes rss.channel.item.*.title a string rather than an object with a
        // single mystery key in it.
        if (children.Count == 0 && attributes.Count == 0)
            return Text(element);

        var obj = new JsonObject();

        // Attributes first, as ordinary fields. Atom puts the link in one — <link href="…"/> — so an entry's url is
        // unreachable without this.
        foreach (var a in attributes)
            obj[Local(a.Name)] = JsonValue.Create(a.Value);

        // Text alongside children is rare but real (mixed content); kept under a name that cannot collide with an
        // element, since element names cannot contain a hash.
        var own = Text(element);
        if (own is not null && children.Count > 0) obj["#text"] = own;
        else if (own is not null && attributes.Count > 0) obj["#text"] = own;

        foreach (var group in children.GroupBy(c => Local(c.Name)))
        {
            var items = group.ToList();

            // Repeated siblings become an array — this is what turns <item> ×20 into a list the wildcard can map
            // over. A single occurrence stays a plain object, because forcing everything into arrays would make every
            // path start with a "0" and read horribly.
            if (items.Count == 1)
            {
                obj[group.Key] = Convert(items[0]);
                continue;
            }

            var array = new JsonArray();
            foreach (var item in items) array.Add(Convert(item));
            obj[group.Key] = array;
        }

        return obj;
    }

    /// <summary>An element's own text, trimmed, or null when it has none worth keeping.</summary>
    private static JsonNode? Text(XElement element)
    {
        var text = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
        return text.Length == 0 ? null : JsonValue.Create(text);
    }

    /// <summary>
    /// The local name, with the namespace dropped.
    ///
    /// <para>
    /// Namespaces are what make feed paths unguessable: the same field is <c>dc:creator</c> in one feed and
    /// <c>creator</c> in another, and a path with a brace-wrapped namespace uri in it is unusable. Dropping the prefix
    /// can in principle collide; in feeds it does not, and being able to write the path you expect is worth more.
    /// </para>
    /// </summary>
    private static string Local(XName name) => name.LocalName;
}
