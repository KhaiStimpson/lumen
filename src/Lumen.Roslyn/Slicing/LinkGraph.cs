using Lumen.Domain;

namespace Lumen.Roslyn.Slicing;

/// <summary>Why one node links to another. Only some kinds make slice membership; the rest are shown, never merged.</summary>
public enum EdgeKind
{
    PageModel,
    Injects,
    References,
    DbSet,
    Partial,
    ViewComponent,
    TagHelper,
    Script,
    Ajax,
    IdSelector,
    Tests,
    Implements,

    /// <summary>A page links to another page (asp-page, RedirectToPage, Url.Page): shown, never merges slices.</summary>
    PageLink,

    /// <summary>Ambiguous or loose: two implementations, IEnumerable&lt;T&gt;, data-url, a URL built at run time.</summary>
    Possible,

    /// <summary>A script loaded by a layout: site-wide, never joins a page slice.</summary>
    SiteWide,
}

/// <summary>A link with its reason in words. <see cref="To"/> is empty for a possible link whose target is unknown.</summary>
public sealed record Link(string From, string To, EdgeKind Kind, string Reason, int Line = 0)
{
    public bool MakesMembership => Kind is not (EdgeKind.PageLink or EdgeKind.Possible or EdgeKind.SiteWide);
}

/// <summary>A node's role, for reading order and for where growth stops.</summary>
public enum NodeRole
{
    EntityOrInput,
    Data,
    Service,
    PageModel,
    View,
    Script,
    Test,
    Config,
    Other,
}

/// <summary>
/// The repository's code as a graph: files and members linked by how they use each other (docs/overhaul.md,
/// "A new link resolver"). Names are qualified by namespace, usings and folder, so every page's <c>EditModel</c> is
/// told apart; an interface with one implementation is followed to it, with several each is a possible link.
/// </summary>
public sealed class LinkGraph
{
    private readonly Dictionary<string, List<Link>> _out = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Link>> _in = new(StringComparer.Ordinal);

    public LinkGraph(RepositoryIndex index, IEnumerable<Link> links)
    {
        Index = index;
        foreach (var link in links.Distinct())
        {
            Add(_out, link.From, link);
            if (link.To.Length > 0)
            {
                Add(_in, link.To, link);
            }
        }
    }

    public RepositoryIndex Index { get; }

    public IEnumerable<Link> All => _out.Values.SelectMany(l => l);

    public IReadOnlyList<Link> From(string node) => _out.TryGetValue(node, out var links) ? links : [];

    public IReadOnlyList<Link> To(string node) => _in.TryGetValue(node, out var links) ? links : [];

    private static void Add(Dictionary<string, List<Link>> map, string key, Link link)
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        list.Add(link);
    }

    /// <summary>The file a node belongs to: "a/b.cs::T.M" → "a/b.cs".</summary>
    public static string FileOf(string node)
    {
        var at = node.IndexOf("::", StringComparison.Ordinal);
        return at < 0 ? node : node[..at];
    }
}

/// <summary>Every file the slicer reads: C#, Razor, scripts and WebForms markup, parsed once per head.</summary>
public sealed class RepositoryIndex
{
    private readonly Dictionary<string, List<TypeInfo>> _bySimpleName = new(StringComparer.Ordinal);

    public RepositoryIndex(
        IReadOnlyDictionary<string, CSharpFile> csharp,
        IReadOnlyDictionary<string, RazorFile> razor,
        IReadOnlyDictionary<string, ScriptFile> scripts,
        IReadOnlyDictionary<string, WebFormsFile> webForms,
        IReadOnlySet<string> allPaths)
    {
        CSharp = csharp;
        Razor = razor;
        Scripts = scripts;
        WebForms = webForms;
        AllPaths = allPaths;
        foreach (var type in csharp.Values.SelectMany(f => f.Types))
        {
            if (!_bySimpleName.TryGetValue(type.SimpleName, out var list))
            {
                _bySimpleName[type.SimpleName] = list = [];
            }

            list.Add(type);
        }

        GlobalUsings = [.. csharp.Values.SelectMany(f => f.GlobalUsings).Distinct(StringComparer.Ordinal)];
    }

    public IReadOnlyDictionary<string, CSharpFile> CSharp { get; }

    public IReadOnlyDictionary<string, RazorFile> Razor { get; }

    public IReadOnlyDictionary<string, ScriptFile> Scripts { get; }

    public IReadOnlyDictionary<string, WebFormsFile> WebForms { get; }

    public IReadOnlySet<string> AllPaths { get; }

    public IReadOnlyList<string> GlobalUsings { get; }

    public IEnumerable<TypeInfo> Types => CSharp.Values.SelectMany(f => f.Types);

    public IReadOnlyList<TypeInfo> Named(string simpleName) => _bySimpleName.TryGetValue(simpleName, out var list) ? list : [];

    /// <summary>Files larger than this (about 2 MB) are generated or bundled, not read for links.</summary>
    public const int MaxIndexedChars = 2_000_000;

    /// <summary>The files worth reading for links: code, views, scripts and WebForms markup; never build output, vendored libraries or minified scripts.</summary>
    public static bool IsIndexed(string path)
    {
        var p = path.Replace('\\', '/');
        if (p.Split('/').Any(s => s is "bin" or "obj" or "node_modules" or ".git" or ".vs") || p.Contains("/wwwroot/lib/", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Path.GetExtension(p).ToLowerInvariant() is ".cs" or ".cshtml" or ".js" or ".aspx" or ".ascx" or ".master";
    }

    /// <summary>Parses files; a file that can't be read is left out rather than failing the whole index.</summary>
    public static RepositoryIndex Build(IEnumerable<KeyValuePair<string, string>> files) => Build(files, cache: null);

    /// <summary>
    /// Parses files, reusing <paramref name="cache"/>'s parse of any file whose path and content are unchanged, so a new
    /// commit only re-reads what it changed.
    /// </summary>
    public static RepositoryIndex Build(IEnumerable<KeyValuePair<string, string>> files, ParsedFileCache? cache)
    {
        var csharp = new Dictionary<string, CSharpFile>(StringComparer.Ordinal);
        var razor = new Dictionary<string, RazorFile>(StringComparer.Ordinal);
        var scripts = new Dictionary<string, ScriptFile>(StringComparer.Ordinal);
        var webForms = new Dictionary<string, WebFormsFile>(StringComparer.Ordinal);
        var all = new HashSet<string>(StringComparer.Ordinal);
        var toParse = new List<(string Path, string Text)>();
        foreach (var (rawPath, text) in files)
        {
            var path = rawPath.Replace('\\', '/');
            all.Add(path);

            // Known to exist (a script tag can point at it), but not read for links: outside the indexed kinds,
            // minified, or too big to be hand-written (generated code, bundles).
            if (IsIndexed(path) && text.Length <= MaxIndexedChars)
            {
                toParse.Add((path, text));
            }
        }

        // Parsed in parallel, added in the files' order so the graph (and its link order) doesn't depend on timing.
        var results = new object?[toParse.Count];
        Parallel.For(0, toParse.Count, i =>
        {
            var (path, text) = toParse[i];
            try
            {
                results[i] = cache is null ? Parse(path, text) : cache.GetOrParse(path, text, Parse);
            }
            catch (Exception)
            {
                // An unreadable file simply has no links.
            }
        });

        for (var i = 0; i < toParse.Count; i++)
        {
            var path = toParse[i].Path;
            switch (results[i])
            {
                case CSharpFile cs:
                    csharp[path] = cs;
                    break;
                case RazorFile view:
                    razor[path] = view;
                    break;
                case ScriptFile script:
                    scripts[path] = script;
                    break;
                case WebFormsFile form:
                    webForms[path] = form;
                    break;
            }
        }

        return new RepositoryIndex(csharp, razor, scripts, webForms, all);
    }

    private static object? Parse(string path, string text) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => CSharpReader.Read(path, text),
        ".cshtml" => MarkupReaders.ReadRazor(path, text),
        ".js" => MarkupReaders.ReadScript(path, text),
        ".aspx" or ".ascx" or ".master" => MarkupReaders.ReadWebForms(path, text),
        _ => null,
    };
}

/// <summary>
/// Parsed files keyed by path and a hash of their content, shared across commits so building the index for a new head
/// only parses what changed. Bounded: the oldest entries go first.
/// </summary>
public sealed class ParsedFileCache(int capacity = 50_000)
{
    private readonly Dictionary<(string Path, string Hash), object?> _parsed = [];
    private readonly Queue<(string, string)> _order = new();
    private readonly Lock _gate = new();

    public int Hits { get; private set; }

    public int Misses { get; private set; }

    public object? GetOrParse(string path, string text, Func<string, string, object?> parse)
    {
        var key = (path, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))));
        lock (_gate)
        {
            if (_parsed.TryGetValue(key, out var hit))
            {
                Hits++;
                return hit;
            }
        }

        var parsed = parse(path, text);
        lock (_gate)
        {
            Misses++;
            if (_parsed.TryAdd(key, parsed))
            {
                _order.Enqueue(key);
                while (_order.Count > capacity)
                {
                    _parsed.Remove(_order.Dequeue());
                }
            }
        }

        return parsed;
    }
}
