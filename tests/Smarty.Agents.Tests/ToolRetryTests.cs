using Smarty.Agents;
using Xunit;
using Xunit.Abstractions;

namespace Smarty.Agents.Tests;

/// <summary>
/// Verifies the "kick it up the arse" behaviour: when a tool returns an error, the agent nudges
/// the model to recover, and the model retries with corrected arguments instead of giving up.
/// Real model (live Ollama), real tool loop — only the tool body is a deterministic stand-in so
/// the failure-then-success path is reliable. Skips if Ollama is unreachable.
/// </summary>
public class ToolRetryTests
{
    private readonly ITestOutputHelper _output;

    public ToolRetryTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Agent_retries_failed_tool_until_it_succeeds()
    {
        if (!await OllamaIsReachableAsync())
        {
            _output.WriteLine("Ollama gateway not reachable at localhost:11434 — skipping live test.");
            return;
        }

        int calls = 0;

        // The first call always fails (transient); a retry succeeds. The model cannot know this in
        // advance — it can only recover by being nudged to try again after the failure.
        var secretTool = new AgentTool(
            "get_secret",
            "Retrieve the secret code from the vault.",
            new[]
            {
                ToolParameter.String("query", "What to look up (e.g. 'secret code').", required: true),
            },
            (ToolCallArguments _, CancellationToken __) =>
            {
                calls++;
                if (calls == 1)
                    return Task.FromResult(ToolOutput.Error(
                        "VAULT ERROR: the vault was temporarily locked. Call get_secret again to retry."));
                return Task.FromResult(ToolOutput.Ok("SECRET CODE = 4242"));
            });

        var input = new AgentInput
        {
            SystemPrompt = "You retrieve secrets using the get_secret tool. Always tell the user the secret code you found.",
            Tools = { secretTool },
            Model = ModelSpec.Ollama("qwen3:4b"),
        };

        var agent = new SmartyAgent(input);
        string answer = await agent.Answer("What is the secret code?");

        _output.WriteLine($"tool calls: {calls}");
        _output.WriteLine($"answer: {answer}");
        foreach (var inv in agent.LastRun!.ToolInvocations)
            _output.WriteLine($"  {inv.ToolName}({inv.Arguments}) error={inv.IsError} -> {inv.Result}");

        // It called the tool more than once — i.e. it retried after the failure instead of quitting.
        Assert.True(calls >= 2, $"expected a retry after failure, but tool was called {calls} time(s)");

        // The first call failed and a later call succeeded.
        Assert.Contains(agent.LastRun!.ToolInvocations, i => i.IsError);
        Assert.Contains(agent.LastRun!.ToolInvocations, i => !i.IsError);

        // A corrective nudge was injected into the transcript after the failure.
        Assert.Contains(agent.LastRun!.Messages, m => m.Role == Role.System && m.Content == input.ToolErrorNudge);

        // It actually recovered and reported the secret.
        Assert.Contains("4242", answer);
    }

    private static async Task<bool> OllamaIsReachableAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var resp = await http.GetAsync($"{OllamaModelProvider.DefaultBaseUrl}/api/tags");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
