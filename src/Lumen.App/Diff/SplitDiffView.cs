using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Lumen.App.Motion;

namespace Lumen.App.Diff;

/// <summary>
/// Side-by-side diff (plan v2 M6): the base on the left, the head on the right, scrolled together. Cards sit on the
/// side their line is on, with a spacer of the same height opposite so rows stay aligned.
/// </summary>
public sealed class SplitDiffView : UserControl
{
    public static readonly StyledProperty<DiffDocument?> DiffProperty =
        AvaloniaProperty.Register<SplitDiffView, DiffDocument?>(nameof(Diff));

    public static readonly StyledProperty<IReadOnlyList<GutterMarker>> MarkersProperty =
        AvaloniaProperty.Register<SplitDiffView, IReadOnlyList<GutterMarker>>(nameof(Markers), []);

    public static readonly StyledProperty<(int Start, int End)?> FocusRangeProperty =
        AvaloniaProperty.Register<SplitDiffView, (int Start, int End)?>(nameof(FocusRange));

    public static readonly StyledProperty<IReadOnlyList<FindMatch>> FindMatchesProperty =
        AvaloniaProperty.Register<SplitDiffView, IReadOnlyList<FindMatch>>(nameof(FindMatches), []);

    public static readonly StyledProperty<FindMatch?> CurrentFindMatchProperty =
        AvaloniaProperty.Register<SplitDiffView, FindMatch?>(nameof(CurrentFindMatch));

    private readonly DiffEditor _left = new() { NumberSide = LineSide.Left, Name = "SplitLeft", ShowRuler = false };
    private readonly DiffEditor _right = new() { NumberSide = LineSide.Right, Name = "SplitRight" };
    private readonly Dictionary<string, double> _heights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _spacers = new(StringComparer.Ordinal);
    private SplitDocument? _split;
    private bool _syncing;

    public SplitDiffView()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
        grid.ColumnDefinitions[0].MinWidth = 120;
        grid.ColumnDefinitions[2].MinWidth = 120;
        grid.Children.Add(_left);
        // A 1px line, with a wider transparent splitter on top so the panes can be resized by dragging the middle.
        var divider = new Border
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false,
            [!Border.BackgroundProperty] = this.GetResourceObservable("Border.Subtle").ToBinding(),
        };
        Grid.SetColumn(divider, 1);
        grid.Children.Add(divider);
        var splitter = new GridSplitter
        {
            Name = "SplitDivider",
            Width = 5,
            Background = Brushes.Transparent,
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
        };
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);
        Grid.SetColumn(_right, 2);
        grid.Children.Add(_right);
        Content = grid;

        _left.CardFactory = id => Card(id, _right);
        _right.CardFactory = id => Card(id, _left);
        _left.Scrolled += (_, offset) => Follow(_right, offset);
        _right.Scrolled += (_, offset) => Follow(_left, offset);
        _left.MarkerClicked += (_, id) => MarkerClicked?.Invoke(this, id);
        _right.MarkerClicked += (_, id) => MarkerClicked?.Invoke(this, id);
        _left.LineCommentRequested += (_, r) => LineCommentRequested?.Invoke(this, r);
        _right.LineCommentRequested += (_, r) => LineCommentRequested?.Invoke(this, r);
    }

    public event EventHandler<string>? MarkerClicked;

    public event EventHandler<LineCommentRequest>? LineCommentRequested;

    /// <summary>Builds the real card for an id (the same factory the unified editor uses).</summary>
    public Func<string, Control?>? CardFactory { get; set; }

    public DiffEditor Left => _left;

    public DiffEditor Right => _right;

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

    public (int Start, int End)? FocusRange
    {
        get => GetValue(FocusRangeProperty);
        set => SetValue(FocusRangeProperty, value);
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

    public Control? CardFor(string id) => _left.CardFor(id) ?? _right.CardFor(id);

    public Task RevealLineAsync(LineSide side, int line, IMotionService? motion) =>
        side == LineSide.Left && _split?.Left.DocumentLineForOldLine(line) is { } left
            ? _left.RevealDocumentLineAsync(left, motion)
            : _right.RevealLineAsync(line, motion);

    public async Task<bool> RevealCardAsync(string id, IMotionService? motion) =>
        await _right.RevealCardAsync(id, motion).ConfigureAwait(true) || await _left.RevealCardAsync(id, motion).ConfigureAwait(true);

    /// <summary>A unified document line (e.g. a find match) on whichever side shows it.</summary>
    public Task RevealUnifiedLineAsync(int unifiedLine, IMotionService? motion)
    {
        if (_split is null || unifiedLine >= _split.RightLines.Count)
        {
            return Task.CompletedTask;
        }

        return _split.RightLines[unifiedLine] is > 0 and var right
            ? _right.RevealDocumentLineAsync(right, motion)
            : _split.LeftLines[unifiedLine] is > 0 and var left ? _left.RevealDocumentLineAsync(left, motion) : Task.CompletedTask;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DiffProperty)
        {
            _spacers.Clear();
            _split = Diff?.Split();
            _left.Diff = _split?.Left;
            _right.Diff = _split?.Right;
            MapFind();
        }
        else if (change.Property == MarkersProperty)
        {
            // Markers are head-line based: they belong to the right side.
            _right.Markers = Markers;
        }
        else if (change.Property == FocusRangeProperty)
        {
            _right.FocusRange = FocusRange;
        }
        else if (change.Property == FindMatchesProperty || change.Property == CurrentFindMatchProperty)
        {
            MapFind();
        }
    }

    /// <summary>Find runs on the unified text; each hit is shown on the side that has its line.</summary>
    private void MapFind()
    {
        if (_split is null)
        {
            return;
        }

        FindMatch? Map(FindMatch match, IReadOnlyList<int> lines) =>
            match.DocumentLine < lines.Count && lines[match.DocumentLine] > 0 ? match with { DocumentLine = lines[match.DocumentLine] } : null;

        _left.FindMatches = [.. FindMatches.Select(m => Map(m, _split.LeftLines)).OfType<FindMatch>()];
        _right.FindMatches = [.. FindMatches.Select(m => Map(m, _split.RightLines)).OfType<FindMatch>()];
        _left.CurrentFindMatch = CurrentFindMatch is { } c ? Map(c, _split.LeftLines) : null;
        _right.CurrentFindMatch = CurrentFindMatch is { } d ? Map(d, _split.RightLines) : null;
    }

    private void Follow(DiffEditor other, double offset)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            other.ScrollTo(offset);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>A real card, or a spacer that keeps the height of the real card on the other side.</summary>
    private Control? Card(string id, DiffEditor opposite)
    {
        if (id.StartsWith("spacer:", StringComparison.Ordinal))
        {
            var cardId = id["spacer:".Length..];
            var spacer = new Border { Height = _heights.GetValueOrDefault(cardId, opposite.CardFor(cardId)?.Bounds.Height ?? 80) };
            _spacers[cardId] = spacer;
            return spacer;
        }

        var card = CardFactory?.Invoke(id);
        if (card is not null)
        {
            card.SizeChanged += (_, e) =>
            {
                _heights[id] = e.NewSize.Height;
                if (_spacers.TryGetValue(id, out var spacer) && Math.Abs(spacer.Height - e.NewSize.Height) > 0.5)
                {
                    spacer.Height = e.NewSize.Height;
                }
            };
        }

        return card;
    }
}
