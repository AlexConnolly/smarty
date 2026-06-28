using Smarty.Agents;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Verifies recovery of a tool call that a model emitted as inline JSON text in its content
/// (instead of a structured call). No network: a scripted provider reproduces the exact qwen
/// misbehaviour, and we assert the agent runs the tool and discards the inline JSON.
/// </summary>
public class InlineToolCallTests
{
    [Fact]
    public async Task Inline_tool_call_in_content_is_executed_not_printed()
    {
        // Turn 1: the model "chats" and embeds the tool call as text (the reported bug).
        // Turn 2 (after the tool runs): it gives the real answer.
        const string inlineTurn =
            "I'll check the temp folder size.\n\n" +
            "{\n  \"name\": \"run_shell_command\",\n  \"arguments\": { \"command\": \"measure-temp\", \"timeout_seconds\": 30 }\n}";

        var provider = new ScriptedProvider(new[]
        {
            ModelTurn.Text(inlineTurn),
            ModelTurn.Text("The temp folder is 4.2 GB."),
        });

        var registry = new ModelProviderRegistry();
        registry.Register("scripted", _ => provider);

        bool toolRan = false;
        string? gotCommand = null;
        var shell = new AgentTool(
            "run_shell_command",
            "Run a shell command.",
            new[] { ToolParameter.String("command", "cmd", required: true), ToolParameter.Integer("timeout_seconds", "t") },
            (ToolCallArguments args, CancellationToken _) =>
            {
                toolRan = true;
                gotCommand = args.GetStringOrNull("command");
                return Task.FromResult(ToolOutput.Ok("Temp = 4.2 GB"));
            });

        var input = new AgentInput
        {
            SystemPrompt = "test",
            Tools = { shell },
            Model = new ModelSpec("scripted", "stub"),
        };

        var agent = new SmartyAgent(input, registry);
        var result = await agent.AnswerStream("How big is the temp folder?").ReadAllAsync();

        // The inline call was recovered and actually executed with the right argument.
        Assert.True(toolRan, "the inline tool call should have been executed");
        Assert.Equal("measure-temp", gotCommand);
        Assert.Single(result.Tools);
        Assert.Equal("run_shell_command", result.Tools[0].ToolName);

        // The final answer is the real one — NOT the inline JSON, and not the preamble.
        Assert.Equal("The temp folder is 4.2 GB.", result.Text.Trim());
        Assert.DoesNotContain("run_shell_command", result.Text);
        Assert.DoesNotContain("\"arguments\"", result.Text);
    }

    [Fact]
    public async Task Xml_inline_tool_call_in_content_is_executed_not_printed()
    {
        const string xmlTurn =
            "Alright Alex — first pass found the weirdness.\n\n" +
            "<tool_call>run_shell_command<arg_key>command</arg_key><arg_value>measure-temp</arg_value><arg_key>timeout_seconds→30</arg_value></tool_call>";

        var provider = new ScriptedProvider(new[]
        {
            ModelTurn.Text(xmlTurn),
            ModelTurn.Text("The temp folder is 4.2 GB."),
        });

        var registry = new ModelProviderRegistry();
        registry.Register("scripted", _ => provider);

        bool toolRan = false;
        string? gotCommand = null;
        var shell = new AgentTool(
            "run_shell_command",
            "Run a shell command.",
            new[] { ToolParameter.String("command", "cmd", required: true), ToolParameter.Integer("timeout_seconds", "t") },
            (ToolCallArguments args, CancellationToken _) =>
            {
                toolRan = true;
                gotCommand = args.GetStringOrNull("command");
                return Task.FromResult(ToolOutput.Ok("Temp = 4.2 GB"));
            });

        var input = new AgentInput
        {
            SystemPrompt = "test",
            Tools = { shell },
            Model = new ModelSpec("scripted", "stub"),
        };

        var agent = new SmartyAgent(input, registry);
        var result = await agent.AnswerStream("How big is the temp folder?").ReadAllAsync();

        Assert.True(toolRan, "the xml inline tool call should have been executed");
        Assert.Equal("measure-temp", gotCommand);
        Assert.Single(result.Tools);
        Assert.Equal("run_shell_command", result.Tools[0].ToolName);

        Assert.Equal("The temp folder is 4.2 GB.", result.Text.Trim());
    }

    // --- a tiny scripted provider: returns canned turns in order ---

    private sealed record ModelTurn(string Content)
    {
        public static ModelTurn Text(string content) => new(content);
    }

    private sealed class ScriptedProvider : IModelProvider
    {
        private readonly IReadOnlyList<ModelTurn> _turns;
        private int _next;

        public ScriptedProvider(IReadOnlyList<ModelTurn> turns) => _turns = turns;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            var turn = _turns[Math.Min(_next, _turns.Count - 1)];
            _next++;

            // Stream the content in a couple of chunks, like a real model would.
            int mid = turn.Content.Length / 2;
            yield return new ModelStreamEvent.Content(turn.Content[..mid]);
            yield return new ModelStreamEvent.Content(turn.Content[mid..]);
            yield return new ModelStreamEvent.Completed(new ModelResponse { Content = turn.Content });
        }
    }
}
