using System.Text.Json;
using Smarty.Agents;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Learning how a job is done, once, instead of every time.
///
/// The recorded runs contain seven eBay listings, five Gmail sends and two Ocado shops, and every single one of
/// them worked the procedure out from nothing: one listing took 417 tool calls. The transcript knew the answer
/// each time and was never asked. So a finished run writes a guide, and the next job of the same SHAPE — a
/// different item, a different recipient — is handed it before it plans.
///
/// Everything here is about that word "shape". A guide filed under the specific thing is a guide nothing ever
/// finds again.
/// </summary>
public class TaskGuideTests : IDisposable
{
    private readonly List<string> _paths = new();

    private TaskHintStore NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"guides-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        return new TaskHintStore(path, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static RunStep Tool(string name, string args, string result) =>
        new() { Kind = "tool", Tool = name, Args = args, Result = result };

    private static RunStep Changed(string name, string find) =>
        Tool(name, $"{{\"find\":\"{find}\"}}", "{\"changed\":[{\"text\":\"ok\"}]}");

    // ── Finding the guide ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_different_item_finds_the_guide_written_for_the_last_one()
    {
        var store = NewStore();
        store.Upsert("list an item on eBay", "Start at the sell hub, not the homepage.", "task 41");

        var found = store.Relevant("list my Nintendo Switch on eBay for £180");

        Assert.Single(found);
        Assert.Equal("list an item on eBay", found[0].Name);
    }

    [Fact]
    public void An_unrelated_job_matches_nothing_and_so_costs_nothing()
    {
        var store = NewStore();
        store.Upsert("list an item on eBay", "Start at the sell hub.", "task 41");

        Assert.Empty(store.Relevant("book a table at the Italian place on Friday"));
        Assert.Equal("", store.NoteFor("book a table at the Italian place on Friday"));
    }

    [Fact]
    public void One_shared_word_is_a_coincidence_not_a_match()
    {
        var store = NewStore();
        store.Upsert("send an email in Gmail", "Compose opens with c.", "task 12");

        // "email" alone is shared. Handing a Gmail procedure to someone reading their inbox in a different
        // client is worse than handing them nothing: they follow it and it doesn't fit.
        Assert.Empty(store.Relevant("summarise the email newsletter I saved"));
    }

    [Fact]
    public void The_note_says_it_may_be_wrong_by_now()
    {
        var store = NewStore();
        store.Upsert("list an item on eBay", "Start at the sell hub.", "task 41");

        var note = store.NoteFor("list the Switch on eBay");

        Assert.Contains("Start at the sell hub", note);
        // A site changes and the guide doesn't. A worker that treats it as instruction gets stuck arguing with
        // a page that no longer exists, which is a worse failure than never having had the guide.
        Assert.Contains("abandon it", note);
    }

    // ── Keeping one guide per shape ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Writing_the_same_name_again_replaces_it_rather_than_competing_with_it()
    {
        var store = NewStore();
        store.Upsert("list an item on eBay", "Old, from before the redesign.", "task 41");
        store.Upsert("List an item on eBay", "The sell hub moved to a new URL.", "task 58");

        var all = store.All();
        Assert.Single(all);
        Assert.Equal("The sell hub moved to a new URL.", all[0].Description);
        Assert.Equal(2, all[0].Revisions);       // two runs have now contributed
        Assert.Equal("task 58", all[0].LearnedFrom);
    }

    [Fact]
    public void Guides_survive_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"guides-{Guid.NewGuid():N}.json");
        _paths.Add(path);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        new TaskHintStore(path, json).Upsert("list an item on eBay", "Start at the sell hub.", "task 41");

        var reopened = new TaskHintStore(path, json);
        Assert.Equal("Start at the sell hub.", reopened.Relevant("list a coat on eBay")[0].Description);
    }

    // ── Asking for a guide mid-run ──────────────────────────────────────────────────────────────────────

    private static async Task<string> Call(AgentTool tool, string? name = null)
    {
        var json = name is null ? "{}" : JsonSerializer.Serialize(new { name });
        var args = new ToolCallArguments(JsonDocument.Parse(json).RootElement);
        return (await tool.InvokeAsync(args, CancellationToken.None)).Content;
    }

    [Fact]
    public async Task With_no_argument_it_lists_names_only()
    {
        // Names, not summaries. The reason for a tool at all is that the library outgrows the prompt, and a
        // list of one-liners is the same problem one step later.
        var store = NewStore();
        store.Upsert("list an item on eBay", "Start at the sell hub, not the homepage.", "task 41");
        store.Upsert("send an email in Gmail", "Compose opens with c.", "task 12");

        var listed = await Call(TaskGuideTools.Tool(store));

        Assert.Contains("list an item on eBay", listed);
        Assert.Contains("send an email in Gmail", listed);
        Assert.DoesNotContain("sell hub", listed);
    }

    [Fact]
    public async Task A_job_that_shares_no_words_with_the_guide_can_still_reach_it()
    {
        // The whole point of the tool. "sort out the spare Switch" scores zero against "list an item on eBay"
        // and always will — the automatic lookup can only match what the task was CALLED. Three steps in, the
        // worker knows what the job actually is.
        var store = NewStore();
        store.Upsert("list an item on eBay", "Start at the sell hub, not the homepage.", "task 41");

        Assert.Empty(store.Relevant("sort out the spare Switch"));
        Assert.Contains("sell hub", await Call(TaskGuideTools.Tool(store), "list an item on eBay"));
    }

    [Fact]
    public async Task Reading_one_on_purpose_counts_as_following_it()
    {
        // Otherwise a guide asked for by name is exempt from the outcome: it could walk every run that loaded it
        // into a wall and never collect a single miss.
        var store = NewStore();
        store.Upsert("list an item on eBay", "Start at the sell hub.", "task 41");
        var followed = new List<string>();

        await Call(TaskGuideTools.Tool(store, followed.Add), "LIST AN ITEM ON EBAY");

        Assert.Equal(new[] { "list an item on eBay" }, followed);   // recorded under its real name
    }

    [Fact]
    public async Task A_name_that_does_not_exist_is_a_dead_end_with_the_real_names_in_it()
    {
        var store = NewStore();
        store.Upsert("list an item on eBay", "Start at the sell hub.", "task 41");
        var followed = new List<string>();

        var args = new ToolCallArguments(JsonDocument.Parse("{\"name\":\"how to sell things\"}").RootElement);
        var result = await TaskGuideTools.Tool(store, followed.Add)
            .InvokeAsync(args, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.False(result.CanRetry);      // guessing again at a list it has already been given is a loop
        Assert.Contains("list an item on eBay", result.Content);
        Assert.Empty(followed);             // nothing was read, so nothing is charged for the outcome
    }

    // ── Length ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_guide_too_long_to_be_a_head_start_is_refused()
    {
        var store = NewStore();
        var essay = new string('x', TaskHintStore.MaxDescriptionLength + 1);

        Assert.False(store.Upsert("list an item on eBay", essay, "task 41"));
        Assert.Empty(store.All());
    }

    [Fact]
    public void A_refused_revision_leaves_the_guide_that_was_there()
    {
        // Held rather than trimmed: half a procedure reads as a whole one, and the version already on disk was
        // written by a run that finished. Losing it to a rambling rewrite is a straight downgrade.
        var store = NewStore();
        store.Upsert("list an item on eBay", "Start at the sell hub.", "task 41");

        Assert.False(store.Upsert("list an item on eBay",
            new string('x', TaskHintStore.MaxDescriptionLength + 1), "task 58"));

        Assert.Equal("Start at the sell hub.", store.All()[0].Description);
        Assert.Equal(1, store.All()[0].Revisions);
    }

    // ── When the guide turns out to be wrong ────────────────────────────────────────────────────────────

    [Fact]
    public void One_failed_run_is_not_enough_to_condemn_a_guide()
    {
        // Runs fail for their own reasons. Dropping a guide on the first one throws away everything that ever
        // gets followed by an unlucky task.
        var store = NewStore();
        store.Upsert("list an item on eBay", "Start at the sell hub.", "task 41");

        Assert.Empty(store.Missed(new[] { "list an item on eBay" }));
        Assert.Single(store.All());
        Assert.Equal(1, store.All()[0].Misses);
    }

    [Fact]
    public void Two_failed_runs_running_and_the_guide_goes()
    {
        // A wrong procedure is worse than no procedure: a worker handed nothing looks at the page, and a worker
        // handed a route that no longer exists argues with it.
        var store = NewStore();
        store.Upsert("list an item on eBay", "Start at the sell hub.", "task 41");

        store.Missed(new[] { "list an item on eBay" });
        var dropped = store.Missed(new[] { "list an item on eBay" });

        Assert.Equal(new[] { "list an item on eBay" }, dropped);
        Assert.Empty(store.All());
    }

    [Fact]
    public void A_run_that_succeeds_clears_what_the_last_failure_held_against_it()
    {
        var store = NewStore();
        store.Upsert("list an item on eBay", "Start at the sell hub.", "task 41");
        store.Missed(new[] { "list an item on eBay" });

        store.Held(new[] { "list an item on eBay" });

        Assert.Equal(0, store.All()[0].Misses);
        // And so the next failure is a first failure again, not the one that drops it.
        Assert.Empty(store.Missed(new[] { "list an item on eBay" }));
        Assert.Single(store.All());
    }

    [Fact]
    public void Rewriting_a_guide_wipes_its_record()
    {
        // Whatever was wrong with it has just been rewritten by a run that finished. Carrying the miss forward
        // would drop the corrected guide on the next unlucky task.
        var store = NewStore();
        store.Upsert("list an item on eBay", "Old route.", "task 41");
        store.Missed(new[] { "list an item on eBay" });

        store.Upsert("list an item on eBay", "The sell hub moved.", "task 58");

        Assert.Equal(0, store.All()[0].Misses);
    }

    [Fact]
    public void A_guide_that_no_longer_exists_is_not_an_error_to_charge()
    {
        var store = NewStore();
        Assert.Empty(store.Missed(new[] { "a guide nobody wrote" }));
        store.Held(new[] { "a guide nobody wrote" });
    }

    // ── What a finished run is asked about ──────────────────────────────────────────────────────────────

    [Fact]
    public void Only_the_steps_that_moved_something_are_the_procedure()
    {
        var steps = new List<RunStep>
        {
            Tool("chrome_read_page", "{}", "{\"text\":\"a whole page\"}"),          // looked
            Tool("chrome_find", "{\"query\":\"sell\"}", "{\"matches\":[]}"),        // looked
            Changed("chrome_click", "Sell it yourself"),                              // moved
            Tool("chrome_click", "{\"find\":\"Continue\"}", "Error: no such element"), // failed
            Changed("chrome_type", "Title"),                                          // moved
        };

        var spine = RunLessons.Spine(steps);

        Assert.Equal(2, spine.Count);
        Assert.Contains(spine, s => s.Contains("Sell it yourself"));
        Assert.DoesNotContain(spine, s => s.Contains("chrome_read_page"));
    }

    [Fact]
    public void Work_that_never_touched_a_browser_still_has_a_procedure()
    {
        // Proof of a change is a question only the browser raises: a click on a dead element and a click that
        // opened a dialog look identical without it. write_file doesn't have that problem — it either wrote the
        // file or it returned an error. Demanding the proof from every tool left a run that built a document and
        // ran a script teaching nothing at all.
        var steps = new List<RunStep>
        {
            Tool("list_files", "{}", "notes.md"),                                     // looked
            Tool("write_file", "{\"name\":\"summary.md\"}", "Saved as summary.md"),
            Tool("run_python", "{\"code\":\"chart\"}", "chart.png written"),
            Tool("build_presentation", "{\"name\":\"deck\"}", "Saved as deck.pptx"),
        };

        var spine = RunLessons.Spine(steps);

        Assert.Equal(3, spine.Count);
        Assert.DoesNotContain(spine, s => s.Contains("list_files"));
    }

    [Fact]
    public void A_click_that_changed_nothing_is_not_a_step()
    {
        // The browser half of the same rule: it is still the case that a page is full of things that look
        // clickable and aren't, and a procedure built from those is a procedure that doesn't work.
        var steps = new List<RunStep>
        {
            // Verbatim from the browser: a missing key means "couldn't tell", so a click that did nothing has
            // to say so out loud rather than stay quiet, and this is what it says.
            Tool("chrome_click", "{\"find\":\"Sell\"}",
                 "{\"clicked\":\"Sell\",\"changed\":\"nothing on the page changed\"}"),
            Tool("chrome_click", "{\"find\":\"Buy\"}", "{\"clicked\":\"Buy\",\"changed\":[]}"),
            Changed("chrome_click", "Sell it yourself"),
        };

        Assert.Single(RunLessons.Spine(steps));
    }

    [Fact]
    public void Several_scripts_are_several_steps()
    {
        // A real run fetched a JSON endpoint, inspected it, reshaped it and wrote the file — four run_python
        // calls. With nothing in the arguments to tell them apart they de-duped to one line, the run scored a
        // single changing step, and the lesson worth keeping (that the endpoint exists) was never written down.
        var steps = new List<RunStep>
        {
            Tool("run_python", "{\"code\":\"print(requests.get(url).text[:400])\"}", "ok"),
            Tool("run_python", "{\"code\":\"data = requests.get(url + '.json').json()\"}", "ok"),
            Tool("run_python", "{\"code\":\"open('out.md','w').write(rows)\"}", "ok"),
        };

        Assert.Equal(3, RunLessons.Spine(steps).Count);
    }

    [Fact]
    public void The_same_step_seven_times_was_learned_once()
    {
        // A listing run clicks "Continue" through five sub-pages. Five identical lines teach nothing beyond
        // the first and crowd out the steps that differ.
        var steps = Enumerable.Range(0, 5).Select(_ => Changed("chrome_click", "Continue")).ToList();

        Assert.Single(RunLessons.Spine(steps));
    }

    [Fact]
    public void A_refusal_is_only_useful_next_to_what_worked_instead()
    {
        var steps = new List<RunStep>
        {
            Tool("chrome_click", "{\"find\":\"Sell\"}", "Error: matched two elements"),
            Changed("chrome_click", "Sell it yourself"),
        };

        var gotchas = RunLessons.Gotchas(steps);

        Assert.Single(gotchas);
        Assert.Contains("matched two elements", gotchas[0]);
        Assert.Contains("worked instead", gotchas[0]);
        Assert.Contains("Sell it yourself", gotchas[0]);
    }

    [Fact]
    public void The_evidence_is_the_task_the_spine_and_the_gotchas()
    {
        var steps = new List<RunStep>
        {
            Changed("chrome_click", "Sell it yourself"),
            Tool("chrome_click", "{\"find\":\"List it\"}", "Error: not operable"),
            Changed("chrome_click", "List it now"),
        };

        var evidence = RunLessons.Evidence("list my Switch on eBay", steps);

        Assert.Contains("list my Switch on eBay", evidence);
        Assert.Contains("Sell it yourself", evidence);
        Assert.Contains("not operable", evidence);
    }

    public void Dispose()
    {
        foreach (var p in _paths) { try { File.Delete(p); } catch { } }
    }
}
