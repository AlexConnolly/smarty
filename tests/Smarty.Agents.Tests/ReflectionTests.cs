using System.Runtime.CompilerServices;
using System.Text.Json;
using Smarty.Agents;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// The periodic stand-back.
///
/// A worker once alternated two tools two hundred times, each call refused by its own cap, because every guard
/// asked "was that call wasteful?" and none asked "is this getting anywhere?". This one lands in the worker's own
/// transcript, so it changes the next decision rather than grading the last.
/// </summary>
public class ReflectionTests
{
    [Fact]
    public async Task It_asks_the_worker_to_explain_itself_every_few_calls()
    {
        var tool = new AgentTool("probe", "Probe.", Array.Empty<ToolParameter>(), _ => "ok") { Repeatable = true };

        var registry = new ModelProviderRegistry();
        registry.Register("scripted", _ => new Calls(9));

        var input = new AgentInput
        {
            SystemPrompt = "test",
            Tools = { tool },
            Model = new ModelSpec("scripted", "stub"),
            MaxIterations = 20,
            MaxCallsPerTool = 50,
            ReflectEvery = 3,
        };

        var agent = new SmartyAgent(input, registry);
        await agent.AnswerStream("go").ReadAllAsync();

        var pauses = agent.LastRun!.Messages
            .Count(m => m.Role == Role.System && m.Content.Contains("Pause. You have made"));

        // Nine calls at every third: after 3, 6 and 9.
        Assert.Equal(3, pauses);
        Assert.Contains(agent.LastRun.Messages, m => m.Content.Contains("What do you expect the next few calls"));
        Assert.Contains(agent.LastRun.Messages, m => m.Content.Contains("that approach is finished"));
    }

    [Fact]
    public async Task Zero_turns_it_off()
    {
        var tool = new AgentTool("probe", "Probe.", Array.Empty<ToolParameter>(), _ => "ok") { Repeatable = true };
        var registry = new ModelProviderRegistry();
        registry.Register("scripted", _ => new Calls(6));

        var input = new AgentInput
        {
            SystemPrompt = "test",
            Tools = { tool },
            Model = new ModelSpec("scripted", "stub"),
            MaxIterations = 20,
            MaxCallsPerTool = 50,
            ReflectEvery = 0,
        };

        var agent = new SmartyAgent(input, registry);
        await agent.AnswerStream("go").ReadAllAsync();

        Assert.DoesNotContain(agent.LastRun!.Messages, m => m.Content.Contains("Pause. You have made"));
    }


    /// <summary>
    /// A stand-back is not an answer.
    ///
    /// The worker replies to a reflection in prose and calls no tool, which is the same shape as a finished job —
    /// so the run ended on it and the user was handed "I'm gathering photos, three to go" as their brochure,
    /// including the sentence saying it was not for them.
    /// </summary>
    [Fact]
    public async Task A_reflection_answer_does_not_end_the_run()
    {
        var tool = new AgentTool("probe", "Probe.", Array.Empty<ToolParameter>(), _ => "ok") { Repeatable = true };

        var registry = new ModelProviderRegistry();
        registry.Register("scripted", _ => new ReflectsThenWorks());

        var input = new AgentInput
        {
            SystemPrompt = "test",
            Tools = { tool },
            Model = new ModelSpec("scripted", "stub"),
            MaxIterations = 20,
            MaxCallsPerTool = 50,
            ReflectEvery = 2,
        };

        var agent = new SmartyAgent(input, registry);
        var result = await agent.AnswerStream("go").ReadAllAsync();

        // The prose plan must not be the answer, and the run must have carried on to finish the work.
        Assert.DoesNotContain("gathering photos", result.Text);
        Assert.Equal("The brochure is ready.", result.Text.Trim());
        Assert.Contains(agent.LastRun!.Messages, m => m.Content.Contains("Now DO the next thing you just described"));
    }

    /// <summary>Two calls, then a prose reflection answer with no tool call, then one more call, then the answer.</summary>
    private sealed class ReflectsThenWorks : IModelProvider
    {
        private int _turn;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            int turn = _turn++;

            // Turns 0,1: work. Turn 2: the reflection reply — prose, no tool. Turn 3: back to work. Turn 4: answer.
            if (turn is 0 or 1 or 3)
            {
                var call = new ToolCall($"c{turn}", "probe", JsonDocument.Parse("{}").RootElement.Clone());
                yield return new ModelStreamEvent.Completed(new ModelResponse { ToolCalls = new[] { call } });
                yield break;
            }

            var text = turn == 2
                ? "I'm gathering photos of each resort. Nothing has failed. This is for me, not the user."
                : "The brochure is ready.";
            yield return new ModelStreamEvent.Content(text);
            yield return new ModelStreamEvent.Completed(new ModelResponse { Content = text });
        }
    }

    /// <summary>Calls one tool N times, then answers.</summary>
    private sealed class Calls : IModelProvider
    {
        private readonly int _times;
        private int _turn;

        public Calls(int times) => _times = times;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            int turn = _turn++;

            if (turn < _times)
            {
                var call = new ToolCall($"c{turn}", "probe", JsonDocument.Parse("{}").RootElement.Clone());
                yield return new ModelStreamEvent.Completed(new ModelResponse { ToolCalls = new[] { call } });
                yield break;
            }

            const string answer = "Done.";
            yield return new ModelStreamEvent.Content(answer);
            yield return new ModelStreamEvent.Completed(new ModelResponse { Content = answer });
        }
    }
}
