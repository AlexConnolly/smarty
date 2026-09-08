namespace Smarty.Api;

/// <summary>
/// Building a panel as a fixed sequence of states, rather than as one long brief and a hope.
///
/// <para>
/// The old shape was a single worker holding twenty-one thousand characters of instruction and every tool at once:
/// agree the shape, draw it, find a source, prove it, publish, check. It worked when the source was easy and fell
/// apart when it wasn't, because nothing in it could tell the difference between "this step failed" and "the job
/// failed". A build asked for the latest Leicester City score guessed a team id, was answered — confidently, with
/// 200 OK — about Reading, then re-issued one identical dead fetch thirty-eight times until its budget was gone. It
/// had written down the right conclusion ("I should stop fiddling with ESPN and pivot to a different key-less
/// source") four minutes before it stopped, and had no mechanism that could act on it.
/// </para>
/// <para>
/// So the sequence is code and only the work inside each state is a model. A state gets its own brief, its own
/// tools, its own budget, and must produce one named artifact; running out is a TRANSITION rather than a mood. A
/// source that will not answer strikes that source and returns to the research state with it struck off — which is
/// what the Leicester build needed and had no way to express.
/// </para>
/// <para>
/// One rule outranks the rest: <b>the design reaches the user</b>. It is drawn before any source is looked for, it
/// goes on the home page the moment it exists, and nothing that happens afterwards takes it down. A build that
/// cannot find a feed leaves the user looking at the panel they asked for, saying what it still needs — not an
/// empty box where a design used to be.
/// </para>
/// </summary>
public enum PanelStep
{
    /// <summary>Agree WHAT it shows: the fields, their types, and what it says when there is nothing on.</summary>
    Agree,

    /// <summary>Draw it against invented values in that shape, and put it in front of the user.</summary>
    Design,

    /// <summary>Work out who could answer: the subject, what identifies it, and which sources to try in order.</summary>
    Research,

    /// <summary>Call the top source for real and look at what comes back.</summary>
    Prove,

    /// <summary>Map the proven response onto the agreed contract and publish the kind.</summary>
    Bind,

    /// <summary>Look at the published panel with actual eyes and say whether it is right.</summary>
    Verify,

    /// <summary>Live, checked, finished.</summary>
    Done,

    /// <summary>Stopped, with a reason a person can act on. The design stays up if there is one.</summary>
    Blocked,
}

/// <summary>What a check of the built panel found wrong, which decides where the machine goes back to.</summary>
public enum PanelFault
{
    None,

    /// <summary>It renders, and it looks wrong — clipped, empty, the wrong shape. The component is at fault.</summary>
    Looks,

    /// <summary>The values are wrong or missing. The mapping from source to contract is at fault.</summary>
    Data,

    /// <summary>The values are honestly reported and the source is the wrong one, or is about the wrong thing.</summary>
    Source,
}

/// <summary>How a state ended: with its artifact, or with a reason it could not produce one.</summary>
/// <param name="Fault">Only meaningful from <see cref="PanelStep.Verify"/> — which way a failed check points.</param>
/// <param name="Terminal">
/// This is the end of the build, not the end of an attempt — so no retry, no other source, no going round.
///
/// <para>
/// The case that needs it is a state stopping to ask the user something only they can answer: a login, a
/// credential, which of two things they meant. Retrying that is asking again in a louder voice, and the machine
/// has no way to supply the answer.
/// </para>
/// </param>
public sealed record PanelStepOutcome(
    bool Ok, string? Reason = null, PanelFault Fault = PanelFault.None, bool Terminal = false)
{
    public static PanelStepOutcome Done() => new(true);
    public static PanelStepOutcome Failed(string reason) => new(false, reason);
    public static PanelStepOutcome Wrong(PanelFault fault, string reason) => new(false, reason, fault);
    public static PanelStepOutcome Stop(string reason) => new(false, reason, Terminal: true);
}

/// <summary>Where the machine goes next, and the sentence explaining it — kept on the record, not in a log file.</summary>
public sealed record PanelMove(PanelStep Step, string Why);

/// <summary>
/// One state's worth of history: which state, which attempt, and how it ended.
/// </summary>
/// <remarks>
/// Kept per attempt rather than per state because the interesting build is the one that went round twice, and the
/// second lap is invisible if the record only holds the last outcome.
/// </remarks>
public sealed class PanelAttempt
{
    public PanelStep Step { get; set; }
    public int Number { get; set; }
    public bool Ok { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>What the panel is going to show, agreed before anything is drawn or fetched.</summary>
/// <remarks>
/// Frozen once agreed. Everything downstream is measured against it — the design renders it, the proof is judged on
/// whether it can supply it, the binding maps onto it — and a contract that quietly widens as the build goes on is
/// the same undifferentiated job the states exist to break up. It can only be reopened by a check that says the
/// panel shows the wrong THING, which is the one fault a redraw cannot fix.
/// </remarks>
public sealed class PanelContract
{
    /// <summary>The fields, in the shape the component will be handed.</summary>
    public List<WidgetField> Fields { get; set; } = new();

    /// <summary>What it shows when the subject has nothing to report — no game on, no flight today, nothing sold.</summary>
    public string? WhenEmpty { get; set; }
}

/// <summary>Who could answer, and how this panel's subject is named to them.</summary>
public sealed class PanelSource
{
    /// <summary>The thing itself, in words: "Leicester City FC, the English football club".</summary>
    public string Subject { get; set; } = "";

    /// <summary>
    /// What the chosen source calls it — a team id, a ticker, an ICAO code.
    /// </summary>
    /// <remarks>
    /// Its own field because it is the half of the research that can be CHECKED. A guessed id is answered with a
    /// perfectly valid response about something else entirely, and the only defence is to state what was expected
    /// and compare. The Leicester build asked ESPN for team 338 and was told all about Reading.
    /// </remarks>
    public string? Identifier { get; set; }

    /// <summary>Where the identifier is expected to appear in the response, so the machine can check it landed.</summary>
    public string? Expect { get; set; }

    /// <summary>The url to call, with {parameters} where this panel's values go.</summary>
    public string Url { get; set; } = "";

    /// <summary>http | browser | client | internal.</summary>
    public string Mode { get; set; } = LoaderModes.Http;

    /// <summary>The next ones to try if this doesn't answer, best first. Spending these is the point of the list.</summary>
    public List<string> Alternatives { get; set; } = new();
}

/// <summary>What the source actually returned when it was called for real.</summary>
public sealed class PanelProof
{
    public string Url { get; set; } = "";
    public int? Status { get; set; }

    /// <summary>The head of the response, so the binding is written against what arrived rather than what was hoped.</summary>
    public string? Body { get; set; }

    /// <summary>Did the thing that came back name the subject we asked about?</summary>
    public bool Subject { get; set; }
}

/// <summary>
/// A build, as it stands. Lives on the panel, so a retry re-enters where it died instead of paying for the
/// design again.
/// </summary>
public sealed class PanelBuildRecord
{
    public string PanelId { get; set; } = "";

    /// <summary>What the user asked for, verbatim. Every state's brief opens with it.</summary>
    public string Shows { get; set; } = "";

    public PanelStep Step { get; set; } = PanelStep.Agree;

    public PanelContract? Contract { get; set; }
    public PanelSource? Source { get; set; }
    public PanelProof? Proof { get; set; }

    /// <summary>The kind that was published, once one was.</summary>
    public string? Kind { get; set; }

    /// <summary>Sources that were tried and did not answer. Never offered again in this build.</summary>
    public List<string> Struck { get; set; } = new();

    /// <summary>Attempts spent per state. The budget, and the thing no model can talk its way past.</summary>
    public Dictionary<PanelStep, int> Attempts { get; set; } = new();

    public List<PanelAttempt> Log { get; set; } = new();

    /// <summary>Why it stopped, when it stopped short. Shown on the panel, under its own design.</summary>
    public string? Blocked { get; set; }

    public int Spent(PanelStep step) => Attempts.TryGetValue(step, out int n) ? n : 0;

    public void Spend(PanelStep step) => Attempts[step] = Spent(step) + 1;

    public void Record(PanelStep step, PanelStepOutcome outcome) => Log.Add(new PanelAttempt
    {
        Step = step,
        Number = Spent(step),
        Ok = outcome.Ok,
        Reason = outcome.Reason,
    });
}

/// <summary>
/// How many goes each state gets before the machine stops asking it.
/// </summary>
/// <remarks>
/// <para>
/// Per state rather than per run, because the states are not worth the same. A design that will not render is cheap
/// to redo and is the thing the user is looking at, so it gets three; proving a source gets exactly one, because a
/// url either answered or it didn't and a second identical call is the failure this whole machine exists to
/// prevent.
/// </para>
/// <para>
/// Nothing inside a state can raise these. The agent loop has its own limits and a supervisor that extends them
/// when it judges the worker to be making progress — reasonable for open work, and the reason one build spent
/// thirty-eight calls on a fetch that had already failed thirty-seven times. A state's budget is spent by the
/// machine and read by nobody else.
/// </para>
/// </remarks>
public static class PanelBudget
{
    public static int Attempts(PanelStep step) => step switch
    {
        PanelStep.Agree => 2,
        PanelStep.Design => 3,
        PanelStep.Research => 2,
        PanelStep.Prove => 1,
        PanelStep.Bind => 2,
        PanelStep.Verify => 1,
        _ => 0,
    };

    /// <summary>How many sources may be struck off before the answer is "this needs a source I can't find".</summary>
    public const int Sources = 3;

    /// <summary>
    /// How many times a check may send the build back round.
    /// </summary>
    /// <remarks>
    /// Two, because the second lap is where a real fix happens and the third is where a panel starts oscillating
    /// between two wrong versions of itself. Past it the panel stays up with what the check said still showing,
    /// which is honest and costs nothing.
    /// </remarks>
    public const int Laps = 2;

    /// <summary>The backstop: no build runs more states than this, whatever the per-state budgets allow.</summary>
    public const int Steps = 14;
}

/// <summary>
/// The transitions. Pure, so the whole build can be reasoned about — and tested — without a model.
/// </summary>
public static class PanelMachine
{
    /// <summary>Where a build goes after a state ends, and why.</summary>
    public static PanelMove Next(PanelBuildRecord r, PanelStepOutcome outcome)
    {
        if (r.Log.Count >= PanelBudget.Steps)
            return Stop(r, "This build has taken more steps than any build should. Stopping rather than going round again.");

        // Nothing to route and nothing to retry: the state hit something the machine cannot supply.
        if (outcome.Terminal) return Stop(r, Reason(outcome));

        return r.Step switch
        {
            PanelStep.Agree => After(r, outcome, PanelStep.Design,
                "Nothing was agreed about what this panel would show, so there is nothing to draw."),

            // Back to where the build actually is. A redraw prompted by a check that said "it looks wrong" has a
            // proven source sitting on the record already, and sending it round the research and proof states again
            // would spend minutes re-establishing something nobody doubted.
            PanelStep.Design => After(r, outcome, r.Proof is not null ? PanelStep.Bind : PanelStep.Research,
                "The design never reached the page, so there is nothing to show while a source is found."),

            PanelStep.Research => AfterResearch(r, outcome),
            PanelStep.Prove => AfterProof(r, outcome),

            PanelStep.Bind => After(r, outcome, PanelStep.Verify,
                "The panel was never published, so there is nothing to check."),

            PanelStep.Verify => AfterVerify(r, outcome),

            _ => new PanelMove(r.Step, "Already finished."),
        };
    }

    // The ordinary shape: it worked, move on; it didn't, have another go until the budget is gone.
    private static PanelMove After(PanelBuildRecord r, PanelStepOutcome outcome, PanelStep then, string exhausted)
    {
        if (outcome.Ok) return new PanelMove(then, "");
        return r.Spent(r.Step) < PanelBudget.Attempts(r.Step)
            ? new PanelMove(r.Step, Retry(outcome))
            : Stop(r, Because(exhausted, outcome));
    }

    // Research is the one state that can be out of ideas rather than out of attempts, and the difference is what
    // the user is told. Everything tried, nothing left to try, is a question for them — not a failure of the build.
    private static PanelMove AfterResearch(PanelBuildRecord r, PanelStepOutcome outcome)
    {
        if (outcome.Ok) return new PanelMove(PanelStep.Prove, "");

        if (r.Struck.Count >= PanelBudget.Sources)
            return Stop(r, Because(
                $"Tried {r.Struck.Count} sources and none of them answers for this: {string.Join(", ", r.Struck)}.",
                outcome));

        return r.Spent(PanelStep.Research) < PanelBudget.Attempts(PanelStep.Research)
            ? new PanelMove(PanelStep.Research, Retry(outcome))
            : Stop(r, Because("No source could be found that carries this.", outcome));
    }

    // A source that will not answer is struck off and the machine goes BACK to research with it gone — the move the
    // Leicester build needed and could not make, because "try the next source" was a sentence in a brief rather than
    // a transition. Research's own budget is restored with it: looking for a second source is a new question, not a
    // retry of the first.
    private static PanelMove AfterProof(PanelBuildRecord r, PanelStepOutcome outcome)
    {
        if (outcome.Ok) return new PanelMove(PanelStep.Bind, "");

        Strike(r);

        if (r.Struck.Count >= PanelBudget.Sources)
            return Stop(r, Because(
                $"Called {r.Struck.Count} sources and none of them answered usefully: {string.Join(", ", r.Struck)}.",
                outcome));

        r.Attempts[PanelStep.Research] = 0;
        r.Source = null;
        r.Proof = null;
        return new PanelMove(PanelStep.Research,
            $"{Reason(outcome)} That source is struck off — find another that is not one of: {string.Join(", ", r.Struck)}.");
    }

    // A check that failed says WHERE it failed, and that is the whole value of doing it: the same "it's wrong" sends
    // the build to three different states depending on what is wrong with it.
    private static PanelMove AfterVerify(PanelBuildRecord r, PanelStepOutcome outcome)
    {
        if (outcome.Ok) return new PanelMove(PanelStep.Done, "");

        int laps = r.Log.Count(a => a.Step == PanelStep.Verify && !a.Ok);
        if (laps > PanelBudget.Laps)
            return Stop(r, Because("It has been round twice and is still not right.", outcome));

        switch (outcome.Fault)
        {
            case PanelFault.Looks:
                r.Attempts[PanelStep.Design] = 0;
                return new PanelMove(PanelStep.Design, $"It renders, and it is wrong to look at. {Reason(outcome)}");

            case PanelFault.Data:
                r.Attempts[PanelStep.Bind] = 0;
                return new PanelMove(PanelStep.Bind, $"The values are wrong. {Reason(outcome)}");

            case PanelFault.Source:
                Strike(r);
                if (r.Struck.Count >= PanelBudget.Sources)
                    return Stop(r, Because("The source is wrong and there are no others left to try.", outcome));
                r.Attempts[PanelStep.Research] = 0;
                r.Source = null;
                r.Proof = null;
                return new PanelMove(PanelStep.Research,
                    $"The source answers, about the wrong thing. {Reason(outcome)} Struck off: {string.Join(", ", r.Struck)}.");

            // A check that cannot say what is wrong is not a reason to rebuild anything. The panel is published and
            // the observation is kept on it; guessing which state to re-run from would be a coin toss with a
            // four-minute stake.
            default:
                return new PanelMove(PanelStep.Done, Reason(outcome));
        }
    }

    private static void Strike(PanelBuildRecord r)
    {
        var name = Named(r.Source);
        if (name is { Length: > 0 } && !r.Struck.Contains(name, StringComparer.OrdinalIgnoreCase))
            r.Struck.Add(name);
    }

    /// <summary>A source named the way a person would strike it off a list: its host, not its whole query string.</summary>
    internal static string? Named(PanelSource? source)
    {
        if (source is null) return null;
        if (Uri.TryCreate(source.Url, UriKind.Absolute, out var uri)) return uri.Host;
        return source.Url is { Length: > 0 } ? source.Url : source.Mode;
    }

    private static PanelMove Stop(PanelBuildRecord r, string why)
    {
        r.Blocked = why;
        return new PanelMove(PanelStep.Blocked, why);
    }

    private static string Retry(PanelStepOutcome outcome) =>
        $"That attempt didn't produce what the step needs. {Reason(outcome)}";

    private static string Because(string headline, PanelStepOutcome outcome) =>
        Reason(outcome) is { Length: > 0 } r ? $"{headline} {r}" : headline;

    private static string Reason(PanelStepOutcome outcome) => (outcome.Reason ?? "").Trim();
}
