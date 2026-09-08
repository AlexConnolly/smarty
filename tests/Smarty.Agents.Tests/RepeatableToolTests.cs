using System.Runtime.CompilerServices;
using System.Text.Json;
using Smarty.Agents;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// The repeat guard, and the exemption a stateful tool needs from it.
///
/// The guard is right for a stateless tool: searching the same query twice is a wasted turn. It was wrong for
/// the browser. Asked for a café's opening hours, a worker read a page, navigated somewhere else, and asked to
/// read again — same <c>{tabId}</c>, different page. The guard saw identical arguments, refused it twice, and the
/// worker had to scrape innerText through a JavaScript call instead, burning its budget and finishing with no
/// answer at all. These tests pin both halves: the guard still bites, and it leaves stateful tools alone.
/// </summary>
public class RepeatableToolTests
{
    [Fact]
    public async Task A_repeatable_tool_may_be_called_twice_with_the_same_arguments()
    {
        int reads = 0;
        var readPage = new AgentTool(
            "read_page",
            "Read the current page.",
            new[] { ToolParameter.Integer("tabId", "which tab") },
            _ => $"page {++reads}")
        {
            Repeatable = true,
        };

        var result = await Drive(readPage, """{"tabId":7}""");

        Assert.Equal(2, reads);                                  // it actually ran both times
        Assert.Equal("page 1", result.Tools[0].Result);
        Assert.Equal("page 2", result.Tools[1].Result);           // …and returned the NEW page
        Assert.DoesNotContain("already", result.Tools[1].Result); // not the dead-end message
    }

    [Fact]
    public async Task A_normal_tool_is_still_blocked_from_repeating_itself()
    {
        int searches = 0;
        var search = new AgentTool(
            "search",
            "Search.",
            new[] { ToolParameter.String("query", "what to look for") },
            _ => $"results {++searches}")
        {
            Repeatable = false,
        };

        var result = await Drive(search, """{"query":"corgi cafe"}""");

        Assert.Equal(1, searches);                        // the second call never reached the tool
        Assert.Contains("already", result.Tools[1].Result);
    }

    /// <summary>
    /// The exemption has a floor. A run asked to gather photos took the same screenshot of the same page twice in
    /// a row and then kept going, because a stateful tool was exempt from the guard forever and a screenshot tells
    /// the model nothing it can read — so there was no signal, from the tool or the loop, that it was going
    /// nowhere. Identical arguments are still allowed to mean a changed world, but not indefinitely.
    /// </summary>
    [Fact]
    public async Task A_repeatable_tool_is_cut_off_once_identical_calls_stop_being_a_fresh_look()
    {
        int shots = 0;
        var screenshot = new AgentTool(
            "screenshot",
            "Photograph the current page.",
            Array.Empty<ToolParameter>(),
            _ => $"shot {++shots}")
        {
            Repeatable = true,
        };

        var result = await Drive(screenshot, "{}", calls: 5);

        // Allowed to repeat — but not forever, and the tool stops being run at all once it's cut off.
        Assert.Equal(3, shots);
        Assert.Equal(5, result.Tools.Count);
        Assert.Contains("already run", result.Tools[3].Result);
        Assert.Contains("already run", result.Tools[4].Result);
    }

    /// <summary>
    /// Browsing is reads separated by clicks, and the counter must not mistake that for a loop.
    ///
    /// It did: a worker clicked a link, asked to read the page it had just opened, and was refused because
    /// <c>read_page{}</c> is byte-identical every time. It then had no way forward and stopped, reporting that it
    /// had run out of room. Only CONSECUTIVE identical calls are a repeat.
    /// </summary>
    [Fact]
    public async Task Reads_separated_by_clicks_are_not_repeats()
    {
        int reads = 0;
        var readPage = new AgentTool("read_page", "Read the page.", Array.Empty<ToolParameter>(),
            _ => $"page {++reads}")
        {
            Repeatable = true,
        };
        var click = new AgentTool("click", "Click something.", Array.Empty<ToolParameter>(), _ => "clicked")
        {
            Repeatable = true,
        };

        var registry = new ModelProviderRegistry();
        registry.Register("scripted", _ => new Alternating());

        var input = new AgentInput
        {
            SystemPrompt = "test",
            Tools = { readPage, click },
            Model = new ModelSpec("scripted", "stub"),
        };

        var result = await new SmartyAgent(input, registry).AnswerStream("browse").ReadAllAsync();

        // Five reads, each after a click. Every one has to actually run.
        Assert.Equal(5, reads);
        Assert.DoesNotContain(result.Tools, t => t.Result.Contains("already run"));
    }

    /// <summary>Alternates read_page and click, five times each, then answers.</summary>
    private sealed class Alternating : IModelProvider
    {
        private int _turn;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            int turn = _turn++;

            if (turn < 10)
            {
                var name = turn % 2 == 0 ? "read_page" : "click";
                var call = new ToolCall($"call_{turn}", name, JsonDocument.Parse("{}").RootElement.Clone());
                yield return new ModelStreamEvent.Completed(new ModelResponse { ToolCalls = new[] { call } });
                yield break;
            }

            const string answer = "Done browsing.";
            yield return new ModelStreamEvent.Content(answer);
            yield return new ModelStreamEvent.Completed(new ModelResponse { Content = answer });
        }
    }

    /// <summary>Run an agent whose model calls the tool <paramref name="calls"/> times with identical arguments,
    /// then answers.</summary>
    private static async Task<AgentAnswer> Drive(AgentTool tool, string arguments, int calls = 2)
    {
        var registry = new ModelProviderRegistry();
        registry.Register("scripted", _ => new TwiceThenAnswer(tool.Name, arguments, calls));

        var input = new AgentInput
        {
            SystemPrompt = "test",
            Tools = { tool },
            Model = new ModelSpec("scripted", "stub"),
        };

        return await new SmartyAgent(input, registry).AnswerStream("what time does it open?").ReadAllAsync();
    }

    /// <summary>Calls one tool N times with the same arguments, then gives a final answer.</summary>
    private sealed class TwiceThenAnswer : IModelProvider
    {
        private readonly string _tool;
        private readonly string _arguments;
        private readonly int _calls;
        private int _turn;

        public TwiceThenAnswer(string tool, string arguments, int calls = 2)
        {
            _tool = tool;
            _arguments = arguments;
            _calls = calls;
        }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            int turn = _turn++;

            if (turn < _calls)
            {
                var call = new ToolCall($"call_{turn}", _tool, JsonDocument.Parse(_arguments).RootElement.Clone());
                yield return new ModelStreamEvent.Completed(new ModelResponse { ToolCalls = new[] { call } });
                yield break;
            }

            const string answer = "It opens at nine.";
            yield return new ModelStreamEvent.Content(answer);
            yield return new ModelStreamEvent.Completed(new ModelResponse { Content = answer });
        }
    }
}
