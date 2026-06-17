using System.Runtime.CompilerServices;
using Smarty.Agents;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Verifies the agent recovers from a stuck (looping) turn: the runaway turn is discarded, a recovery
/// nudge is injected, and the next turn's answer is used — without aborting the whole request. No
/// network: a scripted provider reproduces a loop-cut-off turn followed by a clean one.
/// </summary>
public class LoopRecoveryTests
{
    [Fact]
    public async Task Stuck_looping_turn_is_discarded_and_recovered()
    {
        var provider = new ScriptedProvider(new[]
        {
            // Turn 1: the model loops in its reasoning and is cut off (FinishReason.Loop), no answer.
            ModelTurn.Looped("73 * 1,073,741,824 = 78,  "),
            // Turn 2 (after the nudge): a clean answer.
            ModelTurn.Answer("The answer is 42."),
        });

        var registry = new ModelProviderRegistry();
        registry.Register("scripted", _ => provider);

        var input = new AgentInput { SystemPrompt = "test", Model = new ModelSpec("scripted", "stub") };
        var agent = new SmartyAgent(input, registry);

        var result = await agent.AnswerStream("What is the meaning of life?").ReadAllAsync();

        // It recovered to the real answer rather than returning the looped garbage.
        Assert.Equal("The answer is 42.", result.Text.Trim());
        Assert.DoesNotContain("1,073,741,824", result.Text);

        // The provider was called twice (it tried again after the loop).
        Assert.Equal(2, provider.Calls);

        // A recovery nudge was injected into the transcript.
        Assert.Contains(agent.LastRun!.Messages, m => m.Role == Role.System && m.Content == input.LoopRecoveryNudge);
    }

    private sealed record ModelTurn(string Text, bool IsReasoning, FinishReason Finish)
    {
        public static ModelTurn Looped(string unit) =>
            new(string.Concat(Enumerable.Repeat(unit, 40)), IsReasoning: true, FinishReason.Loop);

        public static ModelTurn Answer(string text) =>
            new(text, IsReasoning: false, FinishReason.Stop);
    }

    private sealed class ScriptedProvider : IModelProvider
    {
        private readonly IReadOnlyList<ModelTurn> _turns;
        public int Calls { get; private set; }

        public ScriptedProvider(IReadOnlyList<ModelTurn> turns) => _turns = turns;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            var turn = _turns[Math.Min(Calls, _turns.Count - 1)];
            Calls++;

            int mid = turn.Text.Length / 2;
            ModelStreamEvent Chunk(string s) =>
                turn.IsReasoning ? new ModelStreamEvent.Reasoning(s) : new ModelStreamEvent.Content(s);

            yield return Chunk(turn.Text[..mid]);
            yield return Chunk(turn.Text[mid..]);
            yield return new ModelStreamEvent.Completed(new ModelResponse
            {
                Content = turn.IsReasoning ? null : turn.Text,
                Reasoning = turn.IsReasoning ? turn.Text : null,
                Finish = turn.Finish,
            });
        }
    }
}
