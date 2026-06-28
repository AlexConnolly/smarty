using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Smarty.Agents;
using Xunit;

namespace Smarty.Agents.Tests;

public class GateAgentTests
{
    private class MockGateProvider : IGateProvider
    {
        public bool ReturnValue { get; set; }
        public string? LastAction { get; private set; }
        public string? LastDescription { get; private set; }

        public Task<bool> RequestAccessAsync(string action, string description, CancellationToken ct = default)
        {
            LastAction = action;
            LastDescription = description;
            return Task.FromResult(ReturnValue);
        }
    }

    [Fact]
    public async Task ShellTool_respects_gate_approval()
    {
        var gate = new MockGateProvider { ReturnValue = true };
        var tool = ShellTool.Create(gate, "run_shell_command");

        var args = new ToolCallArguments(JsonDocument.Parse("{\"command\":\"echo hello\"}").RootElement);
        var output = await tool.InvokeAsync(args, CancellationToken.None);

        Assert.Equal("run_shell_command", gate.LastAction);
        Assert.Equal("echo hello", gate.LastDescription);
        Assert.False(output.IsError);
        Assert.Contains("hello", output.Content);
    }

    [Fact]
    public async Task ShellTool_respects_gate_denial()
    {
        var gate = new MockGateProvider { ReturnValue = false };
        var tool = ShellTool.Create(gate, "run_shell_command");

        var args = new ToolCallArguments(JsonDocument.Parse("{\"command\":\"echo hello\"}").RootElement);
        var output = await tool.InvokeAsync(args, CancellationToken.None);

        Assert.Equal("run_shell_command", gate.LastAction);
        Assert.Equal("echo hello", gate.LastDescription);
        Assert.True(output.IsError);
        Assert.False(output.CanRetry); // DeadEnd
        Assert.Contains("Access denied by the user", output.Content);
    }
}
