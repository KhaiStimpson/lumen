using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Lumen.App.Diff;

/// <summary>
/// The diff's scrollbar, drawn as a map of the file: removed lines in the left lane, added lines in the right, review
/// markers, threads, drafts and find matches on top, and the visible part as a band. Clicking a mark jumps to it;
/// clicking or dragging elsewhere scrolls there.
/// </summary>
internal sealed class DiffOverviewRuler : Control
{
    public const double RulerWidth = 18;

    private const double LaneWidth = 5;
    private const double RemovedLane = 3;
    private const double AddedLane = 10;
    private const double MinMarkHeight = 3;
    private const double HitSlop = 3;

    private readonly DiffEditor _owner;
    private readonly List<(Rect Bounds, int DocumentLine, int Change)> _hits = [];
    private bool _dragging;

    public DiffOverviewRuler(DiffEditor owner)
    {
        _owner = owner;
        Width = RulerWidth;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>The change last jumped to (outlined), or -1.</summary>
    public int CurrentChange { get; set; } = -1;

    public override void Render(DrawingContext context)
    {
        _hits.Clear();
        context.FillRectangle(_owner.Brush("Surface.Sunken"), new Rect(Bounds.Size));
        context.FillRectangle(_owner.Brush("Border.Subtle"), new Rect(0, 0, 1, Bounds.Height));

        var diff = _owner.Diff;
        var view = _owner.TextView;
        if (diff is null || diff.IsEmpty || Bounds.Height <= 0)
        {
            return;
        }

        var scale = Scale();
        if (scale <= 0)
        {
            return;
        }

        double Top(int documentLine) => view.GetVisualTopByDocumentLine(Math.Clamp(documentLine, 1, diff.Rows.Count)) * scale;
        double Bottom(int documentLine) => documentLine >= diff.Rows.Count
            ? view.DocumentHeight * scale
            : view.GetVisualTopByDocumentLine(documentLine + 1) * scale;

        var added = _owner.Brush("Diff.Added.Sign");
        var removed = _owner.Brush("Diff.Removed.Sign");
        var outline = new Pen(_owner.Brush("Text.Primary"), 1.2);

        // In split view a filler row means the other side has the line: removed lines on the head side, added on the base side.
        var fillerIsAdded = _owner.NumberSide == LineSide.Left;
        for (var c = 0; c < diff.Changes.Count; c++)
        {
            var change = diff.Changes[c];
            var line = change.StartLine;
            while (line <= change.EndLine)
            {
                var kind = Lane(diff.RowAt(line)!.Kind, fillerIsAdded);
                var end = line;
                while (end + 1 <= change.EndLine && Lane(diff.RowAt(end + 1)!.Kind, fillerIsAdded) is var next && (next == kind || next is null))
                {
                    end++;
                }

                if (kind is { } isAdded)
                {
                    var top = Top(line);
                    var rect = new Rect(isAdded ? AddedLane : RemovedLane, top, LaneWidth, Math.Max(MinMarkHeight, Bottom(end) - top));
                    context.DrawRectangle(isAdded ? added : removed, c == CurrentChange ? outline : null, rect, 1, 1);
                    _hits.Add((rect.Inflate(new Thickness(2, HitSlop)), change.StartLine, c));
                }

                line = end + 1;
            }
        }

        if (_owner.FindMatches is { Count: > 0 } matches)
        {
            var brush = _owner.Brush("Find.Current");
            foreach (var match in matches)
            {
                context.FillRectangle(brush, new Rect(2, Top(match.DocumentLine), Bounds.Width - 3, 2));
            }
        }

        // Threads and drafts sit on their card's line; review markers on the line they point at.
        for (var i = 0; i < diff.Rows.Count; i++)
        {
            if (diff.Rows[i].CardId is { } id && Card(context, id, new Point(AddedLane + (LaneWidth / 2), Top(i + 1) + 4)))
            {
                _hits.Add((new Rect(AddedLane - 3, Top(i + 1) - 1, LaneWidth + 6, 10), i + 1, -1));
            }
        }

        foreach (var marker in _owner.Markers)
        {
            if (diff.DocumentLineForNewLine(marker.NewLine) is not { } documentLine)
            {
                continue;
            }

            var center = new Point(AddedLane + (LaneWidth / 2), Top(documentLine) + 3);
            DiffGutterMargin.DrawMarkerShape(context, _owner, marker, center, marker.IsCurrent ? 9 : 7);
            _hits.Add((new Rect(center.X - 5, center.Y - 5, 10, 10), documentLine, -1));
        }

        // The visible part of the file.
        var viewport = view.Bounds.Height;
        var band = new Rect(0.5, view.VerticalOffset * scale, Bounds.Width - 0.5, Math.Max(12, viewport * scale));
        context.DrawRectangle(_owner.Brush("Ruler.Thumb"), new Pen(_owner.Brush("Ruler.ThumbEdge"), 1), band, 2, 2);
    }

    private static bool? Lane(DiffRowKind kind, bool fillerIsAdded) => kind switch
    {
        DiffRowKind.Added => true,
        DiffRowKind.Removed => false,
        DiffRowKind.Filler => fillerIsAdded,
        _ => null,
    };

    private bool Card(DrawingContext context, string id, Point center)
    {
        if (id.StartsWith("thread:", StringComparison.Ordinal))
        {
            context.DrawEllipse(null, new Pen(_owner.Brush("Accent"), 1.6), center, 3, 3);
            return true;
        }

        if (id.StartsWith("draft:", StringComparison.Ordinal))
        {
            context.DrawRectangle(_owner.Brush("Accent"), null, new Rect(center.X - 3, center.Y - 3, 6, 6), 1, 1);
            return true;
        }

        return false;
    }

    /// <summary>Ruler pixels per document pixel. A file shorter than the viewport keeps its true positions.</summary>
    private double Scale()
    {
        var view = _owner.TextView;
        var height = Math.Max(view.DocumentHeight, view.Bounds.Height);
        return height > 0 ? Bounds.Height / height : 0;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        var position = e.GetPosition(this);
        if (HitAt(position) is { } hit)
        {
            _owner.JumpTo(hit.DocumentLine, hit.Change);
            return;
        }

        _dragging = true;
        e.Pointer.Capture(this);
        ScrollTo(position.Y);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        if (_dragging)
        {
            ScrollTo(position.Y);
            return;
        }

        ToolTip.SetTip(this, HitAt(position) is { Change: >= 0 } hit ? Describe(hit.Change) : null);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var view = _owner.TextView;
        _owner.ScrollTo(view.VerticalOffset - (e.Delta.Y * 50));
        e.Handled = true;
    }

    /// <summary>Centres the viewport on the clicked point of the map.</summary>
    private void ScrollTo(double y)
    {
        var scale = Scale();
        if (scale > 0)
        {
            _owner.ScrollTo((y / scale) - (_owner.TextView.Bounds.Height / 2));
        }
    }

    private (Rect Bounds, int DocumentLine, int Change)? HitAt(Point position)
    {
        // Markers were added last and sit on top.
        for (var i = _hits.Count - 1; i >= 0; i--)
        {
            if (_hits[i].Bounds.Contains(position))
            {
                return _hits[i];
            }
        }

        return null;
    }

    private string? Describe(int index)
    {
        if (_owner.Diff is not { } diff || index >= diff.Changes.Count)
        {
            return null;
        }

        var change = diff.Changes[index];
        var numbers = Enumerable.Range(change.StartLine, change.EndLine - change.StartLine + 1)
            .Select(diff.RowAt)
            .Select(r => r is null ? 0 : _owner.NumberSide == LineSide.Left || r.Kind == DiffRowKind.Removed ? r.OldNumber : r.NewNumber)
            .Where(n => n > 0)
            .ToList();
        var lines = numbers.Count == 0 ? "" : numbers.Min() == numbers.Max()
            ? string.Create(CultureInfo.InvariantCulture, $"Line {numbers.Min()} · ")
            : string.Create(CultureInfo.InvariantCulture, $"Lines {numbers.Min()}–{numbers.Max()} · ");
        var removed = change.Removed + (_owner.NumberSide == LineSide.Right ? change.Fillers : 0);
        var added = change.Added + (_owner.NumberSide == LineSide.Left ? change.Fillers : 0);
        return string.Create(CultureInfo.InvariantCulture, $"Change {index + 1} of {diff.Changes.Count} · {lines}+{added} −{removed}");
    }
}
