using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Smarty.Agents;

/// <summary>
/// Turns a file on disk into readable plain text — the single place that knows WHICH formats Smarty can
/// read. The strategy is deliberately "be smart, not exhaustive": handle the formats that need real parsing
/// (PDF today; Word/images slot in later), fall back to sniffing the bytes for anything text-shaped (so any
/// code/config/markup file just works without enumerating every extension), and otherwise say plainly that
/// the format isn't supported yet — never throw, never hand back garbage.
/// </summary>
public static class FileText
{
    /// <summary>The outcome of extraction: the text (when <paramref name="Ok"/>), or a friendly reason it
    /// couldn't be read (a missing file, or a format not supported yet).</summary>
    public readonly record struct ExtractResult(string Text, bool Ok, string? Reason);

    private static ExtractResult Fail(string reason) => new("", false, reason);
    private static ExtractResult Success(string text) => new(text, true, null);

    /// <summary>Extract readable text from a file. Best-effort and total — any failure comes back as a
    /// readable reason in <see cref="ExtractResult.Reason"/>, never an exception.</summary>
    public static ExtractResult Extract(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Fail("No file path was given.");
        if (!File.Exists(path)) return Fail($"There's no file at '{path}'.");

        string ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            switch (ext)
            {
                case ".pdf":
                    return ExtractPdf(path);

                case ".html":
                case ".htm":
                    return Success(WebSearcherTool.ToPlainText(File.ReadAllText(path)));

                // Known binaries we can't read yet — say so clearly instead of sniffing them into noise.
                case ".docx":
                case ".doc":
                case ".pptx":
                case ".ppt":
                case ".xlsx":
                case ".xls":
                    return Fail($"I can't read {ext} files yet — text-based files and PDFs are supported so far.");
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".gif":
                case ".webp":
                case ".bmp":
                case ".tiff":
                    return Fail($"That's an image ({ext}); reading images isn't supported yet — text-based files and PDFs are.");

                default:
                    return ExtractTextLike(path);
            }
        }
        catch (Exception ex)
        {
            return Fail($"Couldn't read '{Path.GetFileName(path)}': {ex.Message}");
        }
    }

    private static ExtractResult ExtractPdf(string path)
    {
        using var doc = PdfDocument.Open(path);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages())
        {
            // ContentOrderTextExtractor reconstructs reading order (and spacing) far better than the raw
            // page.Text; fall back to page.Text if it can't for a given page.
            string pageText;
            try { pageText = ContentOrderTextExtractor.GetText(page); }
            catch { pageText = page.Text; }
            if (!string.IsNullOrWhiteSpace(pageText)) sb.Append(pageText.Trim()).Append("\n\n");
        }
        var text = sb.ToString().Trim();
        return text.Length > 0
            ? Success(text)
            : Fail("That PDF has no extractable text (it may be scanned images — that needs OCR, which isn't supported yet).");
    }

    // For anything not a recognised binary: read the bytes and decide whether they're text. Real documents
    // (code, csv, json, yaml, logs, markdown…) decode cleanly as UTF-8 with very few control bytes; a binary
    // file is full of NULs and other control characters. This lets arbitrary text formats "just work" without
    // a hard-coded extension list, while still rejecting binaries we don't have a parser for.
    private static ExtractResult ExtractTextLike(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0) return Success(""); // an empty file is legitimately empty, not unreadable

        // A UTF-16/UTF-8 BOM is a strong "this is text" signal; otherwise sniff a leading sample.
        bool hasBom = bytes.Length >= 2 &&
            ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF) ||
             (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF));

        if (!hasBom)
        {
            int sample = Math.Min(bytes.Length, 8000);
            int control = 0;
            for (int i = 0; i < sample; i++)
            {
                byte b = bytes[i];
                if (b == 0) return Fail(BinaryReason(path));            // a NUL byte → definitely binary
                if (b < 0x09 || (b > 0x0D && b < 0x20)) control++;      // control chars outside tab/CR/LF
            }
            if ((double)control / sample > 0.02) return Fail(BinaryReason(path));
        }

        // StreamReader honours the BOM and otherwise decodes as UTF-8 — good enough for the text formats we
        // expect; a stray bad byte becomes a replacement char rather than failing the read.
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return Success(reader.ReadToEnd());
    }

    private static string BinaryReason(string path)
    {
        string ext = Path.GetExtension(path);
        string kind = string.IsNullOrEmpty(ext) ? "That file" : $"That looks like a binary {ext} file";
        return $"{kind} — I can't read it as text. Text-based files and PDFs are supported so far.";
    }
}
