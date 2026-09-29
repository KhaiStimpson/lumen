using System.Text.RegularExpressions;
using Lumen.Analysis;
using Lumen.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lumen.Roslyn.Triage;

/// <summary>
/// Proves a hunk is a pure rename: base and base-plus-hunk are the same token stream under one consistent,
/// one-to-one identifier map; the old name is gone from head (no partial rename); the new name was not in base
/// (no capture); and every declaration of the old name, found in the pull request, is of a kind whose name does
/// not leak (locals, lambda and private-method parameters, private members, types). Renaming a public member,
/// a record property or an enum member changes serialised names, binding and callers, so it is left reviewable.
/// </summary>
public sealed class RenameClassifier : IHunkClassifier
{
    public HunkVerdict? Classify(HunkContext context)
    {
        if (CSharpHunkProof.For(context) is not { SameTokens: false, SameStructure: true } proof ||
            IdentifierMap.Between(proof.Before.Tokens, proof.After.Tokens) is not { Count: > 0 } map ||
            !CommentsFollowMap(proof, map))
        {
            return null;
        }

        var index = RenameIndex.For(context);
        foreach (var (from, to) in map)
        {
            if (!index.IsSafeRename(context.File.Path, from, to))
            {
                return null;
            }
        }

        var title = string.Join(", ", map.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"`{p.Key}`→`{p.Value}`"));
        var key = "rename:" + string.Join("|", map.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}>{p.Value}"));
        return new(ChangeClass.Rename, TriageTier.Skip, ["pure rename " + title], new GroupClaim(key, "Renamed " + title));
    }

    /// <summary>Comments may change only by the same renames (a <c>cref</c> or a mention of the old name).</summary>
    private static bool CommentsFollowMap(CSharpHunkProof proof, IReadOnlyDictionary<string, string> map)
    {
        if (proof.SameComments)
        {
            return true;
        }

        var renamed = proof.Before.Comments.Select(c => map.Aggregate(c, (text, p) => Regex.Replace(text, $@"\b{Regex.Escape(p.Key)}\b", p.Value)));
        return renamed.SequenceEqual(proof.After.Comments, StringComparer.Ordinal);
    }
}

/// <summary>The identifier map that turns one token stream into another, if one exists.</summary>
public static class IdentifierMap
{
    /// <summary>
    /// Null unless the streams align token for token, every difference is identifier-for-identifier, and the map is
    /// consistent and one-to-one. It says nothing about names left unchanged; callers check partial renames and captures.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Between(IReadOnlyList<SyntaxToken> before, IReadOnlyList<SyntaxToken> after)
    {
        if (before.Count != after.Count)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var reverse = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < before.Count; i++)
        {
            var (a, b) = (before[i], after[i]);
            if (CodeFingerprint.SameToken(a, b))
            {
                continue;
            }

            if (!a.IsKind(SyntaxKind.IdentifierToken) || !b.IsKind(SyntaxKind.IdentifierToken) ||
                !Bind(map, a.ValueText, b.ValueText) || !Bind(reverse, b.ValueText, a.ValueText))
            {
                return null;
            }
        }

        // Partial renames and captures are judged on the real head and base (RenameIndex), not here: a rename that
        // spans several hunks is necessarily partial when one hunk is applied on its own.
        return map;
    }

    private static bool Bind(Dictionary<string, string> map, string from, string to) =>
        map.TryGetValue(from, out var existing) ? existing == to : map.TryAdd(from, to);
}

/// <summary>Every declaration and identifier in the pull request's changed C# files, on both sides.</summary>
internal sealed class RenameIndex
{
    private readonly Dictionary<string, List<DeclarationKind>> declarations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> baseIdentifiers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> headIdentifiers = new(StringComparer.Ordinal);

    private enum DeclarationKind
    {
        Local,
        PrivateMember,
        Type,
        Leaky,
    }

    public static RenameIndex For(HunkContext context) =>
        context.Workspace.GetOrAdd("rename-index", () => new RenameIndex(context));

    private RenameIndex(HunkContext context)
    {
        foreach (var path in context.Sources.Paths.Where(CSharpHunkProof.IsCSharp))
        {
            if (context.Sources.Base(path) is { } baseText &&
                context.Workspace.GetOrAdd("fingerprint:base:" + path, () => CodeFingerprint.Of(baseText)) is { } before)
            {
                baseIdentifiers[path] = Identifiers(before);
                foreach (var token in before.Root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)))
                {
                    if (KindOf(token) is { } kind)
                    {
                        if (!declarations.TryGetValue(token.ValueText, out var list))
                        {
                            declarations[token.ValueText] = list = [];
                        }

                        list.Add(kind);
                    }
                }
            }

            if (context.Sources.Head(path) is { } headText &&
                context.Workspace.GetOrAdd("fingerprint:head:" + path, () => CodeFingerprint.Of(headText)) is { } after)
            {
                headIdentifiers[path] = Identifiers(after);
            }
        }
    }

    public bool IsSafeRename(string path, string from, string to)
    {
        if (!declarations.TryGetValue(from, out var kinds) || kinds.Any(k => k == DeclarationKind.Leaky))
        {
            return false;
        }

        if (!headIdentifiers.TryGetValue(path, out var head) || head.Contains(from) ||
            !baseIdentifiers.TryGetValue(path, out var @base) || @base.Contains(to))
        {
            return false;
        }

        // A type is visible everywhere: no changed file may still use the old name or already use the new one.
        return !kinds.Contains(DeclarationKind.Type) ||
               (!headIdentifiers.Values.Any(ids => ids.Contains(from)) && !baseIdentifiers.Values.Any(ids => ids.Contains(to)));
    }

    private static HashSet<string> Identifiers(CodeFingerprint fingerprint) =>
        fingerprint.Tokens.Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText).ToHashSet(StringComparer.Ordinal);

    /// <summary>The kind of declaration <paramref name="token"/> names, or null when it is a use, not a declaration.</summary>
    private static DeclarationKind? KindOf(SyntaxToken token) => token.Parent switch
    {
        VariableDeclaratorSyntax v when v.Identifier == token => v.Parent?.Parent switch
        {
            FieldDeclarationSyntax f => IsPrivate(f.Modifiers, f) ? DeclarationKind.PrivateMember : DeclarationKind.Leaky,
            EventFieldDeclarationSyntax e => IsPrivate(e.Modifiers, e) ? DeclarationKind.PrivateMember : DeclarationKind.Leaky,
            _ => DeclarationKind.Local,
        },
        SingleVariableDesignationSyntax => DeclarationKind.Local,
        ForEachStatementSyntax f when f.Identifier == token => DeclarationKind.Local,
        CatchDeclarationSyntax => DeclarationKind.Local,
        LocalFunctionStatementSyntax l when l.Identifier == token => DeclarationKind.Local,
        QueryClauseSyntax or QueryContinuationSyntax => DeclarationKind.Local,
        TypeParameterSyntax => DeclarationKind.Local,
        ParameterSyntax p when p.Identifier == token => p.Parent?.Parent switch
        {
            LambdaExpressionSyntax or AnonymousMethodExpressionSyntax or LocalFunctionStatementSyntax => DeclarationKind.Local,
            MethodDeclarationSyntax m => IsPrivate(m.Modifiers, m) && !m.Modifiers.Any(SyntaxKind.PartialKeyword) ? DeclarationKind.Local : DeclarationKind.Leaky,
            _ => DeclarationKind.Leaky,
        },
        MethodDeclarationSyntax m when m.Identifier == token =>
            IsPrivate(m.Modifiers, m) && !m.Modifiers.Any(SyntaxKind.PartialKeyword) ? DeclarationKind.PrivateMember : DeclarationKind.Leaky,
        PropertyDeclarationSyntax p when p.Identifier == token => IsPrivate(p.Modifiers, p) ? DeclarationKind.PrivateMember : DeclarationKind.Leaky,
        EventDeclarationSyntax e when e.Identifier == token => IsPrivate(e.Modifiers, e) ? DeclarationKind.PrivateMember : DeclarationKind.Leaky,
        EnumMemberDeclarationSyntax => DeclarationKind.Leaky,
        BaseTypeDeclarationSyntax t when t.Identifier == token => DeclarationKind.Type,
        DelegateDeclarationSyntax d when d.Identifier == token => DeclarationKind.Type,
        ConstructorDeclarationSyntax or DestructorDeclarationSyntax => DeclarationKind.Type,
        _ => null,
    };

    private static bool IsPrivate(SyntaxTokenList modifiers, SyntaxNode member) =>
        member.Parent is not InterfaceDeclarationSyntax &&
        !modifiers.Any(SyntaxKind.PublicKeyword) &&
        !modifiers.Any(SyntaxKind.ProtectedKeyword) &&
        !modifiers.Any(SyntaxKind.InternalKeyword) &&
        !modifiers.Any(SyntaxKind.OverrideKeyword) &&
        !modifiers.Any(SyntaxKind.ExternKeyword);
}
