using System.Text.Json;
using Smarty.Agents;

var provider = new OllamaModelProvider("http://localhost:11434");
var modelName = "qwen3:4b";

var agentInput = new AgentInput
{
    SystemPrompt =
        "You are a web search assistant. Your task is to answer the user's question by fetching web pages.\n" +
        "You have the 'web_page_load' tool to load any URL. Think about what URL is most likely to contain " +
        "the information (e.g., specific sites, search/news portals, company domains, etc.), fetch it, " +
        "read the content, and then formulate a clear answer. If the page doesn't have the answer, you " +
        "can try another URL. Be concise and accurate.",
    Model = ModelSpec.Ollama(modelName),
    Tools = { WebSearcherTool.CreatePageLoadTool() },
    MaxIterations = 4
};

var registry = new ModelProviderRegistry();
registry.Register("ollama", _ => provider);

var subAgent = new SmartyAgent(agentInput, registry);

Console.WriteLine("Running sub-agent directly for: 'What's the latest BBC news?'\n");

string question = "What's the latest BBC news?";
await foreach (var ev in subAgent.AnswerStream(question))
{
    switch (ev)
    {
        case AgentEvent.ContentDelta d:
            Console.Write(d.Text);
            break;
        case AgentEvent.ReasoningDelta r:
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(r.Text);
            Console.ResetColor();
            break;
        case AgentEvent.ToolStarted ts:
            Console.WriteLine($"\n[Tool Started: {ts.ToolName}] Arguments: {ts.Arguments}");
            break;
        case AgentEvent.ToolCompleted tc:
            Console.WriteLine($"\n[Tool Completed: {tc.ToolName}] Output length: {tc.Result.Length}");
            // Print the first 500 characters of the tool result
            string snippet = tc.Result.Length > 500 ? tc.Result.Substring(0, 500) : tc.Result;
            Console.WriteLine($"--- TOOL OUTPUT SNIPPET ---\n{snippet}\n-------------------------");
            break;
        case AgentEvent.Completed done:
            Console.WriteLine($"\n[Completed] Answer: {done.Answer}");
            break;
    }
}

// Print the entire final transcript of messages
Console.WriteLine("\n=== FULL TRANSCRIPT ===");
foreach (var msg in subAgent.LastRun.Messages)
{
    Console.WriteLine($"[{msg.Role}] {msg.Content}");
    if (msg.ToolCalls is { Count: > 0 })
    {
        foreach (var tc in msg.ToolCalls)
            Console.WriteLine($"  - Tool Call: {tc.Name} with args: {tc.Arguments}");
    }
}
