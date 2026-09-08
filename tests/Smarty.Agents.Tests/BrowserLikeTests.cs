using System.Net;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Fetching a panel's data the way the user's own browser would.
///
/// <para>
/// A panel loader is not a crawler: it fetches one page its owner asked to be kept informed about, on a cadence
/// they chose. It used to say so, politely, in a custom user-agent — and that is precisely what got it refused.
/// Measured against ESPN from this runtime, same url, same minute: <c>curl/8.4.0</c> 200, <c>python-requests</c>
/// 200, <c>Smarty/1.0 (+home-page panel)</c> 403, a Chrome user-agent on its own 403, a full Chrome header set 200.
/// A panel refreshing every five minutes turned that into roughly 288 refusals a day, reported on the page as the
/// source being broken.
/// </para>
/// <para>
/// The two things worth pinning down are the ones that were wrong on the way here: the set has to be WHOLE — the
/// same headers without <c>Accept-Encoding</c> went straight back to 403, because a browser that cannot accept
/// compression is not a browser — and a source that declares headers of its own still has to win.
/// </para>
/// </summary>
public class BrowserLikeTests : IDisposable
{
    /// <summary>A server that answers anything and remembers what it was asked with.</summary>
    private sealed class Echo : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string Url { get; }
        public Dictionary<string, string> Received { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Echo(string body = "{\"temperature\":22}")
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); } catch { return; }
                    foreach (string? name in ctx.Request.Headers.AllKeys)
                        if (name is not null) Received[name] = ctx.Request.Headers[name] ?? "";

                    var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); } catch { /* already gone */ }
        }
    }

    private readonly List<IDisposable> _open = new();

    private Echo Serving(string body = "{\"temperature\":22}")
    {
        var echo = new Echo(body);
        _open.Add(echo);
        return echo;
    }

    private static WidgetKind Weather(string url, params (string Name, string Value)[] headers)
    {
        var kind = new WidgetKind
        {
            Name = "city-weather",
            Title = "City weather",
            Description = "the temperature in a city",
            Model = new() { new WidgetField { Name = "temperature", Type = "number", Description = "degrees" } },
            Loader = new WidgetLoader { Mode = LoaderModes.Http, Url = url },
            Code = "return <Stat label=\"Now\" value={data.temperature} />",
            Refresh = "every 2 hours",
        };
        foreach (var (name, value) in headers) kind.Loader.Headers[name] = value;
        return kind;
    }

    private async Task<Dictionary<string, string>> FetchedWith(WidgetKind kind, Echo echo)
    {
        var http = BrowserLike.Client(TimeSpan.FromSeconds(10));
        _open.Add(http);
        var loader = new WidgetDataLoader(http, (_, _) => Task.FromResult<string?>(null));

        var result = await loader.LoadAsync(kind, new Dictionary<string, string>(), CancellationToken.None);
        Assert.True(result.Worked, result.Error);
        return echo.Received;
    }

    [Fact]
    public async Task A_panel_fetches_as_a_browser_and_the_set_is_whole()
    {
        var echo = Serving();
        var sent = await FetchedWith(Weather(echo.Url), echo);

        // Every header, because a HALF disguise scores worse than none: a Chrome user-agent with none of the
        // headers Chrome always sends is the tell that the 403s were catching.
        foreach (var name in BrowserLike.Headers)
            Assert.True(sent.ContainsKey(name), $"the request went without {name}");

        Assert.Contains("Chrome/", sent["User-Agent"]);
        Assert.DoesNotContain("Smarty", sent["User-Agent"]);
        Assert.Equal("?0", sent["sec-ch-ua-mobile"]);
        Assert.Equal("navigate", sent["Sec-Fetch-Mode"]);
    }

    [Fact]
    public async Task It_asks_for_compression_because_a_browser_does()
    {
        // The one that cost an afternoon: with every other header identical and this one missing, ESPN went back to
        // 403. It isn't written by hand — it comes from the handler, which is the only thing that can decompress
        // what comes back, so asking for it and being able to read it cannot drift apart.
        var echo = Serving();
        var sent = await FetchedWith(Weather(echo.Url), echo);

        Assert.True(sent.ContainsKey("Accept-Encoding"), "a browser that cannot accept compression is not a browser");
        Assert.Contains("gzip", sent["Accept-Encoding"]);
    }

    [Fact]
    public async Task A_source_that_declares_its_own_headers_still_wins()
    {
        // A key, a referer, an Accept the endpoint insists on: the kind knows things about its source that the
        // default set cannot. Applied after, so it overrides rather than being overridden.
        var echo = Serving();
        var kind = Weather(echo.Url, ("Accept", "application/vnd.api+json"), ("X-Api-Key", "secret"));

        var sent = await FetchedWith(kind, echo);

        Assert.Equal("secret", sent["X-Api-Key"]);
        Assert.Contains("application/vnd.api+json", sent["Accept"]);
    }

    [Fact]
    public async Task It_does_not_claim_a_page_sent_it()
    {
        // Nothing linked this fetch, so there is no referer to give. Inventing one is the same mismatched
        // half-truth as a browser user-agent with no client hints behind it.
        var echo = Serving();
        var sent = await FetchedWith(Weather(echo.Url), echo);

        Assert.False(sent.ContainsKey("Referer"));
        Assert.Equal("none", sent["Sec-Fetch-Site"]);
    }

    public void Dispose()
    {
        foreach (var d in _open) { try { d.Dispose(); } catch { /* closing is best-effort */ } }
    }
}
