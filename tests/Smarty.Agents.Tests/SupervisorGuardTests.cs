using System.Runtime.CompilerServices;
using System.Text.Json;
using Smarty.Agents;
using Xunit;

namespace Smarty.Agents.Tests;

public class SupervisorGuardTests
{
    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    [Fact]
    public async Task IterationLimit_Extended_When_Supervisor_Returns_Progress()
    {
        var provider = new SupervisorScriptedProvider();
        // Turn 1: request tool call
        provider.QueueAgentTurn(new ModelResponse
        {
            ToolCalls = new[] { new ToolCall("call_1", "dummy", EmptyArgs) }
        });
        // Turn 2: request tool call
        provider.QueueAgentTurn(new ModelResponse
        {
            ToolCalls = new[] { new ToolCall("call_2", "dummy", EmptyArgs) }
        });
        // Supervisor at end of Turn 2: returns PROGRESS
        provider.QueueSupervisorDecision("PROGRESS");
        // Turn 3: request tool call
        provider.QueueAgentTurn(new ModelResponse
        {
            ToolCalls = new[] { new ToolCall("call_3", "dummy", EmptyArgs) }
        });
        // Turn 4: return final answer
        provider.QueueAgentTurn(new ModelResponse
        {
            Content = "Done!"
        });

        var dummyTool = new AgentTool(
            "dummy",
            "A dummy tool.",
            Array.Empty<ToolParameter>(),
            (args, ct) => Task.FromResult(ToolOutput.Ok("ok"))
        );

        var registry = new ModelProviderRegistry();
        registry.Register("scripted", _ => provider);

        var input = new AgentInput
        {
            SystemPrompt = "test",
            Model = new ModelSpec("scripted", "stub"),
            MaxIterations = 2, // Very low iteration limit to trigger guard quickly
            Tools = { dummyTool }
        };

        var agent = new SmartyAgent(input, registry);
        var result = await agent.AnswerStream("Run").ReadAllAsync();

        Assert.Equal("Done!", result.Text.Trim());
        // Verify progress nudge was injected
        Assert.Contains(agent.LastRun!.Messages, m => m.Role == Role.System && m.Content.Contains("Turns limit is about to be exceeded"));
    }

    [Fact]
    public async Task IterationLimit_TerminatesAfter2Turns_When_Supervisor_Returns_DeadEnd()
    {
        var provider = new SupervisorScriptedProvider();
        // Turn 1: request tool call
        provider.QueueAgentTurn(new ModelResponse
        {
            ToolCalls = new[] { new ToolCall("call_1", "dummy", EmptyArgs) }
        });
        // Turn 2: request tool call
        provider.QueueAgentTurn(new ModelResponse
        {
            ToolCalls = new[] { new ToolCall("call_2", "dummy", EmptyArgs) }
        });
        // Supervisor at end of Turn 2: returns DEAD_END
        provider.QueueSupervisorDecision("DEAD_END");
        // Turn 3: request tool call (first wrap-up turn)
        provider.QueueAgentTurn(new ModelResponse
        {
            ToolCalls = new[] { new ToolCall("call_3", "dummy", EmptyArgs) }
        });
        // Turn 4: request tool call (second wrap-up turn - last allowed)
        provider.QueueAgentTurn(new ModelResponse
        {
            ToolCalls = new[] { new ToolCall("call_4", "dummy", EmptyArgs) }
        });

        var dummyTool = new AgentTool(
            "dummy",
            "A dummy tool.",
            Array.Empty<ToolParameter>(),
            (args, ct) => Task.FromResult(ToolOutput.Ok("ok"))
        );

        var registry = new ModelProviderRegistry();
        registry.Register("scripted", _ => provider);

        var input = new AgentInput
        {
            SystemPrompt = "test",
            Model = new ModelSpec("scripted", "stub"),
            MaxIterations = 2,
            Tools = { dummyTool }
        };

        var agent = new SmartyAgent(input, registry);
        var result = await agent.AnswerStream("Run").ReadAllAsync();

        // The agent was killed after the second wrap-up turn (turn 4).
        // It should have injected the dead end nudge.
        Assert.Contains(agent.LastRun!.Messages, m => m.Role == Role.System && m.Content.Contains("You have exactly 2 more turns"));
        Assert.Equal(4, agent.LastRun.ToolInvocations.Count);
    }

    [Fact]
    public async Task ToolCallingLimit_Extended_When_Supervisor_Returns_Progress()
    {
        var provider = new SupervisorScriptedProvider();
        // Turn 1: call dummy
        provider.QueueAgentTurn(new ModelResponse
        {
            ToolCalls = new[] { new ToolCall("call_1", "dummy", JsonDocument.Parse("{\"step\":1}").RootElement) }
        });
        // Turn 2: call dummy again
        provider.QueueAgentTurn(new ModelResponse
        {
            ToolCalls = new[] { new ToolCall("call_2", "dummy", JsonDocument.Parse("{\"step\":2}").RootElement) }
        });
        // Turn 3: call dummy third time (exceeds MaxCallsPerTool = 2)
        provider.QueueAgentTurn(new ModelResponse
        {
            ToolCalls = new[] { new ToolCall("call_3", "dummy", JsonDocument.Parse("{\"step\":3}").RootElement) }
        });
        // Supervisor triggered at Tool Call 3: returns PROGRESS
        provider.QueueSupervisorDecision("PROGRESS");
        // Turn 4: return final answer
        provider.QueueAgentTurn(new ModelResponse
        {
            Content = "Done!"
        });

        int callCount = 0;
        var dummyTool = new AgentTool(
            "dummy",
            "A dummy tool.",
            new[] { ToolParameter.String("step", "step description") },
            (args, ct) =>
            {
                callCount++;
                return Task.FromResult(ToolOutput.Ok("ok"));
            }
        );

        var registry = new ModelProviderRegistry();
        registry.Register("scripted", _ => provider);

        var input = new AgentInput
        {
            SystemPrompt = "test",
            Model = new ModelSpec("scripted", "stub"),
            MaxCallsPerTool = 2,
            Tools = { dummyTool }
        };

        var agent = new SmartyAgent(input, registry);
        var result = await agent.AnswerStream("Run").ReadAllAsync();

        Assert.Equal("Done!", result.Text.Trim());
        Assert.Equal(3, callCount); // Tool should have been called 3 times (extended successfully)
    }

    [Fact]
    public async Task ToolCallingLimit_Blocked_When_Supervisor_Returns_DeadEnd()
    {
        var provider = new SupervisorScriptedProvider();
        // Turn 1: call dummy
        provider.QueueAgentTurn(new ModelResponse
        {
            ToolCalls = new[] { new ToolCall("call_1", "dummy", JsonDocument.Parse("{\"step\":1}").RootElement) }
        });
        // Turn 2: call dummy again
        provider.QueueAgentTurn(new ModelResponse
        {
            ToolCalls = new[] { new ToolCall("call_2", "dummy", JsonDocument.Parse("{\"step\":2}").RootElement) }
        });
        // Turn 3: call dummy third time (exceeds MaxCallsPerTool = 2)
        provider.QueueAgentTurn(new ModelResponse
        {
            ToolCalls = new[] { new ToolCall("call_3", "dummy", JsonDocument.Parse("{\"step\":3}").RootElement) }
        });
        // Supervisor triggered at Tool Call 3: returns DEAD_END
        provider.QueueSupervisorDecision("DEAD_END");
        // Turn 4: final answer (after wrap-up/dead-end nudges)
        provider.QueueAgentTurn(new ModelResponse
        {
            Content = "Wrap up answer."
        });

        int callCount = 0;
        var dummyTool = new AgentTool(
            "dummy",
            "A dummy tool.",
            new[] { ToolParameter.String("step", "step description") },
            (args, ct) =>
            {
                callCount++;
                return Task.FromResult(ToolOutput.Ok("ok"));
            }
        );

        var registry = new ModelProviderRegistry();
        registry.Register("scripted", _ => provider);

        var input = new AgentInput
        {
            SystemPrompt = "test",
            Model = new ModelSpec("scripted", "stub"),
            MaxCallsPerTool = 2,
            Tools = { dummyTool }
        };

        var agent = new SmartyAgent(input, registry);
        var result = await agent.AnswerStream("Run").ReadAllAsync();

        Assert.Equal("Wrap up answer.", result.Text.Trim());
        Assert.Equal(2, callCount); // The third call should have been blocked
        Assert.Contains(agent.LastRun!.Messages, m => m.Role == Role.System && m.Content.Contains("You have exactly 2 more turns"));
        Assert.Contains(agent.LastRun.ToolInvocations, inv => inv.ToolName == "dummy" && inv.Result.Contains("STOP calling"));
    }

    private sealed class SupervisorScriptedProvider : IModelProvider
    {
        private readonly Queue<ModelResponse> _agentTurns = new();
        private readonly Queue<string> _supervisorDecisions = new();

        public void QueueAgentTurn(ModelResponse response) => _agentTurns.Enqueue(response);
        public void QueueSupervisorDecision(string decision) => _supervisorDecisions.Enqueue(decision);

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (request.SystemPrompt != null && request.SystemPrompt.Contains("supervisor"))
            {
                var decision = _supervisorDecisions.Dequeue();
                yield return new ModelStreamEvent.Content(decision);
                yield return new ModelStreamEvent.Completed(new ModelResponse { Content = decision });
            }
            else
            {
                var turn = _agentTurns.Dequeue();
                if (turn.Content != null)
                {
                    yield return new ModelStreamEvent.Content(turn.Content);
                }
                yield return new ModelStreamEvent.Completed(turn);
            }
        }
    }
}
