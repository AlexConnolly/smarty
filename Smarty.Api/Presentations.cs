using System.Text;
using System.Text.RegularExpressions;
using Smarty.Agents;

namespace Smarty.Api;

/// <summary>
/// Turning findings into something worth looking at.
/// <para>
/// The default split is deliberate: the model writes slides as markdown, this builds the page. Asked for raw HTML
/// throughout, a model spends most of its output on CSS and reinvents a worse layout every time.
/// </para>
/// <para>
/// But a deck that always looks the same is its own failure — a beach hotel and a finance review deserve different
/// typography, and "consistent" shades into "generic". So the model chooses the fonts, the accent and the mood, and
/// may write any individual slide as HTML with Tailwind when that slide needs a layout of its own. Structure stays
/// ours, so paging, printing and keyboard navigation work whatever it does inside a slide; taste is its own.
/// </para>
/// </summary>
public static class Presentations
{
    /// <summary>Slides come in as blocks separated by a rule, the way anyone writing markdown already separates
    /// sections. Inside a slide: a heading line, then bullets, paragraphs, or an image.</summary>
    private const string SlideSeparator = @"^\s*---\s*$";

    public static AgentTool BuildTool(string directory, Func<byte[], string, string?>? images = null) => new(
        "build_presentation",
        "Turn what you've found into a presentation the user can open and page through. Give it a name ending " +
        ".html, a title, and the slides as markdown: separate each slide with a line of ---, start a slide with " +
        "# for its heading, then use - for bullets, plain lines for prose, and ![](url) for a picture. A picture's " +
        "url can point straight at the web — it is fetched and stored for you, so you never need to download " +
        "anything first. Paging, printing and navigation are handled for you. A presentation lives or " +
        "dies on being looked at, so put a real image on any slide that describes a place, a product or a " +
        "person; a deck of bullet lists is a document with extra steps. Use this when the user wants something " +
        "to look at or show someone, rather than an answer to read in the chat.\n" +
        "Which picture matters as much as having one. Read the alt text before you use an image: it usually says " +
        "what the picture is of, and one whose alt never names your subject — or is empty — is probably a banner, " +
        "a logo or somewhere else entirely. Prefer images served from the subject's own site: a hotel's own " +
        "photographs live on the hotel's own domain, while booking and review sites illustrate with stock and " +
        "generic shots. A third-party host is a fallback for when the subject's own site has none, not a " +
        "shortcut. This is about which host served the file and what its alt says, not about which company is " +
        "reputable — a well-known travel agent is perfectly legitimate and still shows you a stock photo of a " +
        "different pool.\n" +
        "Design it for THIS subject. Choose the display font, the body font and the accent colour deliberately: two " +
        "decks on unrelated subjects should not look like each other, and both should look " +
        "like someone made a decision. Leaving them blank gets a safe default, which is the one outcome worth " +
        "avoiding. Where a slide deserves a layout of its own, write THAT slide as HTML rather than markdown — " +
        "start it with a tag — and use Tailwind classes: Tailwind and your chosen fonts are already loaded, so a " +
        "full-bleed photograph with an overlaid line, or a three-column comparison, is a handful of classes and no " +
        "stylesheet. Mix the two freely: markdown for the ordinary slides, HTML for the ones that carry the deck.",
        new[]
        {
            ToolParameter.String("name", "File name, ending .html (e.g. mallorca_resorts.html)."),
            ToolParameter.String("title", "The deck's title, shown on the opening slide."),
            ToolParameter.String("slides", "The slides as markdown, separated by lines containing only ---."),
            ToolParameter.String(
                "font_display",
                "Google font for headings — pick one that suits the subject, not a default. A hotel deck might " +
                "take Playfair Display or Cormorant Garamond; a product update Space Grotesk or Sora; a financial " +
                "review Newsreader. Omit only if you have no view.",
                required: false),
            ToolParameter.String(
                "font_body", "Google font for body text. Pair it deliberately with the heading font.", required: false),
            ToolParameter.String(
                "accent",
                "Accent colour as a hex value, drawn from the subject — the sea for a coastal hotel, a brand's own " +
                "colour for a company. Not blue by default.",
                required: false),
            ToolParameter.String(
                "mood",
                "Either \"light\" or \"dark\". Dark suits a deck built around photographs; light suits one built " +
                "around text and numbers.",
                required: false),
        },
        async (args, ct) =>
        {
            var name = args.GetStringOrNull("name")?.Trim();
            var title = args.GetStringOrNull("title")?.Trim();
            var slides = args.GetStringOrNull("slides");

            if (string.IsNullOrWhiteSpace(name)) return ToolOutput.Error("A presentation needs a file name.");
            if (string.IsNullOrWhiteSpace(slides)) return ToolOutput.Error("A presentation needs slides.");

            name = Path.GetFileName(name);
            if (!name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) name += ".html";

            try
            {
                Directory.CreateDirectory(directory);
                var look = new Look(
                    args.GetStringOrNull("font_display"),
                    args.GetStringOrNull("font_body"),
                    args.GetStringOrNull("accent"),
                    args.GetStringOrNull("mood"));
                var html = Render(title ?? Path.GetFileNameWithoutExtension(name), slides!, images, look);
                await File.WriteAllTextAsync(Path.Combine(directory, name), html, ct).ConfigureAwait(false);

                int count = Regex.Split(slides!, SlideSeparator, RegexOptions.Multiline)
                    .Count(s => !string.IsNullOrWhiteSpace(s));
                int pictures = Regex.Matches(slides!, @"!\[[^\]]*\]\(").Count;
                return ToolOutput.Ok(
                    $"Saved {name}: {count} slide(s), {pictures} image(s). It opens full-screen when the user " +
                    "clicks it. Name it as the deliverable when you finish." +
                    (pictures == 0
                        ? " It has no pictures — if any slide describes something you could show, add one."
                        : ""));
            }
            catch (Exception ex)
            {
                return ToolOutput.Error($"Couldn't write {name}: {ex.Message}");
            }
        });

    /// <summary>Render the deck. One file, so it survives being saved and mailed to someone — though a deck that
    /// chose fonts, or a slide written with Tailwind classes, wants a network the first time it opens.</summary>
    public static string Render(
        string title, string slides, Func<byte[], string, string?>? images = null, Look? look = null)
    {
        var blocks = Regex.Split(slides, SlideSeparator, RegexOptions.Multiline)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .ToList();

        var body = new StringBuilder();
        body.Append($"<section class=\"cover\"><h1>{Escape(title)}</h1></section>\n");
        foreach (var block in blocks) body.Append(Slide(block, images));

        return Shell(title, body.ToString(), blocks.Count + 1, look ?? new Look());
    }

    /// <summary>A deck's chosen look. Every field optional; each falls back to something plain.</summary>
    public sealed record Look(string? Display = null, string? Body = null, string? Accent = null, string? Mood = null)
    {
        public bool Dark => string.Equals(Mood, "dark", StringComparison.OrdinalIgnoreCase);

        /// <summary>The Google Fonts request for whatever was chosen — nothing at all when nothing was.</summary>
        public string FontLink()
        {
            var families = new[] { Display, Body }
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(f => "family=" + Uri.EscapeDataString(f).Replace("%20", "+") + ":wght@400;500;600;700")
                .ToList();

            return families.Count == 0
                ? ""
                : $"<link rel=\"preconnect\" href=\"https://fonts.googleapis.com\">\n" +
                  $"<link rel=\"preconnect\" href=\"https://fonts.gstatic.com\" crossorigin>\n" +
                  $"<link href=\"https://fonts.googleapis.com/css2?{string.Join("&", families)}&display=swap\" rel=\"stylesheet\">";
        }

        public string DisplayStack() => Quote(Display) + "ui-sans-serif, system-ui, sans-serif";

        public string BodyStack() => Quote(Body) + "ui-sans-serif, system-ui, sans-serif";

        private static string Quote(string? font) =>
            string.IsNullOrWhiteSpace(font) ? "" : $"'{font.Trim().Replace("'", "")}', ";

        /// <summary>A hex accent, or the default. Validated rather than trusted: this goes straight into CSS.</summary>
        public string AccentColour(bool dark) =>
            Accent is { Length: > 0 } a && Regex.IsMatch(a.Trim(), "^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")
                ? a.Trim()
                : dark ? "#7aa2ff" : "#2f6df6";
    }

    private static string Slide(string block, Func<byte[], string, string?>? images)
    {
        // A slide written as HTML is passed through untouched: it was asked for by a model that decided this one
        // needed a layout of its own, and second-guessing that would defeat the point. Tailwind is loaded, so what
        // arrives is classes rather than a stylesheet — and it still lands inside the same section, so paging,
        // printing and keyboard navigation work exactly as they do for a markdown slide.
        var trimmed = block.Trim();
        if (trimmed.StartsWith('<'))
            return $"<section class=\"slide custom\">{trimmed}</section>\n";

        var body = new StringBuilder();
        var bullets = new List<string>();
        string? heading = null;
        string? figure = null;

        void FlushBullets()
        {
            if (bullets.Count == 0) return;
            body.Append("<ul>");
            foreach (var b in bullets) body.Append($"<li>{Inline(b)}</li>");
            body.Append("</ul>");
            bullets.Clear();
        }

        foreach (var raw in block.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var image = Regex.Match(line, @"^!\[([^\]]*)\]\(([^)\s]+)\)$");
            if (image.Success)
            {
                FlushBullets();
                var src = Mirror(image.Groups[2].Value, images);
                figure ??= $"<figure><img src=\"{Escape(src)}\" alt=\"{Escape(image.Groups[1].Value)}\"></figure>";
                continue;
            }

            if (line.StartsWith("#"))
            {
                FlushBullets();
                heading ??= line.TrimStart('#').Trim();
                continue;
            }

            if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                bullets.Add(line[2..].Trim());
                continue;
            }

            FlushBullets();
            body.Append($"<p>{Inline(line)}</p>");
        }
        FlushBullets();

        // A slide with a picture splits; one without gives its text the whole stage.
        var cls = figure is null ? "slide" : "slide split";
        var headingHtml = heading is null ? "" : $"<h2>{Escape(heading)}</h2>";
        return $"<section class=\"{cls}\">{headingHtml}<div class=\"body\">{body}</div>{figure}</section>\n";
    }

    /// <summary>Pull a remote picture local, for the same reason a link card does: a hotlink is refused often
    /// enough to be the normal case, and a deck with broken images is worse than no deck.</summary>
    private static string Mirror(string src, Func<byte[], string, string?>? images)
    {
        if (images is null || !src.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return src;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SmartyBot/1.0 (+presentation)");
            using var response = http.GetAsync(src).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return src;

            var mime = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return src;

            var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            if (bytes.Length is 0 or > 8 * 1024 * 1024) return src;
            return images(bytes, mime) ?? src;
        }
        catch (Exception)
        {
            return src;
        }
    }

    private static string Inline(string text)
    {
        var escaped = Escape(text);
        escaped = Regex.Replace(escaped, @"\*\*([^*]+)\*\*", "<strong>$1</strong>");
        escaped = Regex.Replace(escaped, @"(?<!\*)\*([^*]+)\*(?!\*)", "<em>$1</em>");
        return Regex.Replace(escaped, @"\[([^\]]+)\]\((https?://[^)\s]+)\)",
            "<a href=\"$2\" target=\"_blank\" rel=\"noreferrer noopener\">$1</a>");
    }

    private static string Escape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string Shell(string title, string body, int count, Look look) =>
        $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>{{Escape(title)}}</title>
        {{look.FontLink()}}
        <script src="https://cdn.tailwindcss.com"></script>
        <style>
          /*
           * The chosen look, as variables the markdown slides use and the HTML slides can reach for. Tailwind is
           * loaded above so a slide written as markup gets utilities without a build step — the trade is that a
           * themed deck wants a network, where a plain one never did. Worth it: a deck nobody wants to look at is
           * a worse outcome than a deck that needs Wi-Fi.
           */
          :root {
            --ink: {{(look.Dark ? "#eef1f5" : "#14161a")}};
            --ink-soft: {{(look.Dark ? "#a8b0bd" : "#454b55")}};
            --line: {{(look.Dark ? "#2a2f38" : "#e3e6ea")}};
            --bg: {{(look.Dark ? "#0f1115" : "#ffffff")}};
            --surface: {{(look.Dark ? "#171a20" : "#f6f7f9")}};
            --accent: {{look.AccentColour(look.Dark)}};
            --display: {{look.DisplayStack()}};
            --body: {{look.BodyStack()}};
          }
          * { box-sizing: border-box; }
          html, body { margin: 0; height: 100%; background: var(--bg); color: var(--ink); }
          body {
            font: 400 18px/1.55 var(--body);
            -webkit-font-smoothing: antialiased;
          }
          h1, h2, h3 { font-family: var(--display); }
          /* A bespoke slide gets the stage and nothing else: no padding, no centring, no opinions. */
          section.custom { padding: 0; display: none; }
          section.custom.on { display: block; overflow: auto; }
          section {
            display: none; height: 100vh; padding: 7vh 8vw; flex-direction: column; justify-content: center;
            animation: rise .28s ease both;
          }
          section.on { display: flex; }
          @keyframes rise { from { opacity: 0; transform: translateY(10px); } to { opacity: 1; transform: none; } }
          .cover { align-items: flex-start; background: var(--surface); }
          .cover h1 {
            margin: 0; font-size: clamp(34px, 6vw, 76px); line-height: 1.05; letter-spacing: -.02em;
            max-width: 22ch; font-weight: 650;
          }
          h2 {
            margin: 0 0 3vh; font-size: clamp(24px, 3.4vw, 44px); line-height: 1.15; letter-spacing: -.015em;
            font-weight: 620; max-width: 30ch;
          }
          .slide.split { display: none; }
          .slide.split.on { display: grid; grid-template-columns: 1.05fr .95fr; gap: 5vw; align-items: center; }
          .slide.split.on h2 { grid-column: 1 / -1; margin-bottom: 0; }
          ul { margin: 0; padding-left: 1.15em; }
          li { margin: 0 0 .7em; max-width: 46ch; }
          p { margin: 0 0 .8em; max-width: 60ch; color: var(--ink-soft); }
          strong { color: var(--ink); font-weight: 640; }
          a { color: var(--accent); }
          figure { margin: 0; }
          figure img {
            width: 100%; max-height: 66vh; object-fit: cover; border-radius: 14px;
            border: 1px solid var(--line); display: block;
          }
          nav {
            position: fixed; bottom: 18px; right: 22px; display: flex; gap: 10px; align-items: center;
            font-size: 13px; color: var(--ink-soft); user-select: none;
          }
          nav button {
            font: inherit; color: inherit; background: var(--surface); border: 1px solid var(--line);
            border-radius: 8px; padding: 4px 11px; cursor: pointer;
          }
          nav button:hover { border-color: var(--accent); color: var(--ink); }
          @media (max-width: 820px) {
            .slide.split.on { grid-template-columns: 1fr; gap: 3vh; }
            figure img { max-height: 34vh; }
          }
          @media print {
            section, section.on, .slide.split.on { display: block; height: auto; page-break-after: always; animation: none; }
            nav { display: none; }
          }
        </style>
        </head>
        <body>
        {{body}}
        <nav><button data-go="-1">←</button><span id="at">1 / {{count}}</span><button data-go="1">→</button></nav>
        <script>
          const slides = [...document.querySelectorAll('section')];
          let at = 0;
          const show = (n) => {
            at = Math.max(0, Math.min(slides.length - 1, n));
            slides.forEach((s, i) => s.classList.toggle('on', i === at));
            document.getElementById('at').textContent = (at + 1) + ' / ' + slides.length;
          };
          document.addEventListener('keydown', (e) => {
            if (e.key === 'ArrowRight' || e.key === 'PageDown' || e.key === ' ') { show(at + 1); e.preventDefault(); }
            if (e.key === 'ArrowLeft' || e.key === 'PageUp') { show(at - 1); e.preventDefault(); }
            if (e.key === 'Home') show(0);
            if (e.key === 'End') show(slides.length - 1);
          });
          document.querySelectorAll('nav button').forEach((b) =>
            b.addEventListener('click', () => show(at + Number(b.dataset.go))));
          // Clicking the slide itself advances, the way a remote does; the nav and links are exempt.
          document.addEventListener('click', (e) => {
            if (e.target.closest('nav') || e.target.closest('a')) return;
            show(at + 1);
          });
          show(0);
        </script>
        </body>
        </html>
        """;
}
