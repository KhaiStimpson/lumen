using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;

namespace Lumen.App.Diff;

/// <summary>A small, syntax-highlighted, read-only excerpt with real line numbers and highlighted lines.</summary>
public sealed class CodeSnippet : UserControl
{
    public static readonly StyledProperty<string?> CodeProperty =
        AvaloniaProperty.Register<CodeSnippet, string?>(nameof(Code));

    public static readonly StyledProperty<string?> PathProperty =
        AvaloniaProperty.Register<CodeSnippet, string?>(nameof(Path));

    public static readonly StyledProperty<int> StartLineProperty =
        AvaloniaProperty.Register<CodeSnippet, int>(nameof(StartLine), 1);

    public static readonly StyledProperty<IReadOnlyList<int>?> HighlightLinesProperty =
        AvaloniaProperty.Register<CodeSnippet, IReadOnlyList<int>?>(nameof(HighlightLines));

    public static readonly StyledProperty<string> HighlightBrushKeyProperty =
        AvaloniaProperty.Register<CodeSnippet, string>(nameof(HighlightBrushKey), "Diff.Focus.Bg");

    private readonly TextEditor _editor;
    private readonly TextMate.Installation _syntax;

    public CodeSnippet()
    {
        _editor = new TextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = false,
            WordWrap = false,
            Background = Brushes.Transparent,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 6),
        };
        _editor.Bind(TextEditor.FontFamilyProperty, this.GetResourceObservable("CodeFont"));
        _editor.FontSize = 11.5;
        _editor.Options.HighlightCurrentLine = false;
        _editor.Options.EnableHyperlinks = false;
        _editor.TextArea.TextView.Margin = new Thickness(8, 0, 8, 0);
        _editor.TextArea.TextView.BackgroundRenderers.Add(new HighlightRenderer(this));
        _editor.TextArea.LeftMargins.Insert(0, new NumberMargin(this));
        _syntax = SyntaxThemes.Install(_editor, null);
        Content = _editor;
    }

    public string? Code
    {
        get => GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    public string? Path
    {
        get => GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    public int StartLine
    {
        get => GetValue(StartLineProperty);
        set => SetValue(StartLineProperty, value);
    }

    public IReadOnlyList<int>? HighlightLines
    {
        get => GetValue(HighlightLinesProperty);
        set => SetValue(HighlightLinesProperty, value);
    }

    public string HighlightBrushKey
    {
        get => GetValue(HighlightBrushKeyProperty);
        set => SetValue(HighlightBrushKeyProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CodeProperty)
        {
            _editor.Document = new TextDocument(Code ?? "");
        }
        else if (change.Property == PathProperty)
        {
            SyntaxThemes.SetLanguage(_syntax, Path);
        }
        else if (change.Property == HighlightLinesProperty || change.Property == StartLineProperty)
        {
            _editor.TextArea.TextView.Redraw();
        }
    }

    private sealed class HighlightRenderer(CodeSnippet owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (owner.HighlightLines is not { Count: > 0 } lines)
            {
                return;
            }

            var brush = ThemeResources.Brush(owner, owner.HighlightBrushKey);
            foreach (var line in textView.VisualLines)
            {
                if (lines.Contains(line.FirstDocumentLine.LineNumber + owner.StartLine - 1))
                {
                    var y = line.VisualTop - textView.VerticalOffset;
                    drawingContext.FillRectangle(brush, new Rect(-8, y, textView.Bounds.Width + 16, line.Height));
                }
            }
        }
    }

    private sealed class NumberMargin(CodeSnippet owner) : AbstractMargin
    {
        protected override Size MeasureOverride(Size availableSize) => new(36, 0);

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (newTextView is not null)
            {
                newTextView.VisualLinesChanged += (_, _) => InvalidateVisual();
            }

            base.OnTextViewChanged(oldTextView, newTextView);
        }

        public override void Render(DrawingContext context)
        {
            if (TextView is not { VisualLinesValid: true } view)
            {
                return;
            }

            var brush = ThemeResources.Brush(owner, "Code.LineNumber");
            var typeface = new Typeface(owner.TryFindResource("CodeFont", owner.ActualThemeVariant, out var f) && f is FontFamily family ? family : FontFamily.Default);
            foreach (var line in view.VisualLines)
            {
                var number = (line.FirstDocumentLine.LineNumber + owner.StartLine - 1).ToString(CultureInfo.InvariantCulture);
                var text = new FormattedText(number, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10.5, brush);
                context.DrawText(text, new Point(Bounds.Width - text.Width - 6, line.VisualTop - view.VerticalOffset + 1));
            }
        }
    }
}
