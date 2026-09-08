using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Smarty.Api;

/// <summary>
/// One field of a panel kind's data model — the contract between the loader and the component.
///
/// <para>
/// Declaring the shape is what makes the two halves independently fixable. Before this, a component read the feed's
/// raw response and dug through it defensively, so every change in the source's shape was a fault in the COMPONENT:
/// a throw in the browser, a blank box, a repair job. With a declared model, a source that moves its fields breaks
/// the LOADER — server side, where it can be tested without a browser and fixed without touching the rendering.
/// </para>
/// </summary>
public sealed class WidgetField
{
    public string Name { get; set; } = "";

    /// <summary>string | number | boolean | list — enough to render sensibly and to check the loader's output.</summary>
    public string Type { get; set; } = "string";

    public string Description { get; set; } = "";

    /// <summary>A real example value, so the component's author knows what they are formatting.</summary>
    public string? Example { get; set; }

    /// <summary>False for something the source doesn't always carry. Missing required fields are a loader fault.</summary>
    public bool Required { get; set; } = true;
}

/// <summary>
/// How a panel's data is produced: where it comes from and how it becomes the declared shape.
///
/// <para>
/// Entirely server-side, which is the point. The client asks for the panel's data and receives it ready to render —
/// it never learns where the data came from, so a source can move from an API to a browser scrape without the
/// component changing a character.
/// </para>
/// </summary>
public sealed class WidgetLoader
{
    /// <summary>internal | http | browser.</summary>
    public string Mode { get; set; } = LoaderModes.Http;

    /// <summary>For http and browser: the address, with <c>{parameter}</c> holes.</summary>
    public string? Url { get; set; }

    /// <summary>Headers for an http load. Never leave the server — one of them may be a key.</summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>For internal: agenda | lists | schedule | tasks | projects.</summary>
    public string? Internal { get; set; }

    /// <summary>
    /// For http and internal: field name → a dotted path into the response. <c>offers.price</c>,
    /// <c>hourly.temperature_2m.0</c>, <c>searchResult.*.flightNumber</c> for a list.
    ///
    /// <para>
    /// A path language rather than a script, because a script needs an engine and an engine is a lot of new surface
    /// for "reach into an object". The one place a real expression IS available is the browser, which is already a
    /// JavaScript engine — see <see cref="Expression"/>.
    /// </para>
    /// </summary>
    public Dictionary<string, string> Map { get; set; } = new();

    /// <summary>
    /// For browser: a JavaScript expression evaluated IN THE PAGE, returning the shaped object directly.
    ///
    /// <para>
    /// The browser earns its place twice over. Large sites refuse a plain server request — eBay answers 403 to curl
    /// and to us, with any headers — and the interesting data on your OWN listing (what it sold for, who is watching
    /// it) is only visible to someone signed in as you. The user's Chrome is signed in as them. And since it is a
    /// JavaScript engine already, the mapping can be an expression rather than a path language, which is far more
    /// capable for the price of nothing.
    /// </para>
    /// <para>
    /// Still no model at refresh time: a navigate and one fixed expression, both settled when the kind was built.
    /// </para>
    /// </summary>
    public string? Expression { get; set; }

    public bool Fetches => LoaderModes.ServerLoads(Mode) &&
                           (Mode != LoaderModes.Internal || !string.IsNullOrWhiteSpace(Internal));

    /// <summary>This loader with a particular instance's parameter values substituted in.</summary>
    public WidgetLoader For(IReadOnlyDictionary<string, string>? values) => new()
    {
        Mode = Mode,
        Url = Fill(Url, values, escape: true),
        Headers = new Dictionary<string, string>(Headers, StringComparer.OrdinalIgnoreCase),
        Internal = Internal,
        Map = new Dictionary<string, string>(Map, StringComparer.OrdinalIgnoreCase),
        // Not escaped: this is code, and percent-encoding a value inside a JS expression corrupts it.
        Expression = Fill(Expression, values, escape: false),
    };

    /// <summary>
    /// What a parameter hole looks like, and — far more importantly — what does NOT count as one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A browser loader's expression is JavaScript, and JavaScript is full of braces that are nobody's parameter. Two
    /// kinds bit: a regex quantifier, and a template literal. <c>\d{3}</c> was read as a parameter called "3", so
    /// publishing was refused with "the loader has {3} in it but you haven't declared that parameter" — an objection
    /// about something that cannot be declared and must not be substituted. The worker rewrote everything else twelve
    /// times over while the panel sat on "Building the data feed", because the one thing it was being asked to change
    /// was a quantifier in a regular expression.
    /// </para>
    /// <para>
    /// So a hole must be shaped like a name — a letter or underscore first, which rules the digits out — and must not
    /// follow a <c>$</c>, which rules out <c>${…}</c>. The same pattern does the finding AND the filling, because the
    /// other half of the bug is silent: substituting into <c>${seller}</c> turns a variable reference into a syntax
    /// error in someone's browser, with no complaint anywhere.
    /// </para>
    /// </remarks>
    private static readonly System.Text.RegularExpressions.Regex Hole =
        new(@"(?<!\$)\{([A-Za-z_]\w*)\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Substitute <c>{name}</c> holes, escaping only where the result is a url.</summary>
    public static string? Fill(string? template, IReadOnlyDictionary<string, string>? values, bool escape)
    {
        if (string.IsNullOrWhiteSpace(template) || values is null || values.Count == 0) return template;

        // A hole with no value stays exactly as it was: the loader reports an unfilled url by name, which is a far
        // better message than a request to an address with a gap in it.
        return Hole.Replace(template!, m =>
            values.TryGetValue(m.Groups[1].Value, out var value)
                ? (escape ? Uri.EscapeDataString(value ?? "") : value ?? "")
                : m.Value);
    }

    /// <summary>
    /// The <c>{holes}</c> in the URL, so a kind can't declare one it never fills.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The URL, and deliberately NOT the expression. A brace in a url is always a parameter; a brace in JavaScript is
    /// usually not, and the three that turned up are all ordinary code: a regex quantifier (<c>\d{3}</c>), a template
    /// literal (<c>${id}</c>), and object shorthand (<c>JSON.stringify({url})</c>). The first of those refused a
    /// publish twelve times over an objection the worker could not act on, because the thing it was being told to
    /// declare was a quantifier.
    /// </para>
    /// <para>
    /// Shorthand is the one that settles it: <c>{followers}</c> in an expression is indistinguishable from a parameter
    /// reference, and it is how half of these expressions return their data. So the expression is not policed. A hole
    /// left in it with nothing behind it survives into the page as a literal brace, the JavaScript fails, and the load
    /// says so — a real message from the real attempt, which is a better answer than a guess made at publish time.
    /// </para>
    /// </remarks>
    public IEnumerable<string> Holes() =>
        Hole.Matches(Url ?? "")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Who does the fetching, and why.
///
/// <para>
/// The distinction that matters is not "how hard is this" — it is <b>what does the fetcher need to be</b>. Something
/// the page can reach on its own should be reached by the page. Something needing a key, or hidden behind CORS, needs
/// a server. Something only visible when signed in as the user needs to BE the user, which means their browser. Get
/// that ordering wrong and you end up driving a whole browser to read a public JSON endpoint, or — worse — trying to
/// proxy a video stream through a server that had no business being in the path.
/// </para>
/// </summary>
public static class LoaderModes
{
    /// <summary>Read what the system already holds. No network, never stale.</summary>
    public const string Internal = "internal";

    /// <summary>
    /// The COMPONENT does it, in the page.
    ///
    /// <para>
    /// For anything the browser can reach by itself: an endpoint on the local network, a public API that allows
    /// cross-origin requests, and above all things that are not "data" at all — an image that refreshes, a video
    /// stream, an embedded map. A camera on the same network as the browser is the clearest case: the bytes have no
    /// reason to travel through the server, and a stream proxied through one is a stream that stutters.
    /// </para>
    /// <para>
    /// The server loads nothing in this mode, so there is no shaping and no stored data — the component is handed
    /// its parameters and gets on with it.
    /// </para>
    /// </summary>
    public const string Client = "client";

    /// <summary>
    /// A server-side GET. For an endpoint needing a key that must not reach the page, one that refuses
    /// cross-origin requests, or one whose response wants shaping before every tab renders it.
    /// </summary>
    public const string Http = "http";

    /// <summary>
    /// Loaded in the user's own Chrome.
    ///
    /// <para>
    /// Only when the fetcher has to BE them: a page behind a login, their own listing's seller view, their own
    /// orders. Not a general fallback for a site that is merely awkward — it costs a tab and a page load every
    /// refresh, and using it where a plain request would do is pure waste.
    /// </para>
    /// </summary>
    public const string Browser = "browser";

    public static readonly string[] All = { Internal, Client, Http, Browser };

    public static bool Known(string? mode) =>
        mode is { Length: > 0 } && All.Contains(mode, StringComparer.OrdinalIgnoreCase);

    /// <summary>Does the server do any loading? False for <see cref="Client"/>, where the component does it all.</summary>
    public static bool ServerLoads(string? mode) =>
        Known(mode) && !string.Equals(mode, Client, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Turning a source response into the declared shape.
///
/// <para>
/// The whole reason this is a separate step: the component then renders a known object and cannot be broken by the
/// source. Shaping happens once per refresh on the server rather than on every render in every browser tab.
/// </para>
/// </summary>
public static class WidgetShape
{
    /// <summary>
    /// Pull the mapped fields out of a source response.
    /// </summary>
    /// <param name="model">Declared fields — anything not declared is not returned, so the component's contract holds.</param>
    /// <returns>The shaped object, and the required fields the source didn't carry.</returns>
    public static (JsonObject Data, List<string> Missing) Apply(
        JsonElement source, IReadOnlyList<WidgetField> model, IReadOnlyDictionary<string, string> map)
    {
        var shaped = new JsonObject();
        var missing = new List<string>();

        foreach (var field in model)
        {
            if (!map.TryGetValue(field.Name, out var path) || string.IsNullOrWhiteSpace(path))
            {
                if (field.Required) missing.Add(field.Name);
                continue;
            }

            var value = Walk(source, path);
            if (value is null)
            {
                if (field.Required) missing.Add(field.Name);
                continue;
            }
            shaped[field.Name] = value;
        }

        return (shaped, missing);
    }

    /// <summary>
    /// Follow a dotted path. A numeric step indexes an array; <c>*</c> maps the rest of the path over one.
    ///
    /// <para>
    /// Deliberately small: dotted names, integer indexes, and one wildcard for "this field of every element". That
    /// covers every real feed seen so far — a price two levels down, the first hour of a forecast, a flight number
    /// from each row — and stops well short of needing a query language nobody can debug.
    /// </para>
    /// </summary>
    public static JsonNode? Walk(JsonElement node, string path)
    {
        var steps = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return Step(node, steps, 0);
    }

    private static JsonNode? Step(JsonElement node, string[] steps, int at)
    {
        if (at >= steps.Length) return Convert(node);
        var step = steps[at];

        if (step == "*")
        {
            if (node.ValueKind != JsonValueKind.Array) return null;
            var list = new JsonArray();
            foreach (var item in node.EnumerateArray())
            {
                var mapped = Step(item, steps, at + 1);
                if (mapped is not null) list.Add(mapped);
            }
            return list;
        }

        if (int.TryParse(step, out var index))
        {
            if (node.ValueKind != JsonValueKind.Array) return null;
            if (index < 0 || index >= node.GetArrayLength()) return null;
            return Step(node[index], steps, at + 1);
        }

        if (node.ValueKind != JsonValueKind.Object) return null;
        // Case-insensitive, because a source that renames Price to price should not silently empty a panel.
        foreach (var prop in node.EnumerateObject())
            if (string.Equals(prop.Name, step, StringComparison.OrdinalIgnoreCase))
                return Step(prop.Value, steps, at + 1);

        return null;
    }

    private static JsonNode? Convert(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => JsonNode.Parse(value.GetRawText()),
    };
}
