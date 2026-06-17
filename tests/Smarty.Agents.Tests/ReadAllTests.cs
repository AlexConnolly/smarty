using System.Runtime.InteropServices;
using Smarty.Agents;
using Xunit;
using Xunit.Abstractions;

namespace Smarty.Agents.Tests;

/// <summary>
/// Exercises the drain helpers (<see cref="AgentStreamExtensions.ReadAllAsync"/> and
/// <see cref="SmartyAgent.AnswerAll"/>) against the live Ollama gateway. Skips if unreachable.
/// </summary>
public class ReadAllTests
{
    private readonly ITestOutputHelper _output;

    public ReadAllTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ReadAll_aggregates_answer_reasoning_and_tools()
    {
        if (!await OllamaIsReachableAsync())
        {
            _output.WriteLine("Ollama gateway not reachable at localhost:11434 — skipping live test.");
            return;
        }

        string os = RuntimeInformation.OSDescription;
        var input = new AgentInput
        {
            SystemPrompt =
                $"You are a system administrator assistant on this machine: {os}. " +
                "Use the run_shell_command tool to gather real data before answering.",
            Tools = { ShellTool.Create("run_shell_command") },
            Model = ModelSpec.Ollama("qwen3:4b"),
        };

        var agent = new SmartyAgent(input);

        AgentAnswer result = await agent.AnswerStream("What is the current system status?").ReadAllAsync();

        _output.WriteLine($"=== Text ===\n{result.Text}");
        _output.WriteLine($"=== Reasoning ({result.Reasoning.Length} chars) ===");
        _output.WriteLine($"=== Tools ===");
        foreach (var t in result.Tools)
            _output.WriteLine($"{t.ToolName} {t.Arguments}");

        // Aggregated answer text is present.
        Assert.False(string.IsNullOrWhiteSpace(result.Text));

        // Tool invocations were captured with their arguments and results paired up.
        Assert.NotEmpty(result.Tools);
        Assert.All(result.Tools, t =>
        {
            Assert.Equal("run_shell_command", t.ToolName);
            Assert.False(string.IsNullOrWhiteSpace(t.Arguments));
            Assert.False(string.IsNullOrWhiteSpace(t.Result));
        });

        // The drained tool list matches what the agent recorded on its run transcript.
        Assert.Equal(agent.LastRun!.ToolInvocations.Count, result.Tools.Count);

        // The raw event log is lossless and ends with the terminal Completed event.
        Assert.NotEmpty(result.Events);
        Assert.IsType<AgentEvent.Completed>(result.Events[^1]);

        // qwen3 thinks, so reasoning was aggregated too — separate from the answer text.
        Assert.False(string.IsNullOrWhiteSpace(result.Reasoning));
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
