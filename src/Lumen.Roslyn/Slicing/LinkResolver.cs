using System.Text.RegularExpressions;
using Lumen.Domain;

namespace Lumen.Roslyn.Slicing;

/// <summary>
/// Builds the <see cref="LinkGraph"/>: C# edges (injection, interface → single implementation, <c>[FromServices]</c>,
/// DbSet use, references, tests), Razor edges (page model, partials, view components, tag helpers, scripts, page links,
/// form handlers), script edges (AJAX to handlers, <c>#id</c> selectors) and WebForms pairing. Every edge carries its
/// reason in words. Anything ambiguous is a possible link and never makes membership.
/// </summary>
public static partial class LinkResolver
{
    private static readonly HashSet<string> Wrappers = new(StringComparer.Ordinal)
    {
        "IEnumerable", "IList", "ICollection", "IReadOnlyList", "IReadOnlyCollection", "List", "Func", "Lazy", "IOptions", "IOptionsSnapshot", "IOptionsMonitor",
    };

    public static LinkGraph Build(RepositoryIndex index)
    {
        var links = new List<Link>();
        var resolver = new Names(index);
        foreach (var file in index.CSharp.Values)
        {
            CSharpLinks(index, resolver, file, links);
        }

        var pages = PageRoutes(index);
        foreach (var view in index.Razor.Values)
        {
            RazorLinks(index, resolver, view, pages, links);
        }

        ScriptLinks(index, pages, links);
        foreach (var form in index.WebForms.Values)
        {
            WebFormsLinks(index, form, links);
        }

        return new LinkGraph(index, links);
    }

    // ---------------------------------------------------------------- C#

    private static void CSharpLinks(RepositoryIndex index, Names names, CSharpFile file, List<Link> links)
    {
        var isTest = TestPaths.IsTest(file.Path);
        foreach (var type in file.Types)
        {
            var from = file.Path;
            var injected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var parameter in type.Injected.Concat(type.Members.SelectMany(m => m.FromServices)))
            {
                Inject(names, file, type, parameter, links, injected, viaFromServices: type.Members.Any(m => m.FromServices.Contains(parameter)));
            }

            // Interfaces it implements: the implementation reaches its contract.
            foreach (var baseType in type.BaseTypes)
            {
                if (names.Resolve(baseType, file) is { IsInterface: true } contract && contract.Path != from)
                {
                    links.Add(new Link(from, contract.Path, EdgeKind.Implements, $"`{type.SimpleName}` implements `{contract.SimpleName}`"));
                }
            }

            // DbSet use: `_db.Orders` → Order. The DbContext itself is a dead end for growth (it lists every entity).
            foreach (var context in type.Names.Select(n => names.ResolveSimple(n, file)).OfType<TypeInfo>().Where(IsDbContext).Distinct())
            {
                if (context.Path != from)
                {
                    links.Add(new Link(from, context.Path, EdgeKind.References, $"`{type.SimpleName}` uses `{context.SimpleName}`"));
                }

                foreach (var (property, entity) in context.DbSets.Where(kv => type.Accessed.Contains(kv.Key)))
                {
                    if (names.ResolveSimple(entity, file) is { } entityType && entityType.Path != from)
                    {
                        links.Add(new Link(from, entityType.Path, EdgeKind.DbSet, $"`{type.SimpleName}` uses `{context.SimpleName}.{property}` → `{entityType.SimpleName}`"));
                    }
                }
            }

            // Everything else it names: entities, DTOs, input models, helpers. Tests "test" what they construct.
            // A DbContext names every entity, so nothing grows out of it (it is reached, never expanded).
            var constructed = type.Constructed;
            foreach (var name in IsDbContext(type) ? (IEnumerable<string>)[] : type.Names)
            {
                if (injected.Contains(name) || names.ResolveSimple(name, file) is not { } target || target.Path == from || IsDbContext(target))
                {
                    continue;
                }

                if (isTest && !TestPaths.IsTest(target.Path))
                {
                    var kind = constructed.Contains(name) ? "constructs" : "uses";
                    links.Add(new Link(from, target.Path, EdgeKind.Tests, $"`{type.SimpleName}` tests `{target.SimpleName}` ({kind} it)") { });
                    continue;
                }

                links.Add(new Link(from, target.Path, EdgeKind.References, $"`{type.SimpleName}` uses `{target.SimpleName}`"));
            }

            // Member nodes: a handler links to what its body names (for handler sub-sections and AJAX targets).
            foreach (var member in type.Members.Where(m => IsHandler(m.Name)))
            {
                var node = $"{from}::{type.SimpleName}.{member.Name}";
                links.Add(new Link(node, from, EdgeKind.References, $"`{member.Name}` is a handler of `{type.SimpleName}`"));
                foreach (var name in member.Names)
                {
                    if (names.ResolveSimple(name, file) is { } target && target.Path != from && !IsDbContext(target))
                    {
                        links.Add(new Link(node, target.Path, EdgeKind.References, $"`{member.Name}` uses `{target.SimpleName}`"));
                    }
                }
            }
        }

        // A tag helper (or any C#) that writes a script tag loads that script.
        foreach (var literal in file.PathLiterals)
        {
            foreach (var src in ScriptPaths(literal))
            {
                if (ResolveScript(index, file.Path, src) is { } script)
                {
                    links.Add(new Link(file.Path, script, EdgeKind.Script, $"`{Path.GetFileName(file.Path)}` writes a script tag for `{Path.GetFileName(script)}`"));
                }
            }
        }
    }

    private static void Inject(Names names, CSharpFile file, TypeInfo type, TypeRef parameter, List<Link> links, HashSet<string> injected, bool viaFromServices)
    {
        var verb = viaFromServices ? "takes [FromServices]" : "injects";
        if (Wrappers.Contains(parameter.Name) && parameter.Arguments.Count > 0)
        {
            // IEnumerable<T> and friends: whatever is registered, so a possible link.
            foreach (var argument in parameter.Arguments)
            {
                injected.Add(argument.Name);
                if (names.Resolve(argument, file) is { } inner)
                {
                    foreach (var candidate in Implementations(names, inner).DefaultIfEmpty(inner))
                    {
                        links.Add(new Link(file.Path, candidate.Path, EdgeKind.Possible, $"`{type.SimpleName}` {verb} `{parameter}`: `{candidate.SimpleName}` may be one of them"));
                    }
                }
            }

            return;
        }

        injected.Add(parameter.Name);
        if (names.Resolve(parameter, file) is not { } target || target.Path == file.Path)
        {
            return;
        }

        if (!target.IsInterface)
        {
            links.Add(new Link(file.Path, target.Path, EdgeKind.Injects, $"`{type.SimpleName}` {verb} `{target.SimpleName}`"));
            return;
        }

        links.Add(new Link(file.Path, target.Path, EdgeKind.Injects, $"`{type.SimpleName}` {verb} `{target.SimpleName}`"));
        var implementations = Implementations(names, target);
        if (implementations.Count == 1)
        {
            var impl = implementations[0];
            links.Add(new Link(file.Path, impl.Path, EdgeKind.Injects, $"`{type.SimpleName}` {verb} `{target.SimpleName}` → `{impl.SimpleName}`"));
        }
        else
        {
            foreach (var impl in implementations)
            {
                links.Add(new Link(file.Path, impl.Path, EdgeKind.Possible,
                    $"`{type.SimpleName}` {verb} `{target.SimpleName}`, which has {implementations.Count} implementations: `{impl.SimpleName}` is one"));
            }
        }
    }

    private static List<TypeInfo> Implementations(Names names, TypeInfo contract) =>
    [
        .. names.Index.Types.Where(t => !t.IsInterface && t.Kind is "class" or "record" && t.BaseTypes.Any(b =>
            b.Name == contract.SimpleName && names.Resolve(b, names.Index.CSharp[t.Path]) is { } resolved && resolved.QualifiedName == contract.QualifiedName)),
    ];

    public static bool IsDbContext(TypeInfo type) => type.DerivesFrom("DbContext") || type.DerivesFrom("IdentityDbContext") || type.DbSets.Count > 0;

    /// <summary>Razor Pages handlers: OnGet, OnPostSave, OnPostSaveAsync…</summary>
    public static bool IsHandler(string member) => HandlerName().IsMatch(member);

    /// <summary>"OnPostSaveAsync" → ("Post", "Save"); "OnGet" → ("Get", "").</summary>
    public static (string Verb, string Name) HandlerParts(string member)
    {
        var m = HandlerName().Match(member);
        return (m.Groups["verb"].Value, m.Groups["name"].Value);
    }

    [GeneratedRegex(@"^On(?<verb>Get|Post|Put|Delete|Patch)(?<name>[A-Z]\w*?)?(?:Async)?$")]
    private static partial Regex HandlerName();

    // ---------------------------------------------------------------- Razor

    /// <summary>A page and its route: "/Orders/Edit" (route template stripped), with its .cshtml path.</summary>
    public sealed record PageRoute(string Route, string View, string? PageModel);

    private static List<PageRoute> PageRoutes(RepositoryIndex index)
    {
        var routes = new List<PageRoute>();
        foreach (var view in index.Razor.Values.Where(v => v.PageRoute is not null))
        {
            var path = view.Path;
            var rooted = "/" + path;
            var pagesAt = rooted.IndexOf("/Pages/", StringComparison.Ordinal);
            if (pagesAt < 0)
            {
                continue;
            }

            var area = AreaOf(rooted);
            var relative = rooted[(pagesAt + "/Pages/".Length)..^".cshtml".Length];
            if (relative.EndsWith("Index", StringComparison.Ordinal))
            {
                relative = relative[..^"Index".Length].TrimEnd('/');
            }

            var route = "/" + (area is null ? "" : area + "/") + relative;
            var template = view.PageRoute!;
            if (template.StartsWith('/'))
            {
                route = template.Split('{')[0].TrimEnd('/');
            }

            var model = index.CSharp.ContainsKey(path + ".cs") ? path + ".cs" : null;
            routes.Add(new PageRoute(route.Length == 0 ? "/" : route, path, model));
        }

        return routes;
    }

    private static string? AreaOf(string path)
    {
        var m = Regex.Match("/" + path.TrimStart('/'), @"/Areas/(?<area>[^/]+)/");
        return m.Success ? m.Groups["area"].Value : null;
    }

    private static void RazorLinks(RepositoryIndex index, Names names, RazorFile view, List<PageRoute> pages, List<Link> links)
    {
        var path = view.Path;
        var name = Path.GetFileName(path);
        var codeBehind = path + ".cs";
        if (index.CSharp.TryGetValue(codeBehind, out var model))
        {
            var modelType = model.Types.FirstOrDefault(t => t.DerivesFrom("PageModel") || t.SimpleName.EndsWith("Model", StringComparison.Ordinal));
            var title = view.PageRoute is not null ? $"page `{PageTitle(path)}`" : $"`{name}`";
            links.Add(new Link(path, codeBehind, EdgeKind.PageModel, $"{title} has its PageModel `{modelType?.SimpleName ?? Path.GetFileName(codeBehind)}` in `{Path.GetFileName(codeBehind)}`"));
            links.Add(new Link(codeBehind, path, EdgeKind.PageModel, $"`{modelType?.SimpleName ?? Path.GetFileName(codeBehind)}` renders `{name}`"));
        }
        else if (view.Model is { } modelName && names.ResolveRazorModel(modelName, view) is { } modelInfo)
        {
            links.Add(new Link(path, modelInfo.Path, EdgeKind.References, $"`{name}` is typed with `@model {modelInfo.SimpleName}`"));
        }

        foreach (var injected in view.Injected)
        {
            if (names.ResolveRazorModel(injected.Value, view) is { } service)
            {
                links.Add(new Link(path, service.Path, EdgeKind.Injects, $"`{name}` injects `{service.SimpleName}` (`@inject`)", injected.Line));
            }
        }

        foreach (var partial in view.Partials)
        {
            if (ResolvePartial(index, path, partial.Value) is { } target)
            {
                links.Add(new Link(path, target, EdgeKind.Partial, $"`{name}` renders partial `{Path.GetFileNameWithoutExtension(target)}`", partial.Line));
            }
        }

        foreach (var component in view.ViewComponents)
        {
            foreach (var target in ResolveViewComponent(index, component.Value))
            {
                links.Add(new Link(path, target, EdgeKind.ViewComponent, $"`{name}` invokes view component `{component.Value}`", component.Line));
            }
        }

        foreach (var element in view.Elements.DistinctBy(e => e.Value))
        {
            if (ResolveTagHelper(index, element.Value) is { } helper)
            {
                links.Add(new Link(path, helper.Path, EdgeKind.TagHelper, $"`{name}` uses tag helper `<{element.Value}>` (`{helper.SimpleName}`)", element.Line));
            }
        }

        foreach (var script in view.Scripts)
        {
            foreach (var target in ResolveScripts(index, path, script.Value))
            {
                // A layout's scripts are site-wide: they never join a page slice.
                var kind = view.IsLayout ? EdgeKind.SiteWide : EdgeKind.Script;
                var how = script.Value.Contains('*', StringComparison.Ordinal) ? $"(`asp-src-include=\"{script.Value}\"`)" : "";
                links.Add(new Link(path, target, kind, $"`{name}` loads `{Path.GetFileName(target)}` {how}".TrimEnd(), script.Line));
            }
        }

        foreach (var link in view.PageLinks)
        {
            if (ResolvePage(pages, path, link.Value) is { } target)
            {
                links.Add(new Link(path, target.View, EdgeKind.PageLink, $"`{name}` links to page `{PageTitle(target.View)}`", link.Line));
            }
        }

        foreach (var dynamic in view.DynamicPageLinks)
        {
            links.Add(new Link(path, "", EdgeKind.Possible, $"`{name}` links to a page built from `{dynamic.Value.Trim()}` (line {dynamic.Line})", dynamic.Line));
        }

        // Form and Url.Page handlers name the page's own handler methods.
        if (index.CSharp.TryGetValue(codeBehind, out var pageModel))
        {
            foreach (var handler in view.Handlers.DistinctBy(h => h.Value))
            {
                foreach (var member in HandlerMembers(pageModel, handler.Value, verb: null))
                {
                    links.Add(new Link(path, member, EdgeKind.Ajax, $"`{name}` posts to handler `{handler.Value}` → `{member.Split('.')[^1]}`", handler.Line));
                }
            }
        }

        foreach (var url in view.AjaxUrls)
        {
            ResolveAjax(index, pages, path, url, links, path);
        }
    }

    private static string PageTitle(string viewPath)
    {
        var rooted = "/" + viewPath;
        var at = rooted.IndexOf("/Pages/", StringComparison.Ordinal);
        var title = at < 0 ? Path.GetFileNameWithoutExtension(viewPath) : rooted[(at + "/Pages/".Length)..^".cshtml".Length];
        return AreaOf(rooted) is { } area ? $"{area}/{title}" : title;
    }

    private static string? ResolvePartial(RepositoryIndex index, string from, string name)
    {
        var file = name.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) ? Path.GetFileName(name) : Path.GetFileName(name) + ".cshtml";
        var candidates = index.Razor.Keys.Where(p => Path.GetFileName(p).Equals(file, StringComparison.OrdinalIgnoreCase)).ToList();
        if (name.Contains('/', StringComparison.Ordinal))
        {
            var suffix = name.TrimStart('~', '/').Replace(".cshtml", "", StringComparison.OrdinalIgnoreCase) + ".cshtml";
            candidates = [.. candidates.Where(c => c.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty(candidates.FirstOrDefault()!).OfType<string>()];
        }

        // Razor looks in the page's folder, then up through Shared folders: the closest candidate wins.
        return candidates.OrderByDescending(c => CommonPrefix(c, from)).FirstOrDefault();
    }

    private static IEnumerable<string> ResolveViewComponent(RepositoryIndex index, string name)
    {
        var type = index.Types.FirstOrDefault(t => t.SimpleName == name + "ViewComponent" || t.AttributeText.Any(a => a.Contains($"\"{name}\"", StringComparison.Ordinal) && a.StartsWith("ViewComponent", StringComparison.Ordinal)));
        if (type is not null)
        {
            yield return type.Path;
        }

        foreach (var view in index.Razor.Keys.Where(p => p.Contains($"/Components/{name}/", StringComparison.OrdinalIgnoreCase)))
        {
            yield return view;
        }
    }

    private static TypeInfo? ResolveTagHelper(RepositoryIndex index, string element)
    {
        var byAttribute = index.Types.Where(t => t.AttributeText.Any(a => a.Contains($"\"{element}\"", StringComparison.Ordinal) && a.StartsWith("HtmlTargetElement", StringComparison.Ordinal))).ToList();
        if (byAttribute.Count == 1)
        {
            return byAttribute[0];
        }

        var byName = index.Named(MarkupReaders.Pascal(element) + "TagHelper");
        return byName.Count == 1 ? byName[0] : null;
    }

    private static IEnumerable<string> ScriptPaths(string literal) =>
        literal.Contains("<script", StringComparison.OrdinalIgnoreCase)
            ? Regex.Matches(literal, @"src\s*=\s*['""]?(?<src>[^'"" >]+\.js)").Select(m => m.Groups["src"].Value)
            : literal.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? [literal] : [];

    private static string? ResolveScript(RepositoryIndex index, string from, string src) => ResolveScripts(index, from, src).FirstOrDefault();

    private static List<string> ResolveScripts(RepositoryIndex index, string from, string src)
    {
        var clean = src.Split('?')[0].TrimStart('~').TrimStart('/');
        if (clean.Length == 0 || clean.StartsWith("http", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("//", StringComparison.Ordinal))
        {
            return [];
        }

        if (clean.Contains('*', StringComparison.Ordinal))
        {
            var pattern = "wwwroot/" + clean;
            return [.. index.AllPaths.Where(p => p.EndsWith(".js", StringComparison.OrdinalIgnoreCase) && Analysis.PathPattern.Matches("**/" + pattern, p))];
        }

        var candidates = index.AllPaths.Where(p => p.EndsWith("wwwroot/" + clean, StringComparison.OrdinalIgnoreCase)).ToList();
        return [.. candidates.OrderByDescending(c => CommonPrefix(c, from)).Take(1)];
    }

    private static PageRoute? ResolvePage(List<PageRoute> pages, string from, string page)
    {
        var area = AreaOf(from);
        string route;
        if (page.StartsWith('/'))
        {
            route = (area is null ? "" : "/" + area) + page;
        }
        else
        {
            // "./Details" or "Details": relative to the current page's folder.
            var current = pages.FirstOrDefault(p => p.View == from)?.Route ?? "/";
            var folder = current.Contains('/', StringComparison.Ordinal) ? current[..current.LastIndexOf('/')] : "";
            var fromFile = Path.GetFileNameWithoutExtension(from);
            if (fromFile == "Index" && pages.Any(p => p.View == from))
            {
                folder = current;
            }

            route = folder + "/" + page.TrimStart('.', '/');
        }

        route = route.Replace("/Index", "", StringComparison.Ordinal);
        return pages.FirstOrDefault(p => p.Route.Equals(route.Length == 0 ? "/" : route, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> HandlerMembers(CSharpFile pageModel, string handler, string? verb)
    {
        foreach (var type in pageModel.Types)
        {
            foreach (var member in type.Members.Where(m => IsHandler(m.Name)))
            {
                var (v, n) = HandlerParts(member.Name);
                if (n.Equals(handler, StringComparison.OrdinalIgnoreCase) && (verb is null || v == verb))
                {
                    yield return $"{pageModel.Path}::{type.SimpleName}.{member.Name}";
                }
            }
        }
    }

    // ---------------------------------------------------------------- scripts

    private static void ScriptLinks(RepositoryIndex index, List<PageRoute> pages, List<Link> links)
    {
        var loadedBy = links.Where(l => l.Kind == EdgeKind.Script && l.To.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            .GroupBy(l => l.To, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(l => l.From).ToList(), StringComparer.Ordinal);
        var ids = index.Razor.Values.SelectMany(v => v.Ids.Select(i => (i.Value, v.Path))).ToLookup(x => x.Value, x => x.Path, StringComparer.Ordinal);

        foreach (var script in index.Scripts.Values.Where(s => !s.IsVendored))
        {
            var owners = PagesLoading(index, links, loadedBy, script.Path);
            foreach (var url in script.AjaxUrls)
            {
                foreach (var owner in owners.DefaultIfEmpty(""))
                {
                    ResolveAjax(index, pages, owner, url, links, script.Path);
                }
            }

            foreach (var loose in script.LooseAjax)
            {
                links.Add(new Link(script.Path, "", EdgeKind.Possible, $"`{Path.GetFileName(script.Path)}` calls a URL built at run time (`{loose.Value}`, line {loose.Line})", loose.Line));
            }

            foreach (var selector in script.IdSelectors.DistinctBy(s => s.Value))
            {
                var declaring = ids[selector.Value].Distinct(StringComparer.Ordinal).ToList();
                if (declaring.Count == 1)
                {
                    links.Add(new Link(script.Path, declaring[0], EdgeKind.IdSelector, $"`{Path.GetFileName(script.Path)}` selects `#{selector.Value}`, declared in `{Path.GetFileName(declaring[0])}`", selector.Line));
                }
                else if (declaring.Count > 1)
                {
                    foreach (var view in declaring)
                    {
                        links.Add(new Link(script.Path, view, EdgeKind.Possible, $"`#{selector.Value}` is declared in {declaring.Count} views: `{Path.GetFileName(view)}` is one", selector.Line));
                    }
                }
            }
        }
    }

    /// <summary>Pages that load a script directly or through a partial, view component or tag helper.</summary>
    private static List<string> PagesLoading(RepositoryIndex index, List<Link> links, Dictionary<string, List<string>> loadedBy, string script)
    {
        var pages = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(loadedBy.GetValueOrDefault(script) ?? []);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (!seen.Add(node))
            {
                continue;
            }

            if (index.Razor.TryGetValue(node, out var view) && view.PageRoute is not null)
            {
                pages.Add(node);
                continue;
            }

            foreach (var parent in links.Where(l => l.To == node && l.Kind is EdgeKind.Partial or EdgeKind.ViewComponent or EdgeKind.TagHelper))
            {
                queue.Enqueue(parent.From);
            }
        }

        return pages;
    }

    private static void ResolveAjax(RepositoryIndex index, List<PageRoute> pages, string ownerPage, MarkupRef url, List<Link> links, string from)
    {
        var value = url.Value;
        var handler = Regex.Match(value, @"handler=(?<h>\w+)");
        var pagePart = value.Split('?')[0];
        if (pagePart.EndsWith("/Index", StringComparison.OrdinalIgnoreCase))
        {
            pagePart = pagePart[..^"/Index".Length];
        }
        PageRoute? page = pagePart.Length > 0 && !pagePart.Contains('{', StringComparison.Ordinal)
            ? pages.FirstOrDefault(p => p.Route.Equals(pagePart.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            : pages.FirstOrDefault(p => p.View == ownerPage);
        if (page?.PageModel is not { } modelPath || !index.CSharp.TryGetValue(modelPath, out var model))
        {
            if (handler.Success)
            {
                links.Add(new Link(from, "", EdgeKind.Possible, $"`{Path.GetFileName(from)}` calls `{value}`, which no page loading it resolves (line {url.Line})", url.Line));
            }

            return;
        }

        if (!handler.Success)
        {
            links.Add(new Link(from, page.View, EdgeKind.Ajax, $"`{Path.GetFileName(from)}` calls page `{PageTitle(page.View)}`", url.Line));
            return;
        }

        var members = HandlerMembers(model, handler.Groups["h"].Value, verb: null).ToList();
        foreach (var member in members)
        {
            links.Add(new Link(from, member, EdgeKind.Ajax, $"`{Path.GetFileName(from)}` calls `?handler={handler.Groups["h"].Value}` → `{member.Split('.')[^1]}`", url.Line));
        }
    }

    // ---------------------------------------------------------------- WebForms

    private static void WebFormsLinks(RepositoryIndex index, WebFormsFile form, List<Link> links)
    {
        var name = Path.GetFileName(form.Path);
        var codeBehind = form.CodeBehind is { } cb
            ? index.CSharp.Keys.FirstOrDefault(p => p.EndsWith("/" + cb.TrimStart('~', '/'), StringComparison.OrdinalIgnoreCase) || p == Path.Combine(Path.GetDirectoryName(form.Path) ?? "", cb).Replace('\\', '/'))
            : index.CSharp.ContainsKey(form.Path + ".cs") ? form.Path + ".cs" : null;
        if (codeBehind is not null)
        {
            links.Add(new Link(form.Path, codeBehind, EdgeKind.PageModel, $"`{name}` has its code-behind in `{Path.GetFileName(codeBehind)}`"));
            links.Add(new Link(codeBehind, form.Path, EdgeKind.PageModel, $"`{Path.GetFileName(codeBehind)}` is the code-behind of `{name}`"));
            var designer = codeBehind[..^".cs".Length] + ".designer.cs";
            if (index.CSharp.Keys.FirstOrDefault(k => k.Equals(designer, StringComparison.OrdinalIgnoreCase)) is { } d)
            {
                links.Add(new Link(form.Path, d, EdgeKind.PageModel, $"`{name}` has its designer file `{Path.GetFileName(d)}`"));
            }
        }

        foreach (var register in form.Registers)
        {
            var target = index.WebForms.Keys.FirstOrDefault(p => p.EndsWith(register.Value.TrimStart('~', '/'), StringComparison.OrdinalIgnoreCase));
            if (target is not null)
            {
                links.Add(new Link(form.Path, target, EdgeKind.Partial, $"`{name}` registers control `{Path.GetFileName(target)}`", register.Line));
            }
        }

        foreach (var script in form.Scripts)
        {
            var target = index.AllPaths.FirstOrDefault(p => p.EndsWith(script.Value.TrimStart('~', '/'), StringComparison.OrdinalIgnoreCase));
            if (target is not null)
            {
                links.Add(new Link(form.Path, target, form.Path.EndsWith(".master", StringComparison.OrdinalIgnoreCase) ? EdgeKind.SiteWide : EdgeKind.Script,
                    $"`{name}` loads `{Path.GetFileName(target)}`", script.Line));
            }
        }
    }

    private static int CommonPrefix(string a, string b)
    {
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i])
        {
            i++;
        }

        return i;
    }

    /// <summary>
    /// Name resolution without a compiler: a simple name resolves to the one declared type the file can see, by its own
    /// namespace, its usings (global ones included), and its namespace's parents. Two candidates left means unresolved.
    /// </summary>
    internal sealed class Names(RepositoryIndex index)
    {
        private readonly Dictionary<(string, string), TypeInfo?> _cache = [];

        public RepositoryIndex Index { get; } = index;

        public TypeInfo? Resolve(TypeRef reference, CSharpFile file)
        {
            if (reference.Qualifier is { } qualifier)
            {
                var full = $"{qualifier}.{reference.Name}";
                return Index.Named(reference.Name).FirstOrDefault(t => t.QualifiedName == full || t.QualifiedName.EndsWith("." + full, StringComparison.Ordinal) || t.Name == full);
            }

            return ResolveSimple(reference.Name, file);
        }

        public TypeInfo? ResolveSimple(string name, CSharpFile file)
        {
            if (_cache.TryGetValue((file.Path, name), out var cached))
            {
                return cached;
            }

            var resolved = Pick(Index.Named(name), file.Path, file.Namespace, file.Usings.Concat(Index.GlobalUsings));
            _cache[(file.Path, name)] = resolved;
            return resolved;
        }

        public TypeInfo? ResolveRazorModel(string name, RazorFile view)
        {
            var clean = name.Split('<')[0].Trim();
            if (clean.Contains('.', StringComparison.Ordinal))
            {
                var simple = clean[(clean.LastIndexOf('.') + 1)..];
                var exact = Index.Named(simple).Where(t => t.QualifiedName == clean || t.QualifiedName.EndsWith("." + clean, StringComparison.Ordinal)).ToList();
                if (exact.Count == 1)
                {
                    return exact[0];
                }

                clean = simple;
            }

            // _ViewImports up the folder chain supply the namespace and usings a view sees.
            var usings = new List<string>(view.Usings);
            string? ns = view.Namespace;
            foreach (var imports in Index.Razor.Values.Where(v => v.IsViewImports && view.Path.StartsWith(Path.GetDirectoryName(v.Path)!.Replace('\\', '/') + "/", StringComparison.Ordinal)))
            {
                usings.AddRange(imports.Usings);
                ns ??= imports.Namespace;
            }

            return Pick(Index.Named(clean), view.Path, ns ?? "", usings);
        }

        private static TypeInfo? Pick(IReadOnlyList<TypeInfo> candidates, string path, string ns, IEnumerable<string> usings)
        {
            if (candidates.Count <= 1)
            {
                return candidates.Count == 1 ? candidates[0] : null;
            }

            // The file's own type, then its namespace, then what it imports, then the namespace's parents.
            var sameFile = candidates.Where(c => c.Path == path).ToList();
            if (sameFile.Count == 1)
            {
                return sameFile[0];
            }

            var sameNamespace = candidates.Where(c => c.Namespace == ns).ToList();
            if (sameNamespace.Count == 1)
            {
                return sameNamespace[0];
            }

            var visible = usings.ToHashSet(StringComparer.Ordinal);
            for (var parent = ns; parent.Length > 0; parent = parent.Contains('.', StringComparison.Ordinal) ? parent[..parent.LastIndexOf('.')] : "")
            {
                visible.Add(parent);
            }

            var imported = candidates.Where(c => visible.Contains(c.Namespace)).ToList();
            return imported.Count == 1 ? imported[0] : null;
        }
    }
}
