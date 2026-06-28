using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Smarty.Agents;

/// <summary>
/// Fetches a web page, converts it to plain text, chunks it, and returns the chunks most relevant
/// to a question. This gives small models live web context without dumping a huge page into the
/// prompt at once.
/// </summary>
public static class WebSearcherTool
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders =
        {
            UserAgent =
            {
                new("Mozilla", "5.0"),
                new("SmartyWebSearcher", "1.0"),
            },
        },
    };

    private static readonly Regex ScriptLikeRegex = new(
        @"<\s*(script|style|noscript|svg|template|head)\b[^>]*>.*?<\s*/\s*\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex BlockBreakRegex = new(
        @"</?\s*(article|aside|blockquote|br|dd|div|dl|dt|figcaption|figure|footer|h[1-6]|header|hr|li|main|nav|ol|p|pre|section|table|td|th|tr|ul)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"[ \t\f\v]+", RegexOptions.Compiled);
    private static readonly Regex PunctuationSpaceRegex = new(@"\s+([.,;:!?])", RegexOptions.Compiled);
    private static readonly Regex BlankLineRegex = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex WordRegex = new(@"[\p{L}\p{N}][\p{L}\p{N}'-]*", RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "after", "all", "also", "am", "an", "and", "are", "as", "at", "be",
        "been", "but", "by", "can", "could", "did", "do", "does", "for", "from", "had",
        "has", "have", "how", "i", "in", "into", "is", "it", "its", "latest", "me", "new",
        "news", "of", "on", "or", "our", "show", "tell", "that", "the", "their", "there",
        "this", "to", "today", "was", "were", "what", "when", "where", "which", "who",
        "why", "with", "you", "your",
    };

    public static AgentTool Create(string name = "web_searcher", IModelProvider? modelProvider = null, string? model = null)
    {
        return new AgentTool(
            name,
            "Fetches one web page, turns it into plain text, chunks it, ranks chunks against a question, " +
            "and returns only the most relevant source excerpts. Use this for live web/homepage questions " +
            "instead of dumping huge pages through the shell.",
            new[]
            {
                ToolParameter.String("question", "The question to answer from the page, e.g. \"What's the latest news on BBC?\".", required: true),
                ToolParameter.String("url", "The page URL to load. Optional; if omitted, the model will determine the correct URL first.", required: false),
                ToolParameter.Integer("max_chunks", "Maximum relevant chunks to return. Defaults to 5.", required: false),
                ToolParameter.Integer("chunk_chars", "Approximate text characters per chunk. Defaults to 1800.", required: false),
            },
            (args, ct) => SearchAsync(args, modelProvider, model, ct));
    }

    public static AgentTool CreatePageLoadTool(IModelProvider? modelProvider = null, string? model = null)
    {
        return new AgentTool(
            "web_page_load",
            "Loads a web page from a URL, cleans it, and returns the most relevant chunks.",
            new[]
            {
                ToolParameter.String("url", "The absolute HTTP/HTTPS URL of the page to load.", required: true),
                ToolParameter.Integer("max_chunks", "Maximum relevant chunks to return.", required: false),
                ToolParameter.Integer("chunk_chars", "Approximate characters per chunk.", required: false),
                ToolParameter.String("query", "Relevance query to filter/rank chunks.", required: false),
            },
            (args, ct) => LoadPageAsync(args, modelProvider, model, ct));
    }

    private static async Task<ToolOutput> SearchAsync(ToolCallArguments args, IModelProvider? modelProvider, string? model, CancellationToken ct)
    {
        string question = args.GetString("question").Trim();
        string? requestedUrl = args.GetStringOrNull("url");
        int maxChunks = Math.Clamp(args.GetInt("max_chunks", 4), 1, 6);
        int chunkChars = Math.Clamp(args.GetInt("chunk_chars", 1200), 400, 3000);

        if (question.Length == 0)
            return ToolOutput.Error("The question argument was empty.");

        // If URL is explicitly provided, fetch it directly
        if (!string.IsNullOrWhiteSpace(requestedUrl))
        {
            var pageArgs = CreateLoadPageArgs(requestedUrl, maxChunks, chunkChars, question);
            return await LoadPageAsync(pageArgs, modelProvider, model, ct).ConfigureAwait(false);
        }

        // If URL is not provided, run a sub-agent with the web_page_load tool
        var provider = modelProvider ?? 
                       (ModelSpec.SecondaryDefault != null ? ModelProviderRegistry.Default.Resolve(ModelSpec.SecondaryDefault) : null) ?? 
                       ModelProviderRegistry.Default.Resolve(ModelSpec.Default);
        var modelName = model ?? 
                        (ModelSpec.SecondaryDefault != null ? ModelSpec.SecondaryDefault.Model : null) ?? 
                        ModelSpec.Default.Model;

        var agentInput = new AgentInput
        {
            SystemPrompt =
                "You are a web search assistant. Your task is to answer the user's question by fetching web pages.\n" +
                "You have the 'web_page_load' tool to load any URL. Think about what URL is most likely to contain " +
                "the information (e.g., specific sites, search/news portals, company domains, etc.), fetch it, " +
                "read the content, and then formulate a clear answer. If the page doesn't have the answer, you " +
                "can try another URL. Be concise and accurate.",
            Model = ModelSpec.Ollama(modelName),
            Tools = { CreatePageLoadTool(provider, modelName) },
            MaxIterations = 4
        };

        var registry = new ModelProviderRegistry();
        registry.Register("ollama", _ => provider);

        try
        {
            var subAgent = new SmartyAgent(agentInput, registry);
            string answer = await subAgent.Answer(question, ct).ConfigureAwait(false);
            return ToolOutput.Ok(answer);
        }
        catch (Exception ex)
        {
            return ToolOutput.Error($"Web search failed: {ex.Message}");
        }
    }

    private static async Task<ToolOutput> LoadPageAsync(ToolCallArguments args, IModelProvider? modelProvider, string? model, CancellationToken ct)
    {
        string url = args.GetString("url").Trim();
        int maxChunks = Math.Clamp(args.GetInt("max_chunks", 4), 1, 6);
        int chunkChars = Math.Clamp(args.GetInt("chunk_chars", 1200), 400, 3000);
        string? query = args.GetStringOrNull("query")?.Trim();

        if (url.Length == 0)
            return ToolOutput.Error("The url argument was empty.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ToolOutput.Error($"'{url}' is not a valid http/https URL.");
        }

        string html;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,text/plain;q=0.8,*/*;q=0.5");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return ToolOutput.Error($"Could not fetch {uri}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            string mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (html.Length == 0)
                return ToolOutput.Error($"Fetched {uri}, but the response body was empty.");
            if (mediaType.Length > 0 && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) && !mediaType.Contains("text", StringComparison.OrdinalIgnoreCase))
                return ToolOutput.Error($"Fetched {uri}, but it returned '{mediaType}' rather than readable text/HTML.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolOutput.Error($"Could not fetch {uri}: {ex.Message}");
        }

        string title = ExtractTitle(html);
        string text = ToPlainText(html);
        if (text.Length == 0)
            return ToolOutput.Error($"Fetched {uri}, but no readable text could be extracted.");

        var chunks = Chunk(text, chunkChars).ToList();
        List<RankedChunk> selected;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var terms = QueryTerms(query);
            var rankedCandidates = chunks
                .Select((chunk, index) => new RankedChunk(index + 1, chunk, Score(chunk, terms)))
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Number)
                .ToList();

            var provider = modelProvider ?? 
                           (ModelSpec.SecondaryDefault != null ? ModelProviderRegistry.Default.Resolve(ModelSpec.SecondaryDefault) : null) ?? 
                           ModelProviderRegistry.Default.Resolve(ModelSpec.Default);
            var modelName = model ?? 
                            (ModelSpec.SecondaryDefault != null ? ModelSpec.SecondaryDefault.Model : null) ?? 
                            ModelSpec.Default.Model;

            var candidatesToScore = rankedCandidates.Take(Math.Min(maxChunks, 4)).ToList();
            var llmRanked = new List<RankedChunk>();

            var systemPrompt = "You are an AI assistant that evaluates the relevance of web page content to a user's question. " +
                               "Provide your response as a single integer score between 0 and 10, where 10 is extremely relevant " +
                               "(contains direct answers or highly useful information) and 0 is completely irrelevant (e.g. ads, navigation, spam, header/footer, or unrelated topics). " +
                               "Output ONLY the integer score, nothing else. Do not include any explanation or additional text.";

            foreach (var candidate in candidatesToScore)
            {
                int llmScore = 0;
                try
                {
                    var request = new ModelRequest
                    {
                        Model = modelName,
                        SystemPrompt = systemPrompt,
                        Messages = new[] { Message.User($"User Question: \"{query}\"\n\nWeb Page Text Chunk:\n\"\"\"\n{candidate.Text}\n\"\"\"\n\nRelevance Score (0-10):") },
                        Think = false,
                        MaxOutputTokens = 5
                    };
                    var response = await provider.CompleteAsync(request, ct).ConfigureAwait(false);
                    if (response.Content != null)
                    {
                        var match = Regex.Match(response.Content, @"\b(10|[0-9])\b");
                        if (match.Success && int.TryParse(match.Value, out int score))
                        {
                            llmScore = score;
                        }
                    }
                }
                catch
                {
                    // Fall back to 0
                }
                llmRanked.Add(new RankedChunk(candidate.Number, candidate.Text, llmScore));
            }

            selected = llmRanked
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Number)
                .Take(maxChunks)
                .ToList();

            if (selected.Count == 0 || selected.All(c => c.Score <= 0))
            {
                selected = rankedCandidates.Take(maxChunks).ToList();
            }
        }
        else
        {
            selected = chunks.Take(maxChunks).Select((chunk, index) => new RankedChunk(index + 1, chunk, 0)).ToList();
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Source: {uri}");
        if (!string.IsNullOrWhiteSpace(title))
            sb.AppendLine($"Page title: {title}");
        sb.AppendLine($"Plain text length: {text.Length:N0} characters; chunks scanned: {chunks.Count}; chunks returned: {selected.Count}.");
        sb.AppendLine();

        foreach (var chunk in selected)
        {
            sb.AppendLine($"--- chunk {chunk.Number}/{chunks.Count} (score {chunk.Score}) ---");
            sb.AppendLine(chunk.Text);
            sb.AppendLine();
        }

        return ToolOutput.Ok(sb.ToString().TrimEnd());
    }

    private static ToolCallArguments CreateLoadPageArgs(string url, int maxChunks, int chunkChars, string? query)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["url"] = url,
            ["max_chunks"] = maxChunks,
            ["chunk_chars"] = chunkChars
        };
        if (query != null)
        {
            dict["query"] = query;
        }
        string json = System.Text.Json.JsonSerializer.Serialize(dict);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return new ToolCallArguments(doc.RootElement.Clone());
    }

    public static string ToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        string text = ScriptLikeRegex.Replace(html, " ");
        text = BlockBreakRegex.Replace(text, "\n");
        text = TagRegex.Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = text.Replace('\r', '\n');

        var lines = text.Split('\n')
            .Select(line => PunctuationSpaceRegex.Replace(WhitespaceRegex.Replace(line, " ").Trim(), "$1"))
            .Where(line => line.Length > 0);

        return BlankLineRegex.Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    private static string ExtractTitle(string html)
    {
        var match = Regex.Match(html, @"<\s*title\b[^>]*>(.*?)<\s*/\s*title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success) return "";
        return WhitespaceRegex.Replace(WebUtility.HtmlDecode(TagRegex.Replace(match.Groups[1].Value, " ")), " ").Trim();
    }

    private static IEnumerable<string> Chunk(string text, int chunkChars)
    {
        var paragraphs = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var current = new StringBuilder();

        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Length > chunkChars)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString().Trim();
                    current.Clear();
                }

                for (int start = 0; start < paragraph.Length; start += chunkChars)
                    yield return paragraph.Substring(start, Math.Min(chunkChars, paragraph.Length - start)).Trim();
                continue;
            }

            if (current.Length > 0 && current.Length + paragraph.Length + 1 > chunkChars)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }

            if (current.Length > 0) current.AppendLine();
            current.Append(paragraph);
        }

        if (current.Length > 0)
            yield return current.ToString().Trim();
    }

    private static List<string> QueryTerms(string question)
    {
        return WordRegex.Matches(question.ToLowerInvariant())
            .Select(m => m.Value.Trim('\'', '-'))
            .Where(word => word.Length >= 3 && !StopWords.Contains(word))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int Score(string chunk, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return 0;
        string lower = chunk.ToLowerInvariant();
        int score = 0;
        foreach (string term in terms)
        {
            int count = 0;
            int index = lower.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            while (index >= 0 && count < 8)
            {
                count++;
                index = lower.IndexOf(term, index + term.Length, StringComparison.OrdinalIgnoreCase);
            }
            score += count * (term.Length >= 6 ? 2 : 1);
        }

        return score;
    }

    private sealed record RankedChunk(int Number, string Text, int Score);
}
