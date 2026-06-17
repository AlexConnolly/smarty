using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Smarty.Agents;
using Xunit;
using Xunit.Abstractions;

namespace Smarty.Agents.Tests;

/// <summary>
/// Real, end-to-end streaming tests against the live Ollama gateway (qwen3). Asserts that the
/// answer is streamed incrementally as <see cref="AgentEvent.ContentDelta"/>, that the streamed
/// output reconstructs the final answer, and that the model's thinking never leaks into the
/// output stream. Skips if Ollama is unreachable.
/// </summary>
public class StreamingAgentTests
{
    private readonly ITestOutputHelper _output;

    public StreamingAgentTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Output_streams_incrementally_and_excludes_thinking()
    {
        if (!await OllamaIsReachableAsync())
        {
            _output.WriteLine("Ollama gateway not reachable at localhost:11434 — skipping live test.");
            return;
        }

        var input = new AgentInput
        {
            SystemPrompt = "You are concise. Answer in two or three short sentences.",
            Model = ModelSpec.Ollama("qwen3:4b"),
        };

        var agent = new SmartyAgent(input);

        var contentBuilder = new StringBuilder();
        int contentDeltas = 0;
        int reasoningDeltas = 0;
        string finalAnswer = "";

        // Capture wall-clock time of the first content delta vs the last — proves incremental delivery.
        var stopwatch = Stopwatch.StartNew();
        TimeSpan firstDeltaAt = TimeSpan.Zero;
        TimeSpan lastDeltaAt = TimeSpan.Zero;

        await foreach (var ev in agent.AnswerStream("In one breath, what is an operating system?"))
        {
            switch (ev)
            {
                case AgentEvent.ContentDelta delta:
                    if (contentDeltas == 0) firstDeltaAt = stopwatch.Elapsed;
                    lastDeltaAt = stopwatch.Elapsed;
                    contentDeltas++;
                    contentBuilder.Append(delta.Text);
                    break;
                case AgentEvent.ReasoningDelta:
                    reasoningDeltas++;
                    break;
                case AgentEvent.Completed completed:
                    finalAnswer = completed.Answer;
                    break;
            }
        }

        string streamed = contentBuilder.ToString();
        _output.WriteLine($"content deltas: {contentDeltas}, reasoning deltas: {reasoningDeltas}");
        _output.WriteLine($"first delta @ {firstDeltaAt.TotalSeconds:F2}s, last delta @ {lastDeltaAt.TotalSeconds:F2}s");
        _output.WriteLine($"=== streamed answer ===\n{streamed}");

        // The answer arrived in multiple chunks (truly streamed, not one buffered blob).
        Assert.True(contentDeltas > 1, $"expected multiple content deltas, got {contentDeltas}");

        // The streamed output exactly reconstructs the final answer — thinking is not mixed in.
        Assert.Equal(finalAnswer.Trim(), streamed.Trim());
        Assert.False(string.IsNullOrWhiteSpace(streamed));

        // qwen3 is a thinking model, so reasoning should have streamed too — on its OWN channel.
        Assert.True(reasoningDeltas > 0, "expected reasoning to stream on the reasoning channel");

        // Content streaming was spread over time, not delivered all at once at the end.
        Assert.True(lastDeltaAt > firstDeltaAt, "content deltas should be spread over time");
    }

    [Fact]
    public async Task Streams_tool_activity_then_streams_answer()
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

        bool toolStarted = false, toolCompleted = false;
        bool sawContentAfterTool = false;
        var content = new StringBuilder();

        await foreach (var ev in agent.AnswerStream("What is the current system status?"))
        {
            switch (ev)
            {
                case AgentEvent.ToolStarted started:
                    toolStarted = true;
                    Assert.Equal("run_shell_command", started.ToolName);
                    Assert.False(string.IsNullOrWhiteSpace(started.Arguments));
                    break;
                case AgentEvent.ToolCompleted:
                    toolCompleted = true;
                    break;
                case AgentEvent.ContentDelta delta:
                    if (toolCompleted) sawContentAfterTool = true;
                    content.Append(delta.Text);
                    break;
            }
        }

        _output.WriteLine($"=== streamed answer ===\n{content}");

        Assert.True(toolStarted, "expected a ToolStarted event");
        Assert.True(toolCompleted, "expected a ToolCompleted event");
        Assert.True(sawContentAfterTool, "expected the answer to stream after the tool ran");
        Assert.False(string.IsNullOrWhiteSpace(content.ToString()));
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
