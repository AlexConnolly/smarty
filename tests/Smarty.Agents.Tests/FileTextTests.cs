using System.Text;
using Smarty.Agents;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>Covers the file text-extraction registry: text formats just work (sniffed, no extension list),
/// real binaries are rejected with a friendly reason, unsupported-but-known types say so, and PDFs are parsed.</summary>
public sealed class FileTextTests
{
    private static string Temp(string name, byte[] bytes)
    {
        string dir = Path.Combine(Path.GetTempPath(), "smarty-filetext-tests");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Theory]
    [InlineData("notes.txt", "Hello Smarty, this is plain text.")]
    [InlineData("data.json", "{\"name\":\"smarty\",\"ok\":true}")]
    [InlineData("readme.md", "# Title\n\nSome **markdown** body.")]
    [InlineData("script.unknownext", "print('arbitrary code file with no known extension')")]
    public void Extracts_text_based_files(string name, string content)
    {
        var r = FileText.Extract(Temp(name, Encoding.UTF8.GetBytes(content)));
        Assert.True(r.Ok, r.Reason);
        Assert.Contains(content.Split('\n')[0].TrimStart('#', ' '), r.Text);
    }

    [Fact]
    public void Rejects_binary_files_as_not_text()
    {
        // NUL bytes + control chars → unmistakably binary.
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0x00, 0x7F, 0x03 };
        var r = FileText.Extract(Temp("blob.bin", bytes));
        Assert.False(r.Ok);
        Assert.NotNull(r.Reason);
    }

    [Fact]
    public void Reports_known_unsupported_formats_clearly()
    {
        var r = FileText.Extract(Temp("doc.docx", new byte[] { 0x50, 0x4B, 0x03, 0x04 })); // a zip header
        Assert.False(r.Ok);
        Assert.Contains("docx", r.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_file_returns_friendly_reason()
    {
        var r = FileText.Extract(Path.Combine(Path.GetTempPath(), "definitely-not-here-42.txt"));
        Assert.False(r.Ok);
        Assert.NotNull(r.Reason);
    }

    [Fact]
    public void Extracts_text_from_a_pdf()
    {
        const string phrase = "Hello Smarty PDF";
        var r = FileText.Extract(Temp("doc.pdf", MakePdf(phrase)));
        Assert.True(r.Ok, r.Reason);
        // PdfPig may or may not preserve the single spaces; check the words survive.
        Assert.Contains("Smarty", r.Text);
        Assert.Contains("PDF", r.Text);
    }

    // A hand-built, minimal but valid single-page PDF with one line of text. Byte offsets for the xref table
    // are computed from the actual content (all-ASCII, so char length == byte length), so PdfPig parses it
    // without having to rebuild a broken cross-reference.
    private static byte[] MakePdf(string text)
    {
        string content = $"BT /F1 24 Tf 72 700 Td ({text}) Tj ET";
        var objs = new[]
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 4 0 R>>>>/Contents 5 0 R>>",
            "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
            $"<</Length {content.Length}>>\nstream\n{content}\nendstream",
        };

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objs.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
        }
        int xrefPos = sb.Length;
        sb.Append($"xref\n0 {objs.Length + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets) sb.Append(off.ToString("D10") + " 00000 n \n");
        sb.Append($"trailer\n<</Size {objs.Length + 1}/Root 1 0 R>>\nstartxref\n{xrefPos}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
