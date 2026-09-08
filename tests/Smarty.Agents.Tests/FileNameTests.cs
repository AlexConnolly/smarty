using System.Reflection;
using Smarty.Agents;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// The name a file ends up with.
///
/// write_file refuses HTML and told the worker to "write it as .md". Holding a name it had already settled on,
/// the worker did the most literal possible thing and produced Holiday_Playbook_2027_Deck.html.md — a markdown
/// file wearing the name of the deck it had just been told not to write, which reached the user like that. The
/// instruction was followed exactly. The name was the part nobody checked.
/// </summary>
public class FileNameTests
{
    private static string Clean(string name) =>
        (string)typeof(FileTools)
            .GetMethod("SafeFileName", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { name })!;

    [Fact]
    public void The_file_that_went_out_wrong()
    {
        Assert.Equal("Holiday_Playbook_2027_Deck.md", Clean("Holiday_Playbook_2027_Deck.html.md"));
    }

    [Theory]
    [InlineData("report.htm.md", "report.md")]
    [InlineData("notes.md.txt", "notes.txt")]
    [InlineData("page.md.html", "page.html")]
    public void Any_two_stacked_markup_extensions_collapse_to_the_real_one(string given, string expected)
    {
        Assert.Equal(expected, Clean(given));
    }

    [Theory]
    [InlineData("summary.md")]
    [InlineData("data.json")]
    [InlineData("deck.html")]
    [InlineData("plain")]
    public void An_ordinary_name_is_left_exactly_as_it_is(string name)
    {
        Assert.Equal(name, Clean(name));
    }

    [Theory]
    [InlineData("archive.tar.gz")]
    [InlineData("types.d.ts")]
    [InlineData("backup.2026.json")]
    [InlineData("brand.v2.png")]
    public void A_doubled_extension_that_was_meant_is_not_touched(string name)
    {
        // The fix must be narrower than "collapse anything with two dots", or it becomes the worse bug.
        Assert.Equal(name, Clean(name));
    }

    [Fact]
    public void A_path_is_still_reduced_to_a_bare_name()
    {
        Assert.Equal("notes.md", Clean(@"..\..\somewhere\notes.md"));
    }

    [Fact]
    public void Invalid_characters_are_still_replaced()
    {
        Assert.DoesNotContain(":", Clean("a:b.md"));
    }
}
