using System.Text.RegularExpressions;

namespace Lumen.Roslyn.Slicing;

/// <summary>A link written in a page or script, with the 1-based line it is on.</summary>
public sealed record MarkupRef(string Value, int Line);

/// <summary>
/// One Razor view or page (.cshtml): its directives, what it renders and loads, the pages it links to and the element
/// ids it declares. Read with patterns, not a Razor compiler, so it works on any checkout.
/// </summary>
public sealed record RazorFile(string Path)
{
    /// <summary>Set when the file has an <c>@page</c> directive; empty route means the folder path.</summary>
    public string? PageRoute { get; init; }

    public string? Model { get; init; }

    public string? Layout { get; init; }

    public IReadOnlyList<MarkupRef> Partials { get; init; } = [];

    public IReadOnlyList<MarkupRef> ViewComponents { get; init; } = [];

    /// <summary>Element names that could be tag helpers (contain a hyphen and aren't plain HTML).</summary>
    public IReadOnlyList<MarkupRef> Elements { get; init; } = [];

    public IReadOnlyList<MarkupRef> Scripts { get; init; } = [];

    /// <summary><c>asp-page</c>, <c>RedirectToPage</c>-style and literal <c>Url.Page("/x")</c> links to other pages.</summary>
    public IReadOnlyList<MarkupRef> PageLinks { get; init; } = [];

    /// <summary><c>Url.Page(...)</c> built from variables: a possible link only.</summary>
    public IReadOnlyList<MarkupRef> DynamicPageLinks { get; init; } = [];

    /// <summary>Handlers named by <c>asp-page-handler</c> or a literal <c>Url.Page(page, "Handler")</c>.</summary>
    public IReadOnlyList<MarkupRef> Handlers { get; init; } = [];

    public IReadOnlyList<MarkupRef> Injected { get; init; } = [];

    public IReadOnlyList<MarkupRef> Ids { get; init; } = [];

    /// <summary>Literal URLs in inline scripts (<c>$.post('?handler=X')</c>).</summary>
    public IReadOnlyList<MarkupRef> AjaxUrls { get; init; } = [];

    public bool IsLayout => System.IO.Path.GetFileName(Path).StartsWith("_Layout", StringComparison.OrdinalIgnoreCase);

    public bool IsPartial => System.IO.Path.GetFileName(Path).StartsWith('_') && !IsLayout && !IsViewImports;

    public bool IsViewImports => System.IO.Path.GetFileName(Path) is "_ViewImports.cshtml" or "_ViewStart.cshtml";

    public IReadOnlyList<string> Usings { get; init; } = [];

    public string? Namespace { get; init; }
}

/// <summary>One script: AJAX URLs, <c>#id</c> selectors and the looser references that only make possible links.</summary>
public sealed record ScriptFile(string Path)
{
    /// <summary>URLs whose handler or page part is a literal ("?handler=Save", "/Orders/Edit?handler=Lines").</summary>
    public IReadOnlyList<MarkupRef> AjaxUrls { get; init; } = [];

    /// <summary>AJAX calls whose URL isn't a resolvable literal (data-url attributes, variables, concatenated pages).</summary>
    public IReadOnlyList<MarkupRef> LooseAjax { get; init; } = [];

    public IReadOnlyList<MarkupRef> IdSelectors { get; init; } = [];

    public bool IsVendored => Path.Contains("/lib/", StringComparison.OrdinalIgnoreCase) || Path.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A WebForms page, control or master page and what it registers and loads.</summary>
public sealed record WebFormsFile(string Path)
{
    public string? CodeBehind { get; init; }

    public string? Inherits { get; init; }

    public IReadOnlyList<MarkupRef> Registers { get; init; } = [];

    public IReadOnlyList<MarkupRef> Scripts { get; init; } = [];

    public string? MasterPage { get; init; }
}

public static partial class MarkupReaders
{
    private static readonly HashSet<string> HtmlElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "address", "article", "aside", "audio", "b", "blockquote", "body", "br", "button", "canvas", "caption", "code", "col",
        "colgroup", "dd", "details", "div", "dl", "dt", "em", "fieldset", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6",
        "head", "header", "hr", "html", "i", "iframe", "img", "input", "label", "legend", "li", "link", "main", "meta", "nav", "ol",
        "optgroup", "option", "output", "p", "pre", "progress", "script", "section", "select", "small", "span", "strong", "style", "sub",
        "summary", "sup", "svg", "table", "tbody", "td", "template", "textarea", "tfoot", "th", "thead", "time", "title", "tr", "u", "ul",
        "video", "partial", "environment", "cache", "text",
    };

    public static RazorFile ReadRazor(string path, string text)
    {
        var lines = Lines(text);
        var page = PageDirective().Match(text);
        var model = ModelDirective().Match(text);
        var layout = LayoutAssignment().Match(text);
        var ns = NamespaceDirective().Match(text);
        return new RazorFile(path)
        {
            PageRoute = page.Success ? page.Groups["route"].Value : null,
            Model = model.Success ? model.Groups["type"].Value.Trim() : null,
            Layout = layout.Success ? layout.Groups["name"].Value : null,
            Namespace = ns.Success ? ns.Groups["ns"].Value.Trim() : null,
            Usings = [.. UsingDirective().Matches(text).Select(m => m.Groups["ns"].Value.Trim())],
            Partials = Find(lines, PartialTag(), "name").Concat(Find(lines, PartialCall(), "name")).ToList(),
            ViewComponents = Find(lines, VcTag(), "name").Select(r => r with { Value = Pascal(r.Value) }).Concat(Find(lines, VcCall(), "name")).ToList(),
            Elements = [.. Find(lines, ElementTag(), "name").Where(r => r.Value.Contains('-', StringComparison.Ordinal) && !r.Value.StartsWith("vc:", StringComparison.Ordinal) && !HtmlElements.Contains(r.Value))],
            Scripts = Find(lines, ScriptSrc(), "src").Concat(Find(lines, SrcInclude(), "src")).ToList(),
            PageLinks = Find(lines, AspPage(), "page").Concat(Find(lines, UrlPageLiteral(), "page")).Concat(Find(lines, RedirectToPageLiteral(), "page")).ToList(),
            DynamicPageLinks = Find(lines, UrlPageDynamic(), "expr"),
            Handlers = Find(lines, AspPageHandler(), "handler").Concat(Find(lines, UrlPageHandler(), "handler")).ToList(),
            Injected = Find(lines, InjectDirective(), "type"),
            Ids = Find(lines, IdAttribute(), "id"),
            AjaxUrls = Find(lines, AjaxLiteral(), "url").Where(r => r.Value.Contains("handler=", StringComparison.OrdinalIgnoreCase)).ToList(),
        };
    }

    public static ScriptFile ReadScript(string path, string text)
    {
        var lines = Lines(text);
        var literal = Find(lines, AjaxLiteral(), "url").Where(r => r.Value.Contains("handler=", StringComparison.OrdinalIgnoreCase) || r.Value.StartsWith('/')).ToList();
        var literalLines = literal.Select(r => r.Line).ToHashSet();
        var loose = Find(lines, AjaxCall(), "call").Where(r => !literalLines.Contains(r.Line)).ToList();
        return new ScriptFile(path)
        {
            AjaxUrls = literal,
            LooseAjax = loose,
            IdSelectors = Find(lines, IdSelector(), "id"),
        };
    }

    public static WebFormsFile ReadWebForms(string path, string text)
    {
        var lines = Lines(text);
        var directive = WebFormsDirective().Match(text);
        string? Attr(string name) => directive.Success && Regex.Match(directive.Value, name + "\\s*=\\s*\"(?<v>[^\"]+)\"", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups["v"].Value : null;
        return new WebFormsFile(path)
        {
            CodeBehind = Attr("CodeBehind") ?? Attr("CodeFile"),
            Inherits = Attr("Inherits"),
            MasterPage = Attr("MasterPageFile"),
            Registers = Find(lines, RegisterSrc(), "src"),
            Scripts = Find(lines, ScriptSrc(), "src"),
        };
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static List<MarkupRef> Find(string[] lines, Regex pattern, string group)
    {
        var found = new List<MarkupRef>();
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (Match m in pattern.Matches(lines[i]))
            {
                found.Add(new MarkupRef(m.Groups[group].Value, i + 1));
            }
        }

        return found;
    }

    /// <summary>"cart-summary" → "CartSummary".</summary>
    public static string Pascal(string kebab) =>
        string.Concat(kebab.Replace("vc:", "", StringComparison.Ordinal).Split('-', StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    [GeneratedRegex(@"^\s*@page(?:\s+""(?<route>[^""]*)"")?\s*$", RegexOptions.Multiline)]
    private static partial Regex PageDirective();

    [GeneratedRegex(@"^\s*@model\s+(?<type>[\w\.<>,\s\[\]?]+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex ModelDirective();

    [GeneratedRegex(@"^\s*@namespace\s+(?<ns>[\w\.]+)", RegexOptions.Multiline)]
    private static partial Regex NamespaceDirective();

    [GeneratedRegex(@"^\s*@using\s+(?<ns>[\w\.]+)\s*$", RegexOptions.Multiline)]
    private static partial Regex UsingDirective();

    [GeneratedRegex(@"Layout\s*=\s*""(?<name>[^""]+)""")]
    private static partial Regex LayoutAssignment();

    [GeneratedRegex(@"<partial\s+[^>]*name\s*=\s*""(?<name>[^""]+)""")]
    private static partial Regex PartialTag();

    [GeneratedRegex(@"Html\.(?:Render)?Partial(?:Async)?\(\s*""(?<name>[^""]+)""")]
    private static partial Regex PartialCall();

    [GeneratedRegex(@"<vc:(?<name>[\w\-]+)")]
    private static partial Regex VcTag();

    [GeneratedRegex(@"Component\.InvokeAsync\(\s*""(?<name>[^""]+)""")]
    private static partial Regex VcCall();

    [GeneratedRegex(@"<(?<name>[a-z][\w\-:]*)[\s/>]")]
    private static partial Regex ElementTag();

    [GeneratedRegex(@"<script\b[^>]*\bsrc\s*=\s*\\?[""'](?<src>[^""'\\]+)\\?[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptSrc();

    [GeneratedRegex(@"asp-src-include\s*=\s*""(?<src>[^""]+)""")]
    private static partial Regex SrcInclude();

    [GeneratedRegex(@"\basp-page\s*=\s*""(?<page>[^""@]+)""")]
    private static partial Regex AspPage();

    [GeneratedRegex(@"Url\.Page\(\s*""(?<page>[^""]+)""")]
    private static partial Regex UrlPageLiteral();

    [GeneratedRegex(@"RedirectToPage\(\s*""(?<page>[^""]+)""")]
    private static partial Regex RedirectToPageLiteral();

    [GeneratedRegex(@"Url\.Page\(\s*(?<expr>[^""\s][^,\)]*)")]
    private static partial Regex UrlPageDynamic();

    [GeneratedRegex(@"\basp-page-handler\s*=\s*""(?<handler>[^""@]+)""")]
    private static partial Regex AspPageHandler();

    [GeneratedRegex(@"Url\.Page\(\s*""[^""]*""\s*,\s*""(?<handler>[^""]+)""")]
    private static partial Regex UrlPageHandler();

    [GeneratedRegex(@"^\s*@inject\s+(?<type>[\w\.<>]+)\s+\w+", RegexOptions.Multiline)]
    private static partial Regex InjectDirective();

    [GeneratedRegex(@"\bid\s*=\s*""(?<id>[A-Za-z][\w\-]*)""")]
    private static partial Regex IdAttribute();

    [GeneratedRegex(@"(?:\$\.(?:post|get|getJSON|ajax)|fetch|\burl\s*:)\s*\(?\s*(?<q>['""`])(?<url>[^'""`]+)\k<q>")]
    private static partial Regex AjaxLiteral();

    [GeneratedRegex(@"(?<call>\$\.(?:post|get|getJSON|ajax)|fetch)\s*\(")]
    private static partial Regex AjaxCall();

    [GeneratedRegex(@"\$\(\s*['""]#(?<id>[A-Za-z][\w\-]*)['""]\s*\)")]
    private static partial Regex IdSelector();

    [GeneratedRegex(@"<%@\s*(?:Page|Control|Master)\b[^%]*%>", RegexOptions.IgnoreCase)]
    private static partial Regex WebFormsDirective();

    [GeneratedRegex(@"<%@\s*Register\b[^%]*\bSrc\s*=\s*""(?<src>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex RegisterSrc();
}
