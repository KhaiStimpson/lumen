using Lumen.Analysis;
using Lumen.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lumen.Roslyn.Triage;

/// <summary>
/// A signature change and the call-site edits that only apply it. The declaration hunk stays reviewable (a behaviour
/// change, with the change in words); call sites that do nothing but follow it are grouped so they can be cleared
/// together. Without a semantic model a call site cannot be proven to target this member, so call sites are Skim,
/// never Skip. Handles: a renamed member whose rename leaks (public member, enum member), and one parameter added,
/// removed or renamed on a method or constructor whose name is declared once in the pull request.
/// </summary>
public sealed class RippleClassifier : IHunkClassifier
{
    public HunkVerdict? Classify(HunkContext context)
    {
        if (CSharpHunkProof.For(context) is not { SameTokens: false, SameStructure: true } proof)
        {
            return null;
        }

        var index = SignatureIndex.For(context);
        return DeclarationChange(proof, index) ?? RenamedCallSites(context, proof, index) ?? ParameterCallSites(proof, index);
    }

    /// <summary>The hunk that changes a signature: still a behaviour change, but it says what changed and how far it reaches.</summary>
    private static HunkVerdict? DeclarationChange(CSharpHunkProof proof, SignatureIndex index)
    {
        var reasons = new List<string>();
        foreach (var change in index.ParameterChanges.Values)
        {
            if (DeclarationsOf(proof.Before.Root, change.Name).Select(ParameterText).FirstOrDefault() is { } before &&
                DeclarationsOf(proof.After.Root, change.Name).Select(ParameterText).FirstOrDefault() is { } after &&
                before != after)
            {
                reasons.Add($"changes the signature of `{change.Name}`: {change.Description}");
            }
        }

        foreach (var (from, to) in DifferingIdentifiers(proof).Where(d => d.IsDeclaration).Select(d => (d.From, d.To)))
        {
            if (index.MemberRenames.TryGetValue(from, out var renamedTo) && renamedTo == to)
            {
                reasons.Add($"renames `{from}`→`{to}`, which callers and serialised names can see");
            }
        }

        return reasons.Count == 0 ? null : new(ChangeClass.BehaviourChange, TriageTier.WorthALook, reasons.Distinct().ToList());
    }

    private static HunkVerdict? RenamedCallSites(HunkContext context, CSharpHunkProof proof, SignatureIndex index)
    {
        if (IdentifierMap.Between(proof.Before.Tokens, proof.After.Tokens) is not { Count: > 0 } map ||
            !proof.SameComments ||
            DifferingIdentifiers(proof).Any(d => d.IsDeclaration))
        {
            return null;
        }

        foreach (var (from, to) in map)
        {
            if (!index.MemberRenames.TryGetValue(from, out var renamedTo) || renamedTo != to ||
                index.StillUsed(from) || index.UsedInBase(context.File.Path, to))
            {
                return null;
            }
        }

        var names = string.Join(", ", map.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"`{p.Key}`→`{p.Value}`"));
        var key = "ripple:rename:" + string.Join("|", map.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}>{p.Value}"));
        return new(ChangeClass.Ripple, TriageTier.Skim, [$"only follows the rename {names}"], new GroupClaim(key, $"Call sites follow the rename {names}"));
    }

    private static HunkVerdict? ParameterCallSites(CSharpHunkProof proof, SignatureIndex index)
    {
        if (!proof.SameComments)
        {
            return null;
        }

        foreach (var change in index.ParameterChanges.Values)
        {
            var before = CallsOf(proof.Before.Root, change);
            var after = CallsOf(proof.After.Root, change);
            // Everything outside the calls' argument lists must be untouched (the declaration hunk was claimed above).
            if (before.Count == 0 || before.Count != after.Count ||
                !CodeFingerprint.SameTokens(Outside(proof.Before, before), Outside(proof.After, after)))
            {
                continue;
            }

            var pairs = before.Zip(after).Select(p => (Before: Arguments(p.First), After: Arguments(p.Second))).ToList();
            if (pairs.All(p => p.Before.SequenceEqual(p.After)) || !pairs.All(p => p.Before.SequenceEqual(p.After) || change.Applies(p.Before, p.After)))
            {
                continue;
            }

            return new(
                ChangeClass.Ripple,
                TriageTier.Skim,
                [$"only follows the new signature of `{change.Name}` ({change.Description})"],
                new GroupClaim("ripple:signature:" + change.Name, $"Call sites follow the new signature of `{change.Name}`"));
        }

        return null;
    }

    private static List<(string From, string To, bool IsDeclaration)> DifferingIdentifiers(CSharpHunkProof proof)
    {
        var result = new List<(string, string, bool)>();
        if (proof.Before.Tokens.Count != proof.After.Tokens.Count)
        {
            return result;
        }

        for (var i = 0; i < proof.Before.Tokens.Count; i++)
        {
            var (a, b) = (proof.Before.Tokens[i], proof.After.Tokens[i]);
            if (!CodeFingerprint.SameToken(a, b) && a.IsKind(SyntaxKind.IdentifierToken) && b.IsKind(SyntaxKind.IdentifierToken))
            {
                result.Add((a.ValueText, b.ValueText, SignatureIndex.IsDeclarationIdentifier(a)));
            }
        }

        return result;
    }

    internal static IEnumerable<BaseMethodDeclarationSyntax> DeclarationsOf(SyntaxNode root, string name) =>
        root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>().Where(m => m switch
        {
            MethodDeclarationSyntax md => md.Identifier.ValueText == name,
            ConstructorDeclarationSyntax cd => cd.Identifier.ValueText == name,
            _ => false,
        });

    internal static string ParameterText(BaseMethodDeclarationSyntax m) => CodeFingerprint.Normalise(m.ParameterList.ToString());

    /// <summary>Invocations of the method (by name) or object creations of the type, in document order.</summary>
    private static List<SyntaxNode> CallsOf(SyntaxNode root, ParameterChange change) =>
        root.DescendantNodes().Where(n => n switch
        {
            InvocationExpressionSyntax i => !change.IsConstructor && NameOf(i.Expression) == change.Name,
            ObjectCreationExpressionSyntax o => change.IsConstructor && NameOf(o.Type) == change.Name,
            ConstructorInitializerSyntax => false,
            _ => false,
        }).ToList();

    private static string? NameOf(SyntaxNode expression) => expression switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        GenericNameSyntax g => g.Identifier.ValueText,
        MemberAccessExpressionSyntax ma => NameOf(ma.Name),
        QualifiedNameSyntax q => NameOf(q.Right),
        MemberBindingExpressionSyntax mb => NameOf(mb.Name),
        _ => null,
    };

    private static ArgumentListSyntax? ArgumentListOf(SyntaxNode call) => call switch
    {
        InvocationExpressionSyntax i => i.ArgumentList,
        ObjectCreationExpressionSyntax o => o.ArgumentList,
        _ => null,
    };

    private static List<string> Arguments(SyntaxNode call) =>
        ArgumentListOf(call)?.Arguments.Select(a => string.Join(" ", a.DescendantTokens().Select(t => t.Text))).ToList() ?? [];

    /// <summary>Every token not inside the argument list of one of <paramref name="calls"/>.</summary>
    private static List<SyntaxToken> Outside(CodeFingerprint fingerprint, List<SyntaxNode> calls)
    {
        var spans = calls.Select(ArgumentListOf).OfType<ArgumentListSyntax>().Select(a => a.Span).ToList();
        return fingerprint.Tokens.Where(t => !spans.Any(s => s.Contains(t.Span))).ToList();
    }
}

internal enum ParameterChangeKind
{
    Added,
    Removed,
    Renamed,
}

internal sealed record ParameterChange(string Name, bool IsConstructor, ParameterChangeKind Kind, int Index, string Parameter, string? NewName)
{
    public string Description => Kind switch
    {
        ParameterChangeKind.Added => $"added parameter `{Parameter}`",
        ParameterChangeKind.Removed => $"removed parameter `{Parameter}`",
        _ => $"renamed parameter `{Parameter}`→`{NewName}`",
    };

    /// <summary>True when a call's arguments changed exactly as this signature change requires.</summary>
    public bool Applies(List<string> before, List<string> after)
    {
        switch (Kind)
        {
            case ParameterChangeKind.Added when after.Count == before.Count + 1:
                return Enumerable.Range(0, after.Count).Any(i =>
                    (i == Index || after[i].StartsWith(Parameter + " :", StringComparison.Ordinal)) &&
                    after.Where((_, j) => j != i).SequenceEqual(before));
            case ParameterChangeKind.Removed when before.Count == after.Count + 1:
                return Enumerable.Range(0, before.Count).Any(i =>
                    (i == Index || before[i].StartsWith(Parameter + " :", StringComparison.Ordinal)) &&
                    before.Where((_, j) => j != i).SequenceEqual(after));
            case ParameterChangeKind.Renamed when before.Count == after.Count:
                var diffs = before.Zip(after).Where(p => p.First != p.Second).ToList();
                return diffs.Count == 1 &&
                       diffs[0].First.StartsWith(Parameter + " :", StringComparison.Ordinal) &&
                       diffs[0].Second == NewName + diffs[0].First[Parameter.Length..];
            default:
                return false;
        }
    }
}

/// <summary>Signature changes across the pull request: leaky member renames and single-parameter changes.</summary>
internal sealed class SignatureIndex
{
    private readonly Dictionary<string, HashSet<string>> headIdentifiers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> baseIdentifiers = new(StringComparer.Ordinal);

    private SignatureIndex(HunkContext context)
    {
        var declaredNames = new Dictionary<string, int>(StringComparer.Ordinal);
        var bases = new Dictionary<string, CodeFingerprint>(StringComparer.Ordinal);
        var heads = new Dictionary<string, CodeFingerprint>(StringComparer.Ordinal);
        foreach (var path in context.Sources.Paths.Where(CSharpHunkProof.IsCSharp))
        {
            if (context.Sources.Base(path) is { } b && context.Workspace.GetOrAdd("fingerprint:base:" + path, () => CodeFingerprint.Of(b)) is { } before)
            {
                bases[path] = before;
                baseIdentifiers[path] = before.Tokens.Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText).ToHashSet(StringComparer.Ordinal);
                foreach (var m in before.Root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                {
                    if (NameOf(m) is { } n)
                    {
                        declaredNames[n] = declaredNames.GetValueOrDefault(n) + 1;
                    }
                }
            }

            if (context.Sources.Head(path) is { } h && context.Workspace.GetOrAdd("fingerprint:head:" + path, () => CodeFingerprint.Of(h)) is { } after)
            {
                heads[path] = after;
                headIdentifiers[path] = after.Tokens.Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText).ToHashSet(StringComparer.Ordinal);
            }
        }

        // Parameter changes: a method or constructor declared once, in a file present on both sides.
        foreach (var (path, before) in bases)
        {
            if (!heads.TryGetValue(path, out var after))
            {
                continue;
            }

            foreach (var name in before.Root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>().Select(NameOf).OfType<string>().Distinct())
            {
                var b = RippleClassifier.DeclarationsOf(before.Root, name).ToList();
                var a = RippleClassifier.DeclarationsOf(after.Root, name).ToList();
                if (declaredNames.GetValueOrDefault(name) == 1 && b.Count == 1 && a.Count == 1 && Compare(name, b[0], a[0]) is { } change)
                {
                    ParameterChanges[name] = change;
                }
            }
        }

        // Leaky member renames, harvested from hunks whose token streams differ only by identifiers.
        foreach (var file in context.Snapshot.Files.Where(f => CSharpHunkProof.IsCSharp(f.Path) && bases.ContainsKey(f.Path)))
        {
            foreach (var hunk in file.Hunks)
            {
                var proof = CSharpHunkProof.For(context with { File = file, Hunk = hunk, BaseText = context.Sources.Base(file.Path), HeadText = context.Sources.Head(file.Path) });
                if (proof is null || proof.Before.Tokens.Count != proof.After.Tokens.Count)
                {
                    continue;
                }

                for (var i = 0; i < proof.Before.Tokens.Count; i++)
                {
                    var (x, y) = (proof.Before.Tokens[i], proof.After.Tokens[i]);
                    if (x.IsKind(SyntaxKind.IdentifierToken) && y.IsKind(SyntaxKind.IdentifierToken) && x.ValueText != y.ValueText &&
                        IsMemberDeclarationIdentifier(x))
                    {
                        MemberRenames[x.ValueText] = y.ValueText;
                    }
                }
            }
        }
    }

    public Dictionary<string, ParameterChange> ParameterChanges { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> MemberRenames { get; } = new(StringComparer.Ordinal);

    public static SignatureIndex For(HunkContext context) => context.Workspace.GetOrAdd("signature-index", () => new SignatureIndex(context));

    public bool StillUsed(string name) => headIdentifiers.Values.Any(ids => ids.Contains(name));

    public bool UsedInBase(string path, string name) => baseIdentifiers.TryGetValue(path, out var ids) && ids.Contains(name);

    public static bool IsDeclarationIdentifier(SyntaxToken token) => token.Parent switch
    {
        VariableDeclaratorSyntax v => v.Identifier == token,
        ParameterSyntax p => p.Identifier == token,
        MethodDeclarationSyntax m => m.Identifier == token,
        PropertyDeclarationSyntax p => p.Identifier == token,
        EventDeclarationSyntax e => e.Identifier == token,
        EnumMemberDeclarationSyntax e => e.Identifier == token,
        BaseTypeDeclarationSyntax t => t.Identifier == token,
        ConstructorDeclarationSyntax c => c.Identifier == token,
        LocalFunctionStatementSyntax l => l.Identifier == token,
        SingleVariableDesignationSyntax or CatchDeclarationSyntax or TypeParameterSyntax => true,
        ForEachStatementSyntax f => f.Identifier == token,
        _ => false,
    };

    private static bool IsMemberDeclarationIdentifier(SyntaxToken token) => token.Parent switch
    {
        MethodDeclarationSyntax m => m.Identifier == token,
        PropertyDeclarationSyntax p => p.Identifier == token,
        EventDeclarationSyntax e => e.Identifier == token,
        EnumMemberDeclarationSyntax e => e.Identifier == token,
        VariableDeclaratorSyntax v => v.Identifier == token && v.Parent?.Parent is BaseFieldDeclarationSyntax,
        _ => false,
    };

    private static string? NameOf(BaseMethodDeclarationSyntax m) => m switch
    {
        MethodDeclarationSyntax md => md.Identifier.ValueText,
        ConstructorDeclarationSyntax cd => cd.Identifier.ValueText,
        _ => null,
    };

    private static ParameterChange? Compare(string name, BaseMethodDeclarationSyntax before, BaseMethodDeclarationSyntax after)
    {
        var b = before.ParameterList.Parameters;
        var a = after.ParameterList.Parameters;
        if (b.Concat(a).Any(p => p.Modifiers.Any(SyntaxKind.ThisKeyword) || p.Modifiers.Any(SyntaxKind.ParamsKeyword)))
        {
            return null;
        }

        var bt = b.Select(p => CodeFingerprint.Normalise(p.ToString())).ToList();
        var at = a.Select(p => CodeFingerprint.Normalise(p.ToString())).ToList();
        var ctor = before is ConstructorDeclarationSyntax;
        if (at.Count == bt.Count + 1)
        {
            for (var i = 0; i < at.Count; i++)
            {
                if (at.Where((_, j) => j != i).SequenceEqual(bt))
                {
                    return new(name, ctor, ParameterChangeKind.Added, i, a[i].Identifier.ValueText, null);
                }
            }
        }
        else if (bt.Count == at.Count + 1)
        {
            for (var i = 0; i < bt.Count; i++)
            {
                if (bt.Where((_, j) => j != i).SequenceEqual(at))
                {
                    return new(name, ctor, ParameterChangeKind.Removed, i, b[i].Identifier.ValueText, null);
                }
            }
        }
        else if (bt.Count == at.Count)
        {
            var diffs = Enumerable.Range(0, bt.Count).Where(i => bt[i] != at[i]).ToList();
            if (diffs.Count == 1 && b[diffs[0]].Type?.ToString() == a[diffs[0]].Type?.ToString() &&
                b[diffs[0]].Identifier.ValueText != a[diffs[0]].Identifier.ValueText)
            {
                var i = diffs[0];
                return new(name, ctor, ParameterChangeKind.Renamed, i, b[i].Identifier.ValueText, a[i].Identifier.ValueText);
            }
        }

        return null;
    }
}
