using System.Runtime.CompilerServices;
using System.Text.Json;
using Smarty.Agents;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Noticing that the world hasn't moved.
///
/// A shopping run spent 250 of its 320 tool calls on chrome_read_page — 192 of them the byte-identical call — and
/// exhausted its budget having added one item to the basket. The existing guard counts CONSECUTIVE identical
/// calls, and every read had a click between it and the last, so read/click/read never tripped it while every
/// read came back exactly the same. Whether anything changed is a question the RESULT answers, so it's asked
/// there instead — which also stops the second copy of an expensive page read being paid for twice.
/// </summary>
public class UnchangedResultTests
{
    /// <summary>Alternates read_page and click for a set number of rounds, then answers.</summary>
    private sealed class Browses : IModelProvider
    {
        private readonly int _rounds;
        private int _turn;

        public Browses(int rounds) => _rounds = rounds;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            int turn = _turn++;

            if (turn < _rounds * 2)
            {
                var name = turn % 2 == 0 ? "read_page" : "click";
                var call = new ToolCall($"call_{turn}", name, JsonDocument.Parse("{}").RootElement.Clone());
                yield return new ModelStreamEvent.Completed(new ModelResponse { ToolCalls = new[] { call } });
                yield break;
            }

            const string answer = "Done.";
            yield return new ModelStreamEvent.Content(answer);
            yield return new ModelStreamEvent.Completed(new ModelResponse { Content = answer });
        }
    }

    /// <param name="page">What the page reads as on the Nth read — constant for a stuck page.</param>
    private static async Task<AgentAnswer> Browse(Func<int, string> page, int rounds)
    {
        int reads = 0;
        var readPage = new AgentTool("read_page", "Read the page.", Array.Empty<ToolParameter>(),
            _ => page(reads++)) { Repeatable = true };
        var click = new AgentTool("click", "Click something.", Array.Empty<ToolParameter>(),
            _ => "clicked") { Repeatable = true };

        var registry = new ModelProviderRegistry();
        registry.Register("scripted", _ => new Browses(rounds));

        var input = new AgentInput
        {
            SystemPrompt = "test",
            Tools = { readPage, click },
            Model = new ModelSpec("scripted", "stub"),
        };
        return await new SmartyAgent(input, registry).AnswerStream("browse").ReadAllAsync();
    }

    private static List<ToolInvocation> ReadsOf(AgentAnswer answer) =>
        answer.Tools.Where(t => t.ToolName == "read_page").ToList();

    [Fact]
    public async Task An_unchanged_page_is_not_sent_back_a_second_time()
    {
        // The saving that matters: a 1200-node page read is the most expensive thing in the transcript, and an
        // identical second copy of it adds nothing.
        var reads = ReadsOf(await Browse(_ => "the very same page", rounds: 2));

        Assert.Contains("the very same page", reads[0].Result);   // the first look is real
        Assert.DoesNotContain("the very same page", reads[1].Result);
        Assert.Contains("Identical to the last", reads[1].Result);
    }

    [Fact]
    public async Task A_page_that_never_moves_ends_the_attempt_rather_than_grinding_on()
    {
        var reads = ReadsOf(await Browse(_ => "stuck", rounds: 5));

        // Asserted on what the worker is TOLD. (The streamed view doesn't carry the error flag through, so the
        // message is the observable thing here — and it's the part that changes the worker's behaviour anyway.)
        Assert.Contains(reads, r => r.Result.Contains("same result"));
        Assert.Contains(reads, r => r.Result.Contains("genuinely different approach"));
    }

    [Fact]
    public async Task Genuine_browsing_is_never_interfered_with()
    {
        // The case the consecutive-only rule exists to protect: each look differs, so nothing is suppressed.
        var reads = ReadsOf(await Browse(n => $"page {n}", rounds: 5));

        Assert.Equal(5, reads.Count);
        Assert.All(reads, r => Assert.DoesNotContain("Identical", r.Result));
        Assert.All(reads, r => Assert.DoesNotContain("same result", r.Result));
    }

    [Fact]
    public async Task A_page_that_moves_again_clears_the_count()
    {
        // Stuck twice, then something changes: the worker is live again and doesn't carry a strike onwards.
        var reads = ReadsOf(await Browse(n => n < 2 ? "stuck" : $"moved {n}", rounds: 5));

        Assert.All(reads, r => Assert.DoesNotContain("same result", r.Result));
        Assert.Contains("moved", reads[^1].Result);
    }
}
