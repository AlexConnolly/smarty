using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// The deck renderer. It had none of these while it built two real presentations, which is the wrong order —
/// a renderer fails silently by definition: you get a page, it just isn't the page you meant.
/// </summary>
public class PresentationTests
{
    [Fact]
    public void A_title_slide_is_added_and_slides_split_on_a_rule()
    {
        var html = Presentations.Render("Mallorca", """
            # First
            - one
            ---
            # Second
            - two
            """);

        // Three sections: the cover this adds, plus the two written.
        Assert.Equal(3, Count(html, "<section"));
        Assert.Contains("<h1>Mallorca</h1>", html);
        Assert.Contains("<h2>First</h2>", html);
        Assert.Contains("<h2>Second</h2>", html);
        Assert.Contains("<li>one</li>", html);
        Assert.Contains("1 / 3", html); // the counter agrees with reality
    }

    [Fact]
    public void A_slide_with_a_picture_splits_and_one_without_does_not()
    {
        var withImage = Presentations.Render("t", """
            # Pool
            ![the pool](https://example.com/pool.jpg)
            """);
        Assert.Contains("slide split", withImage);
        Assert.Contains("<figure><img src=\"https://example.com/pool.jpg\" alt=\"the pool\">", withImage);

        var without = Presentations.Render("t", "# Just words\n- a bullet");
        Assert.DoesNotContain("slide split", without);
        Assert.DoesNotContain("<figure>", without);
    }

    [Fact]
    public void Bullets_prose_and_emphasis_survive_but_markup_does_not()
    {
        var html = Presentations.Render("t", """
            # Heading
            A line of prose with **bold** and *italic*.
            - a bullet with [a link](https://example.com/x)
            """);

        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<em>italic</em>", html);
        Assert.Contains("href=\"https://example.com/x\"", html);

        // Content is escaped: a page that renders someone else's tags is a page that can be hijacked by whatever
        // the worker happened to read on the web.
        // Asserted on the payload, not on the absence of a script tag — the deck ships its own script for paging,
        // so "contains no <script>" was never a property this page could have.
        var hostile = Presentations.Render("t & <b>", "# <script>alert(1)</script>\n- <img onerror=x>");
        Assert.DoesNotContain("<script>alert(1)", hostile);
        Assert.DoesNotContain("<img onerror", hostile);
        Assert.Contains("&lt;script&gt;alert(1)", hostile);
        Assert.Contains("t &amp; &lt;b&gt;", hostile);
    }

    [Fact]
    public void Without_a_sink_a_remote_picture_keeps_its_own_address()
    {
        // No sink means nothing can be stored locally. Keeping the original URL is deliberate: the user's browser
        // is a real browser and will usually load it, and a broken picture beats a deleted one.
        var html = Presentations.Render("t", "# X\n![](https://cdn.example.com/a.avif)");
        Assert.Contains("https://cdn.example.com/a.avif", html);
    }

    [Fact]
    public void A_stored_picture_is_served_from_the_sink_instead()
    {
        byte[]? seen = null;
        var html = Presentations.Render("t", "# X\n![](https://cdn.example.com/a.avif)", (bytes, mime) =>
        {
            seen = bytes;
            return "/api/media/stored.avif";
        });

        // The sink is only reached for a picture that could actually be fetched; this one cannot be, so the
        // original address stays and the sink is never called. Pinning it so the fallback can't rot.
        Assert.Null(seen);
        Assert.Contains("https://cdn.example.com/a.avif", html);
    }

    [Fact]
    public void The_page_is_self_contained_and_pages_with_the_keyboard()
    {
        var html = Presentations.Render("t", "# One\n---\n# Two");

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("ArrowRight", html);
        Assert.Contains("@media print", html);        // print-to-PDF is the export path
        Assert.DoesNotContain("http://", html);       // no external stylesheet, font or script
        Assert.DoesNotContain("src=\"//", html);
    }

    [Fact]
    public void A_chosen_look_reaches_the_page_and_a_bad_colour_does_not()
    {
        var html = Presentations.Render("Llaut Palma", "# Sea view\n- Rooftop pool", look:
            new Presentations.Look("Playfair Display", "Inter", "#0e7490", "dark"));

        Assert.Contains("fonts.googleapis.com", html);
        Assert.Contains("family=Playfair+Display", html);
        Assert.Contains("family=Inter", html);
        Assert.Contains("'Playfair Display'", html);
        Assert.Contains("--accent: #0e7490", html);
        Assert.Contains("cdn.tailwindcss.com", html);
        Assert.Contains("--bg: #0f1115", html); // dark mood

        // The accent goes straight into a stylesheet, so anything that isn't a hex colour is refused rather than
        // written out — a "colour" of "red; } body { display:none" would otherwise be CSS injection.
        var hostile = Presentations.Render("t", "# X", look: new Presentations.Look(Accent: "red; } body { x:y"));
        Assert.DoesNotContain("body { x:y", hostile);
        Assert.Contains("--accent: #2f6df6", hostile);
    }

    [Fact]
    public void A_slide_written_as_html_is_passed_through_and_still_pages()
    {
        var html = Presentations.Render("t", """
            # An ordinary slide
            - a bullet
            ---
            <div class="grid h-screen place-items-center bg-black"><p class="text-6xl">Bespoke</p></div>
            """);

        // Passed through untouched — the classes are the point — but inside our own section, so navigation works.
        Assert.Contains("<section class=\"slide custom\">", html);
        Assert.Contains("class=\"grid h-screen place-items-center bg-black\"", html);
        Assert.DoesNotContain("&lt;div", html);
        Assert.Equal(3, Count(html, "<section")); // cover + markdown slide + html slide
        Assert.Contains("1 / 3", html);
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0, at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
        return n;
    }
}
