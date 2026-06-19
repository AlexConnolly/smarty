using System.Text.Json;
using Xunit;

namespace Smarty.Agents.Tests;

public class WebSearcherToolTests
{
    [Fact]
    public void ToPlainText_strips_scripts_tags_and_decodes_entities()
    {
        const string html = """
            <html>
              <head><title>Ignored</title><style>.x{color:red}</style></head>
              <body>
                <h1>Top &amp; Latest</h1>
                <script>window.secret = 'nope';</script>
                <p>First story <strong>headline</strong>.</p>
              </body>
            </html>
            """;

        string text = WebSearcherTool.ToPlainText(html);

        Assert.Contains("Top & Latest", text);
        Assert.Contains("First story headline.", text);
        Assert.DoesNotContain("window.secret", text);
        Assert.DoesNotContain("<strong>", text);
    }

    [Fact]
    public async Task Tool_fetches_real_url()
    {
        using var json = JsonDocument.Parse("""{"url":"https://en.wikipedia.org/wiki/Moby-Dick"}""");
        var args = new ToolCallArguments(json.RootElement);

        ToolOutput result = await WebSearcherTool.CreatePageLoadTool().InvokeAsync(args);

        Assert.False(result.IsError, $"Error: {result.Content}");
        Assert.Contains("Moby", result.Content);
    }

    [Fact]
    public async Task Tool_errors_on_invalid_url()
    {
        var tool = WebSearcherTool.CreatePageLoadTool();

        // Missing scheme
        using var json1 = JsonDocument.Parse("""{"url":"bbc.com"}""");
        var args1 = new ToolCallArguments(json1.RootElement);
        var result1 = await tool.InvokeAsync(args1);
        Assert.True(result1.IsError);
        Assert.Contains("not a valid http/https URL", result1.Content);

        // Invalid scheme
        using var json2 = JsonDocument.Parse("""{"url":"ftp://example.com"}""");
        var args2 = new ToolCallArguments(json2.RootElement);
        var result2 = await tool.InvokeAsync(args2);
        Assert.True(result2.IsError);
        Assert.Contains("not a valid http/https URL", result2.Content);

        // Empty URL
        using var json3 = JsonDocument.Parse("""{"url":""}""");
        var args3 = new ToolCallArguments(json3.RootElement);
        var result3 = await tool.InvokeAsync(args3);
        Assert.True(result3.IsError);
        Assert.Contains("argument was empty", result3.Content);
    }

    [Fact]
    public async Task Tool_uses_llm_grading_to_rank_chunks()
    {
        var mockProvider = new MockModelProvider(req =>
        {
            string prompt = req.Messages.Last().Content ?? "";
            int score = prompt.Contains("Moby") ? 10 : 2;
            return new ModelResponse { Content = score.ToString() };
        });

        using var json = JsonDocument.Parse("""{"url":"https://en.wikipedia.org/wiki/Moby-Dick","query":"whale","max_chunks":2}""");
        var args = new ToolCallArguments(json.RootElement);

        ToolOutput result = await WebSearcherTool.CreatePageLoadTool(mockProvider, "mock-model").InvokeAsync(args);

        Assert.False(result.IsError, $"Error: {result.Content}");
        Assert.Contains("score 10", result.Content);
    }

    [Fact]
    public async Task Tool_errors_when_sub_agent_fails()
    {
        var mockProvider = new MockModelProvider(req => throw new Exception("Connection refused"));
        using var json = JsonDocument.Parse("""{"question":"what changed today?"}""");
        var args = new ToolCallArguments(json.RootElement);

        ToolOutput result = await WebSearcherTool.Create("web_searcher", mockProvider, "mock-model").InvokeAsync(args);

        Assert.True(result.IsError);
        Assert.Contains("Web search failed", result.Content);
    }

    [Fact]
    public async Task Tool_runs_sub_agent_when_no_url_is_provided()
    {
        int callCount = 0;
        var mockProvider = new MockModelProvider(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                Assert.Contains(req.Tools, t => t.Name == "web_page_load");
                var argsJson = JsonSerializer.SerializeToElement(new { url = "https://en.wikipedia.org/wiki/Moby-Dick" });
                return new ModelResponse
                {
                    ToolCalls = new List<ToolCall> { new ToolCall("call_1", "web_page_load", argsJson) }
                };
            }
            else
            {
                return new ModelResponse
                {
                    Content = "Herman Melville wrote Moby-Dick in 1851."
                };
            }
        });

        using var json = JsonDocument.Parse("""{"question":"when was Moby Dick written?"}""");
        var args = new ToolCallArguments(json.RootElement);

        var tool = WebSearcherTool.Create("web_searcher", mockProvider, "mock-model");
        ToolOutput result = await tool.InvokeAsync(args);

        Assert.False(result.IsError, $"Error: {result.Content}");
        Assert.Contains("Melville wrote Moby-Dick in 1851", result.Content);
        Assert.Equal(2, callCount);
    }

    private class MockModelProvider : IModelProvider
    {
        private readonly Func<ModelRequest, ModelResponse> _responseFactory;

        public MockModelProvider(Func<ModelRequest, ModelResponse> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            var response = _responseFactory(request);
            yield return new ModelStreamEvent.Completed(response);
        }
    }
}
