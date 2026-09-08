using System.Runtime.CompilerServices;
using System.Text.Json;
using Smarty.Agents;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// The same refusal, over and over — the loop that both other guards were blind to.
///
/// <para>
/// One guard counts identical ARGUMENTS, and a worker trying to satisfy a complaint changes its arguments on every
/// attempt, so the count never rises. The other compares RESULTS and only looks at successful ones. Between them sat
/// the clearest loop signal there is: a tool saying the same sentence about the same objection, forever.
/// </para>
/// <para>
/// Found by watching a panel sit on "Building the data feed" for four minutes. Publishing was refused twelve times
/// with the identical message — it objected to a regex quantifier in the component's expression, which was not
/// something the worker could declare or remove — and nothing in the run reported a fault. From outside it looked
/// like slow work, which is the worst way for a thing to be broken.
/// </para>
/// </summary>
public class SameRefusalTests
{
    /// <summary>Calls one tool with slightly different arguments each time — a worker trying to comply.</summary>
    private sealed class KeepsTrying : IModelProvider
    {
        private readonly int _attempts;
        private int _turn;

        public KeepsTrying(int attempts) => _attempts = attempts;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            int turn = _turn++;

            if (turn < _attempts)
            {
                // Different arguments every time, which is exactly why the identical-arguments guard never fires.
                var args = JsonDocument.Parse($"{{\"attempt\":{turn}}}").RootElement.Clone();
                var call = new ToolCall($"call_{turn}", "publish", args);
                yield return new ModelStreamEvent.Completed(new ModelResponse { ToolCalls = new[] { call } });
                yield break;
            }

            const string answer = "Gave up.";
            yield return new ModelStreamEvent.Content(answer);
            yield return new ModelStreamEvent.Completed(new ModelResponse { Content = answer });
        }
    }

    private static async Task<AgentAnswer> Publish(Func<int, ToolOutput> answer, int attempts)
    {
        int calls = 0;
        var publish = new AgentTool("publish", "Publish the thing.", Array.Empty<ToolParameter>(),
            (_, _) => Task.FromResult(answer(calls++))) { Repeatable = true };

        var registry = new ModelProviderRegistry();
        registry.Register("scripted", _ => new KeepsTrying(attempts));

        var input = new AgentInput
        {
            SystemPrompt = "test",
            Tools = { publish },
            Model = new ModelSpec("scripted", "stub"),
        };
        return await new SmartyAgent(input, registry).AnswerStream("publish it").ReadAllAsync();
    }

    private static List<ToolInvocation> Attempts(AgentAnswer answer) =>
        answer.Tools.Where(t => t.ToolName == "publish").ToList();

    [Fact]
    public async Task The_same_objection_three_times_ends_the_attempt()
    {
        var attempts = Attempts(await Publish(
            _ => ToolOutput.Error("The loader has {3} in it but you haven't declared that parameter."), attempts: 6));

        Assert.Contains(attempts, a => a.Result.Contains("refused"));
        // And it says what to do about it, which "try something else" does not: the objection is literal, so read it
        // literally — or report it, because a refusal nobody can satisfy is itself the fault.
        Assert.Contains(attempts, a => a.Result.Contains("objecting to"));
    }

    [Fact]
    public async Task A_different_objection_each_time_is_left_alone()
    {
        // Progress, even when every attempt fails: each refusal names something new, so the worker is being told
        // something it did not already know and is entitled to keep going.
        var attempts = Attempts(await Publish(n => ToolOutput.Error($"Fault number {n}."), attempts: 5));

        Assert.Equal(5, attempts.Count);
        Assert.All(attempts, a => Assert.DoesNotContain("refused", a.Result));
    }

    [Fact]
    public async Task Getting_through_clears_the_count()
    {
        // Two identical refusals, then it works. The strike is not carried onwards — a later, unrelated refusal of
        // the same wording starts counting again from there.
        var attempts = Attempts(await Publish(
            n => n < 2 ? ToolOutput.Error("Same objection.") : ToolOutput.Ok("published"), attempts: 4));

        Assert.All(attempts, a => Assert.DoesNotContain("refused", a.Result));
        Assert.Contains("published", attempts[^1].Result);
    }
}
