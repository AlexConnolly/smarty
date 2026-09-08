using System.Runtime.CompilerServices;
using System.Text.Json;
using Smarty.Agents;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// What happens when a turn's output degenerates.
///
/// A shopping run produced this, nineteen times, and finished with two of thirty-five items in the basket:
///
///     "Let me find the Waitrose Marylebone link on the feedcrimson\ncrimson\ncrimson\ncrimson…"
///
/// Everything upstream worked. The provider watches the stream, spotted the repetition and cut it, and set
/// FinishReason.Loop. Then the agent threw that away: recovery was keyed on the CONTENT BEING EMPTY, and a
/// collapse is not empty — it's a real sentence welded to a wall of one repeated token. So it read as a good
/// answer, went into the transcript, and became context for the next turn, which made the next collapse likelier.
/// The signal was there the whole time and nothing believed it.
/// </summary>
public class CollapseRecoveryTests
{
    /// <summary>Collapses on its first turn — with a coherent opening, which is the case that slipped through —
    /// then behaves normally.</summary>
    private sealed class CollapsesOnce : IModelProvider
    {
        private readonly bool _withToolCall;
        private int _turn;
        public readonly List<double?> Temperatures = new();

        public CollapsesOnce(bool withToolCall = false) => _withToolCall = withToolCall;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            Temperatures.Add(request.Temperature);
            int turn = _turn++;

            if (turn == 0)
            {
                const string junk = "Let me find the Waitrose link on the feed" + "crimson\ncrimson\ncrimson\n";
                yield return new ModelStreamEvent.Content(junk);
                var calls = _withToolCall
                    ? new[] { new ToolCall("call_0", "look", JsonDocument.Parse("{}").RootElement.Clone()) }
                    : Array.Empty<ToolCall>();
                yield return new ModelStreamEvent.Completed(new ModelResponse
                {
                    Content = junk,
                    ToolCalls = calls,
                    Finish = FinishReason.Loop,
                });
                yield break;
            }

            const string answer = "Two items are in the basket.";
            yield return new ModelStreamEvent.Content(answer);
            yield return new ModelStreamEvent.Completed(new ModelResponse { Content = answer });
        }
    }

    private static async Task<(string Answer, CollapsesOnce Provider, List<AgentEvent> Events)> Run(
        bool withToolCall = false)
    {
        var provider = new CollapsesOnce(withToolCall);
        var registry = new ModelProviderRegistry();
        registry.Register("scripted", _ => provider);

        var input = new AgentInput
        {
            SystemPrompt = "test",
            Model = new ModelSpec("scripted", "stub"),
            Tools = { new AgentTool("look", "Look.", Array.Empty<ToolParameter>(), _ => "looked") { Repeatable = true } },
        };

        var events = new List<AgentEvent>();
        string answer = "";
        await foreach (var ev in new SmartyAgent(input, registry).AnswerStream("go"))
        {
            events.Add(ev);
            if (ev is AgentEvent.Completed done) answer = done.Answer;
        }
        return (answer, provider, events);
    }

    [Fact]
    public async Task A_collapse_with_real_text_in_front_of_it_is_still_a_collapse()
    {
        // The exact miss: non-empty content meant "good answer", so the garbage was kept.
        var (answer, _, _) = await Run();

        Assert.DoesNotContain("crimson", answer);
        Assert.Contains("basket", answer);
    }

    [Fact]
    public async Task The_broken_fragment_is_pulled_back_from_the_user()
    {
        // It was streamed before the loop was detected, so the UI has to be told to drop it.
        var (_, _, events) = await Run();

        Assert.Contains(events, e => e is AgentEvent.ContentCleared);
    }

    [Fact]
    public async Task The_retry_does_not_sample_the_same_way()
    {
        // At temperature 0 the next token is the argmax, so retrying a collapsed context greedily reproduces
        // the collapse exactly. A retry that changes nothing is not a retry.
        var (_, provider, _) = await Run();

        Assert.Null(provider.Temperatures[0]);           // first attempt stays deterministic
        Assert.NotNull(provider.Temperatures[1]);
        Assert.True(provider.Temperatures[1] > 0, "the retry after a collapse needs some noise to escape it");
    }

    [Fact]
    public async Task Deterministic_sampling_returns_once_the_turn_comes_back_clean()
    {
        // The bump is for the one turn that follows a collapse, not for the rest of the run.
        var provider = new CollapsesOnce();
        var registry = new ModelProviderRegistry();
        registry.Register("scripted", _ => provider);
        var input = new AgentInput { SystemPrompt = "t", Model = new ModelSpec("scripted", "stub") };
        await new SmartyAgent(input, registry).AnswerStream("go").ReadAllAsync();

        Assert.Equal(2, provider.Temperatures.Count);
        Assert.Null(provider.Temperatures[0]);
    }

    [Fact]
    public async Task A_collapse_around_a_real_tool_call_keeps_the_call_and_bins_the_words()
    {
        // The intent survived; only the narration degenerated. Dropping the call would throw away the only
        // part of the turn that makes progress.
        var (answer, _, events) = await Run(withToolCall: true);

        Assert.Contains(events, e => e is AgentEvent.ToolStarted { ToolName: "look" });
        Assert.DoesNotContain("crimson", answer);
    }

    [Fact]
    public async Task The_gibberish_never_reaches_the_next_turn_as_context()
    {
        // The compounding part: a collapse left in the transcript is repetitive context, and repetitive context
        // is what provokes the next collapse. Nineteen in a row started this way.
        var (_, _, events) = await Run();

        var streamed = string.Concat(events.OfType<AgentEvent.ContentDelta>().Select(d => d.Text));
        Assert.Contains("crimson", streamed);            // it WAS streamed before detection — unavoidable
        Assert.Contains(events, e => e is AgentEvent.ContentCleared);  // and then retracted
    }
}
