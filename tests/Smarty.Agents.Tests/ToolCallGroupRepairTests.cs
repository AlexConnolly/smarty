using System.Runtime.CompilerServices;
using System.Text.Json;
using Smarty.Agents;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// A worker leg interrupted mid tool-call group (cancel/timeout/watchdog abort) leaves the persisted
/// transcript with an assistant parallel tool-call group only partially answered. Replaying that to a
/// provider fails with "incomplete parallel tool-call group: tool call X has no tool response". The agent
/// must repair the gap before the next model call so every tool call is answered.
/// </summary>
public class ToolCallGroupRepairTests
{
    private static JsonElement EmptyArgs()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Unanswered_parallel_tool_call_is_closed_before_next_model_call()
    {
        // A transcript left over from an interrupted leg: the assistant fired TWO tool calls in parallel,
        // but only the first ("call_a") got a result before the leg was cut off. "call_b" is dangling.
        var conversation = new List<Message>
        {
            Message.User("Analyse the file."),
            Message.Assistant("", null, new[]
            {
                new ToolCall("call_a", "run_python", EmptyArgs()),
                new ToolCall("call_b", "run_python", EmptyArgs()),
            }),
            Message.ToolResult("call_a", "run_python", "exit 0"),
            // call_b has NO tool result — this is the corruption that triggers the provider 400.
        };

        var provider = new RecordingProvider();
        var registry = new ModelProviderRegistry();
        registry.Register("scripted", _ => provider);

        var input = new AgentInput
        {
            SystemPrompt = "test",
            Model = new ModelSpec("scripted", "stub"),
            Conversation = conversation,
        };
        var agent = new SmartyAgent(input, registry);

        var result = await agent.AnswerStream("continue").ReadAllAsync();

        Assert.Equal("Done.", result.Text.Trim());

        // The conversation the provider actually received must answer BOTH parallel tool calls — otherwise
        // a real provider rejects it with invalid_tool_messages.
        var seen = provider.FirstRequestMessages;
        var assistant = seen.First(m => m.Role == Role.Assistant && m.ToolCalls is { Count: > 0 });
        foreach (var call in assistant.ToolCalls!)
        {
            Assert.Contains(seen, m => m.Role == Role.Tool && m.ToolCallId == call.Id);
        }
    }

    [Fact]
    public async Task Wellformed_transcript_is_left_untouched()
    {
        var conversation = new List<Message>
        {
            Message.User("hi"),
            Message.Assistant("", null, new[] { new ToolCall("call_a", "run_python", EmptyArgs()) }),
            Message.ToolResult("call_a", "run_python", "exit 0"),
        };

        var provider = new RecordingProvider();
        var registry = new ModelProviderRegistry();
        registry.Register("scripted", _ => provider);

        var input = new AgentInput
        {
            SystemPrompt = "test",
            Model = new ModelSpec("scripted", "stub"),
            Conversation = conversation,
        };
        var agent = new SmartyAgent(input, registry);

        await agent.AnswerStream("continue").ReadAllAsync();

        // Nothing synthetic was inserted into the already-complete group.
        Assert.DoesNotContain(conversation, m =>
            m.Role == Role.Tool && (m.Content?.Contains("interrupted") ?? false));
        // Exactly one tool result still answers call_a (no duplicate was added).
        Assert.Single(conversation.Where(m => m.Role == Role.Tool && m.ToolCallId == "call_a"));
    }

    private sealed class RecordingProvider : IModelProvider
    {
        public int Calls { get; private set; }
        public List<Message> FirstRequestMessages { get; private set; } = new();

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (Calls == 0)
                FirstRequestMessages = request.Messages.ToList();
            Calls++;

            yield return new ModelStreamEvent.Completed(new ModelResponse
            {
                Content = "Done.",
                Finish = FinishReason.Stop,
            });
        }
    }
}
