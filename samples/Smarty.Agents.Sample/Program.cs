using System.Runtime.InteropServices;
using Smarty.Agents;

// The clean end-to-end demo: create an agent, give it a multi-parameter shell tool,
// and STREAM its answer to the system-status question as it is written. Everything is
// real — real Ollama, real shell. Only the output is streamed, never the thinking.

string os = RuntimeInformation.OSDescription;

var input = new AgentInput
{
    SystemPrompt =
        $"You are a system administrator assistant running on this machine: {os}. " +
        "When asked about the system, use the run_shell_command tool to gather real data, " +
        "then summarise it concisely for the user. Prefer commands valid for this operating system.",
    Tools = { ShellTool.Create() },
    Model = ModelSpec.Ollama("qwen3:4b"),
};

var agent = new SmartyAgent(input);

Console.WriteLine("Asking the agent: \"What is the current system status?\"\n");

await foreach (var ev in agent.AnswerStream("What is the current system status?"))
{
    switch (ev)
    {
        case AgentEvent.ToolStarted tool:
            Console.WriteLine($"\n[tool] {tool.ToolName} {tool.Arguments}");
            break;
        case AgentEvent.ToolCompleted:
            Console.WriteLine("[tool done]\n");
            break;
        case AgentEvent.ContentDelta delta:
            Console.Write(delta.Text);   // <-- the answer streams here, token by token
            break;
        case AgentEvent.Completed:
            Console.WriteLine();
            break;
    }
}
