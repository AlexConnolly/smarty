using System.Text.Json;
using Smarty.Agents;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// Handing pictures over, and why a convention was not enough.
///
/// <para>
/// Asked for photos of a restaurant's food, a job opened the listing, looked at every picture, checked each against the
/// menu — and delivered a markdown document describing them in words. Nothing was missing from the plumbing: markdown
/// images in a result were already copied locally and rendered, and bare links already became cards with their preview
/// image. Both were reachable and neither was reached for, because a convention is not an affordance. A model asked to
/// gather pictures looks at its tools, finds somewhere to write a file, and writes a file.
/// </para>
/// </summary>
public class GalleryToolTests
{
    private static readonly List<IReadOnlyList<GalleryTool.Picture>> Shown = new();

    private static AgentTool Tool(GalleryResult? result = null)
    {
        Shown.Clear();
        return GalleryTool.Create((pictures, _) =>
        {
            Shown.Add(pictures);
            return Task.FromResult(result ?? new GalleryResult(pictures.Count, 0));
        });
    }

    private static ToolCallArguments Args(string json) =>
        new(JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public async Task Pictures_arrive_with_what_each_one_is()
    {
        var tool = Tool();

        var out_ = await tool.InvokeAsync(Args("""
            {"pictures":[
              {"url":"https://x/1.jpg","title":"Baja fish tacos","subtitle":"fried fish, cabbage & salsa — £14"},
              {"url":"https://x/2.jpg","title":"Tuna tartare"}
            ]}
            """), default);

        var pictures = Assert.Single(Shown);
        Assert.Equal(2, pictures.Count);
        Assert.Equal("Baja fish tacos", pictures[0].Title);
        Assert.Equal("fried fish, cabbage & salsa — £14", pictures[0].Subtitle);
        Assert.Null(pictures[1].Subtitle);
        Assert.Contains("Showed 2 pictures", out_.Content);
    }

    [Fact]
    public async Task It_tells_the_caller_not_to_describe_them_again()
    {
        // The whole failure was a job answering in adjectives. Once they are on screen, saying it again is noise.
        var out_ = await Tool().InvokeAsync(Args("""{"pictures":[{"url":"https://x/1.jpg"}]}"""), default);

        Assert.Contains("don't describe them again", out_.Content);
    }

    [Fact]
    public async Task A_bare_list_of_urls_is_accepted_because_that_is_what_it_may_have()
    {
        await Tool().InvokeAsync(Args("""{"pictures":["https://x/1.jpg","https://x/2.jpg"]}"""), default);

        var pictures = Assert.Single(Shown);
        Assert.Equal(2, pictures.Count);
        Assert.All(pictures, p => Assert.Null(p.Title));
    }

    [Fact]
    public async Task One_malformed_entry_does_not_lose_the_others()
    {
        await Tool().InvokeAsync(Args("""
            {"pictures":[
              {"url":"https://x/1.jpg","title":"Good"},
              {"title":"No url at all"},
              {"url":"https://x/3.jpg","title":"Also good"}
            ]}
            """), default);

        var pictures = Assert.Single(Shown);
        Assert.Equal(2, pictures.Count);
        Assert.Equal(new[] { "Good", "Also good" }, pictures.Select(p => p.Title));
    }

    [Fact]
    public async Task Nothing_usable_is_an_error_rather_than_a_silent_success()
    {
        var out_ = await Tool().InvokeAsync(Args("""{"pictures":[{"title":"just words"}]}"""), default);

        Assert.True(out_.IsError);
        Assert.Empty(Shown);
    }

    [Fact]
    public async Task A_contact_sheet_is_trimmed_to_something_lookable_at()
    {
        var many = string.Join(",", Enumerable.Range(1, 30).Select(i => $"\"https://x/{i}.jpg\""));

        await Tool().InvokeAsync(Args($"{{\"pictures\":[{many}]}}"), default);

        Assert.Equal(GalleryTool.Most, Assert.Single(Shown).Count);
    }

    [Fact]
    public async Task Nothing_usable_at_all_is_reported_so_it_can_find_better_urls()
    {
        // Showing four of twelve and reporting success is how an answer ends up mostly empty with nobody told. This now
        // only happens when the addresses themselves were unusable — a refused download is no longer a failure.
        var tool = Tool(new GalleryResult(Shown: 0, Dropped: 3));

        var out_ = await tool.InvokeAsync(Args("""
            {"pictures":[{"url":"not-a-url"},{"url":"also-not"},{"url":"nope"}]}
            """), default);

        Assert.True(out_.IsError);
        Assert.Contains("None of those could be shown", out_.Content);
    }

    [Fact]
    public async Task A_path_the_browser_grabbed_is_a_picture_like_any_other()
    {
        // The route that actually works for the sites holding food photographs: they refuse a plain request and show
        // the image in the browser anyway, so a grab hands back a path on this origin.
        await Tool().InvokeAsync(Args("""
            {"pictures":[{"url":"/media/8f21c3.jpg","title":"Baja fish tacos"}]}
            """), default);

        var picture = Assert.Single(Assert.Single(Shown));
        Assert.Equal("/media/8f21c3.jpg", picture.Url);
        Assert.Equal("Baja fish tacos", picture.Title);
    }

    [Fact]
    public async Task Some_getting_through_says_how_many_did_not()
    {
        var tool = Tool(new GalleryResult(Shown: 2, Dropped: 1));

        var out_ = await tool.InvokeAsync(Args("""
            {"pictures":[{"url":"https://x/a"},{"url":"https://x/b"},{"url":"https://x/c"}]}
            """), default);

        Assert.False(out_.IsError);
        Assert.Contains("Showed 2 pictures", out_.Content);
        Assert.Contains("1 couldn't be loaded", out_.Content);
    }
}
