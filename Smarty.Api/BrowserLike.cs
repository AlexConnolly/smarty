using System.Net;

namespace Smarty.Api;

/// <summary>
/// Fetching the way the user's own browser would, because that is what this is.
///
/// <para>
/// A panel loader and a feed reader are not crawlers. They fetch one page the person asked to be kept informed
/// about, on a cadence they chose, on their behalf — the same request their browser would make if they opened the
/// tab themselves, minus the tab. So it makes that request.
/// </para>
/// <para>
/// The old header pair was a polite, honest custom agent, and being honest is exactly what got it refused. Measured
/// against ESPN, same url, same minute, from this runtime:
/// </para>
/// <code>
///   curl/8.4.0                                     200
///   python-requests/2.31.0                         200
///   Go-http-client/2.0                             200
///   Smarty/1.0 (+home-page panel)                  403
///   Mozilla/5.0 (compatible; SmartyBot/1.0)        403
///   Chrome UA alone, or with Accept + Referer      403
///   Chrome UA + client hints + fetch metadata      200
/// </code>
/// <para>
/// Two things fall out of that and both shape what is below. A <b>half</b> disguise is worse than none: a Chrome
/// user-agent with none of the headers Chrome always sends is a tell, and scores worse than admitting to being a
/// script. And the set has to be COMPLETE — the same headers minus <c>Accept-Encoding</c> went back to 403, because
/// a browser that cannot accept compression is not a browser. That header is not written here: it is added by the
/// handler from <see cref="Client"/>, which is also the only thing that can decompress what comes back.
/// </para>
/// <para>
/// The one thing deliberately not imitated is a referer. A browser sends one when a page linked it; nothing linked
/// this, so claiming otherwise would be the same mismatched half-truth as the user-agent on its own.
/// </para>
/// </summary>
public static class BrowserLike
{
    /// <summary>
    /// The version claimed. Not chased — a browser a couple of releases behind is an ordinary thing to be, and a
    /// number that changes on its own is a fetch that starts failing on a day nobody touched it.
    /// </summary>
    private const string Chrome = "128";

    private const string Agent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/" + Chrome + ".0.0.0 Safari/537.36";

    /// <summary>
    /// A client that can be a browser: it decompresses, so it can also ask for compression.
    /// </summary>
    /// <remarks>
    /// Its own client rather than the process-wide one. Automatic decompression is the right default here and the
    /// wrong one for a file downloader, where a server serving a .gz with <c>Content-Encoding: gzip</c> would have
    /// it silently unpacked into the file the user asked to keep.
    /// </remarks>
    public static HttpClient Client(TimeSpan? timeout = null) =>
        new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };

    /// <summary>
    /// Put the headers on a request: what Chrome sends when a url is opened directly, in its order.
    /// </summary>
    /// <remarks>
    /// Apply this BEFORE anything a feed or a kind declares for itself, so a source that needs a key, an
    /// <c>Accept</c> of its own or a referer can still say so and win.
    /// </remarks>
    public static void Wear(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", Agent);
        // What Chrome asks for when you paste a url into the bar. Verified not to change what any source in use
        // returns — the JSON endpoints still answer JSON and the feeds still answer XML — and a JSON-first Accept
        // no browser ever sends would be another mismatch of exactly the kind this exists to avoid.
        request.Headers.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-GB,en;q=0.9");
        request.Headers.TryAddWithoutValidation("sec-ch-ua",
            $"\"Chromium\";v=\"{Chrome}\", \"Not;A=Brand\";v=\"24\", \"Google Chrome\";v=\"{Chrome}\"");
        request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
    }

    /// <summary>Every header name this puts on a request, for anything that needs to check the set is whole.</summary>
    public static IReadOnlyList<string> Headers { get; } = new[]
    {
        "User-Agent", "Accept", "Accept-Language", "sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform",
        "Sec-Fetch-Dest", "Sec-Fetch-Mode", "Sec-Fetch-Site", "Sec-Fetch-User", "Upgrade-Insecure-Requests",
    };
}
