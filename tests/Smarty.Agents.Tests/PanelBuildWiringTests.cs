using System.Text.Json;
using Smarty.Agents;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// What each state of a panel build is actually handed: its tools, and its slice of the brief.
///
/// <para>
/// Worth its own file because this is where the last feature of this kind died silently. widget_design was written,
/// registered, briefed and tested — and a named allow-list dropped it before the worker ever saw its tools, so the
/// build was told to design first and had nothing to design with. Nothing errored. The panel just appeared the old
/// way, and the only evidence was a tool that was never called.
/// </para>
/// <para>
/// A state machine multiplies that risk by six: six briefs and six tool sets, any of which can be quietly wrong in
/// a way that looks like a model having a bad day.
/// </para>
/// </summary>
public class PanelBuildWiringTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>What a build BORROWS — the same shape as the orchestrator's own list. Not everything in the app.</summary>
    private static readonly IReadOnlySet<string> Borrowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "task_guides", "read_file", "write_file", "list_files", "chrome_navigate", "chrome_read_page",
        "chrome_javascript",
    };

    private static readonly PanelStep[] Working =
        { PanelStep.Agree, PanelStep.Design, PanelStep.Research, PanelStep.Prove, PanelStep.Bind };

    private static Widget Panel() => new()
    {
        Id = "22eabc9a",
        Title = "Leicester City match",
        Size = WidgetSizes.Wide,
    };

    private static PanelBuildRecord Record() => new()
    {
        PanelId = "22eabc9a",
        Shows = "The latest Leicester City result, or a countdown to the next fixture with both crests.",
    };

    // ── tools ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_state_keeps_the_one_tool_that_finishes_it()
    {
        foreach (var step in Working)
        {
            var closes = PanelBuild.Ends(step);
            Assert.NotNull(closes);
            Assert.True(PanelBuild.Keep(closes!, Borrowed, step),
                $"{step} cannot call {closes}, which is the only thing that finishes it");
        }
    }

    [Fact]
    public void No_state_keeps_another_state_s_tool()
    {
        foreach (var step in Working)
            foreach (var other in Working.Where(s => s != step))
                Assert.False(PanelBuild.Keep(PanelBuild.Ends(other)!, Borrowed, step),
                    $"{step} was handed {PanelBuild.Ends(other)}, which belongs to {other}");
    }

    [Fact]
    public void Deciding_and_drawing_and_publishing_have_no_browser()
    {
        // A browser in the room turns the state that was asked what the panel should show into a state that finds
        // out what an API happens to return and answers that instead. Binding already has the proven response in
        // its brief, so a browser there is a way to go and fetch a different one.
        foreach (var step in new[] { PanelStep.Agree, PanelStep.Design, PanelStep.Bind })
            Assert.False(PanelBuild.Keep("chrome_navigate", Borrowed, step), $"{step} was handed a browser");

        foreach (var step in new[] { PanelStep.Research, PanelStep.Prove })
            Assert.True(PanelBuild.Keep("chrome_navigate", Borrowed, step), $"{step} needs a browser and has none");
    }

    [Fact]
    public void A_state_is_still_trimmed_to_what_a_build_uses()
    {
        foreach (var step in Working)
        {
            Assert.False(PanelBuild.Keep("download_file", Borrowed, step));
            Assert.False(PanelBuild.Keep("build_presentation", Borrowed, step));
            Assert.True(PanelBuild.Keep("read_file", Borrowed, step));
        }
    }

    [Fact]
    public void The_tools_a_state_is_given_are_the_tools_it_is_told_to_call()
    {
        // The two halves that drifted apart last time: what PanelBuildTools hands out, and what the trim lets
        // through. Either one alone looks correct.
        var store = NewStore();
        var panelTools = new[] { Blank("widget_design"), Blank("widget_publish") };

        foreach (var step in Working)
        {
            var given = PanelBuildTools.For(step, store, panelTools).Select(t => t.Name).ToList();
            Assert.Contains(PanelBuild.Ends(step)!, given);
            Assert.All(given, name => Assert.True(PanelBuild.Keep(name, Borrowed, step),
                $"{step} is handed {name} and then trimmed out of it"));
        }
    }

    // ── briefs ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_state_is_told_the_one_call_that_finishes_it()
    {
        foreach (var step in Working)
        {
            var brief = PanelBuildBriefs.For(step, Panel(), Record(), "");
            Assert.Contains(PanelBuild.Ends(step)!, brief);
        }
    }

    [Fact]
    public void Only_the_states_that_write_code_are_shown_the_palette()
    {
        // The whole reason for splitting the brief. The palette and the kit are the largest thing in it, and the
        // state deciding what a panel shows has no use for either.
        foreach (var step in new[] { PanelStep.Design, PanelStep.Bind })
            Assert.Contains("line-clamp-2", PanelBuildBriefs.For(step, Panel(), Record(), ""));

        foreach (var step in new[] { PanelStep.Agree, PanelStep.Research, PanelStep.Prove })
            Assert.DoesNotContain("line-clamp-2", PanelBuildBriefs.For(step, Panel(), Record(), ""));
    }

    [Fact]
    public void Only_the_states_that_go_looking_are_shown_the_loader_modes()
    {
        Assert.Contains("internal —", PanelBuildBriefs.For(PanelStep.Research, Panel(), Record(), ""));

        foreach (var step in new[] { PanelStep.Agree, PanelStep.Design })
            Assert.DoesNotContain("internal —", PanelBuildBriefs.For(step, Panel(), Record(), ""));
    }

    [Fact]
    public void A_state_brief_is_a_fraction_of_the_brief_it_replaced()
    {
        // The old build handed one worker 21,000 characters and re-read them on every turn of every state.
        var whole = WidgetTools.Brief(Panel(), Record().Shows);
        foreach (var step in Working)
        {
            var brief = PanelBuildBriefs.For(step, Panel(), Record(), "");
            Assert.True(brief.Length < whole.Length,
                $"{step}'s brief is {brief.Length} against the old {whole.Length}");
        }

        // And the state that starts the whole thing is a page, not a chapter.
        Assert.True(PanelBuildBriefs.For(PanelStep.Agree, Panel(), Record(), "").Length < whole.Length / 4);
    }

    [Fact]
    public void What_an_earlier_state_decided_is_carried_forward_as_fact()
    {
        var record = Record();
        record.Contract = new PanelContract
        {
            Fields = { new WidgetField { Name = "homeTeam", Type = "string", Description = "the home side" } },
            WhenEmpty = "A countdown to the next fixture.",
        };
        record.Source = new PanelSource
        {
            Subject = "Leicester City FC",
            Identifier = "375",
            Expect = "Leicester",
            Url = "https://api.football-data.org/v4/teams/375/matches",
        };
        record.Proof = new PanelProof { Url = record.Source.Url, Status = 200, Body = "{\"matches\":[]}", Subject = true };
        record.Struck.Add("site.api.espn.com");

        var bind = PanelBuildBriefs.For(PanelStep.Bind, Panel(), record, "");
        Assert.Contains("homeTeam", bind);
        Assert.Contains("{\"matches\":[]}", bind);

        // A struck source reaches the state that would otherwise offer it again, and nothing else needs it.
        Assert.Contains("site.api.espn.com", PanelBuildBriefs.For(PanelStep.Research, Panel(), record, ""));
        Assert.DoesNotContain("site.api.espn.com", PanelBuildBriefs.For(PanelStep.Design, Panel(), record, ""));

        // And the design state is told the shape it is drawing, because that is what it renders.
        Assert.Contains("homeTeam", PanelBuildBriefs.For(PanelStep.Design, Panel(), record, ""));
    }

    [Fact]
    public void A_state_is_told_why_it_is_being_run_again()
    {
        var brief = PanelBuildBriefs.For(PanelStep.Research, Panel(), Record(),
            "Failed to fetch. That source is struck off — find another.");
        Assert.Contains("struck off", brief);
    }

    // ── the tools themselves ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_source_with_no_way_to_check_the_answer_is_refused()
    {
        // The Reading fault, refused at the point it is made rather than discovered four states later.
        var store = NewStore();
        var panel = Reserve(store);

        var output = await Call(PanelBuildTools.SourceTool(store), new
        {
            id = panel.Id,
            subject = "Leicester City FC",
            url = "https://site.api.espn.com/apis/site/v2/sports/soccer/eng.1/teams/338/schedule",
        });

        Assert.True(output.IsError);
        Assert.Contains("proves the answer is about this subject", output.Content);
    }

    [Fact]
    public async Task A_response_about_another_club_is_recorded_as_not_proving_anything()
    {
        var store = NewStore();
        var panel = Reserve(store);

        await Call(PanelBuildTools.SourceTool(store), new
        {
            id = panel.Id,
            subject = "Leicester City FC",
            expect = "Leicester",
            url = "https://site.api.espn.com/apis/site/v2/sports/soccer/eng.1/teams/338/schedule",
        });

        // Exactly what ESPN answered: 200, valid, complete — and about Reading.
        var output = await Call(PanelBuildTools.ProofTool(store), new
        {
            id = panel.Id,
            url = "https://site.api.espn.com/apis/site/v2/sports/soccer/eng.1/teams/338/schedule",
            status = 200,
            body = "{\"team\":{\"id\":\"338\",\"displayName\":\"Reading\"},\"events\":[]}",
        });

        Assert.True(output.IsError);
        Assert.False(store.BuildOf(panel.Id)!.Proof!.Subject);
    }

    [Fact]
    public async Task A_response_that_does_name_the_subject_proves_it()
    {
        var store = NewStore();
        var panel = Reserve(store);

        await Call(PanelBuildTools.SourceTool(store), new
        {
            id = panel.Id,
            subject = "Leicester City FC",
            expect = "Leicester",
            url = "https://api.football-data.org/v4/teams/375/matches",
        });

        var output = await Call(PanelBuildTools.ProofTool(store), new
        {
            id = panel.Id,
            url = "https://api.football-data.org/v4/teams/375/matches",
            status = 200,
            body = "{\"matches\":[{\"homeTeam\":{\"name\":\"Leicester City FC\"},\"score\":{\"fullTime\":{\"home\":2}}}]}",
        });

        Assert.False(output.IsError);
        Assert.True(store.BuildOf(panel.Id)!.Proof!.Subject);
    }

    [Fact]
    public async Task A_contract_a_component_cannot_read_is_refused()
    {
        var store = NewStore();
        var panel = Reserve(store);

        var output = await Call(PanelBuildTools.ShowsTool(store), new
        {
            id = panel.Id,
            when_empty = "A countdown to the next fixture.",
            fields = new[] { new { name = "team.name", description = "the club" } },
        });

        Assert.True(output.IsError);
        Assert.Contains("flat name", output.Content);
    }

    [Fact]
    public async Task A_panel_that_never_says_what_it_shows_on_a_quiet_day_is_refused()
    {
        var store = NewStore();
        var panel = Reserve(store);

        var output = await Call(PanelBuildTools.ShowsTool(store), new
        {
            id = panel.Id,
            fields = new[] { new { name = "score", description = "the score" } },
        });

        Assert.True(output.IsError);
    }

    [Fact]
    public async Task Offering_a_source_that_has_already_failed_is_refused()
    {
        var store = NewStore();
        var panel = Reserve(store);
        store.Produced(panel.Id, r => r.Struck.Add("site.api.espn.com"));

        var output = await Call(PanelBuildTools.SourceTool(store), new
        {
            id = panel.Id,
            subject = "Leicester City FC",
            expect = "Leicester",
            url = "https://site.api.espn.com/apis/site/v2/sports/soccer/eng.2/teams/375/schedule",
        });

        Assert.True(output.IsError);
        Assert.Contains("already been tried", output.Content);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────

    private static WidgetStore NewStore() =>
        new(Path.Combine(Path.GetTempPath(), $"widgets-{Guid.NewGuid():N}.json"), Json);

    private static Widget Reserve(WidgetStore store)
    {
        var w = store.Reserve("Leicester City match", WidgetSizes.Wide, 80, null, null, proposed: false);
        store.Begin(w.Id, "The latest Leicester City result.");
        return w;
    }

    private static async Task<ToolOutput> Call(AgentTool tool, object args) =>
        await tool.InvokeAsync(
            new ToolCallArguments(JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement),
            CancellationToken.None);

    private static AgentTool Blank(string name) => new(name, name, Array.Empty<ToolParameter>(),
        (_, _) => Task.FromResult(ToolOutput.Ok("")));
}
