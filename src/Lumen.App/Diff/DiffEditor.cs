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

/// <summary>The reader asked to comment on a line, or on <see cref="StartLine"/>..<see cref="Line"/> (plan v2 M3).</summary>
public sealed record LineCommentRequest(LineSide Side, int Line, int? StartLine);

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

    /// <summary>Split view: which side's line numbers this editor's gutter shows (null shows both).</summary>
    public static readonly StyledProperty<LineSide?> NumberSideProperty =
        AvaloniaProperty.Register<DiffEditor, LineSide?>(nameof(NumberSide));

    /// <summary>Show the gutter's "+" so any diff line can be commented on (off for read-only views).</summary>
    public static readonly StyledProperty<bool> AllowCommentsProperty =
        AvaloniaProperty.Register<DiffEditor, bool>(nameof(AllowComments), true);

    public static readonly StyledProperty<IReadOnlyList<FindMatch>> FindMatchesProperty =
        AvaloniaProperty.Register<DiffEditor, IReadOnlyList<FindMatch>>(nameof(FindMatches), []);

    public static readonly StyledProperty<FindMatch?> CurrentFindMatchProperty =
        AvaloniaProperty.Register<DiffEditor, FindMatch?>(nameof(CurrentFindMatch));

    /// <summary>Show the overview ruler (off for the base side of the split view, which follows the head side).</summary>
    public static readonly StyledProperty<bool> ShowRulerProperty =
        AvaloniaProperty.Register<DiffEditor, bool>(nameof(ShowRuler), true);

    private readonly TextEditor _editor;
    private readonly DiffBackgroundRenderer _background;
    private readonly DiffGutterMargin _gutter;
    private readonly ReviewCardGenerator _cards;
    private readonly TextMate.Installation _syntax;
    private readonly DiffOverviewRuler _ruler;
    private CancellationTokenSource? _scroll;
    private int? _pendingNewLine;

    public DiffEditor()
    {
        _editor = new HostingTextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = false,
            WordWrap = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            // The overview ruler is the vertical scrollbar.
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
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
        view.BackgroundRenderers.Add(new FindRenderer(this));
        view.LineTransformers.Add(new HunkHeaderColorizer(this));
        _cards = new ReviewCardGenerator(this);
        view.ElementGenerators.Add(_cards);

        _gutter = new DiffGutterMargin(this);
        _editor.TextArea.LeftMargins.Insert(0, _gutter);
        _gutter.MarkerClicked += id => MarkerClicked?.Invoke(this, id);
        _gutter.CommentRequested += request => LineCommentRequested?.Invoke(this, request);

        _syntax = SyntaxThemes.Install(_editor, null);
        _ruler = new DiffOverviewRuler(this);
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        layout.Children.Add(_editor);
        Grid.SetColumn(_ruler, 1);
        layout.Children.Add(_ruler);
        Content = layout;
        view.ScrollOffsetChanged += (_, _) =>
        {
            Scrolled?.Invoke(this, _editor.VerticalOffset);
            _ruler.InvalidateVisual();
        };

        // Card heights settle as they render, which moves everything below them on the map.
        view.VisualLinesChanged += (_, _) => _ruler.InvalidateVisual();

        view.SizeChanged += (_, _) =>
        {
            _cards.UpdateWidths(view.Bounds.Width);
            ApplyPendingReveal();
        };
        ActualThemeVariantChanged += (_, _) => view.Redraw();
    }

    public event EventHandler<string>? MarkerClicked;

    public event EventHandler<LineCommentRequest>? LineCommentRequested;

    /// <summary>The change navigated to (index into <see cref="DiffDocument.Changes"/>, -1 for none) moved.</summary>
    public event EventHandler<int>? CurrentChangeChanged;

    public bool ShowRuler
    {
        get => GetValue(ShowRulerProperty);
        set => SetValue(ShowRulerProperty, value);
    }

    /// <summary>The change last stepped or jumped to, or -1.</summary>
    public int CurrentChange => _ruler.CurrentChange;

    public LineSide? NumberSide
    {
        get => GetValue(NumberSideProperty);
        set => SetValue(NumberSideProperty, value);
    }

    public bool AllowComments
    {
        get => GetValue(AllowCommentsProperty);
        set => SetValue(AllowCommentsProperty, value);
    }

    public IReadOnlyList<FindMatch> FindMatches
    {
        get => GetValue(FindMatchesProperty);
        set => SetValue(FindMatchesProperty, value);
    }

    public FindMatch? CurrentFindMatch
    {
        get => GetValue(CurrentFindMatchProperty);
        set => SetValue(CurrentFindMatchProperty, value);
    }

    /// <summary>Document lines being selected in the gutter for a range comment.</summary>
    internal (int Start, int End)? GutterSelection { get; set; }

    /// <summary>The scroll position, so a side-by-side partner can follow.</summary>
    public double VerticalOffset => _editor.VerticalOffset;

    public event EventHandler<double>? Scrolled;

    /// <summary>Follows a partner editor's scroll (split view).</summary>
    public void ScrollTo(double offset)
    {
        if (Math.Abs(offset - _editor.VerticalOffset) > 0.5)
        {
            SetVerticalOffset(offset);
        }
    }

    /// <summary>Scrolls to a card (a thread, a draft) if this document has it.</summary>
    public Task<bool> RevealCardAsync(string cardId, IMotionService? motion)
    {
        if (Diff?.CardLine(cardId) is not { } line)
        {
            return Task.FromResult(false);
        }

        return RevealDocumentLineAsync(Math.Max(1, line - 1), motion).ContinueWith(_ => true, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Scrolls to a line on either side of the diff.</summary>
    public Task RevealLineAsync(LineSide side, int line, IMotionService? motion) =>
        side == LineSide.Left && Diff?.DocumentLineForOldLine(line) is { } documentLine
            ? RevealDocumentLineAsync(documentLine, motion)
            : RevealLineAsync(line, motion);

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
            var samePath = change.OldValue is DiffDocument old && old.Path == diff?.Path;
            var anchor = samePath ? TopAnchor() : null;
            _cards.Reset();

            // Re-rendering the same file (cards, full-file toggle) keeps the runs, so the reader stays on their change.
            SetCurrentChange(samePath && CurrentChange < (diff?.Changes.Count ?? 0) ? CurrentChange : -1);
            _editor.Document = new TextDocument(diff?.Text ?? "");
            SyntaxThemes.SetLanguage(_syntax, diff?.Path);
            SetVerticalOffset(0);
            if (anchor is null)
            {
                _editor.ScrollToHorizontalOffset(0);
            }

            _gutter.InvalidateMeasure();
            ApplyPendingReveal();
            RestoreAnchor(anchor);
        }
        else if (change.Property == NumberSideProperty)
        {
            _gutter.InvalidateMeasure();
        }
        else if (change.Property == ShowRulerProperty)
        {
            _ruler.IsVisible = ShowRuler;
        }
        else if (change.Property == MarkersProperty || change.Property == FocusRangeProperty || change.Property == ShowCardsProperty
            || change.Property == FindMatchesProperty || change.Property == CurrentFindMatchProperty || change.Property == AllowCommentsProperty)
        {
            _gutter.InvalidateVisual();
            _ruler.InvalidateVisual();
            TextView.Redraw();
        }
    }

    /// <summary>
    /// Moves to the next (+1) or previous (-1) change: after the one last stepped to, or else after what's at the top
    /// of the view. False when there is none that way (the caller may move to another file).
    /// </summary>
    public bool StepChange(int direction, IMotionService? motion)
    {
        if (Diff is not { Changes.Count: > 0 } diff)
        {
            return false;
        }

        // Not stepping yet (or scrolled away): next is the first change below the top of the view, previous the last
        // one above the reading line, where a revealed change lands a fifth of the way down.
        TextView.EnsureVisualLines();
        var offset = _editor.VerticalOffset;
        var top = offset <= 0 ? 0 : TextView.GetDocumentLineByVisualTop(offset)?.LineNumber ?? 0;
        var reading = TextView.GetDocumentLineByVisualTop(offset + (TextView.Bounds.Height * 0.22))?.LineNumber ?? 1;
        var current = CurrentChange >= 0 && CurrentChange < diff.Changes.Count && IsOnScreen(diff.Changes[CurrentChange]) ? CurrentChange : -1;
        var target = direction > 0
            ? current >= 0 ? current + 1 : IndexWhere(diff.Changes, c => c.StartLine > top, first: true)
            : current >= 0 ? current - 1 : IndexWhere(diff.Changes, c => c.StartLine < reading, first: false);

        if (target < 0 || target >= diff.Changes.Count)
        {
            return false;
        }

        JumpTo(diff.Changes[target].StartLine, target, motion);
        return true;
    }

    /// <summary>Reveals a document line (the ruler's marks); <paramref name="change"/> is the change it starts, or -1.</summary>
    public void JumpTo(int documentLine, int change, IMotionService? motion = null)
    {
        SetCurrentChange(change);
        _ = RevealDocumentLineAsync(documentLine, motion ?? App.Motion);
    }

    private void SetCurrentChange(int change)
    {
        if (_ruler.CurrentChange == change)
        {
            return;
        }

        _ruler.CurrentChange = change;
        _ruler.InvalidateVisual();
        CurrentChangeChanged?.Invoke(this, change);
    }

    private bool IsOnScreen(DiffChange change)
    {
        var top = TextView.GetVisualTopByDocumentLine(change.StartLine);
        return top >= _editor.VerticalOffset - 1 && top <= _editor.VerticalOffset + TextView.Bounds.Height;
    }

    private static int IndexWhere(IReadOnlyList<DiffChange> changes, Func<DiffChange, bool> predicate, bool first)
    {
        for (var k = 0; k < changes.Count; k++)
        {
            var i = first ? k : changes.Count - 1 - k;
            if (predicate(changes[i]))
            {
                return i;
            }
        }

        return -1;
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

    /// <summary>The first head-side line on screen and how far below the top it sits.</summary>
    private (int NewLine, double ScreenY)? TopAnchor()
    {
        if (!TextView.VisualLinesValid)
        {
            return null;
        }

        foreach (var line in TextView.VisualLines)
        {
            if (RowAt(line.FirstDocumentLine.LineNumber) is { NewNumber: > 0 } row)
            {
                return (row.NewNumber, line.VisualTop - TextView.VerticalOffset);
            }
        }

        return null;
    }

    /// <summary>
    /// Re-rendering the same file (full-file toggle, cards arriving) keeps the code under the reader where it was,
    /// unless a reveal is already queued.
    /// </summary>
    private void RestoreAnchor((int NewLine, double ScreenY)? anchor)
    {
        if (anchor is not { } a)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                if (_pendingNewLine is null && Diff?.DocumentLineForNewLine(a.NewLine) is { } documentLine)
                {
                    TextView.EnsureVisualLines();
                    SetVerticalOffset(TextView.GetVisualTopByDocumentLine(documentLine) - a.ScreenY);
                }
            },
            Avalonia.Threading.DispatcherPriority.Background);
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
                    DiffRowKind.Filler => owner.Brush("Surface.Sunken"),
                    _ => null,
                };

                var lineNumber = line.FirstDocumentLine.LineNumber;
                if (owner.GutterSelection is { } selection && lineNumber >= selection.Start && lineNumber <= selection.End && row.Kind != DiffRowKind.Card)
                {
                    drawingContext.FillRectangle(focus, rect);
                    drawingContext.FillRectangle(owner.Brush("Accent"), new Rect(-textView.HorizontalOffset - 12, y, 3, line.Height));
                }
                else if (range is { } r && row.NewNumber >= r.Start && row.NewNumber <= r.End)
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

    /// <summary>Find in file (Ctrl+F): every match tinted, the current one stronger (plan v2 M6).</summary>
    private sealed class FindRenderer(DiffEditor owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (owner.FindMatches is not { Count: > 0 } matches || textView.Document is not { } document || !textView.VisualLinesValid)
            {
                return;
            }

            var first = textView.VisualLines[0].FirstDocumentLine.LineNumber;
            var last = textView.VisualLines[^1].LastDocumentLine.LineNumber;
            var hit = owner.Brush("Find.Match");
            var current = owner.Brush("Find.Current");
            foreach (var match in matches)
            {
                if (match.DocumentLine < first || match.DocumentLine > last || match.DocumentLine > document.LineCount)
                {
                    continue;
                }

                var line = document.GetLineByNumber(match.DocumentLine);
                var start = line.Offset + Math.Min(match.Column, line.Length);
                var length = Math.Min(match.Length, line.EndOffset - start);
                var builder = new BackgroundGeometryBuilder { CornerRadius = 2 };
                builder.AddSegment(textView, new TextSegment { StartOffset = start, Length = length });
                if (builder.CreateGeometry() is { } geometry)
                {
                    drawingContext.DrawGeometry(match == owner.CurrentFindMatch ? current : hit, null, geometry);
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

    public event Action<LineCommentRequest>? CommentRequested;

    private int? _hoverLine;
    private (LineSide Side, int Line, int DocumentLine)? _pressed;
    private (LineSide Side, int Line, int DocumentLine)? _lastTarget;

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

    private int Columns => _owner.NumberSide is null ? 2 : 1;

    protected override Size MeasureOverride(Size availableSize) =>
        new(MarkerWidth + (ColumnWidth * Columns) + 18, 0);

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
            var side = _owner.NumberSide;
            if (row.OldNumber > 0 && side != LineSide.Right)
            {
                DrawRight(context, row.OldNumber.ToString(CultureInfo.InvariantCulture), MarkerWidth + ColumnWidth - 6, textY, numberBrush);
            }

            if (row.NewNumber > 0 && side != LineSide.Left)
            {
                DrawRight(context, row.NewNumber.ToString(CultureInfo.InvariantCulture), MarkerWidth + (ColumnWidth * Columns) - 6, textY, numberBrush);
            }

            if (row.Kind is DiffRowKind.Added or DiffRowKind.Removed)
            {
                var sign = Text(row.Kind == DiffRowKind.Added ? "+" : "−", row.Kind == DiffRowKind.Added ? addedSign : removedSign);
                context.DrawText(sign, new Point(MarkerWidth + (ColumnWidth * Columns) + 4, textY));
            }

            var centerY = y + (Math.Min(line.Height, view.DefaultLineHeight) / 2);
            var hasMarker = false;
            if (row.NewNumber > 0)
            {
                foreach (var marker in markers[row.NewNumber])
                {
                    DrawMarker(context, marker, centerY);
                    hasMarker = true;
                }
            }

            // "+" on the hovered line: click to comment, drag or shift-click for a range (plan v2 M3).
            if (!hasMarker && _owner.AllowComments && _hoverLine == line.FirstDocumentLine.LineNumber && DiffDocument.CommentTarget(row) is not null)
            {
                DrawPlus(context, centerY);
            }
        }

        // Evidence arrival (TDD §25.3): a new marker grows in once with a soft halo — no toasts.
        if (_arrivals.Values.Any(t => DateTime.UtcNow - t < ArrivalDuration))
        {
            Avalonia.Threading.DispatcherTimer.RunOnce(InvalidateVisual, TimeSpan.FromMilliseconds(16));
        }
    }

    private void DrawPlus(DrawingContext context, double centerY)
    {
        var size = 14.0;
        var rect = new Rect((MarkerWidth - size) / 2, centerY - (size / 2), size, size);
        context.DrawRectangle(_owner.Brush("Accent"), null, rect, 3, 3);
        var pen = new Pen(_owner.Brush("Accent.OnAccent"), 1.6, lineCap: PenLineCap.Round);
        context.DrawLine(pen, new Point(rect.Center.X, rect.Top + 3.5), new Point(rect.Center.X, rect.Bottom - 3.5));
        context.DrawLine(pen, new Point(rect.Left + 3.5, rect.Center.Y), new Point(rect.Right - 3.5, rect.Center.Y));
    }

    private void DrawMarker(DrawingContext context, GutterMarker marker, double centerY)
    {
        if (marker.Kind == "Risk")
        {
            DrawRisk(context, marker, centerY);
            return;
        }

        var brush = _owner.Brush(MarkerBrushKey(marker));
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
        var geometry = MarkerGeometry(marker, center, size);
        if (marker.IsResolved)
        {
            context.DrawGeometry(null, new Pen(brush, 1.8, lineCap: PenLineCap.Round), geometry);
        }
        else
        {
            context.DrawGeometry(brush, marker.IsCurrent ? new Pen(_owner.Brush("Surface.Pane"), 1.5) : null, geometry);
        }
    }

    /// <summary>A walkthrough risky area: a hollow diamond, ticked once checked off. Never a review finding's shape.</summary>
    private void DrawRisk(DrawingContext context, GutterMarker marker, double centerY) =>
        DrawMarkerShape(context, _owner, marker, new Point(MarkerWidth / 2, centerY), marker.IsCurrent ? 12.0 : 10.0);

    /// <summary>A marker's shape without the arrival animation (the overview ruler draws these small).</summary>
    internal static void DrawMarkerShape(DrawingContext context, DiffEditor owner, GutterMarker marker, Point center, double size)
    {
        var brush = owner.Brush(MarkerBrushKey(marker));
        var geometry = MarkerGeometry(marker, center, size);
        if (marker.IsResolved || marker.Kind == "Risk")
        {
            context.DrawGeometry(null, new Pen(brush, marker.IsCurrent && marker.Kind == "Risk" ? 2.2 : 1.6, lineCap: PenLineCap.Round), geometry);
        }
        else
        {
            context.DrawGeometry(brush, null, geometry);
        }
    }

    private static string MarkerBrushKey(GutterMarker marker) => marker.IsResolved ? "Marker.Verified" : marker.Kind switch
    {
        "Risk" => "Severity.Medium",
        "CorrectnessRisk" or "SecurityRisk" => "Marker.Correctness",
        "BehaviourChange" => "Marker.Behaviour",
        "ArchitectureDrift" => "Marker.Architecture",
        _ => "Marker.Deviation",
    };

    private static Geometry MarkerGeometry(GutterMarker marker, Point center, double size) => marker.IsResolved ? Check(center, size) : marker.Kind switch
    {
        "CorrectnessRisk" or "SecurityRisk" => Octagon(center, size),
        "BehaviourChange" => new EllipseGeometry(new Rect(center.X - (size / 2), center.Y - (size / 2), size, size)),
        "ArchitectureDrift" or "Risk" => Diamond(center, size),
        _ => Triangle(center, size),
    };

    private (int DocumentLine, DiffRow Row)? RowUnder(Point position)
    {
        var view = TextView;
        if (view is null || _owner.Diff is null || !view.VisualLinesValid)
        {
            return null;
        }

        var visualLine = view.GetVisualLineFromVisualTop(position.Y + view.VerticalOffset);
        return visualLine is not null && _owner.RowAt(visualLine.FirstDocumentLine.LineNumber) is { } row
            ? (visualLine.FirstDocumentLine.LineNumber, row)
            : null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var under = RowUnder(e.GetPosition(this));
        var hover = under?.DocumentLine;
        if (hover != _hoverLine)
        {
            _hoverLine = hover;
            InvalidateVisual();
        }

        // Dragging from a line selects a range on the same side.
        if (_pressed is { } start && under is { } current && DiffDocument.CommentTarget(current.Row) is { } target && target.Side == start.Side)
        {
            var selection = (Math.Min(start.DocumentLine, current.DocumentLine), Math.Max(start.DocumentLine, current.DocumentLine));
            if (_owner.GutterSelection != selection)
            {
                _owner.GutterSelection = selection;
                _owner.TextView.InvalidateLayer(KnownLayer.Background);
            }
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverLine = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (RowUnder(e.GetPosition(this)) is not { } under)
        {
            return;
        }

        var row = under.Row;
        if (row.NewNumber > 0 && _owner.Markers.FirstOrDefault(m => m.NewLine == row.NewNumber) is { } marker && e.GetPosition(this).X <= MarkerWidth)
        {
            MarkerClicked?.Invoke(marker.Id);
            e.Handled = true;
            return;
        }

        if (row.NewNumber > 0 && _owner.Markers.FirstOrDefault(m => m.NewLine == row.NewNumber) is { } lineMarker && !_owner.AllowComments)
        {
            MarkerClicked?.Invoke(lineMarker.Id);
            e.Handled = true;
            return;
        }

        if (!_owner.AllowComments || DiffDocument.CommentTarget(row) is not { } target)
        {
            return;
        }

        // Shift-click extends from the last line clicked, on the same side.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _lastTarget is { } previous && previous.Side == target.Side)
        {
            _owner.GutterSelection = null;
            _owner.TextView.InvalidateLayer(KnownLayer.Background);
            CommentRequested?.Invoke(new LineCommentRequest(target.Side, target.Line, previous.Line));
            e.Handled = true;
            return;
        }

        _pressed = (target.Side, target.Line, under.DocumentLine);
        _owner.GutterSelection = (under.DocumentLine, under.DocumentLine);
        _owner.TextView.InvalidateLayer(KnownLayer.Background);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressed is not { } start)
        {
            return;
        }

        _pressed = null;
        e.Pointer.Capture(null);
        var end = RowUnder(e.GetPosition(this)) is { } under && DiffDocument.CommentTarget(under.Row) is { } target && target.Side == start.Side
            ? target.Line
            : start.Line;
        _owner.GutterSelection = null;
        _owner.TextView.InvalidateLayer(KnownLayer.Background);
        _lastTarget = (start.Side, end, start.DocumentLine);
        CommentRequested?.Invoke(new LineCommentRequest(start.Side, end, end == start.Line ? null : start.Line));
        e.Handled = true;
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
        if (CurrentContext is null)
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

        if (!owner.ShowCards)
        {
            return new InlineObjectElement(1, new Border { Width = 0, Height = 0 });
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

/// <summary>
/// TextEditor redirects focus to its TextArea whenever anything inside it gets focus, which would make text boxes in
/// inline review cards impossible to type into. Let hosted inputs keep focus.
/// </summary>
internal sealed class HostingTextEditor : TextEditor
{
    protected override Type StyleKeyOverride => typeof(TextEditor);

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        // Inputs inside hosted cards (the comment box, buttons) keep their own focus.
        if (e.Source is TextBox or Button)
        {
            return;
        }

        base.OnGotFocus(e);
    }
}
