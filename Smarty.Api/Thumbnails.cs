using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Smarty.Api;

/// <summary>
/// A picture of a document, so a file list is something you can look at.
///
/// <para>
/// The project view already showed images as themselves and decks as a live scaled-down render, which is the
/// right instinct — a picture of the thing beats its name. Everything else fell through to a grey tile with
/// "PDF" written on it, and a shelf of those tells you nothing: which profile, which brochure, which draft. The
/// one file the user went looking for this morning was a PDF, and it looked identical to every other PDF.
/// </para>
/// <para>
/// PDFs render their first page. Documents and text files have their text drawn onto a page, so a markdown note
/// or a spreadsheet still looks like the document it is. Images never come here — the browser shows those
/// directly, at no cost.
/// </para>
/// <para>
/// Ported from the same approach in autocoder, which has been running long enough to prove it. Rendering is done
/// by the Python that is already validated at startup with PyMuPDF; results are cached and only redrawn when the
/// source is newer. A failure is never fatal: the tile falls back to the label it used to show.
/// </para>
/// </summary>
public static class Thumbnails
{
    /// <summary>What can be drawn. Images are absent on purpose — they are their own thumbnail.</summary>
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
        { "pdf", "html", "htm", "docx", "xlsx", "md", "markdown", "txt", "csv", "tsv", "json", "log" };

    private static bool IsHtml(string name) =>
        name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

    public static bool CanThumbnail(string name) =>
        Supported.Contains(Path.GetExtension(name).TrimStart('.'));

    /// <summary>
    /// Where a rendered thumbnail is kept: a folder of its own, NOT beside the file. The project shelf is listed
    /// by reading its directory, so a sibling .png would show up as a file the project produced.
    /// </summary>
    private static string CachePathFor(string workspaceRoot, string scope, string name)
    {
        var dir = Path.Combine(workspaceRoot, "_thumbs", Safe(scope));
        return Path.Combine(dir, Safe(name) + ".png");
    }

    private static string Safe(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>
    /// The cached thumbnail for a file, drawing it if needed. Null when the type isn't drawable, the source is
    /// gone, or the render failed — all of which the caller shows as the old typed placeholder.
    /// </summary>
    public static async Task<string?> GetOrCreateAsync(
        string workspaceRoot, string scope, string sourcePath, string name, CancellationToken ct = default)
    {
        if (!CanThumbnail(name) || !File.Exists(sourcePath)) return null;

        var outPath = CachePathFor(workspaceRoot, scope, name);
        if (File.Exists(outPath) && File.GetLastWriteTimeUtc(outPath) >= File.GetLastWriteTimeUtc(sourcePath))
            return outPath;

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        // A generated page is best rendered by the thing it was written for. PyMuPDF can lay out simple HTML and
        // makes a mess of anything with real CSS — a deck came back as a text dump, and once given its own
        // stylesheet, as a solid green rectangle. Chrome renders it exactly as the user will see it, headless and
        // offscreen so nothing appears in front of them. Screenshot once, cache, done.
        if (IsHtml(name))
            return await ShotOfPageAsync(sourcePath, outPath, ct).ConfigureAwait(false);

        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        var spec = new JsonObject { ["in"] = sourcePath, ["out"] = outPath, ["ext"] = ext };
        var scriptPath = Path.Combine(Path.GetTempPath(), $"_smarty_thumb_{Guid.NewGuid():N}.py");

        try
        {
            await File.WriteAllTextAsync(scriptPath, Script, ct).ConfigureAwait(false);
            var psi = new ProcessStartInfo(DataScienceCapability.PythonCommand, $"\"{scriptPath}\"")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var p = Process.Start(psi);
            if (p is null) return null;

            await p.StandardInput.WriteAsync(spec.ToJsonString()).ConfigureAwait(false);
            p.StandardInput.Close();
            var stderr = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);

            if (p.ExitCode != 0)
            {
                Console.Error.WriteLine($"[thumb] {name} failed: {stderr.Trim()}");
                return null;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[thumb] {name} errored: {ex.Message}");
            return null;
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* best effort */ }
        }

        return File.Exists(outPath) ? outPath : null;
    }

    /// <summary>
    /// A page, photographed by a real browser.
    ///
    /// <para>
    /// Headless, offscreen, its own throwaway profile, no sandbox surprises — it never touches the Chrome the
    /// user is looking at. <c>--virtual-time-budget</c> lets webfonts and any layout script settle before the
    /// shutter, which is the difference between a rendered deck and a flash of unstyled text.
    /// </para>
    /// <para>
    /// Shot at 900×1200 and kept whole: a tile crops it to the top, which is where a title slide is.
    /// </para>
    /// </summary>
    private static Task<string?> ShotOfPageAsync(string sourcePath, string outPath, CancellationToken ct) =>
        RunChromeAsync(sourcePath, outPath, ct,
            "--hide-scrollbars", "--window-size=900,1200", $"--screenshot=\"{outPath}\"");

    /// <summary>
    /// The whole page as a PDF, printed by the browser it was written for.
    ///
    /// <para>
    /// "Save as PDF" used to call print() on the deck's iframe and hand the user the browser's print dialog.
    /// The page's own CSS is correct — A4, zero margin, a hard break after every <c>.page</c> — and printed
    /// headlessly it produces all nine pages. Through the dialog it did not: an iframe print inherits whatever
    /// scale, margins and background-graphics setting that browser happens to have, and pages went missing.
    /// </para>
    /// <para>
    /// Doing it here makes the export deterministic — the same nine pages for everyone, backgrounds included,
    /// no checkbox to forget — and it works from anywhere a file is listed, not only from inside the presenter.
    /// </para>
    /// </summary>
    public static async Task<string?> PdfOfPageAsync(
        string workspaceRoot, string scope, string sourcePath, string name, CancellationToken ct = default)
    {
        if (!IsHtml(name) || !File.Exists(sourcePath)) return null;

        var outPath = Path.Combine(
            Path.GetDirectoryName(CachePathFor(workspaceRoot, scope, name))!,
            Safe(Path.GetFileNameWithoutExtension(name)) + ".pdf");

        if (File.Exists(outPath) && File.GetLastWriteTimeUtc(outPath) >= File.GetLastWriteTimeUtc(sourcePath))
            return outPath;

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        return await RunChromeAsync(sourcePath, outPath, ct,
            $"--print-to-pdf=\"{outPath}\"", "--no-pdf-header-footer").ConfigureAwait(false);
    }

    /// <summary>One headless run against a local file, with the flags that keep it out of the user's way.</summary>
    private static async Task<string?> RunChromeAsync(
        string sourcePath, string outPath, CancellationToken ct, params string[] extra)
    {
        if (ChromePath() is not { } chrome) return null;

        var profile = Path.Combine(Path.GetTempPath(), $"_smarty_shot_{Guid.NewGuid():N}");
        var args = string.Join(' ', new[]
        {
            "--headless", "--disable-gpu", "--no-sandbox",
            "--no-first-run", "--no-default-browser-check", "--disable-extensions",
            $"--user-data-dir=\"{profile}\"",
            "--virtual-time-budget=5000",
        }.Concat(extra).Append($"\"file:///{sourcePath.Replace('\\', '/')}\""));

        try
        {
            var psi = new ProcessStartInfo(chrome, args)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;

            // A page with a runaway script must not hold a request open forever.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            try { await p.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { } }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[thumb] chrome shot failed: {ex.Message}");
            return null;
        }
        finally
        {
            try { Directory.Delete(profile, recursive: true); } catch { /* best effort */ }
        }

        return File.Exists(outPath) ? outPath : null;
    }

    private static string? _chrome;

    /// <summary>Where Chrome is, for anything that needs to render a page headlessly.</summary>
    public static string? ChromeExecutable() => ChromePath();

    private static string? ChromePath()
    {
        if (_chrome is not null) return _chrome.Length == 0 ? null : _chrome;

        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("SMARTY_CHROME"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
            "/usr/bin/google-chrome",
            "/usr/bin/chromium",
        };

        _chrome = candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c)) ?? "";
        return _chrome.Length == 0 ? null : _chrome;
    }

    // Reads {in,out,ext} from stdin. A PDF renders its first page; anything else has its text drawn on one.
    private const string Script = """
import sys, json
import fitz  # pymupdf

spec = json.loads(sys.stdin.read())
src, out, ext = spec["in"], spec["out"], spec["ext"].lower()

def save_pdf_page(path, out):
    doc = fitz.open(path)
    page = doc[0]
    zoom = 480.0 / page.rect.width if page.rect.width else 1.0
    page.get_pixmap(matrix=fitz.Matrix(zoom, zoom)).save(out)

def extract_text(path, ext):
    if ext == "docx":
        import docx
        return "\n".join(p.text for p in docx.Document(path).paragraphs)
    if ext == "xlsx":
        import openpyxl
        ws = openpyxl.load_workbook(path, read_only=True, data_only=True).active
        rows = []
        for i, row in enumerate(ws.iter_rows(values_only=True)):
            if i >= 40: break
            rows.append("  ".join("" if c is None else str(c) for c in row))
        return "\n".join(rows)
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        return f.read()

def markdown_to_html(text):
    # Enough markdown for a thumbnail to look like the document rather than its source. A preview showed
    # the syntax instead of the headings: a literal hash before the title. Deliberately small — no library,
    # no nesting, no tables. A thumbnail is read at a hundred pixels wide, and all that matters is that a
    # heading looks like a heading.
    import html as _html, re
    out = []
    for line in text.split("\n"):
        stripped = line.strip()
        heading = re.match(r"^(#{1,6})\s+(.*)$", stripped)
        if heading:
            level = min(len(heading.group(1)), 3)
            size = {1: 17, 2: 14, 3: 12}[level]
            out.append(f'<div style="font-size:{size}px;font-weight:700;margin:8px 0 3px;">'
                       + inline(_html.escape(heading.group(2))) + "</div>")
            continue
        bullet = re.match(r"^[-*+]\s+(.*)$", stripped)
        if bullet:
            out.append('<div style="margin-left:10px;">• ' + inline(_html.escape(bullet.group(1))) + "</div>")
            continue
        if not stripped:
            out.append('<div style="height:6px;"></div>')
            continue
        out.append("<div>" + inline(_html.escape(stripped)) + "</div>")
    return "".join(out)

def inline(escaped):
    import re
    escaped = re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", escaped)
    escaped = re.sub(r"(?<!\*)\*([^*]+?)\*(?!\*)", r"<i>\1</i>", escaped)
    escaped = re.sub(r"`([^`]+?)`", r'<span style="font-family:monospace;">\1</span>', escaped)
    # A link's text is what a reader wants; its address is noise at thumbnail size.
    escaped = re.sub(r"\[(.+?)\]\((.+?)\)", r"<u>\1</u>", escaped)
    return escaped

def save_text_page(text, out, ext=""):
    import html
    text = (text or "").strip()
    if not text:
        raise SystemExit("empty")
    doc = fitz.open()
    page = doc.new_page(width=480, height=620)
    rect = fitz.Rect(24, 24, 456, 596)
    if ext in ("md", "markdown"):
        body = markdown_to_html(text[:4000])
    else:
        body = html.escape(text[:4000]).replace("\n", "<br>")
    # insert_htmlbox clips overflow and renders the top that fits. insert_textbox draws NOTHING and returns a
    # negative when the text is taller than the box, which silently produces a blank thumbnail.
    htmlbox = getattr(page, "insert_htmlbox", None)
    if htmlbox:
        htmlbox(rect, '<div style="font-family:sans-serif;font-size:11px;line-height:1.4;'
                      'color:#1f1f28;white-space:pre-wrap;">' + body + '</div>')
    else:
        page.insert_textbox(rect, text[:1600], fontname="helv", fontsize=8,
                            color=(0.12, 0.12, 0.15), lineheight=1.3)
    page.get_pixmap(matrix=fitz.Matrix(2, 2)).save(out)

if ext == "pdf":
    save_pdf_page(src, out)
else:
    save_text_page(extract_text(src, ext), out, ext)
print("ok")
""";
}
