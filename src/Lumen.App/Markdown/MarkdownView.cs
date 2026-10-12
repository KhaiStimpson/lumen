using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Lumen.App.Diff;

namespace Lumen.App.Markdown;

/// <summary>A block of GitHub-flavoured Markdown, parsed into headings, paragraphs, lists, quotes and code.</summary>
public abstract record MarkdownBlock;

public sealed record HeadingBlock(int Level, string Text) : MarkdownBlock;

public sealed record ParagraphBlock(string Text) : MarkdownBlock;

public sealed record ListBlock(bool Ordered, IReadOnlyList<string> Items, IReadOnlyList<bool?> Checked) : MarkdownBlock;

public sealed record QuoteBlock(string Text) : MarkdownBlock;

/// <summary>A fenced code block; <see cref="Language"/> "suggestion" is a GitHub suggested change.</summary>
public sealed record CodeBlock(string Language, string Code) : MarkdownBlock;

public sealed record RuleBlock : MarkdownBlock;

/// <summary>The subset of Markdown that pull request descriptions and review comments use.</summary>
public static partial class MarkdownParser
{
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        var blocks = new List<MarkdownBlock>();
        var lines = (markdown ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var paragraph = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Length > 0)
            {
                blocks.Add(new ParagraphBlock(paragraph.ToString().Trim()));
                paragraph.Clear();
            }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushParagraph();
                var fence = trimmed[..3];
                var language = trimmed[3..].Trim();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal); i++)
                {
                    code.Add(lines[i]);
                }

                blocks.Add(new CodeBlock(language, string.Join('\n', code)));
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (Heading().Match(trimmed) is { Success: true } heading)
            {
                FlushParagraph();
                blocks.Add(new HeadingBlock(heading.Groups[1].Length, heading.Groups[2].Value.Trim()));
                continue;
            }

            if (Rule().IsMatch(trimmed))
            {
                FlushParagraph();
                blocks.Add(new RuleBlock());
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var quote = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('>'); i++)
                {
                    quote.Add(lines[i].TrimStart()[1..].TrimStart());
                }

                i--;
                blocks.Add(new QuoteBlock(string.Join(' ', quote)));
                continue;
            }

            if (ListItem().Match(line) is { Success: true } first)
            {
                FlushParagraph();
                var ordered = char.IsDigit(first.Groups[2].Value[0]);
                var items = new List<string>();
                var checks = new List<bool?>();
                for (; i < lines.Length; i++)
                {
                    if (ListItem().Match(lines[i]) is { Success: true } item && char.IsDigit(item.Groups[2].Value[0]) == ordered)
                    {
                        var text = item.Groups[3].Value;
                        bool? check = null;
                        if (TaskBox().Match(text) is { Success: true } box)
                        {
                            check = box.Groups[1].Value is "x" or "X";
                            text = text[box.Length..];
                        }

                        items.Add(text.Trim());
                        checks.Add(check);
                    }
                    else if (lines[i].StartsWith("  ", StringComparison.Ordinal) && lines[i].Trim().Length > 0 && items.Count > 0)
                    {
                        items[^1] += " " + lines[i].Trim();
                    }
                    else
                    {
                        break;
                    }
                }

                i--;
                blocks.Add(new ListBlock(ordered, items, checks));
                continue;
            }

            paragraph.Append(paragraph.Length == 0 ? "" : " ").Append(line.Trim());
        }

        FlushParagraph();
        return blocks;
    }

    /// <summary>Splits inline Markdown into styled runs: <c>**bold**</c>, <c>*italic*</c>, <c>`code`</c>, links.</summary>
    public static IEnumerable<(string Text, bool Bold, bool Italic, bool Code, string? Link)> Inlines(string text)
    {
        var position = 0;
        foreach (Match match in InlinePattern().Matches(text))
        {
            if (match.Index > position)
            {
                yield return (text[position..match.Index], false, false, false, null);
            }

            if (match.Groups["code"].Success)
            {
                yield return (match.Groups["code"].Value, false, false, true, null);
            }
            else if (match.Groups["bold"].Success)
            {
                yield return (match.Groups["bold"].Value, true, false, false, null);
            }
            else if (match.Groups["italic"].Success)
            {
                yield return (match.Groups["italic"].Value, false, true, false, null);
            }
            else if (match.Groups["label"].Success)
            {
                yield return (match.Groups["label"].Value, false, false, false, match.Groups["url"].Value);
            }
            else
            {
                yield return (match.Value, false, false, false, match.Value);
            }

            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            yield return (text[position..], false, false, false, null);
        }
    }

    /// <summary>The text of the first <c>```suggestion</c> block, if the comment has one.</summary>
    public static string? Suggestion(string? markdown) =>
        Parse(markdown).OfType<CodeBlock>().FirstOrDefault(c => c.Language == "suggestion")?.Code;

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\*\s*\*\s*\*[\s*]*|-\s*-\s*-[\s-]*|_\s*_\s*_[\s_]*)$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\[([ xX])\]\s+")]
    private static partial Regex TaskBox();

    [GeneratedRegex(@"`(?<code>[^`]+)`|\*\*(?<bold>[^*]+)\*\*|__(?<bold>[^_]+)__|(?<![\w*])\*(?<italic>[^*\s][^*]*)\*(?![\w*])|(?<![\w_])_(?<italic>[^_\s][^_]*)_(?![\w_])|\[(?<label>[^\]]+)\]\((?<url>[^)\s]+)\)|https?://[^\s)]+")]
    private static partial Regex InlinePattern();
}

/// <summary>
/// Renders Markdown with the app's typography (plan v2: descriptions, conversation and review comments). Text is
/// selectable; code blocks are highlighted; a <c>```suggestion</c> block is shown as the change it proposes.
/// </summary>
public sealed class MarkdownView : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    /// <summary>The lines a suggestion replaces, shown as removed above the suggestion.</summary>
    public static readonly StyledProperty<string?> SuggestionBaseProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(SuggestionBase));

    public static readonly StyledProperty<string?> PathProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Path));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public string? SuggestionBase
    {
        get => GetValue(SuggestionBaseProperty);
        set => SetValue(SuggestionBaseProperty, value);
    }

    /// <summary>The file a suggestion belongs to, for syntax highlighting.</summary>
    public string? Path
    {
        get => GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty || change.Property == SuggestionBaseProperty || change.Property == PathProperty)
        {
            Content = Render();
        }
    }

    private StackPanel Render()
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var block in MarkdownParser.Parse(Markdown))
        {
            panel.Children.Add(block switch
            {
                HeadingBlock h => Text(h.Text, h.Level <= 2 ? "h3" : "body", FontWeight.SemiBold),
                ParagraphBlock p => Text(p.Text, "body", null),
                QuoteBlock q => new Border
                {
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    [!Border.BorderBrushProperty] = this.GetResourceObservable("Border.Strong").ToBinding(),
                    Padding = new Thickness(10, 0, 0, 0),
                    Child = Text(q.Text, "body secondary", null),
                },
                ListBlock l => List(l),
                CodeBlock { Language: "suggestion" } c => Suggestion(c.Code),
                CodeBlock c => Code(c.Code, c.Language),
                _ => new Border { Height = 1, Margin = new Thickness(0, 4), [!Border.BackgroundProperty] = this.GetResourceObservable("Border.Subtle").ToBinding() },
            });
        }

        return panel;
    }

    private SelectableTextBlock Text(string text, string classes, FontWeight? weight)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 19 };
        foreach (var cls in classes.Split(' '))
        {
            block.Classes.Add(cls);
        }

        if (weight is { } w)
        {
            block.FontWeight = w;
        }

        block.Inlines = Inlines(text);
        return block;
    }

    private InlineCollection Inlines(string text)
    {
        var inlines = new InlineCollection();
        foreach (var (content, bold, italic, code, link) in MarkdownParser.Inlines(text))
        {
            var run = new Run(content);
            if (bold)
            {
                run.FontWeight = FontWeight.SemiBold;
            }

            if (italic)
            {
                run.FontStyle = FontStyle.Italic;
            }

            if (code)
            {
                run.Bind(TextElement.FontFamilyProperty, this.GetResourceObservable("CodeFont"));
                run.Bind(TextElement.BackgroundProperty, this.GetResourceObservable("Surface.Sunken"));
                run.FontSize = 11.5;
            }

            if (link is not null)
            {
                run.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("Accent"));
                run.TextDecorations = TextDecorations.Underline;
            }

            inlines.Add(run);
        }

        return inlines;
    }

    private StackPanel List(ListBlock list)
    {
        var panel = new StackPanel { Spacing = 3 };
        for (var i = 0; i < list.Items.Count; i++)
        {
            var marker = list.Checked[i] switch
            {
                true => "☑",
                false => "☐",
                _ => list.Ordered ? $"{i + 1}." : "•",
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*") };
            row.Children.Add(new TextBlock { Text = marker, FontSize = 12.5, Classes = { "secondary" } });
            var item = Text(list.Items[i], "body", null);
            Grid.SetColumn(item, 1);
            row.Children.Add(item);
            panel.Children.Add(row);
        }

        return panel;
    }

    private Border Code(string code, string language)
    {
        var snippet = new CodeSnippet { Code = code, Path = LanguagePath(language) ?? Path ?? "", StartLine = 1, ShowLineNumbers = false };
        return new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(0, 6),
            [!Border.BackgroundProperty] = this.GetResourceObservable("Surface.Sunken").ToBinding(),
            Child = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = snippet },
        };
    }

    /// <summary>A suggested change reads as a small diff: the lines it replaces, then the suggestion.</summary>
    private Border Suggestion(string code)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Suggested change",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(10, 6, 10, 4),
            Classes = { "secondary" },
        });

        void Lines(string text, string background, string sign)
        {
            foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n'))
            {
                var row = new Border
                {
                    Padding = new Thickness(10, 0),
                    [!Border.BackgroundProperty] = this.GetResourceObservable(background).ToBinding(),
                    Child = new TextBlock
                    {
                        Text = $"{sign} {line}",
                        FontSize = 12,
                        [!TextBlock.FontFamilyProperty] = this.GetResourceObservable("CodeFont").ToBinding(),
                    },
                };
                panel.Children.Add(row);
            }
        }

        if (!string.IsNullOrEmpty(SuggestionBase))
        {
            Lines(SuggestionBase, "Diff.Removed.Bg", "−");
        }

        Lines(code, "Diff.Added.Bg", "+");
        return new Border
        {
            Name = "Suggestion",
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Padding = new Thickness(0, 0, 0, 6),
            [!Border.BorderBrushProperty] = this.GetResourceObservable("Border.Subtle").ToBinding(),
            [!Border.BackgroundProperty] = this.GetResourceObservable("Surface.Raised").ToBinding(),
            Child = panel,
        };
    }

    private static string? LanguagePath(string language) => language.ToLowerInvariant() switch
    {
        "" => null,
        "csharp" or "cs" or "c#" => "x.cs",
        "ts" or "typescript" => "x.ts",
        "js" or "javascript" => "x.js",
        "json" => "x.json",
        "sql" => "x.sql",
        "yaml" or "yml" => "x.yml",
        "xml" => "x.xml",
        "bash" or "sh" or "shell" => "x.sh",
        "powershell" or "ps1" => "x.ps1",
        "diff" => "x.diff",
        var other => $"x.{other}",
    };
}
