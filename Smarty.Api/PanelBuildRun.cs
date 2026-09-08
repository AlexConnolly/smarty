namespace Smarty.Api;

/// <summary>
/// What the machine needs from the panel on the page. Small on purpose: the states are testable exactly to the
/// extent that this is, and a build that could reach the whole store would be a build that could quietly do
/// anything from any state.
/// </summary>
public interface IPanelBoard
{
    /// <summary>Is the design actually on the home page? Asked rather than assumed — see <see cref="PanelRun"/>.</summary>
    bool ShowsDesign(string panelId);

    /// <summary>Is the panel live, against a published kind?</summary>
    bool IsPublished(string panelId);

    /// <summary>Say what is happening to it, in the words that go over the design.</summary>
    void Stage(string panelId, string stage);

    /// <summary>It stopped short. The reason goes on the panel, and the design stays where it is.</summary>
    void Blocked(string panelId, string why);
}

/// <summary>The home page, as the machine sees it.</summary>
public sealed class WidgetBoard : IPanelBoard
{
    private readonly WidgetStore _widgets;

    public WidgetBoard(WidgetStore widgets) => _widgets = widgets;

    public bool ShowsDesign(string panelId) => _widgets.Get(panelId)?.Design is { Length: > 0 };

    public bool IsPublished(string panelId) =>
        _widgets.Get(panelId) is { Status: WidgetStatus.Live, Kind: { Length: > 0 } };

    public void Stage(string panelId, string stage) => _widgets.Reached(panelId, stage);

    // Failed rather than a state of its own, because the panel is not being built any more and nothing is coming
    // unless a person acts. What makes it bearable is that the design survives this now — the user is left looking
    // at the thing they asked for, with the reason it is empty written across it.
    public void Blocked(string panelId, string why) => _widgets.Failed(panelId, why);
}

/// <summary>Runs one state: a brief, a tool set and a budget, with a model somewhere in the middle.</summary>
public interface IPanelStepRunner
{
    /// <param name="note">
    /// Why this state is being entered — the previous state's failure, or the fault a check found. Empty on a first
    /// pass. This is the entire channel between states: no transcript is shared, so anything the next state needs to
    /// know is either an artifact on the record or a sentence here.
    /// </param>
    Task<PanelStepOutcome> RunAsync(PanelBuildRecord record, PanelStep step, string note, CancellationToken ct);
}

/// <summary>The words that go on the panel while each state runs.</summary>
public static class PanelStages
{
    public static string Of(PanelStep step) => step switch
    {
        PanelStep.Agree => "Working out what to show",
        PanelStep.Design => Stages.Designing,
        PanelStep.Research => "Finding a source",
        PanelStep.Prove => "Checking the source answers",
        PanelStep.Bind => Stages.Feed,
        PanelStep.Verify => Stages.Testing,
        _ => Stages.Testing,
    };
}

/// <summary>
/// Driving the states, and checking the world between them.
///
/// <para>
/// The checks are the point, and they are all of the same kind: a state is judged on what it LEFT BEHIND, never on
/// what it said it did. A design state that reports success without the design being on the page has failed, and
/// says so in those words. A proof state that comes back with a response that never names the subject has failed,
/// however cheerful the response was — which is the whole of the Reading-instead-of-Leicester fault, caught by four
/// lines rather than by a paragraph of instruction nobody can enforce.
/// </para>
/// </summary>
public static class PanelRun
{
    public static async Task<PanelBuildRecord> DriveAsync(
        PanelBuildRecord record, IPanelStepRunner runner, IPanelBoard board, CancellationToken ct = default)
    {
        string note = "";

        while (record.Step is not (PanelStep.Done or PanelStep.Blocked))
        {
            if (ct.IsCancellationRequested)
            {
                Stop(record, board, "The build was stopped before it finished.");
                return record;
            }

            var step = record.Step;
            record.Spend(step);
            board.Stage(record.PanelId, PanelStages.Of(step));

            PanelStepOutcome outcome;
            try
            {
                outcome = await runner.RunAsync(record, step, note, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Stop(record, board, "The build was stopped before it finished.");
                return record;
            }
            catch (Exception ex)
            {
                // A state that throws is a state that failed, and the machine is the only thing that can decide what
                // that means. Swallowing it here and letting the transition run is what keeps a broken tool from
                // taking down a build that could have gone round another way.
                outcome = PanelStepOutcome.Failed($"That step ended in an error: {ex.Message}");
            }

            outcome = Left(record, board, step, outcome);
            record.Record(step, outcome);

            var move = PanelMachine.Next(record, outcome);
            record.Step = move.Step;
            note = move.Why;
        }

        if (record.Step == PanelStep.Blocked) Stop(record, board, record.Blocked ?? "The build stopped short.");
        return record;
    }

    /// <summary>
    /// What the state actually left behind, which is the only evidence worth anything.
    /// </summary>
    private static PanelStepOutcome Left(
        PanelBuildRecord r, IPanelBoard board, PanelStep step, PanelStepOutcome outcome)
    {
        if (!outcome.Ok) return outcome;

        return step switch
        {
            PanelStep.Agree when r.Contract is not { Fields.Count: > 0 } => PanelStepOutcome.Failed(
                "Nothing was written down about what the panel shows. Agree the fields with panel_shows before " +
                "anything else — every step after this one is measured against them."),

            // The rule that outranks the others. A build may fail to find a source; it may not leave the user with
            // nothing to look at while it tries, and it may not report a design it never put up.
            PanelStep.Design when !board.ShowsDesign(r.PanelId) => PanelStepOutcome.Failed(
                "The design is not on the page. Call widget_design with the component and sample values — the user " +
                "sees this panel being made, and nothing goes looking for a source until they can."),

            PanelStep.Research when r.Source is not { } source || !Reachable(source) => PanelStepOutcome.Failed(
                "No source was written down. Name the subject, what identifies it to that source, and the url to " +
                "call, with panel_source."),

            // A struck source offered again. The list of what has already failed is in the brief, and a state that
            // ignores it would go round this loop until the backstop — which is the original failure wearing a
            // state machine as a hat. So the strike is enforced here rather than requested there.
            PanelStep.Research when Struck(r) is { } struck => PanelStepOutcome.Failed(
                $"{struck} has already been tried in this build and did not answer. It cannot be the source. Find a " +
                "genuinely different one, or say plainly that nothing you can reach carries this."),

            PanelStep.Prove when r.Proof is not { } proof => PanelStepOutcome.Failed(
                "The source was never actually called. Call it with the real values and record what came back with " +
                "panel_proof — a url nobody has called is a guess."),

            // The Reading test. A wrong identifier is not answered with an error; it is answered correctly, about
            // something else, and every mechanism downstream reports a healthy panel about the wrong club.
            PanelStep.Prove when !r.Proof!.Subject => PanelStepOutcome.Failed(
                $"What came back doesn't name {Subject(r)}" +
                (r.Source?.Expect is { Length: > 0 } expect ? $" — nothing matching \"{expect}\" is in it" : "") +
                ". The identifier is for something else, or that source doesn't carry this subject at all."),

            PanelStep.Bind when !board.IsPublished(r.PanelId) => PanelStepOutcome.Failed(
                "The panel still isn't published. widget_publish is the only thing that puts it on the page — a " +
                "component written anywhere else is not a panel."),

            _ => outcome,
        };
    }

    /// <summary>The name of the source this state offered, if it is one that has already been struck off.</summary>
    private static string? Struck(PanelBuildRecord r) =>
        PanelMachine.Named(r.Source) is { Length: > 0 } name
        && r.Struck.Contains(name, StringComparer.OrdinalIgnoreCase)
            ? name
            : null;

    // A client-mode or internal panel has no url to call and cannot have one: the page fetches for itself, or the
    // data is ours already. Insisting on a url would make those two modes unbuildable, which is how a "prove the
    // source" rule turns into a build port-scanning localhost looking for something to prove.
    private static bool Reachable(PanelSource source) =>
        source.Url is { Length: > 0 }
        || string.Equals(source.Mode, LoaderModes.Client, StringComparison.OrdinalIgnoreCase)
        || string.Equals(source.Mode, LoaderModes.Internal, StringComparison.OrdinalIgnoreCase);

    private static string Subject(PanelBuildRecord r) =>
        r.Source?.Subject is { Length: > 0 } s ? s : "the subject";

    private static void Stop(PanelBuildRecord r, IPanelBoard board, string why)
    {
        r.Blocked = why;
        r.Step = PanelStep.Blocked;
        board.Blocked(r.PanelId, why);
    }
}
