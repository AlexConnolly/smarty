using System;
using System.IO;
using System.Linq;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Smarty.Agents;

public static class MarkdownPdf
{
    static MarkdownPdf()
    {
        // Register QuestPDF Community License (free for individuals/small organizations)
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>
    /// Converts a Markdown string into a PDF byte array.
    /// </summary>
    public static byte[] Convert(string markdownText)
    {
        var pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();

        var document = Markdown.Parse(markdownText, pipeline);

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.5f, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(11).FontFamily("Calibri"));

                page.Content().Column(column =>
                {
                    column.Spacing(8);
                    foreach (var block in document)
                    {
                        RenderBlock(column, block);
                    }
                });

                page.Footer().AlignCenter().Text(x =>
                {
                    x.Span("Page ");
                    x.CurrentPageNumber();
                    x.Span(" of ");
                    x.TotalPages();
                });
            });
        }).GeneratePdf();
    }

    private static void RenderBlock(ColumnDescriptor column, Block block)
    {
        if (block is HeadingBlock heading)
        {
            column.Item().Text(text =>
            {
                float size = heading.Level switch
                {
                    1 => 22,
                    2 => 17,
                    3 => 14,
                    _ => 12
                };
                
                RenderInlines(text, heading.Inline, span =>
                {
                    span.FontSize(size).Bold();
                    if (heading.Level == 1)
                    {
                        span.FontColor(Colors.Blue.Darken3);
                    }
                    else if (heading.Level == 2)
                    {
                        span.FontColor(Colors.Blue.Darken1);
                    }
                });
            });
            column.Item().PaddingBottom(4);
        }
        else if (block is ParagraphBlock paragraph)
        {
            column.Item().Text(text =>
            {
                RenderInlines(text, paragraph.Inline);
            });
            column.Item().PaddingBottom(6);
        }
        else if (block is ListBlock listBlock)
        {
            int index = 1;
            foreach (var item in listBlock)
            {
                if (item is ListItemBlock listItem)
                {
                    column.Item().Row(row =>
                    {
                        row.AutoItem().Text(listBlock.IsOrdered ? $"{index}." : "•");
                        row.RelativeItem().PaddingLeft(6).Column(itemColumn =>
                        {
                            foreach (var subBlock in listItem)
                            {
                                RenderBlock(itemColumn, subBlock);
                            }
                        });
                    });
                    index++;
                }
            }
            column.Item().PaddingBottom(6);
        }
        else if (block is CodeBlock codeBlock)
        {
            string code = GetCodeBlockText(codeBlock);
            column.Item().Background(Colors.Grey.Lighten4).Border(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(6).Text(text =>
            {
                text.Span(code).FontFamily(Fonts.CourierNew).FontSize(9.5f);
            });
            column.Item().PaddingBottom(6);
        }
        else if (block is ThematicBreakBlock)
        {
            column.Item().LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
            column.Item().PaddingBottom(8);
        }
        else if (block is QuoteBlock quoteBlock)
        {
            column.Item().BorderLeft(3).BorderColor(Colors.Grey.Lighten2).PaddingLeft(8).Column(quoteColumn =>
            {
                foreach (var subBlock in quoteBlock)
                {
                    RenderBlock(quoteColumn, subBlock);
                }
            });
            column.Item().PaddingBottom(6);
        }
        else if (block is Table tableBlock)
        {
            column.Item().PaddingBottom(8).Table(pdfTable =>
            {
                int colCount = 0;
                foreach (var row in tableBlock)
                {
                    if (row is TableRow tableRow)
                    {
                        colCount = Math.Max(colCount, tableRow.Count);
                    }
                }

                if (colCount == 0) return;

                pdfTable.ColumnsDefinition(columns =>
                {
                    for (int i = 0; i < colCount; i++)
                    {
                        columns.RelativeColumn();
                    }
                });

                bool isHeader = true;
                foreach (var rowBlock in tableBlock)
                {
                    if (rowBlock is TableRow tableRow)
                    {
                        for (int i = 0; i < tableRow.Count; i++)
                        {
                            var cellBlock = tableRow[i];
                            if (cellBlock is TableCell tableCell)
                            {
                                IContainer cellContainer = pdfTable.Cell();
                                if (isHeader)
                                {
                                    cellContainer = cellContainer.Background(Colors.Grey.Lighten3)
                                           .BorderBottom(1.5f).BorderColor(Colors.Grey.Medium)
                                           .Padding(5);
                                }
                                else
                                {
                                    cellContainer = cellContainer.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                                           .Padding(5);
                                }

                                cellContainer.Column(cellColumn =>
                                {
                                    foreach (var subBlock in tableCell)
                                    {
                                        RenderBlock(cellColumn, subBlock);
                                    }
                                });
                            }
                        }
                        isHeader = false;
                    }
                }
            });
        }
    }

    private static void RenderInlines(TextDescriptor text, ContainerInline? container, Action<TextSpanDescriptor>? styleModifier = null)
    {
        if (container == null) return;
        foreach (var inline in container)
        {
            RenderInline(text, inline, false, false, styleModifier);
        }
    }

    private static void RenderInline(
        TextDescriptor text,
        Inline inline,
        bool isBold,
        bool isItalic,
        Action<TextSpanDescriptor>? styleModifier)
    {
        if (inline is LiteralInline literal)
        {
            var span = text.Span(literal.Content.ToString());
            if (isBold) span.Bold();
            if (isItalic) span.Italic();
            styleModifier?.Invoke(span);
        }
        else if (inline is EmphasisInline emphasis)
        {
            bool nextBold = isBold || (emphasis.DelimiterCount == 2);
            bool nextItalic = isItalic || (emphasis.DelimiterCount == 1);
            foreach (var child in emphasis)
            {
                RenderInline(text, child, nextBold, nextItalic, styleModifier);
            }
        }
        else if (inline is CodeInline code)
        {
            var span = text.Span(code.Content);
            span.FontFamily(Fonts.CourierNew).BackgroundColor(Colors.Grey.Lighten3);
            if (isBold) span.Bold();
            if (isItalic) span.Italic();
            styleModifier?.Invoke(span);
        }
        else if (inline is LinkInline link)
        {
            foreach (var child in link)
            {
                RenderInline(text, child, isBold, isItalic, span =>
                {
                    span.FontColor(Colors.Blue.Medium).Underline();
                    styleModifier?.Invoke(span);
                });
            }
        }
        else if (inline is LineBreakInline)
        {
            text.Span("\n");
        }
        else if (inline is ContainerInline container)
        {
            foreach (var child in container)
            {
                RenderInline(text, child, isBold, isItalic, styleModifier);
            }
        }
    }

    private static string GetCodeBlockText(CodeBlock codeBlock)
    {
        var sb = new StringBuilder();
        foreach (var line in codeBlock.Lines)
        {
            sb.AppendLine(line.ToString());
        }
        return sb.ToString().TrimEnd();
    }
}
