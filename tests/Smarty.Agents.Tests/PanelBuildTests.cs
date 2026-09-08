using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Building a panel as a sequence of states, and what each state has to leave behind before the next one runs.
///
/// <para>
/// Every test here is a thing the one-long-brief version could not do. It was asked for the latest Leicester City
/// score, guessed an ESPN team id, was answered — 200 OK, perfectly valid — about Reading, and then re-issued one
/// identical dead fetch thirty-eight times until its four minutes were gone. It had written "I should stop fiddling
/// with ESPN and pivot to a different key-less source" into its own notes before the loop started, and there was no
/// mechanism in the system that could act on that sentence. It ended with the panel showing nothing at all: the
/// design it had already drawn was taken down when the build failed.
/// </para>
/// <para>
/// So: the sequence is code, each state is judged on what it left behind rather than what it claimed, and the
/// design goes up first and stays up. None of that is a prompt.
/// </para>
/// </summary>
public class PanelBuildTests
{
    // ── the world the machine acts on ───────────────────────────────────────────────────────────────────

    private sealed class Board : IPanelBoard
    {
        public bool Design;
        public bool Published;
        public string? BlockedWhy;
        public readonly List<string> Stages = new();

        public bool ShowsDesign(string panelId) => Design;
        public bool IsPublished(string panelId) => Published;
        public void Stage(string panelId, string stage) => Stages.Add(stage);
        public void Blocked(string panelId, string why) => BlockedWhy = why;
    }

    /// <summary>A runner scripted per state, so a test says only what it is about.</summary>
    private sealed class Runner : IPanelStepRunner
    {
        private readonly Func<PanelBuildRecord, PanelStep, int, PanelStepOutcome> _answer;
        public readonly List<(PanelStep Step, string Note)> Ran = new();

        public Runner(Func<PanelBuildRecord, PanelStep, int, PanelStepOutcome> answer) => _answer = answer;

        public int Count(PanelStep step) => Ran.Count(r => r.Step == step);

        public Task<PanelStepOutcome> RunAsync(PanelBuildRecord record, PanelStep step, string note, CancellationToken ct)
        {
            Ran.Add((step, note));
            return Task.FromResult(_answer(record, step, Count(step)));
        }
    }

    private static PanelBuildRecord New() => new()
    {
        PanelId = "22eabc9a",
        Shows = "The latest Leicester City result, or a countdown to the next fixture.",
    };

    private static PanelContract Contract() => new()
    {
        Fields = { new WidgetField { Name = "home", Type = "string" }, new WidgetField { Name = "score", Type = "string" } },
        WhenEmpty = "A countdown to the next fixture, with both crests.",
    };

    private static PanelSource Source(string url) => new()
    {
        Subject = "Leicester City FC",
        Identifier = "375",
        Expect = "Leicester",
        Url = url,
    };

    /// <summary>A build where every state does its job. The states themselves are what is under test.</summary>
    private static Runner Working(Board board) => new((r, step, _) =>
    {
        switch (step)
        {
            case PanelStep.Agree: r.Contract = Contract(); break;
            case PanelStep.Design: board.Design = true; break;
            case PanelStep.Research: r.Source = Source("https://api.football-data.org/v4/teams/338"); break;
            case PanelStep.Prove: r.Proof = new PanelProof { Url = r.Source!.Url, Status = 200, Subject = true }; break;
            case PanelStep.Bind: board.Published = true; r.Kind = "football-club-match"; break;
        }
        return PanelStepOutcome.Done();
    });

    // ── the rule that outranks the rest ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Nothing_looks_for_a_source_until_the_user_can_see_the_design()
    {
        var board = new Board();
        var runner = Working(board);

        var done = await PanelRun.DriveAsync(New(), runner, board);

        Assert.Equal(PanelStep.Done, done.Step);
        int design = runner.Ran.FindIndex(r => r.Step == PanelStep.Design);
        int research = runner.Ran.FindIndex(r => r.Step == PanelStep.Research);
        Assert.True(design >= 0 && design < research, "the design is drawn before anyone goes looking for data");
    }

    [Fact]
    public async Task A_design_step_that_never_put_a_design_up_has_not_designed_anything()
    {
        var board = new Board();
        // The failure this catches is a worker reporting a design it described but never called the tool for. The
        // old build had no way to tell the two apart: both came back as a paragraph saying the design was done.
        var runner = new Runner((r, step, attempt) =>
        {
            if (step == PanelStep.Agree) r.Contract = Contract();
            if (step == PanelStep.Design && attempt >= 2) board.Design = true;
            return PanelStepOutcome.Done();
        });

        var done = await PanelRun.DriveAsync(New(), runner, board);

        Assert.Equal(2, runner.Count(PanelStep.Design));
        Assert.Contains(runner.Ran, r => r.Step == PanelStep.Design && r.Note.Contains("not on the page"));
        // And it did not go looking for a source in the meantime.
        Assert.True(runner.Ran.FindIndex(r => r.Step == PanelStep.Research)
                    > runner.Ran.FindLastIndex(r => r.Step == PanelStep.Design));
    }

    [Fact]
    public async Task A_build_that_can_find_no_source_leaves_the_design_up_and_says_what_it_needs()
    {
        var board = new Board();
        var runner = new Runner((r, step, _) =>
        {
            if (step == PanelStep.Agree) { r.Contract = Contract(); return PanelStepOutcome.Done(); }
            if (step == PanelStep.Design) { board.Design = true; return PanelStepOutcome.Done(); }
            return PanelStepOutcome.Failed("Leicester aren't in either division this source carries.");
        });

        var done = await PanelRun.DriveAsync(New(), runner, board);

        Assert.Equal(PanelStep.Blocked, done.Step);
        Assert.True(board.Design, "the design the user was shown is still the design the user can see");
        Assert.NotNull(board.BlockedWhy);
        Assert.Contains("source", board.BlockedWhy!, StringComparison.OrdinalIgnoreCase);
    }

    // ── the wrong club ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_response_that_never_names_the_subject_is_not_a_proof()
    {
        var board = new Board();
        // ESPN team 338 answers with 200 and a complete, valid schedule. For Reading.
        var runner = new Runner((r, step, attempt) =>
        {
            switch (step)
            {
                case PanelStep.Agree: r.Contract = Contract(); break;
                case PanelStep.Design: board.Design = true; break;
                case PanelStep.Research: r.Source = Source($"https://site.api.espn.com/eng.1/teams/{337 + attempt}"); break;
                case PanelStep.Prove:
                    r.Proof = new PanelProof { Url = r.Source!.Url, Status = 200, Body = "Reading", Subject = false };
                    break;
            }
            return PanelStepOutcome.Done();
        });

        var done = await PanelRun.DriveAsync(New(), runner, board);

        // Never bound, never published, and the reason names the subject rather than blaming the network.
        Assert.Equal(PanelStep.Blocked, done.Step);
        Assert.Equal(0, runner.Count(PanelStep.Bind));
        Assert.Contains(done.Log, a => a.Step == PanelStep.Prove && !a.Ok
                                       && a.Reason!.Contains("Leicester City FC"));
    }

    // ── the loop ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task One_dead_source_is_not_called_thirty_eight_times()
    {
        var board = new Board();
        var runner = new Runner((r, step, _) =>
        {
            switch (step)
            {
                case PanelStep.Agree: r.Contract = Contract(); return PanelStepOutcome.Done();
                case PanelStep.Design: board.Design = true; return PanelStepOutcome.Done();
                case PanelStep.Research: r.Source = Source("https://site.api.espn.com/eng.1/teams/360/schedule"); return PanelStepOutcome.Done();
                default: return PanelStepOutcome.Failed("Failed to fetch.");
            }
        });

        var done = await PanelRun.DriveAsync(New(), runner, board);

        Assert.Equal(PanelStep.Blocked, done.Step);
        // The real run spent 38 identical calls. The budget here is per state and cannot be extended from inside one.
        Assert.True(runner.Count(PanelStep.Prove) <= PanelBudget.Sources,
            $"proved {runner.Count(PanelStep.Prove)} times against a source that never answered");
        Assert.True(runner.Ran.Count <= PanelBudget.Steps, $"{runner.Ran.Count} states run");
    }

    [Fact]
    public async Task A_source_that_will_not_answer_is_struck_off_and_a_different_one_is_asked_for()
    {
        var board = new Board();
        var runner = new Runner((r, step, attempt) =>
        {
            switch (step)
            {
                case PanelStep.Agree: r.Contract = Contract(); return PanelStepOutcome.Done();
                case PanelStep.Design: board.Design = true; return PanelStepOutcome.Done();
                case PanelStep.Research:
                    r.Source = Source(attempt == 1
                        ? "https://site.api.espn.com/eng.1/teams/360/schedule"
                        : "https://api.football-data.org/v4/teams/338/matches");
                    return PanelStepOutcome.Done();
                case PanelStep.Prove:
                    if (attempt == 1) return PanelStepOutcome.Failed("Failed to fetch.");
                    r.Proof = new PanelProof { Url = r.Source!.Url, Status = 200, Subject = true };
                    return PanelStepOutcome.Done();
                case PanelStep.Bind: board.Published = true; return PanelStepOutcome.Done();
                default: return PanelStepOutcome.Done();
            }
        });

        var done = await PanelRun.DriveAsync(New(), runner, board);

        Assert.Equal(PanelStep.Done, done.Step);
        Assert.Contains("site.api.espn.com", done.Struck);
        // The second research pass was TOLD what had already failed, so it cannot offer it again.
        var second = runner.Ran.Where(x => x.Step == PanelStep.Research).Skip(1).First();
        Assert.Contains("site.api.espn.com", second.Note);
    }

    [Fact]
    public async Task Looking_for_a_second_source_is_a_new_question_not_a_retry_of_the_first()
    {
        // Research is allowed two goes at a question. A struck source makes it a different question, so the count
        // starts again — otherwise the third source can never be looked for however many are left to try.
        var r = New();
        r.Contract = Contract();
        r.Step = PanelStep.Research;
        r.Attempts[PanelStep.Research] = PanelBudget.Attempts(PanelStep.Research);
        r.Source = Source("https://site.api.espn.com/x");
        r.Step = PanelStep.Prove;

        var move = PanelMachine.Next(r, PanelStepOutcome.Failed("Failed to fetch."));

        Assert.Equal(PanelStep.Research, move.Step);
        Assert.Equal(0, r.Spent(PanelStep.Research));
        Assert.Null(r.Source);
    }

    // ── what a check sends back where ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(PanelFault.Looks, PanelStep.Design)]
    [InlineData(PanelFault.Data, PanelStep.Bind)]
    [InlineData(PanelFault.Source, PanelStep.Research)]
    public void A_failed_check_goes_back_to_the_state_that_can_fix_it(PanelFault fault, PanelStep expected)
    {
        var r = New();
        r.Contract = Contract();
        r.Source = Source("https://api.football-data.org/v4/teams/338");
        r.Step = PanelStep.Verify;

        var move = PanelMachine.Next(r, PanelStepOutcome.Wrong(fault, "the score is yesterday's"));

        Assert.Equal(expected, move.Step);
    }

    [Fact]
    public void A_check_that_cannot_say_what_is_wrong_does_not_rebuild_anything()
    {
        // Guessing which state to re-run from is a coin toss with a four-minute stake, and the panel is published
        // and working. The observation is kept; nothing is torn down over it.
        var r = New();
        r.Step = PanelStep.Verify;

        var move = PanelMachine.Next(r, PanelStepOutcome.Wrong(PanelFault.None, "hard to say"));

        Assert.Equal(PanelStep.Done, move.Step);
    }

    [Fact]
    public async Task A_panel_that_keeps_coming_back_wrong_stops_going_round()
    {
        var board = new Board();
        var runner = new Runner((r, step, _) =>
        {
            switch (step)
            {
                case PanelStep.Agree: r.Contract = Contract(); return PanelStepOutcome.Done();
                case PanelStep.Design: board.Design = true; return PanelStepOutcome.Done();
                case PanelStep.Research: r.Source = Source("https://api.football-data.org/v4/teams/338"); return PanelStepOutcome.Done();
                case PanelStep.Prove: r.Proof = new PanelProof { Subject = true }; return PanelStepOutcome.Done();
                case PanelStep.Bind: board.Published = true; return PanelStepOutcome.Done();
                default: return PanelStepOutcome.Wrong(PanelFault.Looks, "the crest is the size of an icon");
            }
        });

        var done = await PanelRun.DriveAsync(New(), runner, board);

        Assert.True(runner.Count(PanelStep.Verify) <= PanelBudget.Laps + 1);
        Assert.Equal(PanelStep.Blocked, done.Step);
        Assert.True(board.Published, "a panel that is up and imperfect stays up");
    }

    // ── a state is judged on what it left behind ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_step_that_says_it_agreed_a_contract_without_writing_one_down_has_not()
    {
        var board = new Board();
        var runner = new Runner((_, _, _) => PanelStepOutcome.Done());

        var done = await PanelRun.DriveAsync(New(), runner, board);

        Assert.Equal(PanelStep.Blocked, done.Step);
        Assert.Equal(0, runner.Count(PanelStep.Design));
        Assert.Contains(done.Log, a => a.Step == PanelStep.Agree && !a.Ok && a.Reason!.Contains("panel_shows"));
    }

    [Fact]
    public async Task Publishing_is_the_only_thing_that_counts_as_having_published()
    {
        var board = new Board();
        var runner = new Runner((r, step, _) =>
        {
            switch (step)
            {
                case PanelStep.Agree: r.Contract = Contract(); break;
                case PanelStep.Design: board.Design = true; break;
                case PanelStep.Research: r.Source = Source("https://api.football-data.org/v4/teams/338"); break;
                case PanelStep.Prove: r.Proof = new PanelProof { Subject = true }; break;
                // Bind reports success and never calls widget_publish — the .jsx-file-on-disk failure.
            }
            return PanelStepOutcome.Done();
        });

        var done = await PanelRun.DriveAsync(New(), runner, board);

        Assert.Equal(PanelStep.Blocked, done.Step);
        Assert.Equal(0, runner.Count(PanelStep.Verify));
        Assert.Contains(done.Log, a => a.Step == PanelStep.Bind && !a.Ok && a.Reason!.Contains("widget_publish"));
    }

    [Fact]
    public async Task A_state_that_throws_is_a_state_that_failed_not_a_build_that_died()
    {
        var board = new Board();
        var runner = new Runner((r, step, attempt) =>
        {
            if (step == PanelStep.Agree && attempt == 1) throw new InvalidOperationException("the model provider fell over");
            switch (step)
            {
                case PanelStep.Agree: r.Contract = Contract(); break;
                case PanelStep.Design: board.Design = true; break;
                case PanelStep.Research: r.Source = Source("https://api.football-data.org/v4/teams/338"); break;
                case PanelStep.Prove: r.Proof = new PanelProof { Subject = true }; break;
                case PanelStep.Bind: board.Published = true; break;
            }
            return PanelStepOutcome.Done();
        });

        var done = await PanelRun.DriveAsync(New(), runner, board);

        Assert.Equal(PanelStep.Done, done.Step);
        Assert.Contains(done.Log, a => a.Step == PanelStep.Agree && !a.Ok && a.Reason!.Contains("fell over"));
    }

    [Fact]
    public async Task A_state_that_has_to_ask_the_user_something_ends_the_build()
    {
        // Retrying a question is asking it again in a louder voice. The machine cannot supply a login or a
        // credential, so the question becomes the reason and sits on the panel over the design.
        var board = new Board();
        var runner = new Runner((r, step, _) =>
        {
            if (step == PanelStep.Agree) { r.Contract = Contract(); return PanelStepOutcome.Done(); }
            if (step == PanelStep.Design) { board.Design = true; return PanelStepOutcome.Done(); }
            return PanelStepOutcome.Stop("It needs an answer: which of your two eBay accounts?");
        });

        var done = await PanelRun.DriveAsync(New(), runner, board);

        Assert.Equal(PanelStep.Blocked, done.Step);
        Assert.Equal(1, runner.Count(PanelStep.Research));
        Assert.Contains("eBay accounts", board.BlockedWhy!);
        Assert.True(board.Design);
    }

    [Fact]
    public async Task A_cancelled_build_stops_where_it_is()
    {
        var board = new Board();
        using var cts = new CancellationTokenSource();
        var runner = new Runner((r, step, _) =>
        {
            if (step == PanelStep.Agree) { r.Contract = Contract(); return PanelStepOutcome.Done(); }
            cts.Cancel();
            board.Design = true;
            return PanelStepOutcome.Done();
        });

        var done = await PanelRun.DriveAsync(New(), runner, board, cts.Token);

        Assert.Equal(PanelStep.Blocked, done.Step);
        Assert.Equal(0, runner.Count(PanelStep.Research));
    }

    [Fact]
    public async Task Every_state_says_what_it_is_doing_on_the_panel()
    {
        // The user is watching this happen. A state with nothing written over it is eight minutes of a spinner.
        var board = new Board();
        var runner = Working(board);

        await PanelRun.DriveAsync(New(), runner, board);

        Assert.Equal(runner.Ran.Count, board.Stages.Count);
        Assert.All(board.Stages, s => Assert.False(string.IsNullOrWhiteSpace(s)));
        Assert.Contains(Stages.Designing, board.Stages);
    }

    [Fact]
    public async Task A_finished_build_can_say_what_it_did()
    {
        var board = new Board();
        var runner = Working(board);

        var done = await PanelRun.DriveAsync(New(), runner, board);

        // The record is the durable account of the build: which states ran, in order, and how each ended. The old
        // shape left only a 200-character error string on the panel and a transcript nobody reads.
        Assert.Equal(new[]
        {
            PanelStep.Agree, PanelStep.Design, PanelStep.Research, PanelStep.Prove, PanelStep.Bind, PanelStep.Verify,
        }, done.Log.Select(a => a.Step));
        Assert.All(done.Log, a => Assert.True(a.Ok));
        Assert.Equal("football-club-match", done.Kind);
        Assert.Null(done.Blocked);
    }
}
