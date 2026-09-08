using System.Text.Json;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// The user's browser, driven from wherever the user actually is.
/// <para>
/// This exists for one situation and it is the situation that matters: a worker hits a login wall while the user is
/// nowhere near the machine. Telling them to go and sign in is not an answer — they are on a train. So the page
/// comes to them: a screenshot after every action, and their taps and keystrokes forwarded back to the real tab.
/// Slow, ugly, and entirely sufficient for typing a password.
/// </para>
/// <para>
/// Built on the browser tools that already work rather than a CDP screencast, deliberately: screenshot, click by
/// coordinate, type_text and press_key are live, tested and shaped for exactly this. A frame per action is not a
/// compromise here — nobody needs sixty of them to fill in a login form.
/// </para>
/// <para>
/// What the user types is forwarded verbatim to the page and is never logged, never stored, and never enters a
/// transcript or a model prompt. The model is not in this loop at all: it asked for a login and is parked. That is
/// the point — a password should reach the login form and nothing else.
/// </para>
/// </summary>
public sealed class RemoteBrowser
{
    private readonly McpHub _mcp;

    public RemoteBrowser(McpHub mcp) => _mcp = mcp;

    /// <summary>Tabs that have refused to be captured. Whatever they report themselves as, they are no use.</summary>
    private readonly HashSet<int> _poisoned = new();

    /// <summary>The id of the tab a tabs_create just made, read out of the context it returns.</summary>
    private static int? NewTabId(string? content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content ?? "{}");
            if (!doc.RootElement.TryGetProperty("availableTabs", out var tabs) ||
                tabs.ValueKind != JsonValueKind.Array) return null;

            int? last = null;
            foreach (var tab in tabs.EnumerateArray())
                if (tab.TryGetProperty("tabId", out var id) && id.ValueKind == JsonValueKind.Number)
                    last = id.GetInt32();
            return last; // the one just opened is the newest in the group
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private AgentTool? Tool(string suffix) => _mcp.AllTools.FirstOrDefault(t =>
        t.Name.EndsWith("_" + suffix, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(t.Name, suffix, StringComparison.OrdinalIgnoreCase));

    public bool Available => Tool("screenshot") is not null && Tool("click") is not null;

    /// <summary>
    /// A tab that can actually be photographed, and the same one every action goes to.
    /// <para>
    /// Chrome refuses to capture another extension's page, and "the first tab in the group" is whatever happens to
    /// be sitting there — so a stray chrome-extension:// tab made every screenshot fail while navigation quietly
    /// succeeded somewhere else. Pick a real page, and pin everything to it.
    /// </para>
    /// </summary>
    private async Task<int?> PageTabAsync(CancellationToken ct)
    {
        if (Tool("tabs_context") is not { } context) return null;

        var output = await Run(context, "{\"createIfEmpty\":true}", ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(output.Content ?? "{}");
            if (!doc.RootElement.TryGetProperty("availableTabs", out var tabs) ||
                tabs.ValueKind != JsonValueKind.Array) return null;

            int? fallback = null;
            foreach (var tab in tabs.EnumerateArray())
            {
                if (!tab.TryGetProperty("tabId", out var id) || id.ValueKind != JsonValueKind.Number) continue;
                fallback ??= id.GetInt32();

                var url = tab.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                bool uncapturable = url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
                                    || url.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase)
                                    || url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase);
                if (!uncapturable && !_poisoned.Contains(id.GetInt32())) return id.GetInt32();
            }
            // Every tab in the group is an internal page — an extension's own UI, most often, which Chrome will
            // never let us photograph. Handing back the first one anyway just reproduces the error every frame,
            // so open a real tab and use that. A blank page is a far better answer than a permanent failure.
            if (Tool("tabs_create") is { } create)
            {
                await Run(create, "{}", ct).ConfigureAwait(false);
                var after = await Run(context, "{\"createIfEmpty\":true}", ct).ConfigureAwait(false);
                try
                {
                    using var fresh = JsonDocument.Parse(after.Content ?? "{}");
                    if (fresh.RootElement.TryGetProperty("availableTabs", out var list) &&
                        list.ValueKind == JsonValueKind.Array)
                        foreach (var tab in list.EnumerateArray())
                        {
                            var url = tab.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                            if (url.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
                                url.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase) ||
                                url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase)) continue;
                            if (tab.TryGetProperty("tabId", out var id) && id.ValueKind == JsonValueKind.Number)
                                return id.GetInt32();
                        }
                }
                catch (JsonException)
                {
                    // fall through to the fallback below
                }
            }

            return fallback;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A picture of the tab, as a URL the chat can show. Returns null when there's no browser connected.</summary>
    public async Task<string?> ViewAsync(int? tabId, CancellationToken ct)
    {
        if (Tool("screenshot") is not { } shot) return null;

        // Opens a tab if the session hasn't got one, and skips pages Chrome will not photograph.
        tabId ??= await PageTabAsync(ct).ConfigureAwait(false);

        var args = tabId is null ? "{}" : $"{{\"tabId\":{tabId}}}";
        var output = await Run(shot, args, ct).ConfigureAwait(false);

        // A screenshot taken while the page is still arriving fails, and the first look after a navigation is
        // exactly that moment — which is how typing an address produced no picture and no explanation. One retry
        // after a breath is the difference between "nothing happened" and a working browser.
        if ((output.Content ?? "").IndexOf("](", StringComparison.Ordinal) < 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);
            output = await Run(shot, args, ct).ConfigureAwait(false);
        }

        // Still refusing. Some tabs simply cannot be captured whatever they claim to be — one reporting itself as
        // about:blank was rejected on every frame as belonging to another extension, presumably still carrying that
        // extension's debugger. There is no interrogating our way out of that, so stop trying to fix the tab and
        // replace it: a tab created a moment ago belongs to nobody else.
        if ((output.Content ?? "").IndexOf("](", StringComparison.Ordinal) < 0 &&
            Tool("tabs_create") is { } create)
        {
            var created = await Run(create, "{}", ct).ConfigureAwait(false);
            if (NewTabId(created.Content) is { } replacement)
            {
                _poisoned.Add(tabId ?? 0);
                output = await Run(shot, $"{{\"tabId\":{replacement}}}", ct).ConfigureAwait(false);
            }
        }

        // The image sink has already stored it; the tool hands back markdown, and the URL is what we want.
        var text = output.Content ?? "";
        int open = text.IndexOf("](", StringComparison.Ordinal);
        if (open < 0)
        {
            // Whatever went wrong, say what it was. Reporting every failure here as "no browser connected" hid a
            // working browser with a failing screenshot, and left the user tapping a button that did nothing.
            LastError = (text.Length > 0 ? text : "the screenshot came back empty")
                        + (tabId is null ? " (no tab chosen)" : $" (tab {tabId})");
            return null;
        }
        int close = text.IndexOf(')', open);
        LastError = null;
        return close > open ? text[(open + 2)..close] : null;
    }

    /// <summary>Why the last view failed, when it did. Shown to the user rather than swallowed.</summary>
    public string? LastError { get; private set; }

    /// <summary>One action on the page. Everything the user can do to a login form: point at it, type into it,
    /// press a key.</summary>
    public async Task<string> ActAsync(string kind, JsonElement body, CancellationToken ct)
    {
        switch (kind)
        {
            case "click":
            {
                if (Tool("click") is not { } click) return "no browser connected";
                int x = Int(body, "x"), y = Int(body, "y");
                int? tab = body.TryGetProperty("tabId", out var t) && t.ValueKind == JsonValueKind.Number
                    ? t.GetInt32() : await PageTabAsync(ct).ConfigureAwait(false);
                var args = tab is null
                    ? $"{{\"coordinate\":[{x},{y}]}}"
                    : $"{{\"coordinate\":[{x},{y}],\"tabId\":{tab}}}";
                return (await Run(click, args, ct).ConfigureAwait(false)).Content ?? "";
            }

            case "type":
            {
                if (Tool("type_text") is not { } type) return "no browser connected";
                var text = body.TryGetProperty("text", out var v) ? v.GetString() ?? "" : "";
                if (text.Length == 0) return "nothing to type";
                // Serialised through the JSON writer so quotes, backslashes and newlines in a password survive.
                var args = JsonSerializer.Serialize(new { text });
                return (await Run(type, args, ct).ConfigureAwait(false)).Content ?? "";
            }

            case "key":
            {
                if (Tool("press_key") is not { } press) return "no browser connected";
                var key = body.TryGetProperty("key", out var v) ? v.GetString() ?? "Enter" : "Enter";
                return (await Run(press, JsonSerializer.Serialize(new { key }), ct).ConfigureAwait(false)).Content ?? "";
            }

            case "navigate":
            {
                if (Tool("navigate") is not { } go) return "no browser connected";
                var url = body.TryGetProperty("url", out var v) ? v.GetString() ?? "" : "";
                if (url.Length == 0) return "no url";
                var tabId = await PageTabAsync(ct).ConfigureAwait(false);
                var payload = tabId is null
                    ? JsonSerializer.Serialize(new { url })
                    : JsonSerializer.Serialize(new { url, tabId });
                return (await Run(go, payload, ct).ConfigureAwait(false)).Content ?? "";
            }

            default:
                return $"unknown action \"{kind}\"";
        }
    }

    /// <summary>
    /// The page's CSS viewport, which is what a click coordinate is measured in.
    /// <para>
    /// A screenshot comes back in DEVICE pixels — 1719x1159 on a display scaled to 125% — while
    /// Input.dispatchMouseEvent takes CSS pixels. Mapping a tap against the image's own size therefore lands it a
    /// quarter of the way further down and to the right, which reads as "clicks don't register" when in fact they
    /// register somewhere else. Fetched once when the panel opens, not per frame.
    /// </para>
    /// </summary>
    public async Task<(int Width, int Height)?> ViewportAsync(CancellationToken ct)
    {
        if (Tool("javascript") is not { } js) return null;

        var tabId = await PageTabAsync(ct).ConfigureAwait(false);
        var expression = "JSON.stringify([window.innerWidth, window.innerHeight])";
        var payload = tabId is null
            ? JsonSerializer.Serialize(new { expression })
            : JsonSerializer.Serialize(new { expression, tabId });

        var output = await Run(js, payload, ct).ConfigureAwait(false);
        var text = output.Content ?? "";
        var open = text.IndexOf('[');
        var close = text.IndexOf(']', open + 1);
        if (open < 0 || close < 0) return null;

        try
        {
            using var doc = JsonDocument.Parse(text[open..(close + 1)]);
            var w = doc.RootElement[0].GetInt32();
            var h = doc.RootElement[1].GetInt32();
            return w > 0 && h > 0 ? (w, h) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The tabs available, so the view can be pointed at the one holding the login.</summary>
    public async Task<string?> TabsAsync(CancellationToken ct)
    {
        if (Tool("tabs_context") is not { } tabs) return null;
        return (await Run(tabs, "{\"createIfEmpty\":true}", ct).ConfigureAwait(false)).Content;
    }

    private static Task<ToolOutput> Run(AgentTool tool, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(argsJson);
        return tool.InvokeAsync(new ToolCallArguments(doc.RootElement.Clone()), ct);
    }

    private static int Int(JsonElement body, string name) =>
        body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
