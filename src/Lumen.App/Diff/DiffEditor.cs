using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;
using Lumen.App.Motion;

namespace Lumen.App.Diff;

/// <summary>A review marker in the gutter. Shape encodes the kind so colour is never the only signal.</summary>
public sealed record GutterMarker(string Id, int NewLine, string Kind, bool IsCurrent, bool IsResolved, bool IsNew);

/// <summary>
/// Read-only unified diff built on AvaloniaEdit (TDD §27): virtualised, syntax highlighted, selectable, with a
/// custom gutter, diff backgrounds, a focus range and inline review cards hosted on placeholder lines.
/// </summary>
public sealed class DiffEditor : UserControl
{
    public static readonly StyledProperty<DiffDocument?> DiffProperty =
        AvaloniaProperty.Register<DiffEditor, DiffDocument?>(nameof(Diff));

    public static readonly StyledProperty<IReadOnlyList<GutterMarker>> MarkersProperty =
        AvaloniaProperty.Register<DiffEditor, IReadOnlyList<GutterMarker>>(nameof(Markers), []);

    public static readonly StyledProperty<(int Start, int End)?> FocusRangeProperty =
        AvaloniaProperty.Register<DiffEditor, (int Start, int End)?>(nameof(FocusRange));

    public static readonly StyledProperty<bool> ShowCardsProperty =
        AvaloniaProperty.Register<DiffEditor, bool>(nameof(ShowCards), true);

    private readonly TextEditor _editor;
    private readonly DiffBackgroundRenderer _background;
    private readonly DiffGutterMargin _gutter;
    private readonly ReviewCardGenerator _cards;
    private readonly TextMate.Installation _syntax;
    private CancellationTokenSource? _scroll;
    private int? _pendingNewLine;

    public DiffEditor()
    {
        _editor = new TextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = false,
            WordWrap = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 4, 0, 24),
        };
        _editor.Bind(TextEditor.FontFamilyProperty, this.GetResourceObservable("CodeFont"));
        _editor.Bind(TextEditor.FontSizeProperty, this.GetResourceObservable("CodeFontSize"));
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.EnableEmailHyperlinks = false;
        _editor.Options.HighlightCurrentLine = false;
        _editor.Options.AllowScrollBelowDocument = false;
        _editor.TextArea.TextView.Margin = new Thickness(10, 0, 0, 0);

        var view = _editor.TextArea.TextView;
        _background = new DiffBackgroundRenderer(this);
        view.BackgroundRenderers.Add(_background);
        view.LineTransformers.Add(new HunkHeaderColorizer(this));
        _cards = new ReviewCardGenerator(this);
        view.ElementGenerators.Add(_cards);

        _gutter = new DiffGutterMargin(this);
        _editor.TextArea.LeftMargins.Insert(0, _gutter);
        _gutter.MarkerClicked += id => MarkerClicked?.Invoke(this, id);

        _syntax = SyntaxThemes.Install(_editor, null);
        Content = _editor;

        view.SizeChanged += (_, _) =>
        {
            _cards.UpdateWidths(view.Bounds.Width);
            ApplyPendingReveal();
        };
        ActualThemeVariantChanged += (_, _) => view.Redraw();
    }

    public event EventHandler<string>? MarkerClicked;

    public DiffDocument? Diff
    {
        get => GetValue(DiffProperty);
        set => SetValue(DiffProperty, value);
    }

    public IReadOnlyList<GutterMarker> Markers
    {
        get => GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    /// <summary>Head-side line range to emphasise (the current review point's code).</summary>
    public (int Start, int End)? FocusRange
    {
        get => GetValue(FocusRangeProperty);
        set => SetValue(FocusRangeProperty, value);
    }

    public bool ShowCards
    {
        get => GetValue(ShowCardsProperty);
        set => SetValue(ShowCardsProperty, value);
    }

    /// <summary>Builds (or returns a cached) inline card for a review point id.</summary>
    public Func<string, Control?>? CardFactory { get; set; }

    public TextView TextView => _editor.TextArea.TextView;

    internal ReviewCardGenerator Cards => _cards;

    public Control? CardFor(string id) => _cards.Existing(id);

    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ApplyPendingReveal();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DiffProperty)
        {
            var diff = Diff;
            _cards.Reset();
            _editor.Document = new TextDocument(diff?.Text ?? "");
            SyntaxThemes.SetLanguage(_syntax, diff?.Path);
            SetVerticalOffset(0);
            _editor.ScrollToHorizontalOffset(0);
            _gutter.InvalidateMeasure();
            ApplyPendingReveal();
        }
        else if (change.Property == MarkersProperty || change.Property == FocusRangeProperty || change.Property == ShowCardsProperty)
        {
            _gutter.InvalidateVisual();
            TextView.Redraw();
        }
    }

    /// <summary>
    /// Scrolls so <paramref name="newLine"/> sits about a quarter of the way down the viewport. Uses the motion
    /// service's spring so navigation keeps spatial context (TDD §25.1).
    /// </summary>
    public Task RevealLineAsync(int newLine, IMotionService? motion, CancellationToken cancellationToken = default)
    {
        // Not ready yet (view attaching, or the document for this line hasn't arrived): reveal once it is.
        if (!IsLoaded || TextView.Bounds.Height <= 0 || Diff?.DocumentLineForNewLine(newLine) is not { } documentLine)
        {
            _pendingNewLine = newLine;
            ApplyPendingReveal();
            return Task.CompletedTask;
        }

        _pendingNewLine = null;
        return RevealDocumentLineAsync(documentLine, motion, cancellationToken);
    }

    /// <summary>Applies a remembered reveal after the current layout pass, when positions are measurable.</summary>
    private void ApplyPendingReveal()
    {
        if (_pendingNewLine is not { } line)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                if (_pendingNewLine == line && IsLoaded && TextView.Bounds.Height > 0 && Diff?.DocumentLineForNewLine(line) is { } documentLine)
                {
                    _pendingNewLine = null;
                    _ = RevealDocumentLineAsync(documentLine, null);
                }
            },
            Avalonia.Threading.DispatcherPriority.Background);
    }

    public async Task RevealDocumentLineAsync(int documentLine, IMotionService? motion, CancellationToken cancellationToken = default)
    {
        _scroll?.Cancel();
        _scroll = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _scroll.Token;

        TextView.EnsureVisualLines();
        var top = TextView.GetVisualTopByDocumentLine(Math.Clamp(documentLine, 1, Math.Max(1, _editor.Document.LineCount)));
        var viewport = TextView.Bounds.Height > 0 ? TextView.Bounds.Height : Bounds.Height;
        var max = Math.Max(0, TextView.DocumentHeight - viewport);
        var target = Math.Clamp(top - (viewport * 0.22), 0, max);

        if (motion is null)
        {
            SetVerticalOffset(target);
            return;
        }

        await motion.SpringAsync(_editor.VerticalOffset, target, SetVerticalOffset, token).ConfigureAwait(true);
    }

    /// <summary>
    /// TextEditor.ScrollToVerticalOffset is a no-op in AvaloniaEdit 12, so drive its ScrollViewer directly.
    /// </summary>
    private void SetVerticalOffset(double offset)
    {
        var scrollViewer = _editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer is not null)
        {
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, Math.Max(0, offset));
        }
    }

    internal DiffRow? RowAt(int documentLine) => Diff?.RowAt(documentLine);

    internal IBrush Brush(string key) => ThemeResources.Brush(this, key);

    internal Typeface CodeTypeface =>
        new(this.TryFindResource("CodeFont", ActualThemeVariant, out var f) && f is FontFamily family ? family : FontFamily.Default);

    internal double CodeFontSize => _editor.FontSize;

    /// <summary>Paints added/removed/hunk rows edge to edge, and the focus range with an accent edge.</summary>
    private sealed class DiffBackgroundRenderer(DiffEditor owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (owner.Diff is null)
            {
                return;
            }

            var added = owner.Brush("Diff.Added.Bg");
            var removed = owner.Brush("Diff.Removed.Bg");
            var hunk = owner.Brush("Diff.Hunk.Bg");
            var focus = owner.Brush("Diff.Focus.Bg");
            var edge = owner.Brush("Diff.Focus.Edge");
            var range = owner.FocusRange;
            var width = Math.Max(textView.Bounds.Width, textView.DocumentHeight > 0 ? textView.Bounds.Width : 0) + textView.HorizontalOffset;

            foreach (var line in textView.VisualLines)
            {
                var row = owner.RowAt(line.FirstDocumentLine.LineNumber);
                if (row is null)
                {
                    continue;
                }

                var y = line.VisualTop - textView.VerticalOffset;
                var rect = new Rect(-textView.HorizontalOffset - 12, y, width + 24, line.Height);
                var brush = row.Kind switch
                {
                    DiffRowKind.Added => added,
                    DiffRowKind.Removed => removed,
                    DiffRowKind.HunkHeader => hunk,
                    _ => null,
                };

                if (range is { } r && row.NewNumber >= r.Start && row.NewNumber <= r.End)
                {
                    drawingContext.FillRectangle(focus, rect);
                    drawingContext.FillRectangle(edge, new Rect(-textView.HorizontalOffset - 12, y, 3, line.Height));
                }
                else if (brush is not null)
                {
                    drawingContext.FillRectangle(brush, rect);
                }
            }
        }
    }

    /// <summary>Hunk headers read as quiet structure, not code.</summary>
    private sealed class HunkHeaderColorizer(DiffEditor owner) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            if (owner.RowAt(line.LineNumber)?.Kind != DiffRowKind.HunkHeader || line.Length == 0)
            {
                return;
            }

            var brush = owner.Brush("Diff.Hunk.Text");
            ChangeLinePart(line.Offset, line.EndOffset, element =>
            {
                element.TextRunProperties.SetForegroundBrush(brush);
                element.TextRunProperties.SetTypeface(new Typeface(element.TextRunProperties.Typeface.FontFamily, FontStyle.Italic));
            });
        }
    }
}

/// <summary>Old/new line numbers, +/- signs and review markers.</summary>
internal sealed class DiffGutterMargin : AbstractMargin
{
    private const double MarkerWidth = 18;
    private static readonly TimeSpan ArrivalDuration = TimeSpan.FromMilliseconds(700);
    private readonly DiffEditor _owner;
    private readonly Dictionary<string, DateTime> _arrivals = new(StringComparer.Ordinal);

    public DiffGutterMargin(DiffEditor owner)
    {
        _owner = owner;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    public event Action<string>? MarkerClicked;

    private double DigitWidth => Measure("0").Width;

    private int Digits
    {
        get
        {
            var max = _owner.Diff?.Rows.Select(r => Math.Max(r.OldNumber, r.NewNumber)).DefaultIfEmpty(0).Max() ?? 0;
            return Math.Max(3, max.ToString(CultureInfo.InvariantCulture).Length);
        }
    }

    private double ColumnWidth => (Digits * DigitWidth) + 14;

    protected override Size MeasureOverride(Size availableSize) =>
        new(MarkerWidth + (ColumnWidth * 2) + 18, 0);

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null)
        {
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        }

        if (newTextView is not null)
        {
            newTextView.VisualLinesChanged += OnVisualLinesChanged;
        }

        base.OnTextViewChanged(oldTextView, newTextView);
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var view = TextView;
        if (view is null || !view.VisualLinesValid || _owner.Diff is null)
        {
            return;
        }

        context.FillRectangle(_owner.Brush("Surface.Pane"), new Rect(Bounds.Size));
        var numberBrush = _owner.Brush("Code.LineNumber");
        var addedSign = _owner.Brush("Diff.Added.Sign");
        var removedSign = _owner.Brush("Diff.Removed.Sign");
        var addedGutter = _owner.Brush("Diff.Added.Gutter");
        var removedGutter = _owner.Brush("Diff.Removed.Gutter");
        var hunk = _owner.Brush("Diff.Hunk.Bg");
        var markers = _owner.Markers.ToLookup(m => m.NewLine);

        foreach (var line in view.VisualLines)
        {
            var row = _owner.RowAt(line.FirstDocumentLine.LineNumber);
            if (row is null)
            {
                continue;
            }

            var y = line.VisualTop - view.VerticalOffset;
            var rowRect = new Rect(MarkerWidth, y, Bounds.Width - MarkerWidth, line.Height);
            switch (row.Kind)
            {
                case DiffRowKind.Added:
                    context.FillRectangle(addedGutter, rowRect);
                    break;
                case DiffRowKind.Removed:
                    context.FillRectangle(removedGutter, rowRect);
                    break;
                case DiffRowKind.HunkHeader:
                    context.FillRectangle(hunk, rowRect);
                    break;
            }

            if (row.Kind == DiffRowKind.Card)
            {
                continue;
            }

            var textHeight = Measure("0").Height;
            var textY = y + ((Math.Min(line.Height, view.DefaultLineHeight) - textHeight) / 2);
            if (row.OldNumber > 0)
            {
                DrawRight(context, row.OldNumber.ToString(CultureInfo.InvariantCulture), MarkerWidth + ColumnWidth - 6, textY, numberBrush);
            }

            if (row.NewNumber > 0)
            {
                DrawRight(context, row.NewNumber.ToString(CultureInfo.InvariantCulture), MarkerWidth + (ColumnWidth * 2) - 6, textY, numberBrush);
            }

            if (row.Kind is DiffRowKind.Added or DiffRowKind.Removed)
            {
                var sign = Text(row.Kind == DiffRowKind.Added ? "+" : "−", row.Kind == DiffRowKind.Added ? addedSign : removedSign);
                context.DrawText(sign, new Point(MarkerWidth + (ColumnWidth * 2) + 4, textY));
            }

            if (row.NewNumber > 0)
            {
                foreach (var marker in markers[row.NewNumber])
                {
                    DrawMarker(context, marker, y + (Math.Min(line.Height, view.DefaultLineHeight) / 2));
                }
            }
        }

        // Evidence arrival (TDD §25.3): a new marker grows in once with a soft halo — no toasts.
        if (_arrivals.Values.Any(t => DateTime.UtcNow - t < ArrivalDuration))
        {
            Avalonia.Threading.DispatcherTimer.RunOnce(InvalidateVisual, TimeSpan.FromMilliseconds(16));
        }
    }

    private void DrawMarker(DrawingContext context, GutterMarker marker, double centerY)
    {
        var key = marker.IsResolved ? "Marker.Verified" : marker.Kind switch
        {
            "CorrectnessRisk" or "SecurityRisk" => "Marker.Correctness",
            "BehaviourChange" => "Marker.Behaviour",
            "ArchitectureDrift" => "Marker.Architecture",
            _ => "Marker.Deviation",
        };
        var brush = _owner.Brush(key);
        var size = marker.IsCurrent ? 11.0 : 9.0;
        var center = new Point(MarkerWidth / 2, centerY);

        if (marker.IsNew && !App.Motion.ReducedMotion)
        {
            _arrivals.TryAdd(marker.Id, DateTime.UtcNow);
        }

        if (_arrivals.TryGetValue(marker.Id, out var arrived) && DateTime.UtcNow - arrived is var age && age < ArrivalDuration)
        {
            var t = age / ArrivalDuration;
            size *= 0.4 + (0.6 * t) + (0.35 * Math.Sin(Math.PI * t) * (1 - t));
            var halo = size * (1 + (1.6 * t));
            using (context.PushOpacity(0.35 * (1 - t)))
            {
                context.DrawEllipse(brush, null, center, halo / 2, halo / 2);
            }
        }
        Geometry geometry = marker.IsResolved ? Check(center, size) : marker.Kind switch
        {
            "CorrectnessRisk" or "SecurityRisk" => (Geometry)Octagon(center, size),
            "BehaviourChange" => new EllipseGeometry(new Rect(center.X - (size / 2), center.Y - (size / 2), size, size)),
            "ArchitectureDrift" => Diamond(center, size),
            _ => Triangle(center, size),
        };

        if (marker.IsResolved)
        {
            context.DrawGeometry(null, new Pen(brush, 1.8, lineCap: PenLineCap.Round), geometry);
        }
        else
        {
            context.DrawGeometry(brush, marker.IsCurrent ? new Pen(_owner.Brush("Surface.Pane"), 1.5) : null, geometry);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var view = TextView;
        if (view is null || _owner.Diff is null)
        {
            return;
        }

        var position = e.GetPosition(this);
        var visualLine = view.GetVisualLineFromVisualTop(position.Y + view.VerticalOffset);
        if (visualLine is null || _owner.RowAt(visualLine.FirstDocumentLine.LineNumber) is not { NewNumber: > 0 } row)
        {
            return;
        }

        if (_owner.Markers.FirstOrDefault(m => m.NewLine == row.NewNumber) is { } marker)
        {
            MarkerClicked?.Invoke(marker.Id);
            e.Handled = true;
        }
    }

    private FormattedText Text(string text, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _owner.CodeTypeface, _owner.CodeFontSize - 1, brush);

    private FormattedText Measure(string text) => Text(text, Brushes.Black);

    private void DrawRight(DrawingContext context, string text, double right, double y, IBrush brush)
    {
        var formatted = Text(text, brush);
        context.DrawText(formatted, new Point(right - formatted.Width, y));
    }

    private static StreamGeometry Triangle(Point c, double s) => Polygon(
        new Point(c.X, c.Y - (s * 0.6)), new Point(c.X + (s * 0.6), c.Y + (s * 0.45)), new Point(c.X - (s * 0.6), c.Y + (s * 0.45)));

    private static StreamGeometry Diamond(Point c, double s) => Polygon(
        new Point(c.X, c.Y - (s / 2)), new Point(c.X + (s / 2), c.Y), new Point(c.X, c.Y + (s / 2)), new Point(c.X - (s / 2), c.Y));

    private static StreamGeometry Octagon(Point c, double s)
    {
        var r = s / 2;
        var k = r * 0.414;
        return Polygon(
            new Point(c.X - k, c.Y - r), new Point(c.X + k, c.Y - r), new Point(c.X + r, c.Y - k), new Point(c.X + r, c.Y + k),
            new Point(c.X + k, c.Y + r), new Point(c.X - k, c.Y + r), new Point(c.X - r, c.Y + k), new Point(c.X - r, c.Y - k));
    }

    private static StreamGeometry Check(Point c, double s)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(new Point(c.X - (s / 2), c.Y), false);
        ctx.LineTo(new Point(c.X - (s / 8), c.Y + (s / 2.5)));
        ctx.LineTo(new Point(c.X + (s / 2), c.Y - (s / 2.5)));
        ctx.EndFigure(false);
        return geometry;
    }

    private static StreamGeometry Polygon(params Point[] points)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(points[0], true);
        foreach (var p in points.Skip(1))
        {
            ctx.LineTo(p);
        }

        ctx.EndFigure(true);
        return geometry;
    }
}

/// <summary>Replaces each card placeholder character with the review card control for that line.</summary>
internal sealed class ReviewCardGenerator(DiffEditor owner) : VisualLineElementGenerator
{
    private readonly Dictionary<string, Control> _cards = new(StringComparer.Ordinal);
    private double _width = 600;

    public Control? Existing(string id) => _cards.GetValueOrDefault(id);

    public void Reset() => _cards.Clear();

    public void UpdateWidths(double viewWidth)
    {
        var width = Math.Max(320, viewWidth - 28);
        if (Math.Abs(width - _width) < 0.5)
        {
            return;
        }

        _width = width;
        foreach (var card in _cards.Values)
        {
            card.Width = width;
        }

        owner.TextView.Redraw();
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (!owner.ShowCards || CurrentContext is null)
        {
            return -1;
        }

        var document = CurrentContext.Document;
        var end = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
        for (var offset = startOffset; offset < end; offset++)
        {
            if (document.GetCharAt(offset) == DiffDocument.CardPlaceholder)
            {
                return offset;
            }
        }

        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        var line = CurrentContext.Document.GetLineByOffset(offset);
        if (owner.RowAt(line.LineNumber)?.CardId is not { } id)
        {
            return null;
        }

        if (!_cards.TryGetValue(id, out var card))
        {
            card = owner.CardFactory?.Invoke(id);
            if (card is null)
            {
                return null;
            }

            card.Width = _width;
            card.Margin = new Thickness(0, 6, 0, 10);
            card.SizeChanged += (_, e) =>
            {
                if (Math.Abs(e.PreviousSize.Height - e.NewSize.Height) > 0.5)
                {
                    owner.TextView.Redraw();
                }
            };
            _cards[id] = card;
        }

        return new InlineObjectElement(1, card);
    }
}
