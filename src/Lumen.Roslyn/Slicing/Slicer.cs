using System.Diagnostics;
using Lumen.Domain;

namespace Lumen.Roslyn.Slicing;

/// <summary>Knobs for the slicer, tuned on the fixture (docs/overhaul.md, Feature slices).</summary>
public sealed record SlicerOptions
{
    /// <summary>A changed file reached from this many slices or more goes to Shared and is reviewed once.</summary>
    public int SharedThreshold { get; init; } = 3;

    /// <summary>How many links growth follows from a seed.</summary>
    public int DepthCap { get; init; } = 6;

    /// <summary>Above this many files or open lines, a page slice is split into sub-sections by handler.</summary>
    public int SectionFiles { get; init; } = 10;

    public int SectionLines { get; init; } = 400;
}

/// <summary>What the slicer needs to know about one changed file.</summary>
public sealed record ChangedFileFacts(string Path, FileChangeKind Kind, IReadOnlySet<int> ChangedHeadLines, int ChangedLines, bool AllMechanical, int OpenLines);

/// <summary>
/// Turns a change into feature slices (docs/overhaul.md): one slice per changed entry point (Razor page, WebForms
/// page, controller, minimal API, hosted service, middleware, filter; a changed test class as a last resort), grown
/// along membership links with a depth cap. A changed file reached from <see cref="SlicerOptions.SharedThreshold"/>
/// slices goes to Shared; changed code nothing reaches goes to Unlinked (used outside this change, or nothing uses
/// it); files whose every change is proven mechanical and that no slice reaches are listed as Mechanical only.
/// </summary>
public static class Slicer
{
    public const string Version = "slicer/1";

    private static readonly string[] SiteWideNames = ["_Layout", "_ViewImports", "_ViewStart"];

    /// <summary>The slicer's view of a change: changed head lines per file, and what folds, from the snapshot and its triage.</summary>
    public static List<ChangedFileFacts> Facts(PullRequestSnapshot snapshot, TriageResult? triage)
    {
        var byPath = (triage?.Hunks ?? []).ToLookup(h => h.Path, StringComparer.Ordinal);
        return
        [
            .. snapshot.Files.Select(f =>
            {
                var lines = f.Hunks.SelectMany(h => h.Lines).Where(l => l.Kind == DiffLineKind.Added && l.NewNumber is not null).Select(l => l.NewNumber!.Value).ToHashSet();

                // A deletion has no head lines: the next head line marks where it happened.
                foreach (var hunk in f.Hunks)
                {
                    int? next = null;
                    foreach (var line in hunk.Lines.Reverse())
                    {
                        if (line.NewNumber is { } n)
                        {
                            next = n;
                        }
                        else if (line.Kind == DiffLineKind.Removed && next is { } at)
                        {
                            lines.Add(at);
                        }
                    }
                }

                var hunks = byPath[f.Path].ToList();
                var changedLines = f.Additions + f.Deletions;
                var allMechanical = f.Mechanical.IsMechanical || (hunks.Count > 0 && hunks.All(h => h.Fold == FoldKind.Mechanical));
                var open = hunks.Count == 0 ? (f.Mechanical.IsMechanical ? 0 : changedLines) : hunks.Where(h => h.Fold == FoldKind.Open).Sum(h => h.ChangedLines);
                return new ChangedFileFacts(f.Path, f.Kind, lines, changedLines, allMechanical, open);
            }),
        ];
    }

    public static SliceResult Slice(LinkGraph graph, IReadOnlyList<ChangedFileFacts> changes, SlicerOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new SlicerOptions();
        var stopwatch = Stopwatch.StartNew();
        var index = graph.Index;
        var changed = changes.ToDictionary(c => c.Path, StringComparer.Ordinal);
        var seeds = Seeds(index, changes);
        if (seeds.Count == 0 && !changes.Any(c => TestPaths.IsTest(c.Path)))
        {
            return SliceResult.Fallback("No changed page, controller, endpoint, hosted service, middleware or filter: showing the file list.", stopwatch.Elapsed);
        }

        // Grow each seed, remembering the edge that first reached each node (its reason in words). Files three or more
        // slices reach go to Shared; growth then stops at them, so what is reachable only through Shared code joins
        // Shared too instead of every slice above it. Repeated until nothing moves.
        var seedFiles = seeds.SelectMany(s => s.Files).ToHashSet(StringComparer.Ordinal);
        var shared = new HashSet<string>(StringComparer.Ordinal);
        List<(Seed Seed, Dictionary<string, Link?> Reached)> grown = [];
        Dictionary<string, List<int>> membership = [];
        for (var round = 0; round < 4; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            grown = [.. seeds.Select(seed => (seed, Grow(graph, seed.Files.Concat(seed.Nodes), seed, options.DepthCap, shared)))];
            membership = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var i = 0; i < grown.Count; i++)
            {
                foreach (var file in grown[i].Reached.Keys.Where(changed.ContainsKey).Where(f => !shared.Contains(f)))
                {
                    if (!membership.TryGetValue(file, out var slices))
                    {
                        membership[file] = slices = [];
                    }

                    slices.Add(i);
                }
            }

            var more = membership.Where(kv => kv.Value.Count >= options.SharedThreshold && !seedFiles.Contains(kv.Key)).Select(kv => kv.Key).ToList();
            if (more.Count == 0)
            {
                break;
            }

            shared.UnionWith(more);
        }

        // A candidate only counts as Shared if three slices reach it directly, not just through other Shared code
        // (a model type declared next to a service another shared service uses).
        foreach (var candidate in shared.ToList())
        {
            var others = shared.Where(f => f != candidate).ToHashSet(StringComparer.Ordinal);
            var direct = seeds.Count(seed => Grow(graph, seed.Files.Concat(seed.Nodes), seed, options.DepthCap, others).ContainsKey(candidate));
            if (direct < options.SharedThreshold)
            {
                shared.Remove(candidate);
            }
        }

        if (shared.Count > 0)
        {
            grown = [.. seeds.Select(seed => (seed, Grow(graph, seed.Files.Concat(seed.Nodes), seed, options.DepthCap, shared)))];
            membership = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var i = 0; i < grown.Count; i++)
            {
                foreach (var file in grown[i].Reached.Keys.Where(changed.ContainsKey).Where(f => !shared.Contains(f)))
                {
                    if (!membership.TryGetValue(file, out var slices))
                    {
                        membership[file] = slices = [];
                    }

                    slices.Add(i);
                }
            }
        }

        // What Shared code reaches and no slice reaches directly is Shared as well (a shared service's repository).
        var sharedReached = shared.Count == 0 ? [] : Grow(graph, shared, null, options.DepthCap, new HashSet<string>());
        var sharedReasons = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (node, via) in sharedReached)
        {
            if (changed.ContainsKey(node) && !membership.ContainsKey(node) && !seedFiles.Contains(node) && shared.Add(node) && via is not null)
            {
                sharedReasons[node] = via.Reason;
            }
        }

        // Tests join the slice of what they test: the most specific type they construct (the one fewest slices reach),
        // else the most specific type they use. A test of a page model lands with that page, not with every user of a service.
        var tests = changes.Where(c => TestPaths.IsTest(c.Path) && !membership.ContainsKey(c.Path)).Select(c => c.Path).ToList();
        var testReasons = new Dictionary<(string, int), Link>();
        var sharedTests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var test in tests)
        {
            var links = graph.From(test).Where(l => l.Kind == EdgeKind.Tests).ToList();

            // xUnit naming first: OrderServiceTests tests OrderService, wherever that lives (a slice, or Shared).
            var named = Path.GetFileNameWithoutExtension(test) is var testName && (testName.EndsWith("Tests", StringComparison.Ordinal) || testName.EndsWith("Test", StringComparison.Ordinal))
                ? links.FirstOrDefault(l => l.Reason.StartsWith($"`{testName}` tests `{testName[..testName.LastIndexOf("Test", StringComparison.Ordinal)]}`", StringComparison.Ordinal))
                : null;
            if (named is not null && shared.Contains(named.To))
            {
                sharedTests.Add(test);
                sharedReasons[test] = named.Reason;
                continue;
            }

            var constructing = links.Where(l => l.Reason.EndsWith("(constructs it)", StringComparison.Ordinal)).ToList();
            var pool = named is not null ? [named] : constructing.Count > 0 ? constructing : links;
            var candidates = pool
                .Select(l => (Link: l, Slices: Enumerable.Range(0, grown.Count).Where(i => grown[i].Reached.ContainsKey(l.To) && !shared.Contains(l.To)).ToList()))
                .Where(c => c.Slices.Count > 0)
                .ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            var fewest = candidates.Min(c => c.Slices.Count);
            foreach (var (link, slices) in candidates.Where(c => c.Slices.Count == fewest))
            {
                foreach (var i in slices)
                {
                    if (!membership.TryGetValue(test, out var joined))
                    {
                        membership[test] = joined = [];
                    }

                    if (!joined.Contains(i))
                    {
                        joined.Add(i);
                        testReasons[(test, i)] = link;
                    }
                }
            }
        }

        shared.UnionWith(sharedTests);

        var sharedCounts = shared.ToDictionary(f => f, f => seeds.Count(seed => Grow(graph, seed.Files.Concat(seed.Nodes), seed, options.DepthCap, []).ContainsKey(f)), StringComparer.Ordinal);

        var slicesOut = new List<Domain.Slice>();
        var edges = new List<SliceEdge>();
        for (var i = 0; i < grown.Count; i++)
        {
            var (seed, reached) = grown[i];
            var files = membership.Where(kv => kv.Value.Contains(i) && !shared.Contains(kv.Key)).Select(kv => kv.Key).ToList();
            if (files.Count == 0)
            {
                continue;
            }

            var sliceFiles = files.Select(path =>
            {
                var why = seed.Files.Contains(path)
                    ? seed.Why
                    : testReasons.TryGetValue((path, i), out var test) ? test.Reason
                    : reached.GetValueOrDefault(path)?.Reason ?? "reached from the entry point";
                return new SliceFile(path, Order(index, path), why) { Members = ChangedMembers(index, changed[path]) };
            }).ToList();

            sliceFiles = Sections(graph, seed, sliceFiles, changed, options);
            var reachedNodes = reached.Keys.ToHashSet(StringComparer.Ordinal);
            var possible = graph.All.Where(l => l.Kind == EdgeKind.Possible && reachedNodes.Contains(l.From))
                .Select(l => new SliceEdge(l.From, l.To, l.Reason, Possible: true)).Distinct().ToList();
            var pageLinks = seed.Files.SelectMany(graph.From).Where(l => l.Kind == EdgeKind.PageLink)
                .Select(l => new SliceEdge(l.From, l.To, l.Reason)).Distinct().ToList();
            foreach (var file in files)
            {
                if (testReasons.TryGetValue((file, i), out var test))
                {
                    edges.Add(new SliceEdge(test.From, test.To, test.Reason));
                }
                else if (reached.GetValueOrDefault(file) is { } via)
                {
                    edges.Add(new SliceEdge(via.From, via.To, via.Reason));
                }
            }

            slicesOut.Add(new Domain.Slice(Domain.Slice.IdFor(SliceKind.Feature, seed.Key), seed.Title, SliceKind.Feature, seed.Key,
                [.. sliceFiles.OrderBy(f => f.Order).ThenBy(f => f.Path, StringComparer.Ordinal)])
            {
                Seed = seed.Kind,
                Sections = [.. sliceFiles.Where(f => f.Section.Length > 0).Select(f => f.Section).Distinct().OrderBy(s => s == "Page" ? 0 : 1)],
                PossibleLinks = possible,
                PageLinks = pageLinks,
            });
        }

        // Test classes no seed reaches become their own slice, with the changed code they test.
        foreach (var test in changes.Where(c => TestPaths.IsTest(c.Path) && !membership.ContainsKey(c.Path) && !shared.Contains(c.Path)).Select(c => c.Path))
        {
            var tested = graph.From(test).Where(l => l.Kind == EdgeKind.Tests && changed.ContainsKey(l.To) && !membership.ContainsKey(l.To) && !shared.Contains(l.To)).ToList();
            var files = new List<SliceFile> { new(test, Order(index, test), "a changed test with no page, controller or endpoint above it") { Members = ChangedMembers(index, changed[test]) } };
            files.AddRange(tested.Select(l => new SliceFile(l.To, Order(index, l.To), l.Reason) { Members = ChangedMembers(index, changed[l.To]) }));
            membership[test] = [];
            tested.ForEach(l => membership[l.To] = []);
            edges.AddRange(tested.Select(l => new SliceEdge(l.From, l.To, l.Reason)));
            slicesOut.Add(new Domain.Slice(Domain.Slice.IdFor(SliceKind.Feature, test), "Tests / " + Path.GetFileNameWithoutExtension(test), SliceKind.Feature, test,
                [.. files.OrderBy(f => f.Order).ThenBy(f => f.Path, StringComparer.Ordinal)])
            {
                Seed = SeedKind.TestOnly,
            });
        }

        if (shared.Count > 0)
        {
            slicesOut.Add(new Domain.Slice(Domain.Slice.IdFor(SliceKind.Shared, "shared"), "Shared", SliceKind.Shared, "",
                [.. shared.Select(path => new SliceFile(path, Order(index, path), sharedReasons.TryGetValue(path, out var why) ? $"{why} (shared code)" : $"used by {sharedCounts[path]} slices") { Members = ChangedMembers(index, changed[path]) })
                    .OrderBy(f => f.Order).ThenBy(f => f.Path, StringComparer.Ordinal)]));
        }

        // Unlinked and Mechanical only: everything no slice reaches.
        var rest = changes.Where(c => !membership.ContainsKey(c.Path) && !shared.Contains(c.Path)).ToList();
        var mechanical = rest.Where(c => c.AllMechanical).ToList();
        var used = new List<SliceFile>();
        var unused = new List<SliceFile>();
        foreach (var file in rest.Where(c => !c.AllMechanical))
        {
            // Anything unchanged that points at it counts: callers, a layout loading it, an unchanged test.
            var callers = graph.To(file.Path)
                .Where(l => l.Kind != EdgeKind.PageLink && !changed.ContainsKey(LinkGraph.FileOf(l.From)))
                .Select(l => LinkGraph.FileOf(l.From)).Distinct(StringComparer.Ordinal).Count();
            var entry = new SliceFile(file.Path, Order(index, file.Path), callers > 0 ? "no changed page, handler or test reaches it" : "nothing uses it")
            {
                Members = ChangedMembers(index, file),
                OutsideCallers = callers,
            };
            (callers > 0 ? used : unused).Add(entry);
        }

        if (used.Count > 0)
        {
            slicesOut.Add(new Domain.Slice(Domain.Slice.IdFor(SliceKind.UnlinkedUsed, "unlinked-used"), "Unlinked · used outside this change", SliceKind.UnlinkedUsed, "", [.. used.OrderBy(f => f.Order).ThenBy(f => f.Path, StringComparer.Ordinal)]));
        }

        if (unused.Count > 0)
        {
            slicesOut.Add(new Domain.Slice(Domain.Slice.IdFor(SliceKind.UnlinkedUnused, "unlinked-unused"), "Unlinked · nothing uses it", SliceKind.UnlinkedUnused, "", [.. unused.OrderBy(f => f.Order).ThenBy(f => f.Path, StringComparer.Ordinal)]));
        }

        if (mechanical.Count > 0)
        {
            slicesOut.Add(new Domain.Slice(Domain.Slice.IdFor(SliceKind.MechanicalOnly, "mechanical"), "Mechanical only", SliceKind.MechanicalOnly, "",
                [.. mechanical.Select(m => new SliceFile(m.Path, Order(index, m.Path), "every change is proven mechanical")).OrderBy(f => f.Order).ThenBy(f => f.Path, StringComparer.Ordinal)]));
        }

        return new SliceResult(slicesOut, [.. edges.Distinct()]) { Duration = stopwatch.Elapsed };
    }

    // ---------------------------------------------------------------- seeds

    private sealed record Seed(string Key, string Title, SeedKind Kind, IReadOnlyList<string> Files, string Why)
    {
        /// <summary>Member nodes growth starts from besides the files (a minimal API's route).</summary>
        public IReadOnlyList<string> Nodes { get; init; } = [];
    }

    private static List<Seed> Seeds(RepositoryIndex index, IReadOnlyList<ChangedFileFacts> changes)
    {
        var seeds = new Dictionary<string, Seed>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            var path = change.Path;
            if (path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cshtml.cs", StringComparison.OrdinalIgnoreCase))
            {
                var view = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;
                if (index.Razor.TryGetValue(view, out var razor) && razor.PageRoute is not null)
                {
                    var files = index.CSharp.ContainsKey(view + ".cs") ? new[] { view, view + ".cs" } : [view];
                    var title = PageTitle(view);
                    seeds.TryAdd(view, new Seed(view, title, SeedKind.RazorPage, files, $"page `{title.Replace(" / ", "/", StringComparison.Ordinal)}`"));
                }

                continue;
            }

            if (path.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".aspx.cs", StringComparison.OrdinalIgnoreCase))
            {
                var page = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;
                var files = new List<string> { page };
                if (index.CSharp.ContainsKey(page + ".cs"))
                {
                    files.Add(page + ".cs");
                }

                seeds.TryAdd(page, new Seed(page, Path.GetFileNameWithoutExtension(page), SeedKind.WebFormsPage, files, $"WebForms page `{Path.GetFileName(page)}`"));
                continue;
            }

            if (!index.CSharp.TryGetValue(path, out var cs))
            {
                continue;
            }

            foreach (var type in cs.Types.Where(t => t.Kind is "class" or "record"))
            {
                var kind = type switch
                {
                    _ when type.DerivesFrom("Controller") || type.DerivesFrom("ControllerBase") || type.Attributes.Contains("ApiController") => SeedKind.ApiController,
                    _ when type.DerivesFrom("BackgroundService") || type.DerivesFrom("IHostedService") => SeedKind.HostedService,
                    _ when type.DerivesFrom("IMiddleware") || (type.Members.Any(m => m.Name is "Invoke" or "InvokeAsync") && type.Injected.Any(i => i.Name == "RequestDelegate")) => SeedKind.Middleware,
                    _ when type.BaseTypes.Any(b => b.Name is "IPageFilter" or "IAsyncPageFilter" or "IActionFilter" or "IAsyncActionFilter" or "IResultFilter" or "IAsyncResultFilter" or "ActionFilterAttribute" or "IExceptionFilter" or "IAsyncExceptionFilter") => SeedKind.Filter,
                    _ => (SeedKind?)null,
                };
                if (kind is { } k && !TestPaths.IsTest(path))
                {
                    seeds.TryAdd(path, new Seed(path, type.SimpleName, k, [path], $"{Describe(k)} `{type.SimpleName}`"));
                }
            }

            // Minimal-API routes whose lines changed.
            foreach (var (route, line) in cs.MinimalApis.Where(r => change.ChangedHeadLines.Contains(r.Line)))
            {
                seeds.TryAdd($"{path}#{route}", new Seed($"{path}#{route}", "Endpoint " + route, SeedKind.MinimalApi, [path], $"minimal API `{route}`"));
            }
        }

        return [.. seeds.Values.OrderBy(s => s.Title, StringComparer.Ordinal)];
    }

    private static string Describe(SeedKind kind) => kind switch
    {
        SeedKind.ApiController => "controller",
        SeedKind.HostedService => "hosted service",
        SeedKind.Middleware => "middleware",
        SeedKind.Filter => "filter",
        _ => "entry point",
    };

    public static string PageTitle(string viewPath)
    {
        var rooted = "/" + viewPath;
        var at = rooted.IndexOf("/Pages/", StringComparison.Ordinal);
        var relative = at < 0 ? Path.GetFileNameWithoutExtension(viewPath) : rooted[(at + "/Pages/".Length)..^".cshtml".Length];
        var area = System.Text.RegularExpressions.Regex.Match(rooted, "/Areas/(?<a>[^/]+)/");
        var parts = (area.Success ? area.Groups["a"].Value + "/" : "") + relative;
        return string.Join(" / ", parts.Split('/'));
    }

    // ---------------------------------------------------------------- growth

    private static Dictionary<string, Link?> Grow(LinkGraph graph, IEnumerable<string> starts, Seed? seed, int depthCap, HashSet<string> stops)
    {
        var reached = new Dictionary<string, Link?>(StringComparer.Ordinal);
        var queue = new Queue<(string Node, int Depth)>();
        foreach (var start in starts)
        {
            reached[start] = null;
            queue.Enqueue((start, 0));
        }

        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            if (depth >= depthCap || (depth > 0 && (StopsGrowth(graph, node, seed) || stops.Contains(LinkGraph.FileOf(node)))))
            {
                continue;
            }

            foreach (var link in graph.From(node).Where(l => l.MakesMembership && l.Kind != EdgeKind.Tests && l.To.Length > 0))
            {
                foreach (var target in new[] { link.To, LinkGraph.FileOf(link.To) }.Distinct(StringComparer.Ordinal))
                {
                    if (reached.TryAdd(target, link))
                    {
                        queue.Enqueue((target, depth + 1));
                    }
                }
            }

            // A file's handler members carry what the handlers use: walk them from the file.
            if (!node.Contains("::", StringComparison.Ordinal))
            {
                foreach (var member in graph.To(node).Where(l => l.From.StartsWith(node + "::", StringComparison.Ordinal)).Select(l => l.From))
                {
                    if (reached.TryAdd(member, null))
                    {
                        queue.Enqueue((member, depth));
                    }
                }
            }
        }

        return reached;
    }

    /// <summary>Layouts, view imports, the composition root and other pages are reached but never grown from.</summary>
    private static bool StopsGrowth(LinkGraph graph, string node, Seed? seed)
    {
        var file = LinkGraph.FileOf(node);
        var name = Path.GetFileNameWithoutExtension(file.Replace(".cshtml", "", StringComparison.Ordinal));
        if (SiteWideNames.Any(n => name.StartsWith(n, StringComparison.OrdinalIgnoreCase)) || Path.GetFileName(file) == "Program.cs" || Path.GetFileName(file) == "Startup.cs")
        {
            return true;
        }

        var view = file.EndsWith(".cshtml.cs", StringComparison.Ordinal) ? file[..^3] : file;
        return !(seed?.Files.Contains(file) ?? false) && graph.Index.Razor.TryGetValue(view, out var razor) && razor.PageRoute is not null;
    }

    // ---------------------------------------------------------------- order, members, sections

    /// <summary>Reading order: entities and input models → data → services → page model → view → script → tests.</summary>
    public static int Order(RepositoryIndex index, string path) => (int)Role(index, path);

    public static NodeRole Role(RepositoryIndex index, string path)
    {
        if (TestPaths.IsTest(path))
        {
            return NodeRole.Test;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".cshtml" or ".aspx" or ".ascx" or ".master")
        {
            return NodeRole.View;
        }

        if (extension == ".js")
        {
            return NodeRole.Script;
        }

        if (extension != ".cs")
        {
            return NodeRole.Config;
        }

        if (path.EndsWith(".cshtml.cs", StringComparison.Ordinal) || path.EndsWith(".aspx.cs", StringComparison.Ordinal) || path.EndsWith(".ascx.cs", StringComparison.Ordinal))
        {
            return NodeRole.PageModel;
        }

        var types = index.CSharp.TryGetValue(path, out var file) ? file.Types : [];
        if (types.Any(t => t.DerivesFrom("PageModel") || t.DerivesFrom("Controller") || t.DerivesFrom("ControllerBase") || t.DerivesFrom("ViewComponent") || t.DerivesFrom("TagHelper")))
        {
            return NodeRole.PageModel;
        }

        if (types.Any(LinkResolver.IsDbContext) || types.Any(t => t.SimpleName.EndsWith("Repository", StringComparison.Ordinal)) || path.Contains("/Repositories/", StringComparison.Ordinal))
        {
            return NodeRole.Data;
        }

        if (path.Contains("/Entities/", StringComparison.Ordinal) || path.Contains("/Models/", StringComparison.Ordinal)
            || types.Any(t => t.SimpleName.EndsWith("Dto", StringComparison.Ordinal) || t.SimpleName.EndsWith("Input", StringComparison.Ordinal) || t.SimpleName.EndsWith("ViewModel", StringComparison.Ordinal) || t.Kind == "enum"))
        {
            return NodeRole.EntityOrInput;
        }

        return NodeRole.Service;
    }

    /// <summary>
    /// Changed members of a C# file: "Type.Member" for an edited member (a constructor is "Type.Type"), or just "Type"
    /// when the whole type is new. Nested types keep their outer type ("EditModel.InputModel.IsAdmin").
    /// </summary>
    public static IReadOnlyList<string> ChangedMembers(RepositoryIndex index, ChangedFileFacts change)
    {
        if (!index.CSharp.TryGetValue(change.Path, out var file))
        {
            return [];
        }

        var found = new List<string>();
        var newTypes = file.Types.Where(t => Enumerable.Range(t.StartLine, t.EndLine - t.StartLine + 1).All(change.ChangedHeadLines.Contains)).ToList();
        foreach (var type in file.Types)
        {
            // A new type that only holds data is listed as itself; a new type with behaviour lists its members.
            var isData = !type.Members.Any(m => m.Kind == "method");
            if (newTypes.Contains(type) && isData)
            {
                if (!newTypes.Any(outer => outer != type && type.Name.StartsWith(outer.Name + ".", StringComparison.Ordinal)))
                {
                    found.Add(type.Name);
                }

                continue;
            }

            var isNew = newTypes.Contains(type);
            foreach (var member in type.Members.Where(m => m.Kind is not "field" && !(isNew && m.Kind == "constructor") && change.ChangedHeadLines.Any(l => l >= m.StartLine && l <= m.EndLine)))
            {
                found.Add($"{type.Name}.{(member.Kind == "constructor" ? type.SimpleName : member.Name)}");
            }
        }

        return [.. found.Distinct(StringComparer.Ordinal)];
    }

    private static List<SliceFile> Sections(LinkGraph graph, Seed seed, List<SliceFile> files, Dictionary<string, ChangedFileFacts> changed, SlicerOptions options)
    {
        var openLines = files.Sum(f => changed[f.Path].OpenLines);
        if (seed.Kind != SeedKind.RazorPage || (files.Count <= options.SectionFiles && openLines <= options.SectionLines))
        {
            return files;
        }

        var model = seed.Files.FirstOrDefault(f => f.EndsWith(".cs", StringComparison.Ordinal));
        if (model is null || !graph.Index.CSharp.TryGetValue(model, out var cs))
        {
            return files;
        }

        // Each handler's own reach, in declaration order; a file goes to the first handler that reaches it.
        var handlers = cs.Types.SelectMany(t => t.Members.Where(m => LinkResolver.IsHandler(m.Name)).Select(m => (Node: $"{model}::{t.SimpleName}.{m.Name}", m.Name))).ToList();
        var reach = handlers.Select(h => (h.Name, Reached: Reach(graph, h.Node, options.DepthCap))).ToList();
        var callers = graph.All.Where(l => l.Kind == EdgeKind.Ajax).GroupBy(l => l.To, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(l => LinkGraph.FileOf(l.From)).ToList(), StringComparer.Ordinal);
        return
        [
            .. files.Select(f =>
            {
                if (seed.Files.Contains(f.Path))
                {
                    return f with { Section = "Page" };
                }

                var section = reach.FirstOrDefault(r => r.Reached.Contains(f.Path)).Name
                    ?? handlers.FirstOrDefault(h => callers.TryGetValue(h.Node, out var from) && from.Contains(f.Path)).Name
                    ?? "Page";
                return f with { Section = Trim(section) };
            }),
        ];

        static string Trim(string handler) => handler.EndsWith("Async", StringComparison.Ordinal) ? handler[..^5] : handler;
    }

    private static HashSet<string> Reach(LinkGraph graph, string start, int depthCap)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        var queue = new Queue<(string, int)>([(start, 0)]);
        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            if (depth >= depthCap)
            {
                continue;
            }

            foreach (var link in graph.From(node).Where(l => l.MakesMembership && l.Kind is not (EdgeKind.Tests or EdgeKind.PageModel) && l.To.Length > 0 && !l.To.StartsWith(LinkGraph.FileOf(start), StringComparison.Ordinal)))
            {
                if (seen.Add(link.To))
                {
                    queue.Enqueue((link.To, depth + 1));
                }
            }
        }

        return seen;
    }
}
