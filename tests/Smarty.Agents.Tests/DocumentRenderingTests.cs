using Smarty.Agents;
using Smarty.Api;
using Xunit;

namespace Smarty.Agents.Tests;

/// <summary>
/// The structured document renderer: a spec of content blocks renders to a real .docx / .pdf, deterministically.
/// We prove it round-trips — render, then read the text back (docx via FileText's docx reader, pdf via PdfPig) —
/// so the content genuinely lands in the file, plus the spec-parsing guardrails the model relies on to self-correct.
/// </summary>
public sealed class DocumentRenderingTests
{
    private static string OutDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "smarty-docgen-tests");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private const string PlaybookBlocks =
        "[{\"type\":\"heading\",\"level\":1,\"text\":\"Q3 Playbook\"}," +
        "{\"type\":\"paragraph\",\"text\":\"Overview of the quarter.\"}," +
        "{\"type\":\"bullets\",\"items\":[\"First point\",\"Second point\"]}," +
        "{\"type\":\"table\",\"headers\":[\"Metric\",\"Value\"],\"rows\":[[\"Revenue\",\"1.2M\"]]}]";

    [Fact]
    public void Renders_a_docx_whose_content_reads_back()
    {
        var spec = DocumentSpec.Parse("docx", "playbook", PlaybookBlocks, "{\"font\":\"Arial\",\"primaryColor\":\"#1A73E8\"}");
        string path = Path.Combine(OutDir(), spec.Filename);
        DocumentRenderer.Render(spec, path, _ => null);

        Assert.True(File.Exists(path));
        var read = FileText.Extract(path);          // exercises the .docx reader too
        Assert.True(read.Ok, read.Reason);
        Assert.Contains("Q3 Playbook", read.Text);
        Assert.Contains("Overview of the quarter.", read.Text);
        Assert.Contains("First point", read.Text);
        Assert.Contains("Metric", read.Text);
        Assert.Contains("Revenue", read.Text);
    }

    [Fact]
    public void Renders_a_pdf_whose_content_reads_back()
    {
        var spec = DocumentSpec.Parse("pdf", "playbook", PlaybookBlocks, null);
        string path = Path.Combine(OutDir(), spec.Filename);
        DocumentRenderer.Render(spec, path, _ => null);

        Assert.True(File.Exists(path));
        var read = FileText.Extract(path);          // PdfPig text extraction
        Assert.True(read.Ok, read.Reason);
        // PdfPig can shuffle spacing, so assert on words rather than exact phrases.
        Assert.Contains("Playbook", read.Text);
        Assert.Contains("Revenue", read.Text);
    }

    [Fact]
    public void Filename_extension_is_corrected_to_match_format()
    {
        var spec = DocumentSpec.Parse("pdf", "report.docx", PlaybookBlocks, null);
        Assert.Equal("report.pdf", spec.Filename);
    }

    [Theory]
    [InlineData("txt", "x", PlaybookBlocks, "format")]     // unsupported format
    [InlineData("docx", "x", "", "blocks is required")]    // no blocks
    [InlineData("docx", "x", "[]", "empty")]               // empty array
    [InlineData("docx", "x", "{not json", "valid JSON")]   // malformed
    public void Parse_rejects_bad_specs_with_a_readable_reason(string format, string filename, string blocks, string expect)
    {
        var ex = Assert.Throws<ArgumentException>(() => DocumentSpec.Parse(format, filename, blocks, null));
        Assert.Contains(expect, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_missing_image_is_noted_not_fatal_docx()
    {
        var spec = DocumentSpec.Parse("docx", "d", "[{\"type\":\"image\",\"file\":\"nope.png\"}]", null);
        string path = Path.Combine(OutDir(), spec.Filename);
        DocumentRenderer.Render(spec, path, _ => null); // resolver returns null → block noted, no throw

        var read = FileText.Extract(path);
        Assert.True(read.Ok, read.Reason);
        Assert.Contains("nope.png", read.Text);
    }

    [Fact]
    public void An_image_that_resolves_is_embedded_docx()
    {
        // A tiny valid 1x1 PNG so the image path resolves and the probe succeeds.
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        string imgPath = Path.Combine(OutDir(), "logo.png");
        File.WriteAllBytes(imgPath, png);

        var spec = DocumentSpec.Parse("docx", "d", "[{\"type\":\"image\",\"file\":\"logo.png\"}]", null);
        string path = Path.Combine(OutDir(), "img-" + spec.Filename);
        var ex = Record.Exception(() => DocumentRenderer.Render(spec, path, name => name == "logo.png" ? imgPath : null));
        Assert.Null(ex);
        Assert.True(new FileInfo(path).Length > 0);
    }
}
