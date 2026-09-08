using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Smarty.Api;

/// <summary>What a link turns out to be about: enough to show a card instead of a bare URL.</summary>
public sealed record LinkPreview(string Url, string? Title, string? Image, string? Site);

/// <summary>
/// Turns the links in an answer into cards. The fetch happens here rather than through a tool because it is not
/// reasoning — it is two meta tags — so it costs no tokens, no browser tab and no model turn.
/// <para>
/// Two things this deliberately does NOT do. It does not fetch anything that isn't plainly public: the model
/// composes these URLs, Smarty runs on the user's own machine, and a link to localhost or a private range would
/// make a stray URL into a probe of their network. And it does not hand the remote image URL to the browser —
/// the bytes are pulled here and re-served locally, because hotlinking is refused often enough to be the common
/// case, and asking the user's browser for it would leak their address and referrer to whatever was linked.
/// </para>
/// </summary>
public sealed class LinkPreviews
{
    private const int MaxLinksPerMessage = 6;
    private const int MaxHeadBytes = 256 * 1024; // og tags live in <head>; no reason to read a whole page
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    private readonly ConcurrentDictionary<string, LinkPreview?> _cache = new(StringComparer.Ordinal);
    private readonly HttpClient _http;
    private readonly Func<byte[], string, string?>? _images;

    public LinkPreviews(HttpClient http, Func<byte[], string, string?>? images = null)
    {
        _http = http;
        _images = images;
    }

    /// <summary>The links in a message, in the order they appear, deduplicated. Markdown image sources are left
    /// out — they are already being shown, and a card for one is the same picture twice.</summary>
    public static IReadOnlyList<string> Extract(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return Array.Empty<string>();

        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(markdown, @"!\[[^\]]*\]\(([^)\s]+)"))
            shown.Add(m.Groups[1].Value);

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(markdown, @"https?://[^\s<>""')\]]+"))
        {
            var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            if (shown.Contains(url) || !seen.Add(url)) continue;
            found.Add(url);
            if (found.Count == MaxLinksPerMessage) break;
        }
        return found;
    }

    /// <summary>Preview every link in a message. Order is preserved and failures are dropped: a card that can't be
    /// built is not worth telling the user about — the link itself is still in the text.</summary>
    public async Task<IReadOnlyList<LinkPreview>> ForMessageAsync(string markdown, CancellationToken ct)
    {
        var urls = Extract(markdown);
        if (urls.Count == 0) return Array.Empty<LinkPreview>();

        var results = await Task.WhenAll(urls.Select(u => GetAsync(u, ct))).ConfigureAwait(false);
        return results.Where(p => p is not null).Select(p => p!).ToList();
    }

    /// <summary>
    /// Rewrite the remote images in an answer to locally served copies, so <c>![…](…)</c> actually shows something.
    /// <para>
    /// A worker that found a photo writes the page's own image URL, and pointing the user's browser at it is a
    /// hotlink — refused often enough to be the normal case, and a leak of their address and referrer when it
    /// isn't. The bytes come here instead, exactly as they do for a link card. What this really fixes is a loop:
    /// with no route that reliably shows a picture, a run asked for photos tried screenshots, then querySelectorAll,
    /// then screenshots again, because every attempt looked like it might be the one that worked.
    /// </para>
    /// <para>An image that can't be mirrored keeps its original URL — it may still load, and a broken picture is a
    /// better answer than a silently deleted one.</para>
    /// </summary>
    public async Task<string> MirrorImagesAsync(string markdown, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(markdown) || !markdown.Contains("](http", StringComparison.Ordinal))
            return markdown;

        var matches = Regex.Matches(markdown, @"!\[[^\]]*\]\((https?://[^)\s]+)\)");
        if (matches.Count == 0) return markdown;

        var remote = matches.Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
        var mirrored = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var url in remote)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            var local = await MirrorAsync(url, uri, ct).ConfigureAwait(false);
            if (local is { Length: > 0 }) mirrored[url] = local;
        }

        if (mirrored.Count == 0) return markdown;
        return Regex.Replace(markdown, @"(!\[[^\]]*\]\()(https?://[^)\s]+)(\))", m =>
            mirrored.TryGetValue(m.Groups[2].Value, out var local)
                ? m.Groups[1].Value + local + m.Groups[3].Value
                : m.Value);
    }

    public async Task<LinkPreview?> GetAsync(string url, CancellationToken ct)
    {
        if (_cache.TryGetValue(url, out var cached)) return cached;

        var preview = await FetchAsync(url, ct).ConfigureAwait(false);
        _cache[url] = preview;
        return preview;
    }

    private async Task<LinkPreview?> FetchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !await IsPublicAsync(uri).ConfigureAwait(false))
            return null;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            // Sites serve og tags to crawlers; ask like one rather than pretending to be a person.
            request.Headers.TryAddWithoutValidation("User-Agent", "SmartyBot/1.0 (+link preview)");
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentType?.MediaType is { } type &&
                !type.Contains("html", StringComparison.OrdinalIgnoreCase)) return null;

            var head = await ReadHeadAsync(response, timeout.Token).ConfigureAwait(false);
            string? title = Meta(head, "og:title") ?? Meta(head, "twitter:title") ?? TitleTag(head);
            string? image = Meta(head, "og:image") ?? Meta(head, "twitter:image") ?? Meta(head, "og:image:url");
            string? site = Meta(head, "og:site_name") ?? uri.Host.Replace("www.", "");

            if (title is null && image is null) return null;

            string? local = image is null ? null : await MirrorAsync(image, uri, timeout.Token).ConfigureAwait(false);
            return new LinkPreview(url, title, local, site);
        }
        catch (Exception)
        {
            // A preview is a nicety. Anything that goes wrong means no card, never a failed answer.
            return null;
        }
    }

    /// <summary>A meta tag's content, matched on either <c>property</c> or <c>name</c> — the og spec says one and
    /// half the web uses the other, and attribute order is nobody's guarantee.</summary>
    private static string? Meta(string html, string key)
    {
        foreach (Match tag in Regex.Matches(html, @"<meta\b[^>]*>", RegexOptions.IgnoreCase))
        {
            if (!Regex.IsMatch(tag.Value, $@"(?:property|name)\s*=\s*[""']{Regex.Escape(key)}[""']",
                    RegexOptions.IgnoreCase)) continue;

            var content = Regex.Match(tag.Value, @"content\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
            if (content.Success && Clean(content.Groups[1].Value) is { Length: > 0 } value) return value;
        }
        return null;
    }

    private static string? TitleTag(string html)
    {
        var match = Regex.Match(html, @"<title[^>]*>(.*?)</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success && Clean(match.Groups[1].Value) is { Length: > 0 } title ? title : null;
    }

    private static string Clean(string value) => WebUtility.HtmlDecode(value).Trim();

    private static async Task<string> ReadHeadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxHeadBytes];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            // Everything wanted is in <head>; stop as soon as it closes.
            if (System.Text.Encoding.UTF8.GetString(buffer, 0, total).Contains("</head", StringComparison.OrdinalIgnoreCase))
                break;
        }
        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }

    /// <summary>Pull an image's bytes and re-serve them locally. Returns null when there's nowhere to put them,
    /// which simply means a card with no picture.</summary>
    /// <summary>
    /// Take a copy of a remote image and return the local path it can be shown from.
    /// </summary>
    /// <remarks>
    /// Public so pictures handed over deliberately go through the same door as the ones found in a link. Mirroring is not
    /// a nicety: a hotlinked image is one that stops loading when the far site decides it should, and the answer then has
    /// a hole in it long after anybody could work out why. It also keeps the referer off the request.
    /// </remarks>
    public Task<string?> CopyAsync(string image, CancellationToken ct) =>
        Uri.TryCreate(image, UriKind.Absolute, out var uri)
            ? MirrorAsync(image, uri, ct)
            : Task.FromResult<string?>(null);

    private async Task<string?> MirrorAsync(string image, Uri page, CancellationToken ct)
    {
        if (_images is null) return null;
        if (!Uri.TryCreate(image, UriKind.RelativeOrAbsolute, out var imageUri)) return null;
        if (!imageUri.IsAbsoluteUri) imageUri = new Uri(page, imageUri);
        if (!await IsPublicAsync(imageUri).ConfigureAwait(false)) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, imageUri);
            request.Headers.TryAddWithoutValidation("User-Agent", "SmartyBot/1.0 (+link preview)");
            // Some CDNs serve an image only to a referrer from their own page.
            request.Headers.TryAddWithoutValidation("Referer", page.GetLeftPart(UriPartial.Authority));

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var mime = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length is 0 or > 8 * 1024 * 1024) return null;
            return _images(bytes, mime);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Public internet only: http(s), and every address the host resolves to must be routable. This is the
    /// guard that keeps a model-composed URL from reaching the machine Smarty is running on.</summary>
    private static async Task<bool> IsPublicAsync(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        try
        {
            var addresses = IPAddress.TryParse(uri.Host, out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(uri.Host).ConfigureAwait(false);

            return addresses.Length > 0 && addresses.All(IsRoutable);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsRoutable(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return !ip.IsIPv6LinkLocal && !ip.IsIPv6SiteLocal && !ip.IsIPv6UniqueLocal &&
                   !(ip.IsIPv4MappedToIPv6 && !IsRoutable(ip.MapToIPv4()));

        var b = ip.GetAddressBytes();
        if (b[0] == 10) return false;                                  // 10/8
        if (b[0] == 127) return false;                                 // loopback
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;     // 172.16/12
        if (b[0] == 192 && b[1] == 168) return false;                  // 192.168/16
        if (b[0] == 169 && b[1] == 254) return false;                  // link-local / cloud metadata
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;    // carrier NAT
        if (b[0] == 0 || b[0] >= 224) return false;                    // this-network, multicast, reserved
        return true;
    }
}
