using System.Runtime.InteropServices;
using Smarty.Agents;
using Xunit;
using Xunit.Abstractions;

namespace Smarty.Agents.Tests;

/// <summary>
/// Real, end-to-end integration tests. Nothing is mocked: these talk to the live local
/// Ollama gateway (qwen3) and execute real shell commands. If Ollama is unreachable the
/// test returns early rather than failing the build.
/// </summary>
public class SystemStatusAgentTests
{
    private readonly ITestOutputHelper _output;

    public SystemStatusAgentTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Agent_uses_shell_tool_to_report_system_status()
    {
        if (!await OllamaIsReachableAsync())
        {
            _output.WriteLine("Ollama gateway not reachable at localhost:11434 — skipping live test.");
            return;
        }

        // Track that the tool actually ran with real, multi-parameter arguments.
        var invokedCommands = new List<string>();
        var shellTool = WrapWithSpy(ShellTool.Create("run_shell_command"), invokedCommands);

        string os = RuntimeInformation.OSDescription;
        var input = new AgentInput
        {
            SystemPrompt =
                $"You are a system administrator assistant on this machine: {os}. " +
                "Use the run_shell_command tool to gather real data before answering, " +
                "and prefer commands valid for this operating system.",
            Tools = { shellTool },
            Model = ModelSpec.Ollama("qwen3:4b"),
        };

        var agent = new SmartyAgent(input);

        // The clean, headline API.
        string answer = await agent.Answer("What is the current system status?");

        _output.WriteLine("=== Answer ===");
        _output.WriteLine(answer);
        _output.WriteLine("=== Tool invocations ===");
        foreach (var inv in agent.LastRun!.ToolInvocations)
            _output.WriteLine($"{inv.ToolName} {inv.Arguments} -> {Truncate(inv.Result, 120)}");

        // (a) The tool was actually invoked by the model.
        Assert.NotEmpty(agent.LastRun!.ToolInvocations);
        Assert.Contains(agent.LastRun.ToolInvocations, i => i.ToolName == "run_shell_command");

        // (b) A real command executed (the spy captured a non-empty command) and produced output.
        Assert.NotEmpty(invokedCommands);
        Assert.All(invokedCommands, c => Assert.False(string.IsNullOrWhiteSpace(c)));
        Assert.Contains(agent.LastRun.ToolInvocations, i => !string.IsNullOrWhiteSpace(i.Result));

        // (c) A non-empty textual answer came back.
        Assert.False(string.IsNullOrWhiteSpace(answer));
    }

    /// <summary>Wraps a tool so we can record the real arguments the model passed.</summary>
    private static AgentTool WrapWithSpy(AgentTool inner, List<string> capturedCommands)
    {
        return new AgentTool(
            inner.Name,
            inner.Description,
            inner.Parameters,
            async (args, ct) =>
            {
                capturedCommands.Add(args.GetStringOrNull("command") ?? "");
                return await inner.InvokeAsync(args, ct);
            });
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

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
