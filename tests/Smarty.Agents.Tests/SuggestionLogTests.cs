using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Remembering what was offered and how it landed.
///
/// <para>
/// The first version held declined titles in a HashSet in RAM. So an idea turned down on Monday came straight back on
/// Tuesday if the process had restarted in between, and an idea that was ACCEPTED left no trace at all — which is the
/// more useful half, because it is the only evidence of what this person actually wants on their page.
/// </para>
/// </summary>
public class SuggestionLogTests
{
    private static (SuggestionLog Log, string Path) New()
    {
        var path = Path.Combine(Path.GetTempPath(), $"suggestions-{Guid.NewGuid():N}.json");
        return (new SuggestionLog(path, new System.Text.Json.JsonSerializerOptions()), path);
    }

    [Fact]
    public void A_decline_survives_a_restart()
    {
        var (log, path) = New();
        log.Offered("Tube status", "You are in Fitzrovia and take the Victoria line.");
        log.Answered("Tube status", accepted: false);

        // The whole point: a new process must still know.
        var reopened = new SuggestionLog(path, new System.Text.Json.JsonSerializerOptions());

        Assert.True(reopened.WasDeclined("Tube status"));
        Assert.True(reopened.WasDeclined("tube STATUS"), "the same idea in different case is the same idea");
    }

    [Fact]
    public void An_offer_nobody_answered_is_not_treated_as_a_refusal()
    {
        // An unanswered card means it has not been read yet, which is a different thing from being turned down.
        var (log, _) = New();
        log.Offered("Holiday countdown", "Your holiday is the one thing with a real date attached.");

        Assert.False(log.WasDeclined("Holiday countdown"));
    }

    [Fact]
    public void An_acceptance_is_remembered_too_and_never_blocks_the_idea()
    {
        var (log, _) = New();
        log.Offered("Dinner plan for this week", "You are mid-way through your week of dinners.");
        log.Answered("Dinner plan for this week", accepted: true);

        Assert.False(log.WasDeclined("Dinner plan for this week"));
        Assert.Contains("they said yes", log.Describe());
    }

    [Fact]
    public void Both_outcomes_reach_the_prompt_with_the_instruction_that_makes_them_useful()
    {
        var (log, _) = New();
        log.Offered("Tube status", "Victoria line.");
        log.Answered("Tube status", accepted: false);
        log.Offered("Dinner plan", "Mid-week.");
        log.Answered("Dinner plan", accepted: true);

        var note = log.Describe();

        Assert.Contains("Tube status", note);
        Assert.Contains("turned it down", note);
        Assert.Contains("Dinner plan", note);
        Assert.Contains("they said yes", note);
        // Reading the pattern is the point — a bare list would just be a changelog.
        Assert.Contains("even reworded", note);
        Assert.Contains("shape of thing worth offering", note);
    }

    [Fact]
    public void With_nothing_settled_the_prompt_gains_nothing()
    {
        var (log, _) = New();
        log.Offered("Something", "Anything.");

        // An offered-but-unanswered idea is not history yet, and padding the prompt with it teaches nothing.
        Assert.Equal("", log.Describe());
    }

    [Fact]
    public void Answering_something_never_offered_is_ignored_rather_than_invented()
    {
        var (log, _) = New();
        log.Answered("A panel nobody proposed", accepted: false);

        Assert.False(log.WasDeclined("A panel nobody proposed"));
        Assert.Empty(log.All());
    }

    [Fact]
    public void An_offer_made_before_the_log_existed_is_still_remembered_when_answered()
    {
        // The first real decline was lost exactly this way: the proposal had been created by an earlier build, so
        // there was no record to update and the refusal vanished. The proposal was real and the answer was real, so
        // recording both is accurate — and without it, that idea comes straight back.
        var (log, _) = New();

        // What the suggester does on behalf of a proposal it did not make.
        log.Offered("Holiday countdown", "Your holiday is the one thing with a real date attached.");
        log.Answered("Holiday countdown", accepted: false);

        Assert.True(log.WasDeclined("Holiday countdown"));
        Assert.Contains("turned it down", log.Describe());
    }

    [Fact]
    public void Offering_the_same_idea_twice_leaves_one_record()
    {
        // Otherwise the history fills with duplicates of whatever it keeps thinking of, and the pattern is buried.
        var (log, _) = New();
        log.Offered("Tube status", "First reason.");
        log.Offered("Tube status", "Second reason.");

        Assert.Single(log.All());
        Assert.Equal("Second reason.", log.All()[0].Why);
    }
}
