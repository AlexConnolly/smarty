using System.Text.RegularExpressions;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// A panel's design lives in four files in two languages, and nothing was checking that they agree.
///
/// <para>
/// The kit is in <c>widgetKit.tsx</c>; the list of it a builder is shown is in <c>WidgetTools.cs</c>; the Tailwind
/// classes a generated panel may use are in that same brief AND in <c>tailwind.config.js</c>; the render harness
/// keeps its own copy of the scope. Every one of those pairings fails silently when it drifts. A component the kit
/// exports and the brief never mentions is a component nothing will ever use — there is no import for a model to
/// read, so the brief is the only way it exists. A class in the brief and not the safelist renders unstyled with
/// nothing in any log.
/// </para>
/// <para>
/// This is not a hypothetical family of bug. <c>widget_design</c> shipped advertised and untrimmed for a week;
/// <c>same_thing</c> was offered with no dispatch behind it; a panel used <c>tabular-nums</c> before it was
/// safelisted and the columns quietly didn't line up. Each was found by a person looking at the result.
/// </para>
/// </summary>
public class TheKitAndTheBriefAgreeTests
{
    private static string Root()
    {
        var here = new DirectoryInfo(Path.GetDirectoryName(typeof(TheKitAndTheBriefAgreeTests).Assembly.Location)!);
        while (here is not null && !File.Exists(Path.Combine(here.FullName, "Smarty.sln"))) here = here.Parent;
        Assert.NotNull(here);
        return here!.FullName;
    }

    private static string Read(params string[] parts)
    {
        var path = Path.Combine(new[] { Root() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"couldn't find {path}");
        return File.ReadAllText(path);
    }

    /// <summary>What the kit actually exports, read off the KIT object the runtime spreads into scope.</summary>
    private static List<string> Kit()
    {
        var source = Read("Smarty.Chat", "src", "widgetKit.tsx");
        var block = Regex.Match(source, @"export const KIT = \{(?<body>[^}]*)\}", RegexOptions.Singleline);
        Assert.True(block.Success, "couldn't find the KIT export in widgetKit.tsx");

        return block.Groups["body"].Value
            .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();
    }

    private static Widget Panel() => new() { Id = "abc12345", Title = "Something", Size = WidgetSizes.Wide };

    /// <summary>The components a brief names, as a builder reads them: a capitalised name taking one object.</summary>
    private static List<string> Advertised(string brief) =>
        Regex.Matches(brief, @"(?m)^ {4}([A-Z]\w*)\(\{")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

    private static List<string> Advertised() => Advertised(WidgetTools.Brief(Panel(), "anything at all"));

    [Fact]
    public void Every_component_the_kit_exports_is_named_in_the_brief()
    {
        // The only way a component exists to a builder. Unlisted, it is dead code that took a design decision with
        // it — which is how a picture grid can be sitting in the kit while panels render photographs as thumbnails.
        var advertised = Advertised();
        var missing = Kit().Where(name => !advertised.Contains(name)).ToList();

        Assert.True(missing.Count == 0,
            $"{string.Join(", ", missing)} — in the kit and not in the builder's brief, so nothing will ever use " +
            "them. There is no import for a generated component to read; the brief is the whole vocabulary.");
    }

    [Fact]
    public void Every_component_the_brief_names_is_one_the_kit_has()
    {
        // The other direction is worse, because it fails at render rather than not at all: a component that reaches
        // for a name the scope doesn't have gets undefined, and React throws on it.
        var kit = Kit();
        var invented = Advertised().Where(name => !kit.Contains(name)).ToList();

        Assert.True(invented.Count == 0,
            $"The brief offers {string.Join(", ", invented)}, which the kit doesn't export. A panel that used one " +
            "would render undefined and throw.");
    }

    [Fact]
    public void An_adjustment_is_shown_the_kit_as_well_as_a_build()
    {
        // The job MOST likely to need a component the original build didn't use, and it was the one job the list was
        // withheld from: the brief told it the kit was "unchanged from when this component was written" and never
        // said what was in it. So "make the pictures bigger" was answered by editing hand-rolled markup, because
        // nothing in front of it said a picture grid existed.
        var adjusted = WidgetTools.AdjustBrief(Panel(), null, "make the pictures bigger");
        var missing = Kit().Where(name => !Advertised(adjusted).Contains(name)).ToList();

        Assert.True(missing.Count == 0,
            $"An adjustment isn't shown {string.Join(", ", missing)}, so it cannot use them however well they fit " +
            "what was asked for.");
    }

    [Fact]
    public void The_render_harness_knows_every_component_too()
    {
        // check-widgets.mjs compiles every live panel outside a browser, and it keeps its own copy of the scope. A
        // name missing there doesn't break the app — it breaks the check that would have caught the app breaking.
        var harness = Read("Smarty.Chat", "scripts", "check-widgets.mjs");
        var missing = Kit().Where(name => !Regex.IsMatch(harness, $@"'{name}'")).ToList();

        Assert.True(missing.Count == 0,
            $"{string.Join(", ", missing)} — the kit exports them and the render harness doesn't know them, so its " +
            "verdict on any panel that uses one is meaningless.");
    }

    [Fact]
    public void Every_class_the_brief_allows_is_in_the_compiled_stylesheet()
    {
        // The silent one. Tailwind emits what it finds in the source at build time, so a class first used inside a
        // component written an hour ago is not in the CSS at all: the element renders unstyled and nothing says so.
        // The brief is the promise and the safelist is what keeps it.
        // Comments stripped BEFORE the quotes are read. An apostrophe in prose — "the times didn't align" — pairs up
        // with the next real quote and shifts every string after it by one, so a naive read of the file reports the
        // gaps between the class names as the class names. Which is a fine demonstration of why this test exists.
        var config = Regex.Replace(Read("Smarty.Chat", "tailwind.config.js"), @"//[^\n]*", "");
        var safelisted = Regex.Matches(config, @"'([^'\n]+)'")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = WidgetTools.PaletteClasses.Where(cls => !safelisted.Contains(cls)).OrderBy(c => c).ToList();

        Assert.True(missing.Count == 0,
            $"The builder's brief offers {string.Join(" ", missing)} and tailwind.config.js doesn't safelist " +
            "them, so a panel that uses one renders unstyled with nothing in any log.");
    }

    [Fact]
    public void A_picture_can_be_asked_to_be_bigger_than_an_icon()
    {
        // The rule this is really about, stated as the thing that broke: the palette is a closed list, so its largest
        // size IS the largest picture any panel can ever show. It stopped at h-8, and a panel of 640px photographs
        // rendered them 32px tall — the model wrote the biggest image the tooling would allow, and the brief
        // telling it to make the picture the panel could not have been obeyed.
        var palette = WidgetTools.PaletteClasses;

        Assert.Contains("aspect-square", palette);
        Assert.Contains("auto-rows-fr", palette);
        Assert.True(palette.Contains("h-16") && palette.Contains("h-24"),
            "the palette has no picture-sized heights in it, so every picture in every panel will be icon-sized " +
            "whatever the brief says");
    }
}
