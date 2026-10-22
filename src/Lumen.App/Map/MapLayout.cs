using Avalonia;
using Lumen.Contracts;

namespace Lumen.App.Map;

/// <summary>A component on the Map: a box holding its files.</summary>
public sealed record MapNode(string Id, string Title, ComponentLayer Layer, IReadOnlyList<MapLeaf> Files)
{
    public double Weight => Files.Sum(f => f.Weight);
}

/// <summary>A file tile, sized by lines added (with a floor so one-line files stay clickable).</summary>
public sealed record MapLeaf(string Path, int Additions)
{
    public const int MinWeight = 6;

    public double Weight => Math.Max(Additions, MinWeight);
}

/// <summary><c>From</c> uses <c>To</c>.</summary>
public sealed record MapLink(string From, string To, bool Inferred, string Label, LinkKind Kind = LinkKind.References)
{
    /// <summary>
    /// How the link is drawn: an implementation is drawn from the interface it implements ("implemented by"), so
    /// providers sit after the seam their callers go through rather than before it.
    /// </summary>
    public MapLink Drawn => Kind == LinkKind.Implements
        ? this with { From = To, To = From, Label = Label.Length > 0 ? Label : "implemented by" }
        : this;
}

/// <summary>An orthogonal connector between two components; the arrow is drawn at the last point.</summary>
public sealed record MapRoute(string From, string To, IReadOnlyList<Point> Points, bool Inferred, string Label);

/// <summary>A column of the blueprint, labelled by the layer most of its components share.</summary>
public sealed record MapColumn(double X, double Width, string Label);

/// <summary>Where everything goes for one layout. Both layouts place the same keys, so one can morph into the other.</summary>
public sealed record MapGeometry(
    IReadOnlyDictionary<string, Rect> Components,
    IReadOnlyDictionary<string, Rect> Files,
    IReadOnlyList<MapRoute> Routes,
    IReadOnlyList<MapColumn> Columns)
{
    public static readonly MapGeometry Empty = new(new Dictionary<string, Rect>(), new Dictionary<string, Rect>(), [], []);

    /// <summary>The geometry part-way from <paramref name="from"/> to <paramref name="to"/>; routes and columns come from <paramref name="to"/>.</summary>
    public static MapGeometry Lerp(MapGeometry from, MapGeometry to, double t)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return new MapGeometry(Mix(from.Components, to.Components, t), Mix(from.Files, to.Files, t), to.Routes, to.Columns);
    }

    private static Dictionary<string, Rect> Mix(IReadOnlyDictionary<string, Rect> from, IReadOnlyDictionary<string, Rect> to, double t)
    {
        var mixed = new Dictionary<string, Rect>(to.Count, StringComparer.Ordinal);
        foreach (var (key, target) in to)
        {
            if (!from.TryGetValue(key, out var start))
            {
                mixed[key] = target;
                continue;
            }

            mixed[key] = new Rect(
                start.X + ((target.X - start.X) * t),
                start.Y + ((target.Y - start.Y) * t),
                start.Width + ((target.Width - start.Width) * t),
                start.Height + ((target.Height - start.Height) * t));
        }

        return mixed;
    }
}

/// <summary>
/// The Map's two layouts (plan v3). <b>Terrain</b> sizes every component and file by lines added (a squarified
/// treemap); <b>Blueprint</b> places the components by how they use each other (a layered graph drawing, callers left of
/// what they call) and sizes the files inside each box the same way. Pure geometry, so it is tested without a UI.
/// </summary>
public static class MapLayout
{
    public const double HeaderHeight = 22;
    public const double Inset = 4;
    public const double Margin = 14;
    public const double ColumnHeaderHeight = 26;
    public const double ColumnGap = 46;
    public const double RowGap = 12;
    public const double MinNodeHeight = 52;

    /// <summary>Lays items out in <paramref name="area"/> with areas proportional to their weights, keeping tiles near square.</summary>
    public static Dictionary<string, Rect> Squarify(IEnumerable<(string Key, double Weight)> items, Rect area)
    {
        ArgumentNullException.ThrowIfNull(items);
        var result = new Dictionary<string, Rect>(StringComparer.Ordinal);
        var rest = items.Where(i => i.Weight > 0).OrderByDescending(i => i.Weight).ThenBy(i => i.Key, StringComparer.Ordinal).ToList();
        double x = area.X, y = area.Y, w = Math.Max(area.Width, 0), h = Math.Max(area.Height, 0);
        while (rest.Count > 0)
        {
            var total = rest.Sum(i => i.Weight);
            if (w <= 0 || h <= 0 || total <= 0)
            {
                foreach (var item in rest)
                {
                    result[item.Key] = new Rect(x, y, Math.Max(w, 0), Math.Max(h, 0));
                }

                break;
            }

            var scale = w * h / total;
            var side = Math.Min(w, h);
            var row = new List<(string Key, double Weight)>();
            var best = double.MaxValue;
            foreach (var item in rest)
            {
                row.Add(item);
                var worst = Worst(row, side, scale);
                if (worst > best)
                {
                    row.RemoveAt(row.Count - 1);
                    break;
                }

                best = worst;
            }

            var rowWeight = row.Sum(i => i.Weight);
            var fraction = rowWeight / total;
            if (w >= h)
            {
                var columnWidth = w * fraction;
                var cy = y;
                foreach (var item in row)
                {
                    var ch = h * item.Weight / rowWeight;
                    result[item.Key] = new Rect(x, cy, columnWidth, ch);
                    cy += ch;
                }

                x += columnWidth;
                w -= columnWidth;
            }
            else
            {
                var rowHeight = h * fraction;
                var cx = x;
                foreach (var item in row)
                {
                    var cw = w * item.Weight / rowWeight;
                    result[item.Key] = new Rect(cx, y, cw, rowHeight);
                    cx += cw;
                }

                y += rowHeight;
                h -= rowHeight;
            }

            rest.RemoveRange(0, row.Count);
        }

        return result;
    }

    /// <summary>Every component sized by its lines, then every file inside it.</summary>
    public static MapGeometry Terrain(IReadOnlyList<MapNode> nodes, Size size)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var area = new Rect(Margin, Margin, Math.Max(size.Width - (2 * Margin), 0), Math.Max(size.Height - (2 * Margin), 0));
        var boxes = Squarify(nodes.Select(n => (n.Id, n.Weight)), area);
        var components = boxes.ToDictionary(b => b.Key, b => Deflate(b.Value, 3), StringComparer.Ordinal);
        return new MapGeometry(components, FilesIn(nodes, components), [], []);
    }

    /// <summary>Components in columns by how they use each other, connectors between them, files inside each box.</summary>
    public static MapGeometry Blueprint(IReadOnlyList<MapNode> nodes, IReadOnlyList<MapLink> links, Size size)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(links);
        if (nodes.Count == 0)
        {
            return MapGeometry.Empty;
        }

        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var edges = links.Where(l => l.From != l.To && byId.ContainsKey(l.From) && byId.ContainsKey(l.To))
            .Select(l => l.Drawn)
            .DistinctBy(l => (l.From, l.To))
            .ToList();
        var column = Columns(nodes, edges);
        var count = column.Values.Max() + 1;

        // Order within each column: by layer and size first, then a few barycentre sweeps to untangle the connectors.
        var columns = Enumerable.Range(0, count)
            .Select(c => nodes.Where(n => column[n.Id] == c).OrderBy(n => LayerOrder(n.Layer)).ThenByDescending(n => n.Weight).Select(n => n.Id).ToList())
            .ToList();
        var position = new Dictionary<string, double>(StringComparer.Ordinal);
        void Number()
        {
            foreach (var col in columns)
            {
                for (var i = 0; i < col.Count; i++)
                {
                    position[col[i]] = col.Count == 1 ? 0.5 : (double)i / (col.Count - 1);
                }
            }
        }

        Number();
        for (var sweep = 0; sweep < 4; sweep++)
        {
            var forward = sweep % 2 == 0;
            foreach (var col in forward ? columns.Skip(1) : Enumerable.Reverse(columns).Skip(1))
            {
                var keyed = col.Select((id, i) =>
                {
                    var neighbours = edges.Where(e => forward ? e.To == id : e.From == id).Select(e => forward ? e.From : e.To)
                        .Where(n => column[n] != column[id]).ToList();
                    return (Id: id, Key: neighbours.Count > 0 ? neighbours.Average(n => position[n]) : position[id], Index: i);
                }).OrderBy(k => k.Key).ThenBy(k => k.Index).Select(k => k.Id).ToList();
                col.Clear();
                col.AddRange(keyed);
                Number();
            }
        }

        // Sizes: columns share the width; boxes in a column share its height by the square root of their lines.
        var width = Math.Max(size.Width - (2 * Margin), 0);
        var height = Math.Max(size.Height - (2 * Margin) - ColumnHeaderHeight, 0);
        var columnWidth = Math.Max((width - ((count - 1) * ColumnGap)) / count, 40);
        var components = new Dictionary<string, Rect>(StringComparer.Ordinal);
        var labels = new List<MapColumn>();
        for (var c = 0; c < count; c++)
        {
            var x = Margin + (c * (columnWidth + ColumnGap));
            var col = columns[c];
            labels.Add(new MapColumn(x, columnWidth, ColumnLabel(col.Select(id => byId[id].Layer))));
            if (col.Count == 0)
            {
                continue;
            }

            var available = Math.Max(height - ((col.Count - 1) * RowGap), col.Count * MinNodeHeight);
            var roots = col.Select(id => Math.Sqrt(byId[id].Weight)).ToList();
            var heights = roots.Select(r => Math.Max(MinNodeHeight, available * r / roots.Sum())).ToList();
            var total = heights.Sum();
            if (total > available)
            {
                // Floors pushed it over: take the excess from the boxes above the floor.
                var flexible = heights.Where(h => h > MinNodeHeight).Sum(h => h - MinNodeHeight);
                var excess = total - available;
                heights = [.. heights.Select(h => h > MinNodeHeight && flexible > 0 ? h - ((h - MinNodeHeight) * Math.Min(1, excess / flexible)) : h)];
            }

            var stack = heights.Sum() + ((col.Count - 1) * RowGap);
            var y = Margin + ColumnHeaderHeight + Math.Max((height - stack) / 2, 0);
            for (var i = 0; i < col.Count; i++)
            {
                components[col[i]] = new Rect(x, y, columnWidth, heights[i]);
                y += heights[i] + RowGap;
            }
        }

        return new MapGeometry(components, FilesIn(nodes, components), Routes(edges, components, column), labels);
    }

    /// <summary>
    /// Columns by longest path from the components nothing uses (cycles broken where a search first meets them);
    /// components with no links go to the column their layer suggests.
    /// </summary>
    private static Dictionary<string, int> Columns(IReadOnlyList<MapNode> nodes, IReadOnlyList<MapLink> edges)
    {
        var outgoing = nodes.ToDictionary(n => n.Id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var e in edges)
        {
            outgoing[e.From].Add(e.To);
        }

        // Depth-first search to drop the edges that close a cycle.
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var forward = new List<(string From, string To)>();
        void Visit(string id)
        {
            state[id] = 1;
            foreach (var next in outgoing[id])
            {
                if (state.GetValueOrDefault(next) == 1)
                {
                    continue;
                }

                forward.Add((id, next));
                if (state.GetValueOrDefault(next) == 0)
                {
                    Visit(next);
                }
            }

            state[id] = 2;
        }

        var linked = edges.SelectMany(e => new[] { e.From, e.To }).ToHashSet(StringComparer.Ordinal);
        foreach (var node in nodes.Where(n => linked.Contains(n.Id)).OrderBy(n => edges.Count(e => e.To == n.Id)).ThenBy(n => LayerOrder(n.Layer)))
        {
            if (state.GetValueOrDefault(node.Id) == 0)
            {
                Visit(node.Id);
            }
        }

        // Longest path over the acyclic edges, in topological order.
        var rank = nodes.Where(n => linked.Contains(n.Id)).ToDictionary(n => n.Id, _ => 0, StringComparer.Ordinal);
        var indegree = rank.Keys.ToDictionary(k => k, k => forward.Count(f => f.To == k), StringComparer.Ordinal);
        var ready = new Queue<string>(indegree.Where(d => d.Value == 0).Select(d => d.Key));
        while (ready.Count > 0)
        {
            var id = ready.Dequeue();
            foreach (var (_, to) in forward.Where(f => f.From == id))
            {
                rank[to] = Math.Max(rank[to], rank[id] + 1);
                if (--indegree[to] == 0)
                {
                    ready.Enqueue(to);
                }
            }
        }

        var maxRank = rank.Count == 0 ? 0 : rank.Values.Max();
        var column = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            column[node.Id] = rank.TryGetValue(node.Id, out var r)
                ? r
                : maxRank == 0 ? LayerColumn(node.Layer) : (int)Math.Round(LayerColumn(node.Layer) / 3.0 * maxRank);
        }

        // Close the gaps left by unused columns.
        var used = column.Values.Distinct().Order().Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
        return column.ToDictionary(c => c.Key, c => used[c.Value], StringComparer.Ordinal);
    }

    /// <summary>
    /// Orthogonal connectors. Each box spreads its connectors along its side (ordered by where the other end is, so
    /// they don't cross at the box) and each source gets its own channel in the gap after its column.
    /// </summary>
    private static List<MapRoute> Routes(List<MapLink> edges, Dictionary<string, Rect> boxes, Dictionary<string, int> column)
    {
        var forward = edges.Where(e => column[e.To] > column[e.From]).ToList();
        var outPorts = Ports(forward, e => e.From, e => boxes[e.To].Center.Y, boxes);
        var inPorts = Ports(forward, e => e.To, e => boxes[e.From].Center.Y, boxes);

        // Sources in one column take turns across the gap so their vertical runs stay apart.
        var channel = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var group in forward.Select(e => e.From).Distinct(StringComparer.Ordinal).GroupBy(id => column[id]))
        {
            var sources = group.OrderBy(id => boxes[id].Y).ToList();
            for (var i = 0; i < sources.Count; i++)
            {
                channel[sources[i]] = boxes[sources[i]].Right + 8 + ((ColumnGap - 16) * (i + 1) / (sources.Count + 1));
            }
        }

        var routes = new List<MapRoute>();
        foreach (var edge in edges)
        {
            var from = boxes[edge.From];
            var to = boxes[edge.To];
            List<Point> points;
            if (column[edge.To] > column[edge.From])
            {
                var fy = outPorts[edge];
                var ty = inPorts[edge];
                var x = channel[edge.From];
                points = [new(from.Right, fy), new(x, fy), new(x, ty), new(to.X, ty)];
            }
            else if (column[edge.To] == column[edge.From])
            {
                var x = from.Right + 10;
                points = [new(from.Right, from.Center.Y), new(x, from.Center.Y), new(x, to.Center.Y), new(to.Right, to.Center.Y)];
            }
            else
            {
                // Backwards (a cycle): out of the left side and back into the target's right side.
                var x = from.X - (ColumnGap / 2) + 6;
                points = [new(from.X, from.Center.Y), new(x, from.Center.Y), new(x, to.Center.Y), new(to.Right, to.Center.Y)];
            }

            routes.Add(new MapRoute(edge.From, edge.To, points, edge.Inferred, edge.Label));
        }

        return routes;
    }

    private static Dictionary<MapLink, double> Ports(List<MapLink> edges, Func<MapLink, string> at, Func<MapLink, double> otherEnd, Dictionary<string, Rect> boxes)
    {
        var ports = new Dictionary<MapLink, double>();
        foreach (var group in edges.GroupBy(at, StringComparer.Ordinal))
        {
            var box = boxes[group.Key];
            var ordered = group.OrderBy(otherEnd).ToList();
            var top = box.Y + HeaderHeight;
            var span = Math.Max(box.Height - HeaderHeight - Inset, 1);
            for (var i = 0; i < ordered.Count; i++)
            {
                ports[ordered[i]] = top + (span * (i + 1) / (ordered.Count + 1));
            }
        }

        return ports;
    }

    private static Dictionary<string, Rect> FilesIn(IReadOnlyList<MapNode> nodes, Dictionary<string, Rect> components)
    {
        var files = new Dictionary<string, Rect>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!components.TryGetValue(node.Id, out var box))
            {
                continue;
            }

            var inner = new Rect(box.X + Inset, box.Y + HeaderHeight, Math.Max(box.Width - (2 * Inset), 0), Math.Max(box.Height - HeaderHeight - Inset, 0));
            foreach (var (path, rect) in Squarify(node.Files.Select(f => (f.Path, f.Weight)), inner))
            {
                files[path] = Deflate(rect, 1);
            }
        }

        return files;
    }

    private static Rect Deflate(Rect rect, double by) =>
        new(rect.X + by, rect.Y + by, Math.Max(rect.Width - (2 * by), 0), Math.Max(rect.Height - (2 * by), 0));

    private static double Worst(List<(string Key, double Weight)> row, double side, double scale)
    {
        double sum = 0, max = 0, min = double.MaxValue;
        foreach (var (_, weight) in row)
        {
            var a = weight * scale;
            sum += a;
            max = Math.Max(max, a);
            min = Math.Min(min, a);
        }

        return Math.Max(side * side * max / (sum * sum), sum * sum / (side * side * min));
    }

    private static int LayerOrder(ComponentLayer layer) => layer switch
    {
        ComponentLayer.Entry => 0,
        ComponentLayer.Ui => 1,
        ComponentLayer.Core => 2,
        ComponentLayer.Integration => 3,
        ComponentLayer.Data => 4,
        ComponentLayer.Config => 5,
        _ => 6,
    };

    private static int LayerColumn(ComponentLayer layer) => layer switch
    {
        ComponentLayer.Entry or ComponentLayer.Ui => 0,
        ComponentLayer.Core => 1,
        ComponentLayer.Integration => 2,
        _ => 3,
    };

    public static string LayerLabel(ComponentLayer layer) => layer switch
    {
        ComponentLayer.Entry => "Entry points",
        ComponentLayer.Ui => "Views",
        ComponentLayer.Core => "Core",
        ComponentLayer.Integration => "Integrations",
        ComponentLayer.Data => "Data",
        ComponentLayer.Config => "Configuration",
        _ => "Other",
    };

    private static string ColumnLabel(IEnumerable<ComponentLayer> layers) =>
        layers.GroupBy(l => l).OrderByDescending(g => g.Count()).ThenBy(g => LayerOrder(g.Key)).Select(g => LayerLabel(g.Key)).FirstOrDefault() ?? "";
}
