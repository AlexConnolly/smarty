using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Smarty.Agents;

/// <summary>
/// Two focused web tools that together do real research:
///
/// 1. <c>web_search(query)</c> — finds candidate pages (title, URL, snippet) so the model can pick the
///    right URL instead of guessing one.
/// 2. <c>get_page_answer(url, question)</c> — a small research sub-step: it fetches one page, turns it into
///    clean text, chunks it with overlap, ranks the chunks against the question (BM25), keeps the best few,
///    and has the model write a short answer grounded in those excerpts. It returns that ANSWER, not a raw
///    dump — so a small model gets the fact it needs, already distilled.
/// </summary>
public static class WebResearch
{
    /// <summary>An optional time-boxed cache for search results and fetched page text. Defaults to a no-op
    /// (no caching); a host sets this once at startup (e.g. a <see cref="FileResearchCache"/>) to make
    /// repeated searches/fetches within the hour reuse the earlier result instead of re-hitting the network.</summary>
    public static IResearchCache Cache { get; set; } = NullResearchCache.Instance;

    /// <summary>How long a cached search result or page fetch stays fresh.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);


    // Present as a real browser. A bare "Mozilla/5.0" plus a custom product token gets 403'd by sites behind
    // Cloudflare/WAFs that block non-browser clients — so we send a full Chrome User-Agent and the headers a
    // browser actually sends, and transparently decompress gzip/brotli responses.
    private static readonly HttpClient Http = CreateBrowserClient();

    private static HttpClient CreateBrowserClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        var h = http.DefaultRequestHeaders;
        h.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                             "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        h.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        h.AcceptLanguage.ParseAdd("en-GB,en;q=0.9");
        h.Add("Sec-Fetch-Dest", "document");
        h.Add("Sec-Fetch-Mode", "navigate");
        h.Add("Sec-Fetch-Site", "none");
        h.Add("Sec-Fetch-User", "?1");
        h.Add("Upgrade-Insecure-Requests", "1");
        return http;
    }

    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex WordRegex = new(@"[\p{L}\p{N}][\p{L}\p{N}'-]*", RegexOptions.Compiled);

    private static readonly Regex ResultLinkRegex = new(
        "<a[^>]*class=\"result__a\"[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex SnippetRegex = new(
        "<a[^>]*class=\"result__snippet\"[^>]*>(.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // A search engine served a bot-check / CAPTCHA / rate-limit page instead of results.
    private static readonly Regex BlockRegex = new(
        "captcha|unusual traffic|bots use|complete the following challenge|/sorry/|are you a (?:ro)?bot|verify you'?re human|access denied",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // DuckDuckGo Lite: any anchor whose href is the /l/?uddg= redirect (attribute order varies).
    private static readonly Regex UddgLinkRegex = new(
        "<a[^>]*href=[\"']([^\"']*uddg=[^\"']*)[\"'][^>]*>(.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // Bing organic result: the <h2> title link inside a b_algo list item, plus the rest of the item for its snippet.
    private static readonly Regex BingResultRegex = new(
        "<li class=\"b_algo\"[^>]*>.*?<h2[^>]*>\\s*<a[^>]*href=\"(https?://[^\"]+)\"[^>]*>(.*?)</a>(.*?)</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex BingSnippetRegex = new(
        "<p[^>]*>(.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "after", "all", "also", "an", "and", "any", "are", "as", "at", "be", "been",
        "but", "by", "can", "could", "did", "do", "does", "for", "from", "had", "has", "have", "how",
        "i", "if", "in", "into", "is", "it", "its", "me", "of", "on", "or", "our", "so", "than", "that",
        "the", "their", "them", "then", "there", "this", "to", "up", "was", "we", "were", "what", "when",
        "where", "which", "who", "why", "will", "with", "you", "your",
    };

    // ---- web_search ---------------------------------------------------------------------------

    public static AgentTool SearchTool(string name = "web_search")
    {
        return new AgentTool(
            name,
            "Searches the web and returns the top results (title, URL, and a short snippet) for a query.",
            new[]
            {
                ToolParameter.String("query", "What to search for.", required: true),
                ToolParameter.Integer("max_results", "How many results to return. Defaults to 5.", required: false),
            },
            SearchAsync);
    }

    private sealed record SearchHit(string Title, string Url, string Snippet);

    private sealed record SearchEngine(string Name, Func<string, string> Url, Func<string, List<SearchHit>> Parse);

    // Try engines in order; any one getting bot-checked or returning nothing falls through to the next.
    private static readonly SearchEngine[] Engines =
    {
        new("DuckDuckGo", q => "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(q), ParseDuckDuckGo),
        new("Bing", q => "https://www.bing.com/search?q=" + Uri.EscapeDataString(q) + "&setlang=en", ParseBing),
        new("DuckDuckGo Lite", q => "https://lite.duckduckgo.com/lite/?q=" + Uri.EscapeDataString(q), ParseDuckDuckGoLite),
    };

    private static async Task<ToolOutput> SearchAsync(ToolCallArguments args, CancellationToken ct)
    {
        string query = args.GetString("query").Trim();
        int maxResults = Math.Clamp(args.GetInt("max_results", 5), 1, 10);
        if (query.Length == 0)
            return ToolOutput.Error("The query argument was empty.");

        // Reuse an identical recent search rather than re-hitting (and re-tripping the bot-blocks of) the engines.
        string cacheKey = $"search:{maxResults}:{query.ToLowerInvariant()}";
        if (Cache.TryGet(cacheKey, out var cachedSearch))
            return ToolOutput.Ok(cachedSearch);

        bool anyBlocked = false;
        var tried = new List<string>();

        foreach (var engine in Engines)
        {
            string html;
            try
            {
                html = await FetchSearchHtml(engine.Url(query), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { tried.Add($"{engine.Name}: {ex.Message}"); continue; }

            if (LooksBlocked(html)) { anyBlocked = true; tried.Add($"{engine.Name}: bot-check"); continue; }

            var hits = engine.Parse(html).Take(maxResults).ToList();
            if (hits.Count == 0) { tried.Add($"{engine.Name}: no results"); continue; }

            var sb = new StringBuilder($"Top results for \"{query}\" (via {engine.Name}):\n");
            for (int i = 0; i < hits.Count; i++)
            {
                sb.Append($"\n{i + 1}. {hits[i].Title}\n   {hits[i].Url}\n");
                if (hits[i].Snippet.Length > 0) sb.Append($"   {Truncate(hits[i].Snippet, 240)}\n");
            }
            var resultText = sb.ToString().TrimEnd();
            Cache.Set(cacheKey, resultText, CacheTtl); // only successful searches are cached; blocks/errors stay retryable
            return ToolOutput.Ok(resultText);
        }

        if (anyBlocked)
            return ToolOutput.Error(
                "Web search is being rate-limited / bot-blocked right now (engines returned a CAPTCHA). " +
                "Wait a short while and try again, or — if you can name a likely page — call get_page_answer " +
                "on that URL directly instead. Tried: " + string.Join("; ", tried));

        return ToolOutput.Ok($"No results found for \"{query}\". Tried: {string.Join("; ", tried)}");
    }

    private static async Task<string> FetchSearchHtml(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        req.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); // 202 challenge pages still parse as "blocked"
    }

    private static bool LooksBlocked(string html) =>
        string.IsNullOrWhiteSpace(html) || BlockRegex.IsMatch(html);

    private static List<SearchHit> ParseDuckDuckGo(string html)
    {
        var hits = new List<SearchHit>();
        var snippets = SnippetRegex.Matches(html);
        foreach (Match link in ResultLinkRegex.Matches(html))
        {
            string url = ResolveDdgUrl(link.Groups[1].Value);
            if (IsAdOrJunk(url)) continue;
            hits.Add(new SearchHit(CleanInline(link.Groups[2].Value), url, SnippetAfter(snippets, link.Index)));
        }
        return hits;
    }

    private static List<SearchHit> ParseDuckDuckGoLite(string html)
    {
        var hits = new List<SearchHit>();
        var seen = new HashSet<string>();
        foreach (Match m in UddgLinkRegex.Matches(html))
        {
            string url = ResolveDdgUrl(m.Groups[1].Value);
            string title = CleanInline(m.Groups[2].Value);
            if (IsAdOrJunk(url) || title.Length == 0 || !seen.Add(url)) continue;
            hits.Add(new SearchHit(title, url, ""));
        }
        return hits;
    }

    private static List<SearchHit> ParseBing(string html)
    {
        var hits = new List<SearchHit>();
        var seen = new HashSet<string>();
        foreach (Match m in BingResultRegex.Matches(html))
        {
            string url = ResolveBingUrl(m.Groups[1].Value);
            string title = CleanInline(m.Groups[2].Value);
            if (title.Length == 0 || url.Contains("bing.com", StringComparison.OrdinalIgnoreCase) || !seen.Add(url))
                continue;
            string snippet = BingSnippetRegex.Match(m.Groups[3].Value) is { Success: true } sm
                ? CleanInline(sm.Groups[1].Value) : "";
            hits.Add(new SearchHit(title, url, snippet));
        }
        return hits;
    }

    // ---- get_page_answer ----------------------------------------------------------------------

    public static AgentTool PageAnswerTool(IModelProvider provider, string model, string name = "get_page_answer")
    {
        return new AgentTool(
            name,
            "Reads one web page and answers a specific question from it. Give the page URL and the question; " +
            "returns a short answer grounded in that page.",
            new[]
            {
                ToolParameter.String("url", "The absolute http/https URL of the page to read.", required: true),
                ToolParameter.String("question", "The specific question to answer from that page.", required: true),
            },
            (args, ct) => PageAnswerAsync(args, provider, model, ct));
    }

    private static async Task<ToolOutput> PageAnswerAsync(
        ToolCallArguments args, IModelProvider provider, string model, CancellationToken ct)
    {
        string url = args.GetString("url").Trim();
        string question = args.GetString("question").Trim();
        if (question.Length == 0)
            return ToolOutput.Error("The question argument was empty.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return ToolOutput.Error($"'{url}' is not a valid http/https URL.");

        // The page's extracted text is what's slow and rate-limited to obtain — cache it by URL so a second
        // question about the same page (common) doesn't refetch. The answer step below still runs per question.
        string pageKey = "page:" + url;
        string text;
        if (Cache.TryGet(pageKey, out var cachedText))
        {
            text = cachedText;
        }
        else
        {
            string html;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    int status = (int)response.StatusCode;
                    // A bot wall (Cloudflare/WAF JS challenge, rate-limit) we can't pass without a real browser.
                    // Don't fail the task on it — tell the model to read a DIFFERENT search result instead.
                    bool blocked = status is 401 or 403 or 429 or 503
                        || (response.Headers.Server?.ToString() ?? "").Contains("cloudflare", StringComparison.OrdinalIgnoreCase)
                        || response.Headers.Contains("cf-mitigated");
                    if (blocked)
                        // A bot wall — retrying this URL can NEVER work without a real browser. Dead end.
                        return ToolOutput.DeadEnd(
                            $"{uri} blocks automated access (HTTP {status} — a bot/Cloudflare challenge that needs a " +
                            "real browser). Don't retry this URL; pick a DIFFERENT result from web_search and read that.");
                    // Same status will recur on retry of the same URL — treat it as a dead end for this URL.
                    return ToolOutput.DeadEnd($"Could not fetch {uri}: HTTP {status} {response.ReasonPhrase}. Try a different source.");
                }
                string mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (mediaType.Length > 0 && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
                    !mediaType.Contains("text", StringComparison.OrdinalIgnoreCase) &&
                    !mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
                    return ToolOutput.DeadEnd($"Fetched {uri}, but it returned '{mediaType}' rather than readable text/HTML. Try a different source.");
                html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Network/timeout — could be transient, so this one's worth another go.
                return ToolOutput.Error($"Could not fetch {uri}: {ex.Message}");
            }

            text = WebSearcherTool.ToPlainText(html);
            if (text.Length == 0)
                return ToolOutput.DeadEnd($"Fetched {uri}, but no readable text could be extracted. Try a different source.");
            // Some walls answer 200 with a challenge/"verify you're human" interstitial instead of the page.
            if (text.Length < 2000 && BlockRegex.IsMatch(text))
                return ToolOutput.DeadEnd(
                    $"{uri} served a bot-check page instead of content (needs a real browser). Don't retry it; " +
                    "pick a DIFFERENT result from web_search and read that.");

            Cache.Set(pageKey, text, CacheTtl); // only a clean, readable page is cached
        }

        var chunks = Chunk(text, size: 1200, overlap: 200);
        var top = RankByQuestion(chunks, question, take: 4);
        if (top.Count == 0)
            return ToolOutput.Error($"Read {uri}, but found no text relevant to the question.");

        var excerpts = new StringBuilder();
        for (int i = 0; i < top.Count; i++)
            excerpts.Append($"[Excerpt {i + 1}]\n{top[i]}\n\n");

        var answerRequest = new ModelRequest
        {
            Model = model,
            SystemPrompt =
                "You answer a question using ONLY the excerpts provided from a web page. Be concise, factual, " +
                "and specific. Quote concrete figures, names, or dates when they appear. If the excerpts do not " +
                "contain the answer, say so plainly — do not invent anything.",
            Messages = new[]
            {
                Message.User($"Question: {question}\n\nExcerpts from {uri}:\n\n{excerpts.ToString().TrimEnd()}\n\nAnswer the question:"),
            },
            Think = false,
            RepeatPenalty = 1.0,
            MaxOutputTokens = 400,
            TurnTimeout = TimeSpan.FromSeconds(60),
        };

        string answer;
        try
        {
            var response = await provider.CompleteAsync(answerRequest, ct).ConfigureAwait(false);
            answer = response.Content?.Trim() ?? "";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return ToolOutput.Error($"Read {uri} but couldn't summarise it: {ex.Message}");
        }

        if (answer.Length == 0)
            return ToolOutput.Error($"Read {uri} but couldn't produce an answer from it.");

        return ToolOutput.Ok($"{answer}\n\n(source: {uri})");
    }

    // ---- chunking + ranking -------------------------------------------------------------------

    /// <summary>Split text into overlapping windows, preferring to break on whitespace near the boundary.</summary>
    internal static List<string> Chunk(string text, int size, int overlap)
    {
        var chunks = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            int end = Math.Min(i + size, text.Length);
            if (end < text.Length)
            {
                // back up to the last whitespace within the final third, so we don't cut mid-word/sentence
                int search = Math.Min(size / 3, end - i - 1);
                int br = text.LastIndexOfAny(new[] { '\n', ' ' }, end - 1, search);
                if (br > i) end = br;
            }

            string chunk = text[i..end].Trim();
            if (chunk.Length > 0) chunks.Add(chunk);

            if (end >= text.Length) break;
            i = Math.Max(end - overlap, i + 1);
        }
        return chunks;
    }

    /// <summary>Rank chunks against the question with BM25 and return the top-N chunk texts.</summary>
    internal static List<string> RankByQuestion(List<string> chunks, string question, int take)
    {
        if (chunks.Count == 0) return new List<string>();

        var queryTerms = Tokenize(question).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (queryTerms.Count == 0)
            return chunks.Take(take).ToList(); // no usable terms — fall back to leading chunks

        var docTokens = chunks.Select(Tokenize).ToList();
        int n = chunks.Count;
        double avgdl = docTokens.Average(d => d.Count == 0 ? 1 : d.Count);

        // document frequency per query term
        var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in queryTerms)
            df[term] = docTokens.Count(d => d.Contains(term, StringComparer.OrdinalIgnoreCase));

        const double k1 = 1.5, b = 0.75;
        var scored = new List<(int idx, double score)>();
        for (int d = 0; d < n; d++)
        {
            var counts = docTokens[d]
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            int dl = Math.Max(1, docTokens[d].Count);

            double score = 0;
            foreach (var term in queryTerms)
            {
                if (!counts.TryGetValue(term, out int tf) || tf == 0) continue;
                double idf = Math.Log(1 + (n - df[term] + 0.5) / (df[term] + 0.5));
                score += idf * (tf * (k1 + 1)) / (tf + k1 * (1 - b + b * dl / avgdl));
            }
            scored.Add((d, score));
        }

        return scored
            .Where(s => s.score > 0)
            .OrderByDescending(s => s.score)
            .ThenBy(s => s.idx) // earlier-on-page tie-breaker (usually more salient)
            .Take(take)
            .OrderBy(s => s.idx) // present in reading order
            .Select(s => chunks[s.idx])
            .ToList();
    }

    private static List<string> Tokenize(string text) =>
        WordRegex.Matches(text.ToLowerInvariant())
            .Select(m => m.Value.Trim('\'', '-'))
            .Where(w => w.Length >= 2 && !StopWords.Contains(w))
            .ToList();

    // ---- helpers ------------------------------------------------------------------------------

    private static bool IsAdOrJunk(string url) =>
        url.Contains("duckduckgo.com/y.js", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("ad_provider=", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("ad_domain=", StringComparison.OrdinalIgnoreCase);

    // The snippet for a result link is the first result__snippet that appears after it in the HTML.
    private static string SnippetAfter(MatchCollection snippets, int afterIndex)
    {
        foreach (Match s in snippets)
            if (s.Index > afterIndex)
                return CleanInline(s.Groups[1].Value);
        return "";
    }

    private static string ResolveDdgUrl(string href)
    {
        if (href.StartsWith("//", StringComparison.Ordinal)) href = "https:" + href;
        var m = Regex.Match(href, @"[?&]uddg=([^&]+)");
        if (m.Success) return Uri.UnescapeDataString(m.Groups[1].Value);
        return href;
    }

    // Bing wraps results in a redirect: bing.com/ck/a?...&u=a1<base64url(real-url)>&... — decode the target.
    private static string ResolveBingUrl(string href)
    {
        href = WebUtility.HtmlDecode(href);
        var m = Regex.Match(href, @"[?&]u=a1([^&]+)");
        if (!m.Success) return href;
        string b64 = m.Groups[1].Value.Replace('-', '+').Replace('_', '/');
        b64 += (b64.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
        catch { return href; }
    }

    private static string CleanInline(string html) =>
        WebUtility.HtmlDecode(TagRegex.Replace(html, "")).Replace('\n', ' ').Trim();

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
