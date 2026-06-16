using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using MBlock = Markdig.Syntax.Block;
using MInline = Markdig.Syntax.Inlines.Inline;

namespace EdgeKit.App.Controls;

public sealed class MarkdownView : StackPanel
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UsePipeTables(new PipeTableOptions())
        .UseGridTables()
        .UseTaskLists()
        .Build();

    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownView),
        new PropertyMetadata(string.Empty, OnMarkdownChanged));

    public MarkdownView()
    {
        Spacing = 8;
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownView view)
        {
            view.RenderMarkdown(e.NewValue as string ?? string.Empty);
        }
    }

    private const int MaxRenderChars = 20000;
    private const int MaxRenderBlocks = 400;

    private void RenderMarkdown(string markdown)
    {
        Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return;
        }

        // 超长内容直接降级为纯文本，避免生成超深可视树导致渲染线程卡死/闪退。
        if (markdown.Length > MaxRenderChars)
        {
            Children.Add(CreateTextBlock(markdown));
            return;
        }

        MarkdownDocument document;
        try
        {
            document = Markdig.Markdown.Parse(markdown, Pipeline);
        }
        catch
        {
            Children.Add(CreateTextBlock(markdown));
            return;
        }

        var blockCount = 0;
        foreach (var block in document)
        {
            if (blockCount >= MaxRenderBlocks)
            {
                Children.Add(CreateTextBlock("…（内容过长，已折叠剩余部分）"));
                break;
            }

            try
            {
                if (RenderBlock(block, listDepth: 0) is { } element)
                {
                    Children.Add(element);
                    blockCount++;
                }
            }
            catch
            {
                var fallback = block switch
                {
                    LeafBlock leaf => ExtractLeafLines(leaf),
                    Table table => ExtractTableText(table),
                    _ => block.ToString()
                };
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    Children.Add(CreateTextBlock(fallback));
                    blockCount++;
                }
            }
        }
    }

    private FrameworkElement? RenderBlock(MBlock block, int listDepth)
        => block switch
        {
            HeadingBlock heading => RenderHeading(heading),
            ParagraphBlock paragraph => RenderParagraph(paragraph),
            ListBlock list => RenderList(list, listDepth),
            QuoteBlock quote => RenderQuote(quote, listDepth),
            CodeBlock code => RenderCodeBlock(code),
            ThematicBreakBlock => RenderDivider(),
            Table table => RenderTable(table),
            HtmlBlock html => CreateTextBlock(ExtractHtmlText(html)),
            LeafBlock leaf => RenderLeafBlock(leaf),
            ContainerBlock container => RenderContainer(container, listDepth),
            _ => null
        };

    private TextBlock RenderHeading(HeadingBlock heading)
    {
        var block = CreateTextBlock();
        block.FontWeight = FontWeights.SemiBold;
        block.FontSize = heading.Level switch
        {
            1 => 22,
            2 => 19,
            3 => 17,
            4 => 15,
            _ => 14
        };

        AppendInlines(block.Inlines, heading.Inline);
        block.Margin = new Thickness(0, heading.Level <= 2 ? 4 : 2, 0, 0);
        return block;
    }

    private TextBlock RenderParagraph(ParagraphBlock paragraph)
    {
        var block = CreateTextBlock();
        AppendInlines(block.Inlines, paragraph.Inline);
        return block;
    }

    private FrameworkElement RenderList(ListBlock list, int listDepth)
    {
        var panel = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(Math.Min(listDepth, 3) * 14, 0, 0, 0)
        };

        var index = int.TryParse(list.OrderedStart, out var orderedStart) ? orderedStart : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            panel.Children.Add(RenderListItem(item, list.IsOrdered, index, listDepth));
            if (list.IsOrdered)
            {
                index++;
            }
        }

        return panel;
    }

    private FrameworkElement RenderListItem(ListItemBlock item, bool ordered, int index, int listDepth)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var marker = CreateTextBlock();
        marker.Text = ordered ? $"{index}." : GetBulletText(item);
        marker.MinWidth = ordered ? 24 : 18;
        marker.Foreground = ResourceBrush("EdgeMutedBrush");
        marker.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(marker, 0);
        grid.Children.Add(marker);

        var content = new StackPanel { Spacing = 6 };
        foreach (var block in item)
        {
            if (RenderBlock(block, listDepth + 1) is { } element)
            {
                content.Children.Add(element);
            }
        }

        if (content.Children.Count == 0)
        {
            content.Children.Add(CreateTextBlock(string.Empty));
        }

        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private FrameworkElement RenderQuote(QuoteBlock quote, int listDepth)
    {
        var content = new StackPanel { Spacing = 6 };
        foreach (var block in quote)
        {
            if (RenderBlock(block, listDepth) is { } element)
            {
                content.Children.Add(element);
            }
        }

        return new Border
        {
            BorderBrush = ResourceBrush("EdgeAccentBrush"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Background = ResourceBrush("EdgeControlBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 8, 8),
            Child = content
        };
    }

    private FrameworkElement RenderCodeBlock(CodeBlock code)
    {
        var text = ExtractLeafLines(code).TrimEnd('\r', '\n');
        var block = CreateTextBlock(text);
        block.FontFamily = new FontFamily("Consolas");
        block.FontSize = 13;
        block.LineHeight = 18;

        return new Border
        {
            Background = ResourceBrush("EdgeControlBrush"),
            BorderBrush = ResourceBrush("EdgeLineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Child = block
        };
    }

    private FrameworkElement RenderDivider()
        => new Border
        {
            Height = 1,
            Background = ResourceBrush("EdgeLineBrush"),
            Margin = new Thickness(0, 4, 0, 4)
        };

    private FrameworkElement RenderTable(Table table)
    {
        var rows = table.OfType<TableRow>().ToList();
        var columnCount = rows.Select(r => r.OfType<TableCell>().Count()).DefaultIfEmpty(1).Max();
        var grid = new Grid
        {
            MinWidth = Math.Min(560, Math.Max(260, columnCount * 120))
        };

        for (var column = 0; column < columnCount; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = rows[rowIndex];
            var cells = row.OfType<TableCell>().ToList();
            for (var cellIndex = 0; cellIndex < columnCount; cellIndex++)
            {
                var content = cellIndex < cells.Count
                    ? RenderTableCell(cells[cellIndex])
                    : new StackPanel { Spacing = 4 };

                var border = new Border
                {
                    Background = row.IsHeader ? ResourceBrush("EdgeControlBrush") : ResourceBrush("EdgePanelBrush"),
                    BorderBrush = ResourceBrush("EdgeLineBrush"),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 6, 8, 6),
                    Child = content
                };

                Grid.SetRow(border, rowIndex);
                Grid.SetColumn(border, cellIndex);
                grid.Children.Add(border);
            }
        }

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = grid
        };
    }

    private FrameworkElement RenderTableCell(TableCell cell)
    {
        var content = new StackPanel { Spacing = 4 };
        foreach (var block in cell)
        {
            if (RenderBlock(block, listDepth: 0) is { } element)
            {
                content.Children.Add(element);
            }
        }

        if (content.Children.Count == 0)
        {
            content.Children.Add(CreateTextBlock(string.Empty));
        }

        return content;
    }

    private FrameworkElement? RenderLeafBlock(LeafBlock leaf)
    {
        if (leaf.Inline is not null)
        {
            var block = CreateTextBlock();
            AppendInlines(block.Inlines, leaf.Inline);
            return block;
        }

        var text = ExtractLeafLines(leaf);
        return string.IsNullOrWhiteSpace(text) ? null : CreateTextBlock(text);
    }

    private FrameworkElement? RenderContainer(ContainerBlock container, int listDepth)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var block in container)
        {
            if (RenderBlock(block, listDepth) is { } element)
            {
                panel.Children.Add(element);
            }
        }

        return panel.Children.Count == 0 ? null : panel;
    }

    private void AppendInlines(InlineCollection inlines, ContainerInline? container, InlineStyle style = default)
    {
        if (container is null)
        {
            return;
        }

        foreach (var inline in container)
        {
            AppendInline(inlines, inline, style);
        }
    }

    private void AppendInline(InlineCollection inlines, MInline inline, InlineStyle style)
    {
        switch (inline)
        {
            case LiteralInline literal:
                AddRun(inlines, literal.Content.ToString(), style);
                break;
            case CodeInline code:
                AddRun(inlines, code.Content, style with { IsCode = true });
                break;
            case EmphasisInline emphasis:
                var marker = emphasis.DelimiterChar;
                var count = emphasis.DelimiterCount;
                var next = style with
                {
                    IsBold = style.IsBold || marker == '*' && count >= 2 || marker == '_' && count >= 2,
                    IsItalic = style.IsItalic || marker == '*' && count == 1 || marker == '_' && count == 1,
                    IsStrike = style.IsStrike || marker == '~'
                };
                AppendInlines(inlines, emphasis, next);
                break;
            case LinkInline link:
                AppendLink(inlines, link, style);
                break;
            case LineBreakInline:
                inlines.Add(new LineBreak());
                break;
            case HtmlInline html:
                AddRun(inlines, html.Tag, style);
                break;
            case ContainerInline nested:
                AppendInlines(inlines, nested, style);
                break;
            case TaskList task:
                break;
            default:
                var text = inline.ToString();
                if (!string.IsNullOrEmpty(text))
                {
                    AddRun(inlines, text, style);
                }

                break;
        }
    }

    private void AppendLink(InlineCollection inlines, LinkInline link, InlineStyle style)
    {
        var text = ExtractInlineText(link);
        if (string.IsNullOrWhiteSpace(text))
        {
            text = link.Url ?? string.Empty;
        }

        if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "mailto")
        {
            var hyperlink = new Hyperlink
            {
                NavigateUri = uri,
                Foreground = ResourceBrush("EdgeAccentBrush")
            };
            AddRun(hyperlink.Inlines, text, style);
            inlines.Add(hyperlink);
            return;
        }

        AddRun(inlines, string.IsNullOrWhiteSpace(link.Url) ? text : $"{text} ({link.Url})", style);
    }

    private static void AddRun(InlineCollection inlines, string text, InlineStyle style)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var run = new Run { Text = text };
        if (style.IsBold)
        {
            run.FontWeight = FontWeights.SemiBold;
        }

        if (style.IsItalic)
        {
            run.FontStyle = global::Windows.UI.Text.FontStyle.Italic;
        }

        if (style.IsStrike)
        {
            run.TextDecorations = global::Windows.UI.Text.TextDecorations.Strikethrough;
        }

        if (style.IsCode)
        {
            run.FontFamily = new FontFamily("Consolas");
            run.FontSize = 13;
        }

        inlines.Add(run);
    }

    private static string ExtractInlineText(ContainerInline container)
    {
        var builder = new StringBuilder();
        foreach (var inline in container)
        {
            builder.Append(inline switch
            {
                LiteralInline literal => literal.Content.ToString(),
                CodeInline code => code.Content,
                LineBreakInline => Environment.NewLine,
                HtmlInline html => html.Tag,
                ContainerInline nested => ExtractInlineText(nested),
                TaskList => string.Empty,
                _ => inline.ToString()
            });
        }

        return builder.ToString();
    }

    private static string ExtractLeafLines(LeafBlock block)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < block.Lines.Count; i++)
        {
            var line = block.Lines.Lines[i];
            var slice = line.Slice;
            if (slice.Text is not null)
            {
                builder.Append(slice.Text, slice.Start, slice.Length);
            }

            if (i < block.Lines.Count - 1)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static string ExtractHtmlText(HtmlBlock block)
    {
        var text = ExtractLeafLines(block);
        return string.IsNullOrWhiteSpace(text) ? block.ToString() ?? string.Empty : text;
    }

    private static string ExtractTableText(Table table)
    {
        var builder = new StringBuilder();
        foreach (var row in table.OfType<TableRow>())
        {
            var cells = row.OfType<TableCell>().Select(cell =>
            {
                var text = string.Join(" ", cell.Select(block => block is LeafBlock leaf ? ExtractLeafLines(leaf) : block.ToString()));
                return text.ReplaceLineEndings(" ").Trim();
            });
            builder.AppendLine(string.Join(" | ", cells));
        }

        return builder.ToString().TrimEnd();
    }

    private static string GetBulletText(ListItemBlock item)
    {
        foreach (var descendant in item.Descendants())
        {
            if (descendant is ParagraphBlock paragraph && paragraph.Inline is not null)
            {
                var firstInline = paragraph.Inline.FirstChild;
                if (firstInline is TaskList task)
                {
                    return task.Checked ? "[x]" : "[ ]";
                }
            }
        }

        return "•";
    }

    private static TextBlock CreateTextBlock(string? text = null)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            LineHeight = 20,
            Foreground = ResourceBrush("EdgeTextBrush")
        };

        if (text is not null)
        {
            block.Text = text;
        }

        return block;
    }

    private static Brush ResourceBrush(string key)
        => (Brush)Application.Current.Resources[key];

    private readonly record struct InlineStyle(bool IsBold, bool IsItalic, bool IsStrike, bool IsCode);
}
