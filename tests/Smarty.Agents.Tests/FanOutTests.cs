using System.Text.Json;
using Smarty.Agents;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Doing the same thing to many things, and staying in charge of it.
///
/// The run that prompted this drove one browser tab at a 49-item shopping list and exhausted 320 tool calls with
/// one item in the basket — because item two cost as much as item one and nothing carried over. fan_out is the
/// other shape: explore once, then hand the recipe and the list to a worker each.
///
/// What's tested here is the part that makes it an ORCHESTRATION rather than a spawn-and-pray: the parent gets
/// every item back with a definite answer, a failure stops the next batch rather than the whole list quietly
/// draining away, and what was never attempted is named so it can be picked up.
/// </summary>
public class FanOutTests
{
    private static Orchestrator.ItemOutcome Ok(int n, string item) => new(n, item, true, $"\"did {item}\"");
    private static Orchestrator.ItemOutcome Bad(int n, string item) => new(n, item, false, "not verified: no confirmation");

    /// <summary>Drives the real batching rule with a stub batch runner, recording which batches actually ran.</summary>
    private static async Task<(List<Orchestrator.ItemOutcome> Outcomes, bool Halted, string Reason, List<int> Ran)>
        Drive(IReadOnlyList<string> items, int batchSize, Func<string, bool> succeeds)
    {
        var ran = new List<int>();
        var (outcomes, halted, reason) = await Orchestrator.DriveBatchesAsync(
            items, batchSize,
            (slice, b) =>
            {
                ran.Add(b);
                IReadOnlyList<Orchestrator.ItemOutcome> results = slice
                    .Select(s => succeeds(s.Item) ? Ok(s.Number, s.Item) : Bad(s.Number, s.Item))
                    .ToList();
                return Task.FromResult(results);
            });
        return (outcomes, halted, reason, ran);
    }

    private static readonly string[] Shop =
    {
        "eggs", "sourdough", "oat milk", "chicken thighs",
        "basmati rice", "tomatoes", "parmesan", "olive oil",
        "spinach", "lemons",
    };

    [Fact]
    public async Task A_clean_run_gets_through_every_item()
    {
        var (outcomes, halted, _, ran) = await Drive(Shop, batchSize: 4, succeeds: _ => true);

        Assert.False(halted);
        Assert.Equal(10, outcomes.Count);
        Assert.All(outcomes, o => Assert.True(o.Ok));
        Assert.Equal(new[] { 0, 1, 2, 3 }, ran);   // 1 (trial) + 4 + 4 + 1
    }

    [Fact]
    public async Task The_recipe_is_tried_on_one_item_before_the_rest_are_committed()
    {
        // A recipe written after exploring is a hypothesis, and it is about to be applied thirty-four more
        // times. Testing it on four at once means a wrong one costs four workers to discover.
        var (outcomes, _, _, _) = await Drive(Shop, batchSize: 4, succeeds: _ => true);

        Assert.Equal("eggs", outcomes[0].Item);
        var (_, _, _, ran) = await Drive(Shop, batchSize: 4, succeeds: i => i != "eggs");
        Assert.Single(ran);   // the trial failed, so nothing else was attempted at all
    }

    [Fact]
    public async Task A_recipe_that_never_worked_costs_one_worker_to_find_out()
    {
        var (outcomes, halted, reason, _) = await Drive(Shop, batchSize: 4, succeeds: i => i != "eggs");

        Assert.True(halted);
        Assert.Single(outcomes);
        Assert.Contains("trial item", reason);
    }

    [Fact]
    public async Task A_failure_after_the_trial_stops_the_next_batch()
    {
        // The recipe worked once, so the items that follow are worth attempting — but the moment one fails,
        // the budget stops going into it.
        var (_, halted, reason, ran) = await Drive(Shop, batchSize: 4, succeeds: i => i != "oat milk");

        Assert.True(halted);
        Assert.Contains("oat milk", reason);
        Assert.DoesNotContain("trial item", reason);
        Assert.Equal(new[] { 0, 1 }, ran);   // trial, then the batch that failed; nothing after
    }

    [Fact]
    public async Task The_batch_it_failed_in_still_finishes()
    {
        // Deliberately NOT killed mid-flight. A worker stopped halfway through a mutation leaves "did that reach
        // the basket or not?", and an unknown is worse to recover from than a clean failure.
        var (outcomes, _, _, _) = await Drive(Shop, batchSize: 4, succeeds: i => i != "oat milk");

        Assert.Equal(5, outcomes.Count);                 // the trial plus the batch of four
        Assert.Equal(4, outcomes.Count(o => o.Ok));
        Assert.Contains(outcomes, o => o.Item == "basmati rice" && o.Ok);   // ran after the failing sibling
    }

    [Fact]
    public async Task Every_item_ends_with_a_definite_answer()
    {
        // No item may be left ambiguous: it is verified, it failed, or it was never attempted.
        var (outcomes, _, _, _) = await Drive(Shop, batchSize: 4, succeeds: i => i != "oat milk");

        var attempted = outcomes.Select(o => o.Item).ToHashSet();
        var untouched = Shop.Where(i => !attempted.Contains(i)).ToList();

        Assert.Equal(Shop.Length, attempted.Count + untouched.Count);
        Assert.Empty(attempted.Intersect(untouched));
    }

    [Fact]
    public async Task A_stop_between_batches_is_honoured()
    {
        bool stop = false;
        var (outcomes, halted, _) = await Orchestrator.DriveBatchesAsync(
            Shop, 4,
            (slice, _) =>
            {
                stop = true;   // the user cancels while batch 1 is running
                IReadOnlyList<Orchestrator.ItemOutcome> r = slice.Select(s => Ok(s.Number, s.Item)).ToList();
                return Task.FromResult(r);
            },
            stopped: () => stop);

        Assert.False(halted);          // stopped, not failed
        Assert.Single(outcomes);       // cancelled during the trial item, so nothing else was started
    }

    // ---- the report the parent reads ----

    private static string Report(IReadOnlyList<string> items, List<Orchestrator.ItemOutcome> outcomes, bool halted, string reason)
    {
        var attempted = outcomes.Select(o => o.Item).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Orchestrator.FanOutReport("Add the item to the Ocado basket", items.Count, outcomes,
            items.Where(i => !attempted.Contains(i)).ToList(), halted, reason);
    }

    [Fact]
    public async Task The_report_names_what_was_never_attempted_so_it_can_be_picked_up()
    {
        var (outcomes, halted, reason, _) = await Drive(Shop, batchSize: 4, succeeds: i => i != "oat milk");
        var report = Report(Shop, outcomes, halted, reason);

        // Trial (eggs) passes, then the batch of four in which oat milk fails: four verified, five never tried.
        Assert.Contains("4 of 10 verified", report);
        Assert.Contains("5 not attempted", report);
        Assert.Contains("tomatoes", report);   // named, not just counted
    }

    [Fact]
    public async Task The_report_says_what_failed_and_what_to_do_about_it()
    {
        var (outcomes, halted, reason, _) = await Drive(Shop, batchSize: 4, succeeds: i => i != "oat milk");
        var report = Report(Shop, outcomes, halted, reason);

        Assert.Contains("STOPPED", report);
        Assert.Contains("oat milk", report);
        Assert.Contains("fan_out again", report);      // the handover is explicit
        Assert.Contains("don't grind", report);        // and so is the limit on retrying
    }

    [Fact]
    public async Task A_clean_report_does_not_invite_a_re_check()
    {
        var (outcomes, halted, reason, _) = await Drive(Shop, batchSize: 4, succeeds: _ => true);
        var report = Report(Shop, outcomes, halted, reason);

        Assert.Contains("All items verified", report);
        Assert.DoesNotContain("STOPPED", report);
        Assert.DoesNotContain("not attempted", report);
    }

    // ---- reading the list off the call ----

    private static List<string> Items(string json) =>
        Orchestrator.ReadItems(new ToolCallArguments(JsonDocument.Parse(json).RootElement.Clone()));

    [Fact]
    public void A_json_array_is_the_normal_case()
    {
        Assert.Equal(new[] { "eggs", "sourdough" }, Items("""{"items":["eggs","sourdough"]}"""));
    }

    [Fact]
    public void A_list_written_as_text_is_not_thrown_away()
    {
        // Losing a 40-item call because the model wrote a list instead of an array is not a good trade.
        Assert.Equal(new[] { "eggs", "sourdough" }, Items("""{"items":"- eggs\n- sourdough"}"""));
        Assert.Equal(new[] { "eggs", "sourdough" }, Items("""{"items":"eggs, sourdough"}"""));
    }

    [Fact]
    public void The_same_item_twice_is_one_job()
    {
        Assert.Equal(new[] { "eggs", "sourdough" }, Items("""{"items":["eggs","Eggs","sourdough"]}"""));
    }

    [Fact]
    public void No_items_reads_as_none_rather_than_throwing()
    {
        Assert.Empty(Items("""{"goal":"do a thing"}"""));
    }
}
