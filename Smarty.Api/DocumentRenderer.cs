using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using Migra = MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;

namespace Smarty.Api;

/// <summary>One brand token set for a from-scratch document (no template): a body font and a primary colour
/// used for headings. Optional — omitted fields fall back to sensible defaults.</summary>
public sealed record DocStyle(string? Font = null, string? PrimaryColor = null);

/// <summary>One content block in a <see cref="DocumentSpec"/>. A tagged union: <see cref="Type"/> selects which
/// of the other fields matter (heading→Level/Text, bullets→Items, table→Headers/Rows, image→File, …). This is
/// what the model fills in — content and intent — never rendering code.</summary>
public sealed record DocBlock(
    string Type,
    int Level = 1,
    string? Text = null,
    IReadOnlyList<string>? Items = null,
    IReadOnlyList<string>? Headers = null,
    IReadOnlyList<IReadOnlyList<string>>? Rows = null,
    string? File = null);

/// <summary>A whole document to build from scratch (used only when there is NO template — a template document
/// goes through the LLM/run_python path instead). Format + filename + optional brand tokens + ordered blocks.</summary>
public sealed record DocumentSpec(
    string Format,
    string Filename,
    DocStyle? Style,
    IReadOnlyList<DocBlock> Blocks)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Parse the tool's raw arguments (blocks + optional style as JSON) into a spec. Throws
    /// <see cref="ArgumentException"/> with a model-readable message on anything malformed.</summary>
    public static DocumentSpec Parse(string format, string filename, string blocksJson, string? styleJson)
    {
        format = (format ?? "").Trim().ToLowerInvariant();
        if (format is not ("docx" or "pdf"))
            throw new ArgumentException($"format must be \"docx\" or \"pdf\" (got \"{format}\").");
        if (string.IsNullOrWhiteSpace(filename))
            throw new ArgumentException("filename is required.");
        if (string.IsNullOrWhiteSpace(blocksJson))
            throw new ArgumentException("blocks is required — a JSON array of content blocks.");

        List<DocBlock>? blocks;
        try { blocks = JsonSerializer.Deserialize<List<DocBlock>>(blocksJson, JsonOpts); }
        catch (JsonException ex) { throw new ArgumentException($"blocks isn't valid JSON: {ex.Message}"); }
        if (blocks is null || blocks.Count == 0)
            throw new ArgumentException("blocks is empty — give at least one content block.");

        DocStyle? style = null;
        if (!string.IsNullOrWhiteSpace(styleJson))
            try { style = JsonSerializer.Deserialize<DocStyle>(styleJson!, JsonOpts); }
            catch (JsonException ex) { throw new ArgumentException($"style isn't valid JSON: {ex.Message}"); }

        // Make the filename match the format so the deliverable opens correctly regardless of what was passed.
        string stem = Path.GetFileNameWithoutExtension(filename.Trim());
        if (string.IsNullOrWhiteSpace(stem)) stem = "document";
        filename = stem + "." + format;

        return new DocumentSpec(format, filename, style, blocks);
    }
}

/// <summary>
/// Renders a <see cref="DocumentSpec"/> to a real .docx (OpenXML) or .pdf (MigraDoc/PDFsharp) — the
/// deterministic, tested half of structured document production. The model decides WHAT goes in the document;
/// this decides HOW it's drawn, the same way every time. Greenfield only: applying an existing template
/// document is the LLM/run_python path, not this.
/// </summary>
public static class DocumentRenderer
{
    /// <summary>Render the spec to <paramref name="outputPath"/>. <paramref name="resolveImage"/> maps an image
    /// block's file name to an absolute path (or null if it can't be found), so image blocks can be skipped
    /// gracefully rather than failing the whole document.</summary>
    public static void Render(DocumentSpec spec, string outputPath, Func<string, string?> resolveImage)
    {
        if (spec.Format == "pdf") RenderPdf(spec, outputPath, resolveImage);
        else RenderDocx(spec, outputPath, resolveImage);
    }

    // Heading point sizes by level (1..3), and the body size. Word/OpenXML express size in HALF-points.
    private static int HeadingPts(int level) => level <= 1 ? 20 : level == 2 ? 16 : 13;
    private const int BodyPts = 11;

    // ---- .docx (OpenXML) ----------------------------------------------------------------------

    private static void RenderDocx(DocumentSpec spec, string outputPath, Func<string, string?> resolveImage)
    {
        using var doc = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document();
        var body = main.Document.AppendChild(new Body());

        string? font = spec.Style?.Font;
        string? accent = NormalizeHex(spec.Style?.PrimaryColor);

        foreach (var b in spec.Blocks)
        {
            switch ((b.Type ?? "").Trim().ToLowerInvariant())
            {
                case "heading":
                    body.AppendChild(TextParagraph(b.Text ?? "", font, bold: true,
                        sizeHalfPts: HeadingPts(b.Level) * 2, color: accent, spaceBeforePts: 8, spaceAfterPts: 4));
                    break;
                case "paragraph":
                case "text":
                    body.AppendChild(TextParagraph(b.Text ?? "", font, sizeHalfPts: BodyPts * 2, spaceAfterPts: 6));
                    break;
                case "bullets":
                case "numbered":
                {
                    bool numbered = b.Type!.Trim().Equals("numbered", StringComparison.OrdinalIgnoreCase);
                    int i = 1;
                    foreach (var item in b.Items ?? Array.Empty<string>())
                        body.AppendChild(TextParagraph((numbered ? $"{i++}.  " : "•  ") + item, font,
                            sizeHalfPts: BodyPts * 2, spaceAfterPts: 2, indentTwips: 360));
                    break;
                }
                case "table":
                    body.AppendChild(DocxTable(b, font));
                    break;
                case "image":
                {
                    var path = b.File is null ? null : resolveImage(b.File);
                    var drawing = path is not null && System.IO.File.Exists(path) ? DocxImage(main, path) : null;
                    body.AppendChild(drawing is not null
                        ? new Paragraph(new Run(drawing))
                        : TextParagraph($"[image: {b.File} — not found]", font, italic: true, sizeHalfPts: BodyPts * 2));
                    break;
                }
                case "pagebreak":
                    body.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                    break;
                case "spacer":
                    body.AppendChild(new Paragraph());
                    break;
                default:
                    // Unknown block type: don't lose the content — render any text it carried as a paragraph.
                    if (!string.IsNullOrWhiteSpace(b.Text))
                        body.AppendChild(TextParagraph(b.Text!, font, sizeHalfPts: BodyPts * 2, spaceAfterPts: 6));
                    break;
            }
        }
        main.Document.Save();
    }

    private static Paragraph TextParagraph(string text, string? font, bool bold = false, bool italic = false,
        int sizeHalfPts = 22, string? color = null, int spaceBeforePts = 0, int spaceAfterPts = 0, int indentTwips = 0)
    {
        var runProps = new RunProperties();
        if (font is not null) runProps.Append(new RunFonts { Ascii = font, HighAnsi = font });
        if (bold) runProps.Append(new Bold());
        if (italic) runProps.Append(new Italic());
        runProps.Append(new FontSize { Val = sizeHalfPts.ToString() });
        if (color is not null) runProps.Append(new Color { Val = color });

        var run = new Run();
        run.Append(runProps);
        run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });

        var para = new Paragraph();
        if (spaceBeforePts > 0 || spaceAfterPts > 0 || indentTwips > 0)
        {
            var pp = new ParagraphProperties();
            if (indentTwips > 0) pp.Append(new Indentation { Left = indentTwips.ToString() });
            pp.Append(new SpacingBetweenLines
            {
                Before = (spaceBeforePts * 20).ToString(),
                After = (spaceAfterPts * 20).ToString(),
            });
            para.Append(pp);
        }
        para.Append(run);
        return para;
    }

    private static Table DocxTable(DocBlock b, string? font)
    {
        var table = new Table();
        table.AppendChild(new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }),
            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }));

        if (b.Headers is { Count: > 0 })
        {
            var header = new TableRow();
            foreach (var h in b.Headers)
                header.Append(new TableCell(TextParagraph(h, font, bold: true, sizeHalfPts: BodyPts * 2)));
            table.Append(header);
        }
        foreach (var row in b.Rows ?? Array.Empty<IReadOnlyList<string>>())
        {
            var tr = new TableRow();
            foreach (var cell in row)
                tr.Append(new TableCell(TextParagraph(cell ?? "", font, sizeHalfPts: BodyPts * 2)));
            table.Append(tr);
        }
        return table;
    }

    private const long EmuPerPixel = 9525;   // at 96 DPI
    private const long MaxImageWidthEmu = 5486400; // 6 inches

    private static Drawing DocxImage(MainDocumentPart main, string path)
    {
        var bytes = System.IO.File.ReadAllBytes(path);
        var (pxW, pxH) = ImageSize.Probe(bytes) ?? (600, 400);
        long emuW = pxW * EmuPerPixel, emuH = pxH * EmuPerPixel;
        if (emuW > MaxImageWidthEmu) { emuH = emuH * MaxImageWidthEmu / emuW; emuW = MaxImageWidthEmu; }

        string ext = Path.GetExtension(path).ToLowerInvariant();
        var partType = ext is ".jpg" or ".jpeg" ? ImagePartType.Jpeg : ext is ".gif" ? ImagePartType.Gif : ImagePartType.Png;
        var imagePart = main.AddImagePart(partType);
        using (var s = System.IO.File.OpenRead(path)) imagePart.FeedData(s);
        string relId = main.GetIdOfPart(imagePart);
        uint id = (uint)Math.Abs(path.GetHashCode()) % 1_000_000 + 1;

        return new Drawing(new DW.Inline(
            new DW.Extent { Cx = emuW, Cy = emuH },
            new DW.DocProperties { Id = id, Name = Path.GetFileName(path) },
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(
                        new PIC.NonVisualDrawingProperties { Id = id, Name = Path.GetFileName(path) },
                        new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(
                        new A.Blip { Embed = relId },
                        new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(
                        new A.Transform2D(
                            new A.Offset { X = 0, Y = 0 },
                            new A.Extents { Cx = emuW, Cy = emuH }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }))
                )
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        {
            DistanceFromTop = 0,
            DistanceFromBottom = 0,
            DistanceFromLeft = 0,
            DistanceFromRight = 0,
        });
    }

    // ---- .pdf (MigraDoc / PDFsharp) -----------------------------------------------------------

    private static void RenderPdf(DocumentSpec spec, string outputPath, Func<string, string?> resolveImage)
    {
        var doc = new Migra.Document();
        if (spec.Style?.Font is { Length: > 0 } f) doc.Styles["Normal"]!.Font.Name = f;
        var accent = ParseMigraColor(spec.Style?.PrimaryColor);
        var section = doc.AddSection();

        foreach (var b in spec.Blocks)
        {
            switch ((b.Type ?? "").Trim().ToLowerInvariant())
            {
                case "heading":
                {
                    var p = section.AddParagraph(b.Text ?? "");
                    p.Format.Font.Bold = true;
                    p.Format.Font.Size = HeadingPts(b.Level);
                    if (accent is { } c) p.Format.Font.Color = c;
                    p.Format.SpaceBefore = "8pt";
                    p.Format.SpaceAfter = "4pt";
                    break;
                }
                case "paragraph":
                case "text":
                {
                    var p = section.AddParagraph(b.Text ?? "");
                    p.Format.SpaceAfter = "6pt";
                    break;
                }
                case "bullets":
                case "numbered":
                {
                    bool numbered = b.Type!.Trim().Equals("numbered", StringComparison.OrdinalIgnoreCase);
                    foreach (var item in b.Items ?? Array.Empty<string>())
                    {
                        var p = section.AddParagraph(item);
                        p.Format.ListInfo = new Migra.ListInfo
                        {
                            ListType = numbered ? Migra.ListType.NumberList1 : Migra.ListType.BulletList1,
                        };
                        p.Format.LeftIndent = "0.5cm";
                    }
                    break;
                }
                case "table":
                    PdfTable(section, b, accent);
                    break;
                case "image":
                {
                    var path = b.File is null ? null : resolveImage(b.File);
                    if (path is not null && System.IO.File.Exists(path))
                    {
                        var img = section.AddImage(path);
                        img.Width = "15cm";        // ~6in; MigraDoc keeps the aspect ratio when only width is set
                        img.LockAspectRatio = true;
                    }
                    else
                    {
                        var p = section.AddParagraph($"[image: {b.File} — not found]");
                        p.Format.Font.Italic = true;
                    }
                    break;
                }
                case "pagebreak":
                    section.AddPageBreak();
                    break;
                case "spacer":
                    section.AddParagraph();
                    break;
                default:
                    if (!string.IsNullOrWhiteSpace(b.Text)) section.AddParagraph(b.Text!);
                    break;
            }
        }

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(outputPath);
    }

    private static void PdfTable(Migra.Section section, DocBlock b, Migra.Color? accent)
    {
        int cols = Math.Max(b.Headers?.Count ?? 0, b.Rows?.Count > 0 ? b.Rows[0].Count : 0);
        if (cols == 0) return;

        var table = section.AddTable();
        table.Borders.Width = 0.5;
        double colWidthCm = 16.0 / cols;
        for (int c = 0; c < cols; c++) table.AddColumn(Migra.Unit.FromCentimeter(colWidthCm));

        if (b.Headers is { Count: > 0 })
        {
            var hr = table.AddRow();
            hr.Format.Font.Bold = true;
            if (accent is { } c) hr.Shading.Color = c;
            for (int i = 0; i < cols; i++)
                hr.Cells[i].AddParagraph(i < b.Headers.Count ? b.Headers[i] ?? "" : "");
        }
        foreach (var row in b.Rows ?? Array.Empty<IReadOnlyList<string>>())
        {
            var tr = table.AddRow();
            for (int i = 0; i < cols; i++)
                tr.Cells[i].AddParagraph(i < row.Count ? row[i] ?? "" : "");
        }
    }

    // ---- shared helpers ------------------------------------------------------------------------

    // OpenXML wants a bare 6-hex colour (no '#'); returns null for anything unparseable so we just skip colour.
    private static string? NormalizeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        hex = hex.Trim().TrimStart('#');
        return hex.Length == 6 && hex.All(Uri.IsHexDigit) ? hex.ToUpperInvariant() : null;
    }

    private static Migra.Color? ParseMigraColor(string? hex)
    {
        var norm = NormalizeHex(hex);
        if (norm is null) return null;
        uint rgb = Convert.ToUInt32(norm, 16);
        return new Migra.Color((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
    }
}

/// <summary>Reads a raster image's pixel dimensions straight from its header — just enough to size a picture in
/// a .docx (PNG and JPEG cover what workers produce: matplotlib PNGs, uploaded logos). No System.Drawing (which
/// is Windows-only on modern .NET). Returns null if it isn't a format we probe, so the caller uses a default.</summary>
internal static class ImageSize
{
    public static (int Width, int Height)? Probe(byte[] b)
    {
        if (b.Length >= 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) // PNG
        {
            int w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
            int h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
            if (w > 0 && h > 0) return (w, h);
        }
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8) // JPEG: walk the segment markers to a Start-Of-Frame
        {
            int i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                byte marker = b[i + 1];
                // SOF0..SOF3, SOF5..SOF7, SOF9..SOF11, SOF13..SOF15 carry the frame dimensions.
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                {
                    int h = (b[i + 5] << 8) | b[i + 6];
                    int w = (b[i + 7] << 8) | b[i + 8];
                    if (w > 0 && h > 0) return (w, h);
                    return null;
                }
                int segLen = (b[i + 2] << 8) | b[i + 3];
                if (segLen < 2) return null;
                i += 2 + segLen;
            }
        }
        return null;
    }
}
