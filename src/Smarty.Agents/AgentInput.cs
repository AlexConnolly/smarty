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

    /// <summary>Which provider and model to use for lighter tasks (like chunk ranking) when speed/cost is preferred over full reasoning.</summary>
    public ModelSpec? SecondaryModel { get; set; }

    /// <summary>Whether the model should run its chain-of-thought. Off makes each turn much faster on a
    /// thinking model (no deliberation) — good for mechanical tool-driven work where deep reasoning isn't
    /// needed. Defaults to true.</summary>
    public bool Think { get; set; } = true;

    /// <summary>Maximum model round-trips before the loop gives up (guards against tool loops).</summary>
    public int MaxIterations { get; set; } = 8;

    /// <summary>When a tool reports an error, inject a corrective nudge so the model retries
    /// instead of giving up and merely describing the failure.</summary>
    public bool NudgeOnToolError { get; set; } = true;

    /// <summary>The nudge appended after a TRANSIENT failed tool call (one worth retrying) when
    /// <see cref="NudgeOnToolError"/> is on. Dead-end failures and exhausted budgets get different,
    /// stronger steers from the loop instead.</summary>
    public string ToolErrorNudge { get; set; } =
        "That tool call FAILED (see the error in the tool result above). Do not give up, apologise, " +
        "or just explain the problem to the user. Work out what went wrong, then call the tool again " +
        "with a corrected command/arguments that are valid for this environment.";

    /// <summary>How many failed tool calls a run tolerates before the loop tells the model to stop trying
    /// new calls and conclude with whatever it has (or admit it couldn't). Guards against thrashing against
    /// dead ends; resilience below this stays high.</summary>
    public int MaxToolFailures { get; set; } = 4;

    /// <summary>How many times a SINGLE tool may be called in one run before further calls are blocked. Stops
    /// a relentless loop (e.g. searching the same un-findable thing over and over) even when each call
    /// "succeeds" with useless results — the failure budget never catches that, but this does. Per tool name.</summary>
    public int MaxCallsPerTool { get; set; } = 5;

    /// <summary>Hard cap on tokens the model may generate per turn (backstop against runaway output).</summary>
    public int MaxOutputTokensPerTurn { get; set; } = 16384;

    /// <summary>Sampling repetition penalty. Keep this LOW (1.0 = off). Higher values push the model
    /// off its normal vocabulary into degenerate, non-cyclic "token-salad" rambling on long outputs —
    /// which the loop-detector can't catch (it only catches tight cyclic repetition). Real loops are
    /// handled by the detector + recovery, so we don't lean on this at all.</summary>
    public double RepeatPenalty { get; set; } = 1.0;

    /// <summary>Abort a turn that runs longer than this (a stuck generation can't run forever).</summary>
    public TimeSpan? TurnTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>When a turn is cut off for looping or timing out, nudge the model to conclude and retry
    /// (keeping all prior work) instead of returning the runaway turn as the answer.</summary>
    public bool RecoverFromLoops { get; set; } = true;

    /// <summary>Optional source of out-of-band messages, drained between iterations and appended to the
    /// running conversation. Lets a caller steer or interrupt a long task while it is still working
    /// (e.g. "actually, stop after the first batch"). Returns the messages to inject, or none.</summary>
    public Func<IReadOnlyList<Message>>? DrainInbox { get; set; }

    /// <summary>The nudge injected when a turn produced no usable answer (looped, ran too long, or left
    /// the answer in its thinking instead of replying).</summary>
    public string LoopRecoveryNudge { get; set; } =
        "You did NOT give the user a final answer (you may have only reasoned internally without " +
        "replying, started repeating yourself, or run too long). Write your final answer NOW as your " +
        "actual reply to the user — concise and complete. Put the answer in your response, not only in " +
        "your private thinking. Do not repeat your earlier reasoning. If you genuinely still need " +
        "information, call a tool — but do not loop or over-think.";
}
