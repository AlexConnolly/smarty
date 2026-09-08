using Smarty.Agents;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// What you get when you ask to look at a picture without saying what you want to know.
///
/// describe_image was being called TWICE on the same image, almost every time the point was to see the picture
/// rather than vet it. The tool invited a bare call for "a plain description"; the bare call actually ran a
/// VETTING prompt — two sentences, plus whether this was a logo or a stock shot — which is the right question
/// for checking an image before it goes in a brochure and useless for describing one. So the model got "a man
/// standing in front of a hair salon", learned nothing it could write with, and asked again properly. The cache
/// keys on the question, so the second look was a full-price call to the vision model.
///
/// The default has to serve BOTH reasons for asking in one call, because both are common and only one of them
/// was being served.
/// </summary>
public class VisionPromptTests
{
    private static AgentTool Tool() => Vision.Tool(Path.GetTempPath(), Path.GetTempPath());

    private static string QuestionParamDescription() =>
        Tool().Parameters.Single(p => p.Name == "question").Description;

    [Fact]
    public void A_bare_call_asks_for_an_actual_description()
    {
        // The failure was that it didn't. "Describe" plus a cap of two sentences is a caption, not a description.
        var prompt = Vision.DefaultPrompt;

        Assert.Contains("Describe this image", prompt);
        Assert.Contains("setting", prompt);
        Assert.Contains("text or signage", prompt);
        Assert.DoesNotContain("one or two sentences", prompt);
    }

    [Fact]
    public void It_still_says_whether_the_picture_is_real()
    {
        // The brochure case has to keep working — it's the reason the old prompt existed, and it was the one
        // thing the old prompt was good at. Now a rider on a real description rather than the whole answer.
        var prompt = Vision.DefaultPrompt;

        Assert.Contains("logo", prompt);
        Assert.Contains("map", prompt);
        Assert.Contains("stock photograph", prompt);
    }

    [Fact]
    public void The_description_is_written_for_someone_who_cannot_see_the_image()
    {
        // The whole point of the tool: its answer is the only access anything downstream has to the picture.
        Assert.Contains("cannot see the picture", Vision.DefaultPrompt);
    }

    [Fact]
    public void The_tool_no_longer_invites_a_throwaway_first_look()
    {
        // "Omit for a plain description" is the exact wording that produced the wasted call.
        var description = QuestionParamDescription();

        Assert.DoesNotContain("Omit for a plain description", description);
        Assert.Contains("FIRST call", description);
    }

    [Fact]
    public void The_cost_of_asking_twice_is_stated_where_the_decision_is_made()
    {
        // On the parameter itself, not buried in the tool blurb — this is the choice being made.
        Assert.Contains("full-price", QuestionParamDescription());
    }

    [Fact]
    public void Asking_the_same_thing_twice_is_still_free()
    {
        // The existing cache covers the identical repeat; this is about the DIFFERENT-question case, and the
        // promise the tool makes about repeats must stay true.
        Assert.Contains("asking about the same one again is", Tool().Description);
    }
}
