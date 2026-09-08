using System.Net;
using System.Text;
using System.Text.Json;
using Smarty.Agents;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Three things that were each costing time on every run: reasoning that could not be switched off, guides
/// attached to jobs they had nothing to do with, and a rule taught in prose that the code can simply refuse.
/// </summary>
public class ThinkingAndGuidesTests
{
    /// <summary>Keeps the body it was sent and answers with an empty stream, so the payload can be read.</summary>
    private sealed class Captures : HttpMessageHandler
    {
        public string Body { get; private set; } = "";
        public int Attempts { get; private set; }
        private readonly HttpStatusCode[] _refuse;

        public Captures(params HttpStatusCode[] refuse) => _refuse = refuse;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++;
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);

            if (Attempts <= _refuse.Length)
                return new HttpResponseMessage(_refuse[Attempts - 1])
                {
                    Content = new StringContent("{\"error\":{\"message\":\"unknown field reasoning_effort\"}}"),
                };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    /// <summary>A model name of its own per test: which models reject the field is remembered process-wide
    /// and for good, which is the point of it — and would otherwise leak from one test into the next.</summary>
    private static async Task<string> SentAsync(bool think, Captures handler, [System.Runtime.CompilerServices.CallerMemberName] string model = "")
    {
        var provider = new TogetherModelProvider("test-key", null, new HttpClient(handler));
        var request = new ModelRequest
        {
            Model = $"deepseek-ai/DeepSeek-V4-Flash-0731-{model}",
            Messages = new[] { Message.User("hello") },
            Think = think,
        };

        await foreach (var _ in provider.StreamAsync(request, CancellationToken.None)) { }
        return handler.Body;
    }

    [Fact]
    public async Task Declining_to_think_now_actually_reaches_the_model()
    {
        // Think was honoured only by the Ollama provider. Every "Think = false" in the orchestrator — the
        // re-voice, the one-word classifications, "keep it fast" — was a comment on any model reached through
        // Together, and reasoning ran at full length on all of them.
        var quick = await SentAsync(think: false, new Captures());
        Assert.Contains("\"reasoning_effort\":\"low\"", quick);
    }

    [Fact]
    public async Task Wanting_to_think_leaves_the_deployment_alone()
    {
        // Sent only when reasoning was DECLINED. Staying silent otherwise means this changes exactly the calls
        // that already said they didn't need it, and nothing else.
        var thinking = await SentAsync(think: true, new Captures());
        Assert.DoesNotContain("reasoning_effort", thinking);
    }

    [Fact]
    public async Task A_model_that_has_never_heard_of_it_still_runs()
    {
        // A leg must not die because we asked a model to think less. The 400 is answered by dropping the field
        // and going again, the same way the Ollama provider handles a model that rejects `think`.
        var handler = new Captures(HttpStatusCode.BadRequest);
        var body = await SentAsync(think: false, handler);

        Assert.Equal(2, handler.Attempts);
        Assert.DoesNotContain("reasoning_effort", body);
    }

    // ---- guides ----

    private static TaskHintStore Guides()
    {
        var store = new TaskHintStore(
            Path.Combine(Path.GetTempPath(), "smarty-guide-tests", Guid.NewGuid().ToString("n"), "hints.json"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        store.Upsert("find things to do in London on a specific date", "Open the listings site and filter by date.", "task 1");
        store.Upsert("find a place on Google Maps and get its details", "Search the name, open the card, read the hours.", "task 2");
        store.Upsert("build a reusable home-page panel", "Start with widget_design, not chrome_navigate.", "task 3");
        return store;
    }

    [Fact]
    public void A_long_brief_no_longer_matches_a_guide_it_has_nothing_to_do_with()
    {
        // The bar was two shared words, which reads as a subject only when what is being matched is a sentence.
        // A panel build hands its WHOLE step brief in as the description, and against a couple of hundred words
        // almost any guide clears two — which is how restyling a vacuum panel was handed "find things to do in
        // London" and then went off to drive a website.
        var brief =
            "STEP 1 OF 6 — AGREE WHAT IT SHOWS. Decide the FIELDS. What does a person get from a glance at this " +
            "panel, and what would the component have to be handed to show it? Name each one, say what it is, and " +
            "give an example value. Keep it to what fits. THE EMPTY DAY: no game on, no flight today, nothing " +
            "sold. Every panel has them. You are not choosing a source. Do not open a browser, do not search, do " +
            "not wonder whether an API exists — deciding what the panel is FOR against what happens to be easy " +
            "to fetch is how panels end up showing whatever an API returned. FINISH BY CALLING panel_shows.";

        var matched = Guides().Relevant(brief).Select(g => g.Name).ToList();

        Assert.DoesNotContain("find things to do in London on a specific date", matched);
        Assert.DoesNotContain("find a place on Google Maps and get its details", matched);
    }

    [Fact]
    public void A_job_that_really_is_the_same_job_still_matches()
    {
        // The point isn't to match less. A guide has to survive the thing it was written for.
        var matched = Guides().Relevant("build a reusable home-page panel showing the weather")
            .Select(g => g.Name).ToList();

        Assert.Contains("build a reusable home-page panel", matched);
    }

    // ---- what an adjust is handed ----

    [Fact]
    public void The_palette_is_enforced_at_publish_rather_than_pasted_into_every_adjust()
    {
        var panel = new Widget { Id = "p1", Title = "Vacuum", Size = WidgetSizes.Kpi, Kind = "vacuum-control" };
        var brief = WidgetTools.AdjustBrief(panel, null, "make it look nicer");

        // ~825 tokens of Tailwind class list, on every adjust for ever, teaching a rule widget_publish already
        // enforces - and which the staged build path has never been given and does not need.
        Assert.DoesNotContain("gap-x-2", brief);
        Assert.DoesNotContain("line-clamp-3", brief);

        // The component list stays: it is the one thing an adjust is most likely to need and least likely to
        // know, since the panel may predate half of it.
        Assert.Contains("Pictures(", brief);
        Assert.Contains("Action(", brief);
    }

    [Fact]
    public void A_class_outside_the_palette_is_refused_WITH_the_palette()
    {
        // The refusal used to say "use only what your brief listed". For a staged build that brief listed
        // nothing, so the only way out of the refusal was to guess - which is the same as no refusal at all.
        var refusal = WidgetTools.Rejected(
            "return (<div className=\"tabular-nums border-ink-faint\">{data.x}</div>)", clientMode: false);

        Assert.NotNull(refusal);
        Assert.Contains("tabular-nums", refusal);
        Assert.Contains("gap-x-2", refusal);   // the list itself, where it is actually needed
    }

    // ---- what an adjust is allowed to hold ----

    [Fact]
    public void An_adjust_keeps_its_panel_tools_and_loses_the_browser()
    {
        var borrowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "write_file", "read_file", "list_files", "find_in_file",
            "chrome_navigate", "chrome_read_page", "chrome_tabs_create",
        };

        // It must be able to finish: changing a live kind means publishing it.
        Assert.True(PanelBuild.KeepForAdjust("widget_publish", borrowed));
        Assert.True(PanelBuild.KeepForAdjust("widget_design", borrowed));
        Assert.True(PanelBuild.KeepForAdjust("read_file", borrowed));

        // And it must not be able to go shopping for a different source. An adjust used to fall through to the
        // whole-build set - 23 tools including all of Chrome for a job that edits JSX - and it used them: two
        // minutes of opening tabs before it had read its own brief.
        Assert.False(PanelBuild.KeepForAdjust("chrome_navigate", borrowed));
        Assert.False(PanelBuild.KeepForAdjust("chrome_read_page", borrowed));

        // Nothing outside what a panel job was lent.
        Assert.False(PanelBuild.KeepForAdjust("run_shell_command", borrowed));
    }

    // ---- what a state is allowed to spend ----

    [Fact]
    public void The_states_that_make_their_values_up_do_not_pay_for_scepticism()
    {
        // Reasoning is on for a worker so it reads tool output properly rather than writing down whatever it was
        // handed. Agreeing what a panel shows and drawing it call nothing and read nothing — the values are
        // invented — so there is no output to be sceptical about and the deliberation is pure latency.
        Assert.True(PanelBuild.Drafts(PanelStep.Agree));
        Assert.True(PanelBuild.Drafts(PanelStep.Design));
    }

    [Fact]
    public void Every_state_that_looks_at_something_real_still_thinks()
    {
        // The line is drawn at "did this state touch a source", not at "is this state slow". Prove reads a live
        // response, Bind maps a real one onto the contract, Verify looks at the built panel with eyes — each is
        // the case the reasoning was turned on for, and a panel bound to a field that was never in the response
        // is the failure that costs the whole build.
        Assert.False(PanelBuild.Drafts(PanelStep.Research));
        Assert.False(PanelBuild.Drafts(PanelStep.Prove));
        Assert.False(PanelBuild.Drafts(PanelStep.Bind));
        Assert.False(PanelBuild.Drafts(PanelStep.Verify));

        // A job with no panel state at all is an ordinary worker and keeps everything it had.
        Assert.False(PanelBuild.Drafts(null));
    }

    [Fact]
    public void Most_of_a_guides_name_has_to_be_there()
    {
        // Scored against the GUIDE's own length, so the test doesn't depend on how much text it is matched
        // against — "London" and "date" alone are two words in common and no longer enough.
        var matched = Guides().Relevant("what is the date in London").Select(g => g.Name).ToList();

        Assert.DoesNotContain("find things to do in London on a specific date", matched);
    }
}
