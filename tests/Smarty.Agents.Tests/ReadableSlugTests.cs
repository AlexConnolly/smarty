using System.Reflection;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// A slug written the way a person reads it.
///
/// <para>
/// Slugs leak into the UI wherever a name could not be found — a pill for a project that has since been deleted, a
/// conversation tagged with a key rather than a title — and <c>selling-the-car</c> is not something to put in front of
/// somebody. The rule is deliberately dull: hyphens to spaces, first letter up, and nothing else, because anything
/// cleverer would start editing text that a person actually typed.
/// </para>
/// </summary>
public class ReadableSlugTests
{
    /// <remarks>
    /// Reached by reflection because it is a local static in the API's top-level program, which has no accessible type
    /// name of its own. Testing it through a public wrapper written only for the test would be testing the wrapper.
    /// </remarks>
    private static string Readable(string? slug)
    {
        var program = typeof(Smarty.Api.Project).Assembly.GetType("Program", throwOnError: true)!;
        var method = program.GetMethod("<<Main>$>g__Readable|0_10", BindingFlags.Static | BindingFlags.NonPublic)
                     ?? program.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                         .FirstOrDefault(m => m.Name.Contains("Readable") && m.GetParameters().Length == 1);

        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object?[] { slug })!;
    }

    [Theory]
    [InlineData("selling-the-car", "Selling the car")]
    [InlineData("dinner-spots", "Dinner spots")]
    [InlineData("fenwick-harriers", "Fenwick harriers")]
    [InlineData("marlow-lido", "Marlow lido")]
    public void Hyphens_become_spaces_and_the_first_letter_goes_up(string slug, string expected)
        => Assert.Equal(expected, Readable(slug));

    [Theory]
    [InlineData("meal_prep", "Meal prep")]
    [InlineData("widget-caf37865", "Widget caf37865")]
    public void Underscores_read_the_same_way_and_digits_are_left_alone(string slug, string expected)
        => Assert.Equal(expected, Readable(slug));

    [Theory]
    [InlineData("coffee", "Coffee")]
    [InlineData("Coffee", "Coffee")]
    public void Something_with_no_separator_only_gains_a_capital(string slug, string expected)
        => Assert.Equal(expected, Readable(slug));

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("-", "")]
    public void Nothing_in_means_nothing_out_rather_than_an_exception(string? slug, string expected)
        => Assert.Equal(expected, Readable(slug));

    [Fact]
    public void Only_the_first_word_is_capitalised()
    {
        // Sentence case, not title case, because that is how every other name in this system reads — a project is
        // "Selling the car", never "Selling The Car".
        Assert.Equal("Selling the car and the van", Readable("selling-the-car-and-the-van"));
    }

    [Fact]
    public void Already_capitalised_words_are_not_flattened()
    {
        // It only ever raises the first character. Lower-casing the rest would turn a name carrying real capitals into
        // mush, and a slug can hold them.
        Assert.Equal("BA2604 ticket", Readable("BA2604-ticket"));
    }
}
