namespace Smarty.Api;

/// <summary>
/// One step of a multi-discipline <see cref="WorkPlan"/>: a single specialist persona doing one self-contained
/// piece, handing a concrete artifact to the next. A step is run as a hidden child <see cref="TaskInfo"/> (a
/// normal worker), so it keeps its own transcript and can be resumed when the user refines.
/// </summary>
public sealed record PlanStep(string Persona, string Instruction, string Produces)
{
    /// <summary>pending | running | done | failed.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>The step's final output once it has run — threaded into the next step's brief.</summary>
    public string? Result { get; set; }

    /// <summary>The id of the child task that ran this step, so a refine can resume it (not redo it).</summary>
    public string? ChildTaskId { get; set; }
}

/// <summary>
/// An ordered, multi-discipline plan: the thing a single persona can't do alone because it crosses disciplines
/// (data → product → engineering → review). Built by the planner when triage finds more than one discipline,
/// pinned onto the coordinator task, and run step-by-step — each step's output feeding the next over the shared
/// thread file area. A finished or paused plan is re-entered at a step (by a user refine or a recovery) and
/// cascades forward, never starting over.
/// </summary>
public sealed class WorkPlan
{
    public required string Goal { get; init; }
    public required List<PlanStep> Steps { get; init; }

    /// <summary>The step the coordinator is on (0-based). Advanced as steps complete; rewound to the targeted
    /// step on a refine so execution resumes there and re-runs downstream.</summary>
    public int CurrentStep { get; set; }
}
