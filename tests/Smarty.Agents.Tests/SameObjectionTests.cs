using System.Runtime.CompilerServices;
using System.Text.Json;
using Smarty.Agents;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// A refusal that counts its own repeats, and the guard that could never see it.
///
/// <para>
/// The repeat guard answers a call it has already answered with "you have already run X with these exact arguments
/// 3 times" — and then 4, and then 5. The guard below it exists to notice a tool refusing with the same objection
/// over and over, and it compared the sentences verbatim. Every one of those sentences is different, so the count
/// reset on every single refusal and the escalation was unreachable against the one refusal in the system
/// guaranteed to repeat.
/// </para>
/// <para>
/// This is what it cost, from the recorded run: a panel build looking for a football fixture called
/// chrome_javascript with byte-identical arguments thirty-eight times in a row. One real call, two "identical
/// result" notes, then thirty-six numbered refusals — and nothing anywhere concluded that one objection was being
/// restated thirty-six times. Half the build's budget went on it.
/// </para>
/// </summary>
public class SameObjectionTests
{
    /// <summary>Calls one repeatable tool with the same arguments, over and over, then answers.</summary>
    private sealed class Insists : IModelProvider
    {
        private readonly int _times;
        private int _turn;

        public Insists(int times) => _times = times;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (_turn++ < _times)
            {
                var args = JsonDocument.Parse("{\"expression\":\"fetch('https://site.api.espn.com/x')\"}")
                    .RootElement.Clone();
                yield return new ModelStreamEvent.Completed(new ModelResponse
                {
                    ToolCalls = new[] { new ToolCall($"call_{_turn}", "run_script", args) },
                });
                yield break;
            }

            const string answer = "Couldn't get it.";
            yield return new ModelStreamEvent.Content(answer);
            yield return new ModelStreamEvent.Completed(new ModelResponse { Content = answer });
        }
    }

    private static async Task<AgentAnswer> Insist(int times)
    {
        var script = new AgentTool("run_script", "Run something in the page.",
            new[] { ToolParameter.String("expression", "the script", required: true) },
            _ => "ERR TypeError: Failed to fetch") { Repeatable = true };

        var registry = new ModelProviderRegistry();
        registry.Register("scripted", _ => new Insists(times));

        return await new SmartyAgent(new AgentInput
        {
            SystemPrompt = "test",
            Tools = { script },
            Model = new ModelSpec("scripted", "stub"),
            EnableSupervisorGuard = false,
        }, registry).AnswerStream("go").ReadAllAsync();
    }

    [Fact]
    public async Task One_objection_restated_with_a_bigger_number_is_still_one_objection()
    {
        var calls = (await Insist(12)).Tools.Where(t => t.ToolName == "run_script").ToList();

        // It is told, in terms, that it is being refused the same thing repeatedly — the escalation that was
        // written for exactly this and had never once been reached.
        Assert.Contains(calls, c => c.Result.Contains("exactly the same objection"));
    }

    [Fact]
    public async Task A_genuinely_different_objection_still_starts_the_count_again()
    {
        // The behaviour the verbatim comparison was protecting: two different complaints are two complaints, and a
        // worker working through them in turn is making progress, not looping.
        int n = 0;
        var picky = new AgentTool("check", "Check something.",
            new[] { ToolParameter.String("what", "what to check", required: true) },
            _ => throw new InvalidOperationException($"objection number {++n} is a different objection"))
        { Repeatable = true };

        var registry = new ModelProviderRegistry();
        registry.Register("scripted", _ => new Insists(0));

        var input = new AgentInput
        {
            SystemPrompt = "test",
            Tools = { picky },
            Model = new ModelSpec("scripted", "stub"),
            EnableSupervisorGuard = false,
        };

        var answer = await new SmartyAgent(input, registry).AnswerStream("go").ReadAllAsync();
        Assert.DoesNotContain(answer.Tools, t => t.Result.Contains("exactly the same objection"));
    }
}
