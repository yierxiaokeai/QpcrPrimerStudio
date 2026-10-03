using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;
using MdBlock = Markdig.Syntax.Block;
using WpfBlock = System.Windows.Documents.Block;

namespace QpcrPrimerStudio.Desktop;

public static class MarkdownHelp
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().DisableHtml().Build();
    public static string Load(string topic)
    {
        using var stream = typeof(MarkdownHelp).Assembly.GetManifestResourceStream($"QpcrPrimerStudio.Desktop.Help.{topic}.md")
            ?? throw new FileNotFoundException($"Help topic missing: {topic}");
        using var reader = new StreamReader(stream, Encoding.UTF8); return reader.ReadToEnd();
    }
    public static FlowDocument Render(string markdown)
    {
        var document = new FlowDocument { FontSize = 14, FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), PagePadding = new Thickness(24), ColumnWidth = double.PositiveInfinity };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "InkBrush");
        foreach (var block in Markdown.Parse(markdown, Pipeline)) document.Blocks.Add(RenderBlock(block));
        return document;
    }
    private static WpfBlock RenderBlock(MdBlock block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var title = Paragraph(heading.Inline); title.FontSize = heading.Level == 1 ? 25 : heading.Level == 2 ? 19 : 16;
                title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 16, 0, 10); return title;
            case ParagraphBlock paragraph: return Paragraph(paragraph.Inline);
            case ListBlock list:
                var result = new System.Windows.Documents.List { MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(8, 4, 0, 12) };
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var entry = new ListItem(); foreach (var child in item) entry.Blocks.Add(RenderBlock(child)); result.ListItems.Add(entry);
                }
                return result;
            case Markdig.Extensions.Tables.Table table:
                var grid = new System.Windows.Documents.Table { CellSpacing = 0, Margin = new Thickness(0, 8, 0, 16) };
                var rows = new TableRowGroup(); grid.RowGroups.Add(rows);
                foreach (var row in table.OfType<Markdig.Extensions.Tables.TableRow>())
                {
                    var target = new System.Windows.Documents.TableRow(); rows.Rows.Add(target);
                    foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
                    {
                        var output = new System.Windows.Documents.TableCell { Padding = new Thickness(8), BorderThickness = new Thickness(0, 0, 0, 1) };
                        output.SetResourceReference(System.Windows.Documents.TableCell.BorderBrushProperty, "LineBrush");
                        if (row.IsHeader) { output.FontWeight = FontWeights.SemiBold; output.SetResourceReference(System.Windows.Documents.TableCell.BackgroundProperty, "CanvasBrush"); }
                        foreach (var child in cell) output.Blocks.Add(RenderBlock(child)); target.Cells.Add(output);
                    }
                }
                return grid;
            case CodeBlock code:
                return new Paragraph(new Run(code.Lines.ToString())) { FontFamily = new FontFamily("Consolas"), Padding = new Thickness(12), Margin = new Thickness(0, 8, 0, 12) };
            case ContainerBlock container:
                var section = new Section(); foreach (var child in container) section.Blocks.Add(RenderBlock(child)); return section;
            default: throw new NotSupportedException($"Unsupported help block: {block.GetType().Name}");
        }
    }
    private static Paragraph Paragraph(ContainerInline? inline)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10), LineHeight = 23 };
        if (inline is not null) Append(paragraph.Inlines, inline); return paragraph;
    }
    private static void Append(InlineCollection target, ContainerInline container)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal: target.Add(new Run(literal.Content.ToString())); break;
                case CodeInline code: target.Add(new Run(code.Content) { FontFamily = new FontFamily("Consolas") }); break;
                case LineBreakInline: target.Add(new LineBreak()); break;
                case EmphasisInline emphasis:
                    Span span = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic(); Append(span.Inlines, emphasis); target.Add(span); break;
                case ContainerInline child: Append(target, child); break;
                default: throw new NotSupportedException($"Unsupported help inline: {inline.GetType().Name}");
            }
        }
    }
}
