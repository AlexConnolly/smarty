namespace Smarty.Agents;

/// <summary>
/// Everything a <see cref="SmartyAgent"/> needs: a system prompt, the current
/// conversation, the available tools, and which model provider/model to use.
/// </summary>
public sealed class AgentInput
{
    /// <summary>Instructions that frame the agent's behaviour.</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>Prior conversation. New turns are appended as the agent runs.</summary>
    public List<Message> Conversation { get; set; } = new();

    /// <summary>Tools the agent may call. Supports multiple, typed parameters each.</summary>
    public List<AgentTool> Tools { get; set; } = new();

    /// <summary>Which provider and model to use. Defaults to the local Ollama gateway.</summary>
    public ModelSpec Model { get; set; } = ModelSpec.Default;

    /// <summary>Maximum model round-trips before the loop gives up (guards against tool loops).</summary>
    public int MaxIterations { get; set; } = 8;

    /// <summary>When a tool reports an error, inject a corrective nudge so the model retries
    /// instead of giving up and merely describing the failure.</summary>
    public bool NudgeOnToolError { get; set; } = true;

    /// <summary>The nudge appended after a failed tool call when <see cref="NudgeOnToolError"/> is on.</summary>
    public string ToolErrorNudge { get; set; } =
        "That tool call FAILED (see the error in the tool result above). Do not give up, apologise, " +
        "or just explain the problem to the user. Work out what went wrong, then call the tool again " +
        "with a corrected command/arguments that are valid for this environment. Keep trying different " +
        "approaches until the tool succeeds and you can actually answer the question.";
}
