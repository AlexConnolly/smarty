# Smarty.Agents — Specification

`Smarty.Agents` is a small, dependency-light agentic framework for C#. A *smart agent*
takes an **input** and produces an **output**:

- **Input** = the current conversation + the available tools + a system prompt +
  a specification of which model provider/model to use.
- **Output** = the agent's textual answer, produced after (optionally) calling tools.

The headline goal is that it is trivial to use:

```csharp
var input = new AgentInput
{
    SystemPrompt = "You are a system administrator assistant.",
    Tools = { shellTool },                 // a tool with multiple parameters
    Model = ModelSpec.Ollama("qwen3:4b"),  // provider spec lives in the input
};

string answer = await new SmartyAgent(input).Answer("What is the current system status?");
```

The default model provider is the **local Ollama gateway** (`http://localhost:11434`)
running **qwen3**.

---

## Core concepts

### Conversation & messages

`Message` is a single turn. `Role` is one of `System`, `User`, `Assistant`, `Tool`.
An assistant message may carry `ToolCalls`; a tool message carries the result of one
tool call (`ToolCallId` + `ToolName` + `Content`).

The conversation is just an ordered `List<Message>`. The system prompt is supplied
separately on `AgentInput` and prepended when talking to the provider.

### Tools

A tool is data + a delegate:

```csharp
new AgentTool(
    name: "run_shell_command",
    description: "Run a shell command on the local machine and return its output.",
    parameters: new[]
    {
        ToolParameter.String("command",     "The command line to execute.", required: true),
        ToolParameter.String("working_dir", "Directory to run the command in.", required: false),
        ToolParameter.Integer("timeout_seconds", "Max seconds to wait.", required: false),
    },
    execute: async (args, ct) =>
    {
        var cmd = args.GetString("command");
        var dir = args.GetStringOrNull("working_dir");
        ...
        return output;
    });
```

`ToolParameter` describes one input field (`Name`, `Description`, `Type`, `Required`).
Tools therefore support **multiple, typed input parameters**, serialized to a
JSON-Schema `parameters` object for the model.

`ToolCallArguments` wraps the model-supplied argument object and exposes typed getters
(`GetString`, `GetStringOrNull`, `GetInt`, `GetBool`, ...).

### Model provider abstraction

```csharp
public interface IModelProvider
{
    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct = default);
}
```

- `ModelRequest`  = model name, system prompt, messages, tool definitions.
- `ModelResponse` = the assistant message (clean text content + any tool calls + optional reasoning).

`ModelSpec` selects a provider and model: `(Provider, Model, BaseUrl?)`. Factory helpers:
`ModelSpec.Ollama("qwen3:4b")`, `ModelSpec.Default` (== Ollama qwen3:4b).

`ModelProviderRegistry` resolves a `ModelSpec` to an `IModelProvider`. The default
registry knows the `"ollama"` provider. Callers can register more providers later.

### Ollama provider

`OllamaModelProvider` talks to `POST {BaseUrl}/api/chat` with `stream:true`, reading the
NDJSON chunk stream and emitting `ModelStreamEvent`s.

Key detail discovered against the live gateway: qwen3 is a *thinking* model. With
`think:true` each chunk carries `content` and `thinking` as **separate delta fields** — so we
stream `content` as the answer and `thinking` as reasoning, and they never mix. Tool calls
arrive complete in `message.tool_calls[].function` with `name` and a **parsed** `arguments`
object (typically in a chunk near the end); the final chunk has `done:true`.

Tool results are sent back as messages with `role:"tool"`, `tool_name`, and the
textual result.

### Streaming

Everything is streaming-first. The provider's primary method is:

```csharp
IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken ct);
```

`ModelStreamEvent` is `Content(text)` | `Reasoning(text)` | `Completed(ModelResponse)`.
Crucially **content and reasoning stream on separate channels** — so callers can stream the
*output* (`Content`) without ever seeing the chain-of-thought (`Reasoning`). The terminal
`Completed` carries the fully assembled response (content + tool calls) for the loop to act on.
`CompleteAsync` is a default-interface convenience that just drains the stream, so new providers
only implement streaming.

The agent exposes the same shape one level up:

```csharp
await foreach (var ev in agent.AnswerStream("..."))   // AgentEvent stream
    if (ev is AgentEvent.ContentDelta d) Console.Write(d.Text);
```

`AgentEvent` is `ContentDelta` | `ReasoningDelta` | `ToolStarted` | `ToolCompleted` | `Completed`.
`Answer(...)` is a thin buffered wrapper that drains `AnswerStream` and returns the final answer,
so both APIs share one code path.

### The agent loop

`SmartyAgent.AnswerStream(userMessage)`:

1. Append the user message to the conversation.
2. Up to `MaxIterations` times:
   - Stream a completion from the provider; forward `Content`→`ContentDelta` and
     `Reasoning`→`ReasoningDelta`, capturing the terminal `Completed` response.
   - Append the assistant message to the conversation (recording a transcript).
   - If there are **no** tool calls → emit `AgentEvent.Completed(content)` and stop.
   - Otherwise, per tool call: emit `ToolStarted`, execute the tool, append a `Tool` message,
     emit `ToolCompleted`; then loop.
3. If iterations are exhausted, emit `Completed` with the last assistant text.

The full transcript of a run is exposed via `SmartyAgent.LastRun` for inspection/testing.

---

## Project layout

```
src/Smarty.Agents/           class library (net7.0, System.Text.Json only)
samples/Smarty.Agents.Sample console app: the clean end-to-end demo
tests/Smarty.Agents.Tests/   xUnit integration test against the LIVE Ollama gateway
Smarty.sln
```

## Testing philosophy

Tests are **real integration tests** — no mocked model, no mocked shell. The
canonical test creates an agent, gives it the multi-parameter `run_shell_command`
tool, asks for system status, and asserts that (a) the tool was actually invoked,
(b) a real command executed and produced output, and (c) a non-empty answer came
back. If the local Ollama gateway is not reachable the test is skipped (not failed).
