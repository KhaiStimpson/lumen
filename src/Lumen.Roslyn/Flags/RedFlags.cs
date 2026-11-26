using System.Text.RegularExpressions;
using Lumen.Analysis;
using Lumen.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lumen.Roslyn.Flags;

/// <summary>One changed file as the red-flag detectors see it: the diff plus both sides' text where readable.</summary>
public sealed class FlagFile(ChangedFile file, string? baseText, string? headText)
{
    private static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);
    private SyntaxTree? _baseTree;
    private SyntaxTree? _headTree;

    public ChangedFile File { get; } = file;

    public string Path => File.Path;

    public string? BaseText { get; } = baseText;

    public string? HeadText { get; } = headText;

    public bool IsCSharp => Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    public IEnumerable<DiffLine> Added => File.Hunks.SelectMany(h => h.Lines).Where(l => l.Kind == DiffLineKind.Added && l.NewNumber is not null);

    /// <summary>Added lines that are code, not comments (<c>//</c>, <c>/* */</c>, <c>@* *@</c>, <c>&lt;!-- --&gt;</c>).</summary>
    public IEnumerable<DiffLine> AddedCode => Added.Where(l => !RedFlags.IsCommentLine(l.Text));

    public IEnumerable<DiffLine> Removed => File.Hunks.SelectMany(h => h.Lines).Where(l => l.Kind == DiffLineKind.Removed && l.OldNumber is not null);

    /// <summary>Head syntax for C# files; null when unreadable or not C#.</summary>
    public SyntaxTree? HeadTree => IsCSharp && HeadText is { } t ? _headTree ??= CSharpSyntaxTree.ParseText(t, Parse) : null;

    public SyntaxTree? BaseTree => IsCSharp && BaseText is { } t ? _baseTree ??= CSharpSyntaxTree.ParseText(t, Parse) : null;

    /// <summary>Whether an added line with the same normalised text exists: a moved line was not removed.</summary>
    public bool StillAdded(string text) => Added.Any(l => RedFlag.Normalise(l.Text) == RedFlag.Normalise(text));

    public bool HeadContains(string normalised) =>
        HeadText is { } t && t.Split('\n').Any(l => RedFlag.Normalise(l) == normalised);
}

/// <summary>
/// Everything one detector needs beyond its file: the other changed files (a test moved to another file was not
/// deleted) and the types bound from requests.
/// </summary>
public sealed class FlagContext(IReadOnlyList<FlagFile> files)
{
    private IReadOnlySet<string>? _bound;

    public IReadOnlyList<FlagFile> Files { get; } = files;

    /// <summary>
    /// Type names bound from a request in the changed sources: <c>[BindProperty]</c> property types and page or
    /// controller handler parameter types.
    /// </summary>
    public IReadOnlySet<string> BoundTypes => _bound ??= RedFlags.BoundTypes(Files);
}

/// <summary>One row of the red-flag table (docs/overhaul.md). Says only what it found.</summary>
public interface IRedFlagDetector
{
    IEnumerable<RedFlag> Detect(FlagFile file, FlagContext context);
}

/// <summary>Runs every detector over a change and opens any fold a flag sits in.</summary>
public static partial class RedFlags
{
    public static IReadOnlyList<IRedFlagDetector> Detectors() =>
    [
        new WeakenedTestsDetector(),
        new AntiforgeryDetector(),
        new AuthorisationDetector(),
        new OverPostingDetector(),
        new GuardRemovedDetector(),
        new RawOutputDetector(),
        new RawSqlDetector(),
        new QuerySafetyDetector(),
        new SwallowedErrorsDetector(),
        new WarningsSilencedDetector(),
        new DependenciesDetector(),
        new SecurityConfigDetector(),
    ];

    /// <summary>
    /// Every flag in the change, in path and line order. Skipped paths are left out; generated and vendored files
    /// are read only for dependency and build changes.
    /// </summary>
    public static IReadOnlyList<RedFlag> Detect(PullRequestSnapshot snapshot, TriageSources sources, ReviewSettings settings)
    {
        var files = snapshot.Files
            .Where(f => !f.IsBinary && !settings.IsSkipped(f.Path))
            .Select(f => new FlagFile(f, f.Kind == FileChangeKind.Added ? null : sources.Base(f.Path), f.Kind == FileChangeKind.Deleted ? null : sources.Head(f.Path)))
            .ToList();
        var context = new FlagContext(files);
        var flags = new List<RedFlag>();
        foreach (var file in files)
        {
            var detectors = file.File.Mechanical.IsMechanical ? Detectors().OfType<DependenciesDetector>().Cast<IRedFlagDetector>() : Detectors();
            foreach (var detector in detectors)
            {
                try
                {
                    flags.AddRange(detector.Detect(file, context).Select(f => WithIdentity(f, file)));
                }
                catch (Exception e) when (e is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
                {
                    // A detector that cannot read a file finds nothing in it; the others still run.
                }
            }
        }

        return [.. flags.DistinctBy(f => f.Id).OrderBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Line)];
    }

    /// <summary>
    /// Opens every fold that contains a flag, on either side: a flag is a fact the reviewer must see, and a fold never
    /// hides it (docs/overhaul.md, Collapsing).
    /// </summary>
    public static TriageResult OpenFlaggedFolds(TriageResult triage, IReadOnlyList<RedFlag> flags)
    {
        bool Contains(HunkTriage h, RedFlag f) => h.Path == f.Path && (f.Side == DiffSide.Right
            ? h.NewStart is { } s && h.NewEnd is { } e && f.Line >= s && f.Line <= e
            : h.OldStart is { } os && h.OldEnd is { } oe && f.Line >= os && f.Line <= oe);

        var opened = new Dictionary<HunkTriage, HunkTriage>(ReferenceEqualityComparer.Instance);
        foreach (var hunk in triage.Hunks.Where(h => h.Fold != FoldKind.Open))
        {
            if (flags.FirstOrDefault(f => Contains(hunk, f)) is { } flag)
            {
                opened[hunk] = hunk with { Fold = FoldKind.Open, Reasons = [$"contains a red flag ({Label(flag.Kind)})", .. hunk.Reasons] };
            }
        }

        if (opened.Count == 0)
        {
            return triage with { Flags = flags };
        }

        HunkTriage Swap(HunkTriage h) => opened.GetValueOrDefault(h) ?? h;
        return new TriageResult([.. triage.Hunks.Select(Swap)], [.. triage.Groups.Select(g => g with { Members = [.. g.Members.Select(Swap)] })]) { Flags = flags };
    }

    public static string Label(FlagKind kind) => kind switch
    {
        FlagKind.WeakenedTests => "Weakened tests",
        FlagKind.AntiforgeryOff => "Antiforgery off",
        FlagKind.AuthorisationLoosened => "Authorisation loosened",
        FlagKind.OverPosting => "Over-posting",
        FlagKind.GuardRemoved => "Guard or validation removed",
        FlagKind.RawOutput => "Raw output",
        FlagKind.RawSql => "Raw SQL",
        FlagKind.QuerySafety => "Query safety",
        FlagKind.SwallowedErrors => "Swallowed errors",
        FlagKind.WarningsSilenced => "Warnings silenced",
        FlagKind.DependenciesAndBuild => "Dependencies and build",
        _ => "Security config",
    };

    private static RedFlag WithIdentity(RedFlag flag, FlagFile file)
    {
        var tree = flag.Side == DiffSide.Right ? file.HeadTree : file.BaseTree;
        var text = flag.Side == DiffSide.Right ? file.HeadText : file.BaseText;
        var line = text?.Split('\n') is { } lines && flag.Line >= 1 && flag.Line <= lines.Length ? lines[flag.Line - 1] : "";
        return flag with
        {
            Member = flag.Member.Length > 0 ? flag.Member : tree is null ? "" : MemberAt(tree, flag.Line),
            NormalisedLine = flag.NormalisedLine.Length > 0 ? flag.NormalisedLine : RedFlag.Normalise(line),
        };
    }

    /// <summary>"Type.Member" enclosing a 1-based line, or "Type", or empty.</summary>
    public static string MemberAt(SyntaxTree tree, int line)
    {
        var text = tree.GetText();
        if (line < 1 || line > text.Lines.Count)
        {
            return "";
        }

        var span = text.Lines[line - 1].Span;
        var node = tree.GetRoot().FindNode(span, getInnermostNodeForTie: true);
        var member = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault(m => m is not BaseTypeDeclarationSyntax and not BaseNamespaceDeclarationSyntax);
        var type = node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        var memberName = member switch
        {
            MethodDeclarationSyntax m => m.Identifier.ValueText,
            PropertyDeclarationSyntax p => p.Identifier.ValueText,
            ConstructorDeclarationSyntax c => c.Identifier.ValueText,
            FieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.ValueText,
            _ => null,
        };
        return type is null ? memberName ?? "" : memberName is null ? type.Identifier.ValueText : $"{type.Identifier.ValueText}.{memberName}";
    }

    internal static IReadOnlySet<string> BoundTypes(IEnumerable<FlagFile> files)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in files.Select(f => f.HeadTree?.GetRoot()).OfType<SyntaxNode>())
        {
            foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>().Where(p => HasAttribute(p.AttributeLists, "BindProperty")))
            {
                names.UnionWith(property.Type.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().Select(n => n.Identifier.ValueText));
            }

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(IsHandler))
            {
                foreach (var parameter in method.ParameterList.Parameters.Where(p => p.Type is not null && !HasAttribute(p.AttributeLists, "FromServices")))
                {
                    names.UnionWith(parameter.Type!.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().Select(n => n.Identifier.ValueText));
                }
            }
        }

        names.ExceptWith(["int", "string", "bool", "Guid", "CancellationToken", "List", "IEnumerable", "IFormFile"]);
        return names;
    }

    /// <summary>A Razor Pages handler (OnGet, OnPostSave…) or a public action on a controller.</summary>
    public static bool IsHandler(MethodDeclarationSyntax method) =>
        Regex.IsMatch(method.Identifier.ValueText, "^On(Get|Post|Put|Delete|Patch)")
        || (method.Modifiers.Any(SyntaxKind.PublicKeyword) && method.Ancestors().OfType<ClassDeclarationSyntax>().Any(c =>
            c.BaseList?.Types.Any(t => t.Type.ToString() is "Controller" or "ControllerBase") == true));

    public static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, string name) =>
        lists.SelectMany(l => l.Attributes).Any(a => AttributeName(a) == name);

    public static string AttributeName(AttributeSyntax attribute)
    {
        var simple = attribute.Name.ToString().Split('.')[^1];
        return simple.EndsWith("Attribute", StringComparison.Ordinal) ? simple[..^"Attribute".Length] : simple;
    }

    public static int LineOf(SyntaxNode node) => node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1;

    public static int LineOf(SyntaxToken token) => token.SyntaxTree!.GetLineSpan(token.Span).StartLinePosition.Line + 1;

    public static bool IsCommentLine(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("/*", StringComparison.Ordinal) || t.StartsWith('*')
            || t.StartsWith("@*", StringComparison.Ordinal) || t.StartsWith("<!--", StringComparison.Ordinal);
    }

    public static string Code(string text) => $"`{RedFlag.Normalise(text).Trim()}`";

    public static bool IsJs(string path) => path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase);

    public static bool IsMarkup(string path) => path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".ascx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".master", StringComparison.OrdinalIgnoreCase);

    /// <summary>Script inside markup counts as script: the JS rules apply to both.</summary>
    public static bool HasScript(string path) => IsJs(path) || IsMarkup(path);

    /// <summary>
    /// The text of a call starting at <paramref name="start"/> (0-based char index of its opening parenthesis), up to
    /// the matching close, bounded so a broken file never scans far.
    /// </summary>
    public static string CallText(string text, int open, int max = 4000)
    {
        var depth = 0;
        for (var i = open; i < text.Length && i < open + max; i++)
        {
            var c = text[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return text[open..(i + 1)];
            }
        }

        return text[open..Math.Min(text.Length, open + max)];
    }

    public static int CharIndexOfLine(string text, int line)
    {
        var index = 0;
        for (var n = 1; n < line && index >= 0; n++)
        {
            index = text.IndexOf('\n', index) is var i and >= 0 ? i + 1 : -1;
        }

        return Math.Max(0, index);
    }

    public static string LineText(string text, int line)
    {
        var start = CharIndexOfLine(text, line);
        var end = text.IndexOf('\n', start);
        return end < 0 ? text[start..] : text[start..end];
    }

    public static int LineAt(string text, int index) => text.AsSpan(0, Math.Min(index, text.Length)).Count('\n') + 1;
}
