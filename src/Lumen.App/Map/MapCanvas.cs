using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Lumen.App.Diff;
using Lumen.App.ViewModels;

namespace Lumen.App.Map;

/// <summary>
/// Draws the Map: component boxes holding their file tiles, and in Blueprint the connectors between components.
/// One control drawn in one pass (like the diff ruler), so a 60-file pull request stays smooth. A layout change morphs
/// every box and tile from where it was to where it goes; reduced motion jumps straight there.
/// </summary>
public sealed class MapCanvas : Control
{
    public static readonly StyledProperty<MapViewModel?> MapProperty = AvaloniaProperty.Register<MapCanvas, MapViewModel?>(nameof(Map));

    /// <summary>How long a layout change morphs. <see cref="DevCapture"/> slows it down to record its frames.</summary>
    internal static TimeSpan MorphDuration { get; set; } = TimeSpan.FromMilliseconds(460);

    private MapGeometry _from = MapGeometry.Empty;
    private MapGeometry _to = MapGeometry.Empty;
    private MapGeometry _current = MapGeometry.Empty;
    private IReadOnlyList<MapRoute> _fadingRoutes = [];
    private double _progress = 1;
    private TimeSpan? _morphStart;
    private bool _framePending;
    private MapLayoutKind _laidOut;

    public MapCanvas()
    {
        ClipToBounds = true;
        Focusable = false;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public MapViewModel? Map
    {
        get => GetValue(MapProperty);
        set => SetValue(MapProperty, value);
    }

    /// <summary>The geometry being drawn (mid-morph while animating); tests read where things are.</summary>
    internal MapGeometry Current => _current;

    internal bool IsMorphing => _progress < 1;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MapProperty)
        {
            if (change.OldValue is MapViewModel old)
            {
                old.PropertyChanged -= OnMapChanged;
            }

            if (change.NewValue is MapViewModel map)
            {
                map.PropertyChanged += OnMapChanged;
            }

            Relayout(animate: false);
        }
        else if (change.Property == BoundsProperty)
        {
            // A resize mid-morph re-aims the morph instead of cutting it short.
            Relayout(animate: false, retarget: _progress < 1);
        }
    }

    private void OnMapChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MapViewModel.Layout):
            case nameof(MapViewModel.Version):
                Relayout(animate: true);
                break;
            case nameof(MapViewModel.Lens):
            case nameof(MapViewModel.SelectedPath):
            case nameof(MapViewModel.HoverPath):
                InvalidateVisual();
                break;
        }
    }

    private void Relayout(bool animate, bool retarget = false)
    {
        var map = Map;
        var size = Bounds.Size;
        if (map is null || size.Width <= 0 || size.Height <= 0)
        {
            _from = _to = _current = MapGeometry.Empty;
            InvalidateVisual();
            return;
        }

        var nodes = map.Components.Select(c => c.Node).ToList();
        var target = map.IsBlueprint ? MapLayout.Blueprint(nodes, map.DrawnLinks, size) : MapLayout.Terrain(nodes, size);
        if (retarget)
        {
            _to = target;
            return;
        }

        var leavingBlueprint = _laidOut == MapLayoutKind.Blueprint && !map.IsBlueprint;
        _laidOut = map.Layout;

        if (!animate || App.Motion.ReducedMotion || _current.Components.Count == 0)
        {
            _from = _to = _current = target;
            _fadingRoutes = [];
            _progress = 1;
            _morphStart = null;
            InvalidateVisual();
            return;
        }

        _from = _current;
        _to = target;
        _fadingRoutes = leavingBlueprint ? _current.Routes : [];
        _progress = 0;
        _morphStart = null;
        RequestFrame();
    }

    private void RequestFrame()
    {
        if (_framePending)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            _progress = 1;
            _current = _to;
            InvalidateVisual();
            return;
        }

        _framePending = true;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan time)
    {
        _framePending = false;
        _morphStart ??= time;
        var t = Math.Clamp((time - _morphStart.Value).TotalMilliseconds / MorphDuration.TotalMilliseconds, 0, 1);
        _progress = t;
        var eased = 1 - Math.Pow(1 - t, 3);
        _current = t < 1 ? MapGeometry.Lerp(_from, _to, eased) : _to;
        InvalidateVisual();
        if (t < 1)
        {
            RequestFrame();
        }
        else
        {
            _fadingRoutes = [];
        }
    }

    // Drawing ------------------------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brush("Surface.Pane"), new Rect(Bounds.Size));
        var map = Map;
        if (map is null || map.IsEmpty)
        {
            return;
        }

        var eased = 1 - Math.Pow(1 - _progress, 3);
        var ui = UiFamily();

        // Connectors fade in under the boxes when arriving at Blueprint, and fade out when leaving it.
        if (_fadingRoutes.Count > 0)
        {
            using (context.PushOpacity(1 - eased))
            {
                DrawRoutes(context, _fadingRoutes, [], ui);
            }
        }

        if (map.IsBlueprint)
        {
            using (context.PushOpacity(_from.Routes.Count > 0 && _from.Routes.Count == _to.Routes.Count ? 1 : eased))
            {
                DrawRoutes(context, _to.Routes, _to.Columns, ui);
            }
        }

        var selected = map.SelectedFile;
        foreach (var component in map.Components)
        {
            if (!_current.Components.TryGetValue(component.Id, out var box) || box.Width < 2 || box.Height < 2)
            {
                continue;
            }

            var isSelected = selected?.ComponentId == component.Id;
            context.DrawRectangle(
                Brush("Surface.Sunken"),
                new Pen(Brush(isSelected ? "Accent" : "Border.Strong"), isSelected ? 1.5 : 1),
                box,
                8,
                8);

            var title = map.IsTerrain ? $"{component.Title}  +{component.Additions.ToString("N0", CultureInfo.InvariantCulture)}" : component.Title;
            DrawText(context, title, new Point(box.X + 7, box.Y + 4), box.Width - 14, 11.5, FontWeight.SemiBold, Brush("Text.Primary"), ui);

            foreach (var file in component.Files)
            {
                if (_current.Files.TryGetValue(file.Path, out var tile) && tile.Width >= 1 && tile.Height >= 1)
                {
                    DrawTile(context, map, file, tile, ui);
                }
            }
        }
    }

    private void DrawRoutes(DrawingContext context, IReadOnlyList<MapRoute> routes, IReadOnlyList<MapColumn> columns, FontFamily ui)
    {
        foreach (var column in columns.Where(c => c.Label.Length > 0 && c.Label != "Other"))
        {
            DrawText(context, column.Label.ToUpperInvariant(), new Point(column.X + 2, MapLayout.Margin + 2), column.Width, 10.5, FontWeight.SemiBold, Brush("Text.Tertiary"), ui);
        }

        var ink = Brush("Border.Strong");
        var solid = new Pen(ink, 1.3);
        var dashed = new Pen(ink, 1.3, new DashStyle([3, 3], 0));
        var selected = Map?.SelectedComponent?.Id;
        var accent = new Pen(Brush("Accent"), 1.8);
        var accentDashed = new Pen(Brush("Accent"), 1.8, new DashStyle([3, 3], 0));

        // Routes touching the selected component draw last, in the accent colour (their labels are beside the Map).
        foreach (var route in routes.OrderBy(r => r.From == selected || r.To == selected))
        {
            var hot = route.From == selected || route.To == selected;
            var pen = hot ? (route.Inferred ? accentDashed : accent) : route.Inferred ? dashed : solid;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(route.Points[0], false);
                foreach (var point in route.Points.Skip(1))
                {
                    g.LineTo(point);
                }

                g.EndFigure(false);
            }

            context.DrawGeometry(null, pen, geometry);
            DrawArrow(context, route.Points[^2], route.Points[^1], pen.Brush!);

        }
    }

    private static void DrawArrow(DrawingContext context, Point from, Point to, IBrush brush)
    {
        var direction = to.X >= from.X ? 1 : -1;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(to, true);
            g.LineTo(new Point(to.X - (7 * direction), to.Y - 4.5));
            g.LineTo(new Point(to.X - (7 * direction), to.Y + 4.5));
            g.EndFigure(true);
        }

        context.DrawGeometry(brush, null, geometry);
    }

    private void DrawTile(DrawingContext context, MapViewModel map, MapFileViewModel file, Rect tile, FontFamily ui)
    {
        var (fill, stroke, text) = Look(map.Lens, file);
        var isSelected = map.SelectedPath == file.Path;
        var isHover = map.HoverPath == file.Path;
        var pen = isSelected ? new Pen(Brush("Text.Primary"), 2) : isHover ? new Pen(Brush("Accent"), 1.5) : new Pen(stroke, 1);
        context.DrawRectangle(fill, pen, tile, 4, 4);

        using (context.PushClip(tile))
        {
            if (tile.Width > 44 && tile.Height > 19)
            {
                DrawText(context, TileName(file.Name), new Point(tile.X + 5, tile.Y + 3), tile.Width - 10, 11, FontWeight.Medium, text, ui);
            }

            if (tile.Width > 40 && tile.Height > 36)
            {
                DrawText(context, $"+{file.Additions.ToString(CultureInfo.InvariantCulture)}", new Point(tile.X + 5, tile.Y + 18), tile.Width - 10, 10.5, FontWeight.Normal, Brush("Text.Secondary"), ui);
            }

            // Badges: risks (amber) and open threads (accent), bottom right.
            if (map.Lens == MapLens.Attention && tile.Width > 22 && tile.Height > 18)
            {
                var right = tile.Right - 4;
                if (file.OpenThreads > 0)
                {
                    right = Badge(context, file.OpenThreads.ToString(CultureInfo.InvariantCulture), right, tile.Bottom - 4, Brush("Accent"), ui) - 3;
                }

                if (file.Risks.Count > 0)
                {
                    Badge(context, file.Risks.Count == 1 ? "!" : $"! {file.Risks.Count}", right, tile.Bottom - 4, Brush("Severity.Medium"), ui);
                }
            }
        }
    }

    /// <summary>"EnrichmentService" for EnrichmentService.cs, but "Ai.cshtml.cs" in full so it isn't read as the view.</summary>
    internal static string TileName(string name) =>
        System.IO.Path.GetFileNameWithoutExtension(name) is var stem && stem.Contains('.', StringComparison.Ordinal) ? name : stem;

    private double Badge(DrawingContext context, string label, double right, double bottom, IBrush fill, FontFamily ui)
    {
        var text = Format(label, 9.5, FontWeight.Bold, Brush("Accent.OnAccent"), ui, 60);
        var width = Math.Max(text.Width + 9, 15);
        var rect = new Rect(right - width, bottom - 15, width, 15);
        context.DrawRectangle(fill, null, rect, 7.5, 7.5);
        context.DrawText(text, new Point(rect.X + ((width - text.Width) / 2), rect.Y + ((15 - text.Height) / 2)));
        return rect.X;
    }

    private (IBrush Fill, IBrush Stroke, IBrush Text) Look(MapLens lens, MapFileViewModel file)
    {
        (IBrush, IBrush, IBrush) Of(string fill, string stroke, string text) => (Brush(fill), Brush(stroke), Brush(text));
        var quiet = Of("Surface.Hover", "Border.Subtle", "Text.Secondary");
        return lens switch
        {
            MapLens.SinceReview => file.IsSinceReview ? Of("Accent.Soft", "Accent", "Text.Primary") : Of("Surface.Hover", "Border.Subtle", "Text.Tertiary"),
            MapLens.Tests when !file.IsCode => quiet,
            MapLens.Tests => file.IsTested ? Of("Accent.Soft", "Accent", "Text.Primary") : Of("Severity.Medium.Soft", "Severity.Medium", "Text.Primary"),
            _ when file.Risks.Count > 0 => Of("Severity.Medium.Soft", "Severity.Medium", "Text.Primary"),
            _ when !file.IsCode => quiet,
            _ => Of("Surface.Raised", "Border.Strong", "Text.Primary"),
        };
    }

    private static void DrawText(DrawingContext context, string text, Point origin, double maxWidth, double size, FontWeight weight, IBrush brush, FontFamily ui)
    {
        if (maxWidth < 8)
        {
            return;
        }

        context.DrawText(Format(text, size, weight, brush, ui, maxWidth), origin);
    }

    private static FormattedText Format(string text, double size, FontWeight weight, IBrush brush, FontFamily ui, double maxWidth) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(ui, FontStyle.Normal, weight), size, brush)
        {
            MaxTextWidth = Math.Max(maxWidth, 1),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

    private FontFamily UiFamily() =>
        this.TryFindResource("UiFont", ActualThemeVariant, out var value) && value is FontFamily family ? family : FontFamily.Default;

    private IBrush Brush(string key) => ThemeResources.Brush(this, key);

    // Input --------------------------------------------------------------------------------------------

    /// <summary>The file at a point, or the component whose header is there.</summary>
    internal (string? File, string? Component) HitTest(Point point)
    {
        foreach (var (path, rect) in _current.Files)
        {
            if (rect.Contains(point))
            {
                return (path, null);
            }
        }

        foreach (var (id, rect) in _current.Components)
        {
            if (rect.Contains(point))
            {
                return (null, id);
            }
        }

        return (null, null);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Map is not { } map || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var (file, component) = HitTest(e.GetPosition(this));
        if (file is not null)
        {
            map.Select(file);
            if (e.ClickCount == 2)
            {
                map.Open();
            }
        }
        else if (component is not null)
        {
            map.SelectComponent(component);
        }

        e.Handled = file is not null || component is not null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Map is not { } map)
        {
            return;
        }

        var (file, _) = HitTest(e.GetPosition(this));
        if (map.HoverPath != file)
        {
            map.HoverPath = file;
            ToolTip.SetTip(this, file is null ? null : Tip(map, file));
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (Map is { } map)
        {
            map.HoverPath = null;
        }
    }

    private static string? Tip(MapViewModel map, string path) => map.File(path) is { } file
        ? string.Create(CultureInfo.InvariantCulture, $"{file.Path}  +{file.Additions} −{file.Deletions}")
        : null;
}
