using System.Globalization;
using System.Text.RegularExpressions;
using Lumen.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Lumen.Roslyn.Flags.RedFlags;

namespace Lumen.Roslyn.Flags;

/// <summary>
/// Weakened tests: a test method deleted, a skip added, asserts removed with no replacement, an assert made vacuous,
/// or <c>.only</c>/<c>xit</c> in JS tests.
/// </summary>
public sealed partial class WeakenedTestsDetector : IRedFlagDetector
{
    private static readonly HashSet<string> TestAttributes = new(StringComparer.Ordinal) { "Fact", "Theory", "Test", "TestMethod", "TestCase" };

    public IEnumerable<RedFlag> Detect(FlagFile file, FlagContext context)
    {
        if (!TestPaths.IsTest(file.Path) && !Regex.IsMatch(file.Path, @"\.(spec|test)\.[jt]s$", RegexOptions.IgnoreCase))
        {
            yield break;
        }

        foreach (var line in file.AddedCode)
        {
            if (file.IsCSharp && SkipAdded().Match(line.Text) is { Success: true } skip)
            {
                yield return new RedFlag(FlagKind.WeakenedTests, file.Path, DiffSide.Right, line.NewNumber!.Value, $"Test skipped: {Code(skip.Value)} added.");
            }
            else if (file.IsCSharp && Vacuous(line.Text) is { } vacuous)
            {
                yield return new RedFlag(FlagKind.WeakenedTests, file.Path, DiffSide.Right, line.NewNumber!.Value, $"Assert can never fail: {Code(vacuous)}.");
            }
            else if (IsJs(file.Path) && JsFocus().Match(line.Text) is { Success: true } focus)
            {
                yield return new RedFlag(FlagKind.WeakenedTests, file.Path, DiffSide.Right, line.NewNumber!.Value,
                    focus.Value.StartsWith('x') ? $"Test disabled: {Code(focus.Value)}." : $"Only this test runs: {Code(focus.Value)}.");
            }
        }

        if (file.BaseTree is not { } baseTree)
        {
            yield break;
        }

        var headMethods = (file.HeadTree?.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>() ?? [])
            .GroupBy(m => m.Identifier.ValueText).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var removedLines = file.Removed.Select(l => l.OldNumber!.Value).ToHashSet();
        foreach (var test in baseTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Where(IsTest))
        {
            var name = test.Identifier.ValueText;
            if (!headMethods.TryGetValue(name, out var now))
            {
                // Moved to another changed file is not deleted.
                var movedTo = context.Files.Any(f => f != file && f.HeadTree?.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.ValueText == name && IsTest(m)) == true);
                if (!movedTo && removedLines.Contains(LineOf(test.Identifier)))
                {
                    yield return new RedFlag(FlagKind.WeakenedTests, file.Path, DiffSide.Left, LineOf(test.Identifier), $"Test `{name}` deleted.");
                }

                continue;
            }

            var before = Asserts(test);
            var after = Asserts(now);
            var removedAsserts = before.Where(a => removedLines.Contains(LineOf(a))).ToList();
            if (after.Count < before.Count && removedAsserts.Count > 0)
            {
                var gone = before.Count - after.Count;
                yield return new RedFlag(FlagKind.WeakenedTests, file.Path, DiffSide.Left, LineOf(removedAsserts[0]),
                    string.Create(CultureInfo.InvariantCulture, $"{gone} assert{(gone == 1 ? "" : "s")} removed from `{name}` with no replacement ({before.Count} → {after.Count})."));
            }
        }
    }

    private static bool IsTest(MethodDeclarationSyntax method) => method.AttributeLists.SelectMany(l => l.Attributes).Any(a => TestAttributes.Contains(AttributeName(a)));

    /// <summary>Assert.*, Should*, Verify* and Expect calls in a test body.</summary>
    private static List<InvocationExpressionSyntax> Asserts(MethodDeclarationSyntax method) =>
    [
        .. method.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => i.Expression switch
        {
            MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Assert" or "CollectionAssert" or "StringAssert" } } => true,
            MemberAccessExpressionSyntax { Name.Identifier.ValueText: var n } => n.StartsWith("Should", StringComparison.Ordinal) || n is "Verify" or "VerifyAll",
            _ => false,
        }),
    ];

    private static string? Vacuous(string line)
    {
        if (VacuousLiteral().Match(line) is { Success: true } literal)
        {
            return literal.Value;
        }

        // Assert.Equal(x, x): the expected value replaced with the actual.
        return SameArguments().Match(line) is { Success: true } same && same.Groups["a"].Value.Trim() == same.Groups["b"].Value.Trim() ? same.Value : null;
    }

    [GeneratedRegex(@"\[\s*(Fact|Theory|Test|TestMethod)\s*\(\s*Skip\s*=[^\]]*\]|\[\s*(Ignore|Skip)\b[^\]]*\]")]
    private static partial Regex SkipAdded();

    [GeneratedRegex(@"Assert\.(True\(\s*true\s*\)|False\(\s*false\s*\)|Pass\(\s*\))")]
    private static partial Regex VacuousLiteral();

    [GeneratedRegex(@"Assert\.(Equal|Same|StrictEqual|AreEqual)\((?<a>[^,()]+),(?<b>[^,()]+)\)")]
    private static partial Regex SameArguments();

    [GeneratedRegex(@"\b(it|describe|test)\.only\s*\(|\bx(it|describe|test)\s*\(")]
    private static partial Regex JsFocus();
}

/// <summary>
/// Antiforgery off: <c>[IgnoreAntiforgeryToken]</c>, <c>asp-antiforgery="false"</c>, the convention filter in
/// Program.cs, and a jQuery POST that sends neither a serialised form nor the token header.
/// </summary>
public sealed partial class AntiforgeryDetector : IRedFlagDetector
{
    public IEnumerable<RedFlag> Detect(FlagFile file, FlagContext context)
    {
        foreach (var line in file.AddedCode)
        {
            if ((file.IsCSharp && line.Text.Contains("IgnoreAntiforgeryToken", StringComparison.Ordinal))
                || (IsMarkup(file.Path) && AspOff().IsMatch(line.Text)))
            {
                yield return new RedFlag(FlagKind.AntiforgeryOff, file.Path, DiffSide.Right, line.NewNumber!.Value, $"Antiforgery validation turned off: {Code(line.Text)}.");
            }
        }

        if (!HasScript(file.Path) || file.HeadText is not { } head)
        {
            yield break;
        }

        var added = file.Added.Select(l => l.NewNumber!.Value).ToHashSet();
        var fileHasToken = head.Contains("RequestVerificationToken", StringComparison.Ordinal);
        foreach (Match call in Post().Matches(head))
        {
            var line = LineAt(head, call.Index);
            if (!added.Contains(line) || IsCommentLine(LineText(head, line)))
            {
                continue;
            }

            var text = CallText(head, call.Index + call.Length - 1);
            var isPost = call.Value.Contains("post", StringComparison.OrdinalIgnoreCase) || PostType().IsMatch(text);
            var sendsForm = text.Contains("serialize", StringComparison.Ordinal) || text.Contains("FormData", StringComparison.Ordinal);
            var sendsToken = text.Contains("RequestVerificationToken", StringComparison.Ordinal) || (fileHasToken && text.Contains("headers", StringComparison.Ordinal));
            if (isPost && !sendsForm && !sendsToken)
            {
                yield return new RedFlag(FlagKind.AntiforgeryOff, file.Path, DiffSide.Right, line,
                    "jQuery POST sends neither a serialised form nor a `RequestVerificationToken` header.");
            }
        }
    }

    [GeneratedRegex(@"asp-antiforgery\s*=\s*""false""", RegexOptions.IgnoreCase)]
    private static partial Regex AspOff();

    [GeneratedRegex(@"\$\.(post|ajax)\s*\(")]
    private static partial Regex Post();

    [GeneratedRegex(@"(type|method)\s*:\s*['""]post['""]", RegexOptions.IgnoreCase)]
    private static partial Regex PostType();
}

/// <summary>
/// Authorisation loosened: an <c>Authorize*</c> convention removed, <c>AllowAnonymousTo*</c> added, the fallback
/// policy changed, <c>[Authorize]</c> removed, or <c>[AllowAnonymous]</c> added.
/// </summary>
public sealed partial class AuthorisationDetector : IRedFlagDetector
{
    public IEnumerable<RedFlag> Detect(FlagFile file, FlagContext context)
    {
        if (!file.IsCSharp)
        {
            yield break;
        }

        foreach (var line in file.Removed.Where(l => !file.StillAdded(l.Text) && !IsCommentLine(l.Text)))
        {
            if (Convention().IsMatch(line.Text))
            {
                yield return new RedFlag(FlagKind.AuthorisationLoosened, file.Path, DiffSide.Left, line.OldNumber!.Value, $"Authorisation convention removed: {Code(line.Text)}.");
            }
            else if (AuthorizeAttribute().IsMatch(line.Text))
            {
                yield return new RedFlag(FlagKind.AuthorisationLoosened, file.Path, DiffSide.Left, line.OldNumber!.Value, $"{Code(line.Text)} removed.");
            }
            else if (line.Text.Contains("FallbackPolicy", StringComparison.Ordinal))
            {
                yield return new RedFlag(FlagKind.AuthorisationLoosened, file.Path, DiffSide.Left, line.OldNumber!.Value, $"Fallback policy changed: {Code(line.Text)} removed.");
            }
        }

        foreach (var line in file.AddedCode)
        {
            if (line.Text.Contains("AllowAnonymousTo", StringComparison.Ordinal) || AllowAnonymous().IsMatch(line.Text))
            {
                yield return new RedFlag(FlagKind.AuthorisationLoosened, file.Path, DiffSide.Right, line.NewNumber!.Value, $"Anonymous access added: {Code(line.Text)}.");
            }
            else if (line.Text.Contains("FallbackPolicy", StringComparison.Ordinal) && !file.Removed.Any(r => RedFlag.Normalise(r.Text) == RedFlag.Normalise(line.Text)))
            {
                yield return new RedFlag(FlagKind.AuthorisationLoosened, file.Path, DiffSide.Right, line.NewNumber!.Value, $"Fallback policy changed: {Code(line.Text)}.");
            }
        }
    }

    [GeneratedRegex(@"\bAuthorize(Area)?(Folder|Page)\s*\(")]
    private static partial Regex Convention();

    [GeneratedRegex(@"\[\s*Authorize\b")]
    private static partial Regex AuthorizeAttribute();

    [GeneratedRegex(@"\[\s*AllowAnonymous\b")]
    private static partial Regex AllowAnonymous();
}

/// <summary>
/// Over-posting: a new settable property on a type that already existed and is bound from the request (a
/// <c>[BindProperty]</c> model, a handler parameter, an entity bound directly), or a new <c>[BindProperty]</c> on an
/// existing page. Brand-new pages and models are not flagged: everything on them is new.
/// </summary>
public sealed class OverPostingDetector : IRedFlagDetector
{
    public IEnumerable<RedFlag> Detect(FlagFile file, FlagContext context)
    {
        if (file.HeadTree is not { } headTree || file.BaseTree is not { } baseTree)
        {
            yield break;
        }

        var added = file.Added.Select(l => l.NewNumber!.Value).ToHashSet();
        var baseTypes = baseTree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().Select(t => t.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        var baseProperties = baseTree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Select(p => (p.Parent as TypeDeclarationSyntax)?.Identifier.ValueText + "." + p.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        foreach (var property in headTree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            if (property.Parent is not TypeDeclarationSyntax owner || !baseTypes.Contains(owner.Identifier.ValueText) || !added.Contains(LineOf(property.Identifier))
                || baseProperties.Contains(owner.Identifier.ValueText + "." + property.Identifier.ValueText) || !Settable(property))
            {
                continue;
            }

            if (HasAttribute(property.AttributeLists, "BindProperty"))
            {
                yield return new RedFlag(FlagKind.OverPosting, file.Path, DiffSide.Right, LineOf(property.Identifier),
                    $"New `[BindProperty]` `{property.Identifier.ValueText}` on `{owner.Identifier.ValueText}`: the request can set it.");
            }
            else if (context.BoundTypes.Contains(owner.Identifier.ValueText) && !HasAttribute(property.AttributeLists, "BindNever") && !HasAttribute(property.AttributeLists, "ValidateNever"))
            {
                yield return new RedFlag(FlagKind.OverPosting, file.Path, DiffSide.Right, LineOf(property.Identifier),
                    $"New settable property `{property.Identifier.ValueText}` on `{owner.Identifier.ValueText}`, which is bound from the request.");
            }
        }
    }

    private static bool Settable(PropertyDeclarationSyntax property) =>
        property.Modifiers.Any(SyntaxKind.PublicKeyword)
        && property.AccessorList?.Accessors.Any(a => a.Kind() is SyntaxKind.SetAccessorDeclaration or SyntaxKind.InitAccessorDeclaration && !a.Modifiers.Any(m => m.Kind() is SyntaxKind.PrivateKeyword or SyntaxKind.ProtectedKeyword or SyntaxKind.InternalKeyword)) == true;
}

/// <summary>
/// Guard or validation removed: a data-annotation attribute, a <c>ModelState.IsValid</c> check, or a null or
/// argument guard removed with nothing (<c>ThrowIfNull</c>, <c>?.</c>, <c>??</c>) taking its place on the same
/// expression.
/// </summary>
public sealed partial class GuardRemovedDetector : IRedFlagDetector
{
    public IEnumerable<RedFlag> Detect(FlagFile file, FlagContext context)
    {
        if (!file.IsCSharp)
        {
            yield break;
        }

        var added = file.Added.Select(l => l.Text).ToList();
        var guarded = new Dictionary<string, int>(StringComparer.Ordinal);
        // Moved elsewhere in the file is not removed.
        foreach (var line in file.Removed.Where(l => !IsCommentLine(l.Text) && !file.HeadContains(RedFlag.Normalise(l.Text))))
        {
            if (Annotation().Match(line.Text) is { Success: true } annotation)
            {
                yield return new RedFlag(FlagKind.GuardRemoved, file.Path, DiffSide.Left, line.OldNumber!.Value, $"Validation attribute removed: {Code(annotation.Value)}.");
            }
            else if (line.Text.Contains("ModelState.IsValid", StringComparison.Ordinal) && !added.Any(a => a.Contains("ModelState.IsValid", StringComparison.Ordinal)))
            {
                yield return new RedFlag(FlagKind.GuardRemoved, file.Path, DiffSide.Left, line.OldNumber!.Value, "`ModelState.IsValid` check removed.");
            }
            else if (NullGuard().Match(line.Text) is { Success: true } guard && IsGuard(file, line, guard) && StillUsed(file, line, guard.Groups["name"].Value) && !MovedToAnotherFile(file, line, context))
            {
                var name = guard.Groups["name"].Value;
                var escaped = Regex.Escape(name);
                var replaced = added.Any(a => Regex.IsMatch(a, $@"{escaped}\s*\?[.\[]|{escaped}\s*\?\?|ThrowIfNull\(\s*{escaped}\b|{escaped}\s*(is|==|!=)\s*(not\s+)?null|{escaped}\s*is\s*\{{|!\s*[\w.]*Try\w*\(.*\bout\s+(var\s+|[\w<>?]+\s+)?{escaped}\b"));
                // One guard written over a few lines (`if (x is null)` then its `throw`) is one flag.
                var sameGuard = guarded.TryGetValue(name, out var at) && line.OldNumber!.Value - at <= 3;
                guarded[name] = line.OldNumber!.Value;
                if (!replaced && !sameGuard)
                {
                    yield return new RedFlag(FlagKind.GuardRemoved, file.Path, DiffSide.Left, line.OldNumber!.Value, $"Null guard on `{name}` removed: {Code(line.Text)}.");
                }
            }
        }
    }

    /// <summary>
    /// The guarded name is still used by the same member at head. A guard removed with everything it guarded (the
    /// member rewritten to call a service, the variable gone) protected nothing that is left.
    /// </summary>
    private static bool StillUsed(FlagFile file, DiffLine line, string name)
    {
        // A deleted file takes its guards with it.
        if (file.File.Kind == FileChangeKind.Deleted)
        {
            return false;
        }

        if (file.BaseTree is not { } baseTree || file.HeadTree is not { } headTree || line.OldNumber is not { } number)
        {
            return true;
        }

        var member = MemberAt(baseTree, number);
        var root = name.Split('.')[0];
        var now = headTree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Where(m => m is MethodDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax)
            .FirstOrDefault(m => member.Length > 0 && MemberAt(headTree, LineOf(m)) == member);
        return now is not null && now.DescendantNodes().OfType<IdentifierNameSyntax>().Any(i => i.Identifier.ValueText == root);
    }

    /// <summary>The whole guard statement (condition and body) added, word for word, in another changed file.</summary>
    private static bool MovedToAnotherFile(FlagFile file, DiffLine line, FlagContext context)
    {
        if (file.BaseTree is not { } tree || line.OldNumber is not { } number
            || tree.GetRoot().DescendantNodes().OfType<IfStatementSyntax>().FirstOrDefault(i => LineOf(i) == number) is not { } check)
        {
            return false;
        }

        var text = RedFlag.Normalise(check.ToString());
        return context.Files.Where(f => f != file && f.HeadTree is not null).Any(f =>
        {
            var added = f.Added.Select(l => l.NewNumber!.Value).ToHashSet();
            return f.HeadTree!.GetRoot().DescendantNodes().OfType<IfStatementSyntax>().Any(i => added.Contains(LineOf(i)) && RedFlag.Normalise(i.ToString()) == text);
        });
    }

    /// <summary>
    /// A null check is a guard when it exits: <c>if (x is null)</c> whose body returns, throws, continues or breaks.
    /// One that fills the value in (<c>if (x is null) x = new()</c>) is not. ThrowIf*, <c>?? throw</c> and
    /// <c>throw new ArgumentNullException</c> always are.
    /// </summary>
    private static bool IsGuard(FlagFile file, DiffLine line, Match guard)
    {
        if (!guard.Value.StartsWith("if", StringComparison.Ordinal))
        {
            return true;
        }

        if (file.BaseTree is not { } tree || line.OldNumber is not { } number)
        {
            return false;
        }

        var check = tree.GetRoot().DescendantNodes().OfType<IfStatementSyntax>().FirstOrDefault(i => LineOf(i) == number);
        return check?.Statement switch
        {
            BlockSyntax block => block.Statements.LastOrDefault() is ReturnStatementSyntax or ThrowStatementSyntax or ContinueStatementSyntax or BreakStatementSyntax,
            ReturnStatementSyntax or ThrowStatementSyntax or ContinueStatementSyntax or BreakStatementSyntax => true,
            _ => false,
        };
    }

    [GeneratedRegex(@"\[\s*(Required|StringLength|MaxLength|MinLength|Range|RegularExpression|EmailAddress|Phone|Url|CreditCard|Compare|Length|AllowedValues|DeniedValues)\b[^\]]*\]")]
    private static partial Regex Annotation();

    [GeneratedRegex(@"if\s*\(\s*(?<name>[\w.]+)\s*(==\s*null|is\s+null)\s*\)|ThrowIf(Null|NullOrEmpty|NullOrWhiteSpace)\(\s*(?<name>[\w.]+)|throw\s+new\s+ArgumentNullException\(\s*nameof\(\s*(?<name>[\w.]+)\s*\)|(?<name>[\w.]+)\s*\?\?\s*throw\b")]
    private static partial Regex NullGuard();
}

/// <summary>Raw output: <c>@Html.Raw</c>, <c>new HtmlString(</c>, and jQuery <c>.html(</c>/<c>.append(</c>/<c>$(…)</c> built by concatenation.</summary>
public sealed partial class RawOutputDetector : IRedFlagDetector
{
    public IEnumerable<RedFlag> Detect(FlagFile file, FlagContext context)
    {
        foreach (var line in file.AddedCode)
        {
            if ((IsMarkup(file.Path) || file.IsCSharp) && (RazorRaw().Match(line.Text) is { Success: true } raw))
            {
                yield return new RedFlag(FlagKind.RawOutput, file.Path, DiffSide.Right, line.NewNumber!.Value, $"{Code(raw.Value)} writes without encoding.");
            }
        }

        if (!HasScript(file.Path) || file.HeadText is not { } head)
        {
            yield break;
        }

        var added = file.Added.Select(l => l.NewNumber!.Value).ToHashSet();
        foreach (Match call in JQueryInsert().Matches(head))
        {
            var line = LineAt(head, call.Index);
            if (!added.Contains(line) || IsCommentLine(LineText(head, line)))
            {
                continue;
            }

            var argument = CallText(head, call.Index + call.Length - 1);
            if (BuiltMarkup(argument))
            {
                var method = call.Groups["m"].Success ? "." + call.Groups["m"].Value + "(" : "$(";
                yield return new RedFlag(FlagKind.RawOutput, file.Path, DiffSide.Right, line, $"`{method}` is given markup built by concatenation.");
            }
        }
    }

    /// <summary>A string with a tag in it, joined to something with <c>+</c> or a template literal with <c>${…}</c>.</summary>
    private static bool BuiltMarkup(string argument) =>
        (Tag().IsMatch(argument) && Concatenated().IsMatch(argument)) || TemplateMarkup().IsMatch(argument);

    [GeneratedRegex(@"@?Html\.Raw\s*\(|new\s+(Microsoft\.AspNetCore\.Html\.)?HtmlString\s*\(")]
    private static partial Regex RazorRaw();

    [GeneratedRegex(@"\.(?<m>html|append|prepend|after|before|replaceWith)\s*\(|\$\s*\(")]
    private static partial Regex JQueryInsert();

    [GeneratedRegex(@"['""][^'""]*<\w")]
    private static partial Regex Tag();

    [GeneratedRegex(@"['""]\s*\+|\+\s*['""]")]
    private static partial Regex Concatenated();

    [GeneratedRegex(@"`[^`]*<\w[^`]*\$\{[^`]*`")]
    private static partial Regex TemplateMarkup();
}

/// <summary>Raw SQL: <c>FromSqlRaw</c>/<c>ExecuteSqlRaw</c>/<c>SqlQueryRaw</c> given an interpolated or concatenated string, outside tests.</summary>
public sealed partial class RawSqlDetector : IRedFlagDetector
{
    public IEnumerable<RedFlag> Detect(FlagFile file, FlagContext context)
    {
        // Test code never serves a request.
        if (!file.IsCSharp || TestPaths.IsTest(file.Path) || file.HeadText is not { } head)
        {
            yield break;
        }

        var added = file.Added.Select(l => l.NewNumber!.Value).ToHashSet();
        foreach (Match call in RawCall().Matches(head))
        {
            var line = LineAt(head, call.Index);
            var argument = CallText(head, call.Index + call.Length - 1);
            var argumentLines = Enumerable.Range(line, argument.Count(c => c == '\n') + 1);
            if (!IsCommentLine(LineText(head, line)) && argumentLines.Any(added.Contains) && (argument.Contains("$\"", StringComparison.Ordinal) || argument.Contains("$@\"", StringComparison.Ordinal)
                || argument.Contains("@$\"", StringComparison.Ordinal) || Concatenated().IsMatch(argument)))
            {
                yield return new RedFlag(FlagKind.RawSql, file.Path, DiffSide.Right, line, $"`{call.Groups["m"].Value}` is given an interpolated or concatenated string.");
            }
        }
    }

    [GeneratedRegex(@"\b(?<m>FromSqlRaw|ExecuteSqlRaw|ExecuteSqlRawAsync|SqlQueryRaw)\s*(<[^>]*>)?\s*\(")]
    private static partial Regex RawCall();

    [GeneratedRegex(@"""\s*\+|\+\s*""")]
    private static partial Regex Concatenated();
}

/// <summary>Query safety: <c>IgnoreQueryFilters()</c> added. Test code never serves a request, so it is not read.</summary>
public sealed class QuerySafetyDetector : IRedFlagDetector
{
    public IEnumerable<RedFlag> Detect(FlagFile file, FlagContext context) =>
        file.IsCSharp && !TestPaths.IsTest(file.Path)
            ? file.AddedCode.Where(l => l.Text.Contains("IgnoreQueryFilters(", StringComparison.Ordinal))
                .Select(l => new RedFlag(FlagKind.QuerySafety, file.Path, DiffSide.Right, l.NewNumber!.Value, "`IgnoreQueryFilters()` turns off the global query filters (soft delete, tenant) for this query."))
            : [];
}

/// <summary>Swallowed errors: an empty <c>catch</c>, a <c>catch</c> that neither logs nor rethrows, an empty jQuery <c>.fail(…)</c>.</summary>
public sealed partial class SwallowedErrorsDetector : IRedFlagDetector
{
    public IEnumerable<RedFlag> Detect(FlagFile file, FlagContext context)
    {
        var added = file.Added.Select(l => l.NewNumber!.Value).ToHashSet();
        if (file.HeadTree is { } tree)
        {
            foreach (var clause in tree.GetRoot().DescendantNodes().OfType<CatchClauseSyntax>())
            {
                var line = LineOf(clause.CatchKeyword);
                if (!added.Contains(line))
                {
                    continue;
                }

                if (clause.Block.Statements.Count == 0)
                {
                    yield return new RedFlag(FlagKind.SwallowedErrors, file.Path, DiffSide.Right, line, "Empty `catch`: the error is dropped.");
                }
                else if (!Rethrows(clause) && !Logs(clause) && !HandsOn(clause) && !Retries(clause))
                {
                    yield return new RedFlag(FlagKind.SwallowedErrors, file.Path, DiffSide.Right, line, "`catch` neither logs nor rethrows.");
                }
            }
        }

        if (HasScript(file.Path))
        {
            foreach (var line in file.AddedCode.Where(l => EmptyFail().IsMatch(l.Text)))
            {
                yield return new RedFlag(FlagKind.SwallowedErrors, file.Path, DiffSide.Right, line.NewNumber!.Value, $"Empty error handler: {Code(EmptyFail().Match(line.Text).Value)}.");
            }
        }
    }

    private static bool Rethrows(CatchClauseSyntax clause) =>
        clause.Block.DescendantNodes().Any(n => n is ThrowStatementSyntax or ThrowExpressionSyntax);

    /// <summary>A filtered catch inside a loop (<c>when (ex.IsTransient &amp;&amp; retry &lt; max)</c>) retries; the last failure escapes.</summary>
    private static bool Retries(CatchClauseSyntax clause) =>
        clause.Filter is not null && clause.Ancestors().TakeWhile(a => a is not MemberDeclarationSyntax).Any(a => a is ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax or ForEachStatementSyntax);

    /// <summary>The block uses the caught exception (returns its message, wraps it, records it): the error is passed on.</summary>
    private static bool HandsOn(CatchClauseSyntax clause) =>
        clause.Declaration?.Identifier.ValueText is { Length: > 0 } name
        && clause.Block.DescendantNodes().OfType<IdentifierNameSyntax>().Any(i => i.Identifier.ValueText == name);

    /// <summary>A call to a logger: <c>Log*</c>, or any call on something named like a logger.</summary>
    private static bool Logs(CatchClauseSyntax clause) =>
        clause.Block.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => i.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText.StartsWith("Log", StringComparison.Ordinal)
                || m.Expression.ToString().Contains("log", StringComparison.OrdinalIgnoreCase),
            IdentifierNameSyntax n => n.Identifier.ValueText.StartsWith("Log", StringComparison.Ordinal),
            _ => false,
        });

    [GeneratedRegex(@"\.(fail|catch)\s*\(\s*(function\s*\([^)]*\)|\([^)]*\)\s*=>|\w+\s*=>)\s*\{\s*\}\s*\)")]
    private static partial Regex EmptyFail();
}

/// <summary>Warnings silenced: <c>#pragma warning disable</c>, <c>[SuppressMessage]</c>, <c>NoWarn</c>, analyser severity lowered in .editorconfig.</summary>
public sealed partial class WarningsSilencedDetector : IRedFlagDetector
{
    public IEnumerable<RedFlag> Detect(FlagFile file, FlagContext context)
    {
        var name = Path.GetFileName(file.Path);
        foreach (var line in file.AddedCode)
        {
            var silenced = (file.IsCSharp && (Pragma().IsMatch(line.Text) || line.Text.Contains("SuppressMessage", StringComparison.Ordinal)))
                || (name.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase) && Severity().IsMatch(line.Text))
                || (IsBuildFile(file.Path) && line.Text.Contains("<NoWarn>", StringComparison.Ordinal));
            if (silenced)
            {
                yield return new RedFlag(FlagKind.WarningsSilenced, file.Path, DiffSide.Right, line.NewNumber!.Value, $"Warnings silenced: {Code(line.Text)}.");
            }
        }
    }

    public static bool IsBuildFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^\s*#\s*pragma\s+warning\s+disable")]
    private static partial Regex Pragma();

    [GeneratedRegex(@"severity\s*=\s*(none|silent|suggestion)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Severity();
}

/// <summary>
/// Dependencies and build: a NuGet package added or upgraded, a new CDN <c>&lt;script src&gt;</c>, other project or
/// <c>Directory.Build.*</c> changes, and pipeline YAML.
/// </summary>
public sealed partial class DependenciesDetector : IRedFlagDetector
{
    public IEnumerable<RedFlag> Detect(FlagFile file, FlagContext context)
    {
        if (WarningsSilencedDetector.IsBuildFile(file.Path))
        {
            var removedPackages = file.Removed.Select(l => Package().Match(l.Text)).Where(m => m.Success).ToDictionary(m => m.Groups["id"].Value, m => m.Groups["v"].Value, StringComparer.OrdinalIgnoreCase);
            var other = false;
            foreach (var line in file.AddedCode)
            {
                if (Package().Match(line.Text) is { Success: true } package)
                {
                    var id = package.Groups["id"].Value;
                    var version = package.Groups["v"].Value;
                    yield return removedPackages.TryGetValue(id, out var was)
                        ? new RedFlag(FlagKind.DependenciesAndBuild, file.Path, DiffSide.Right, line.NewNumber!.Value, $"Package `{id}` upgraded: {was} → {version}.")
                        : new RedFlag(FlagKind.DependenciesAndBuild, file.Path, DiffSide.Right, line.NewNumber!.Value, $"Package `{id}` {version} added.");
                }
                else if (!other && !string.IsNullOrWhiteSpace(line.Text) && !line.Text.Contains("<NoWarn>", StringComparison.Ordinal))
                {
                    other = true;
                    yield return new RedFlag(FlagKind.DependenciesAndBuild, file.Path, DiffSide.Right, line.NewNumber!.Value, $"Build file changed: {Code(line.Text)}.");
                }
            }

            yield break;
        }

        if (IsPipeline(file.Path) && file.File.Hunks.SelectMany(h => h.Lines).FirstOrDefault(l => l.Kind != DiffLineKind.Context && !string.IsNullOrWhiteSpace(l.Text)) is { } first)
        {
            yield return first.Kind == DiffLineKind.Added
                ? new RedFlag(FlagKind.DependenciesAndBuild, file.Path, DiffSide.Right, first.NewNumber!.Value, "Pipeline changed.")
                : new RedFlag(FlagKind.DependenciesAndBuild, file.Path, DiffSide.Left, first.OldNumber!.Value, "Pipeline changed.");
            yield break;
        }

        if (IsMarkup(file.Path))
        {
            foreach (var line in file.AddedCode.Where(l => CdnScript().IsMatch(l.Text)))
            {
                yield return new RedFlag(FlagKind.DependenciesAndBuild, file.Path, DiffSide.Right, line.NewNumber!.Value, $"Script loaded from another site: `{CdnScript().Match(line.Text).Groups["src"].Value}`.");
            }
        }
    }

    public static bool IsPipeline(string path) =>
        Regex.IsMatch(path, @"(^|/)(\.github/workflows/[^/]+|azure-pipelines[^/]*|\.gitlab-ci|bitbucket-pipelines|[^/]*\.pipeline)\.ya?ml$", RegexOptions.IgnoreCase);

    [GeneratedRegex(@"<Package(Reference|Version)\s+(Include|Update)\s*=\s*""(?<id>[^""]+)""[^>]*?Version\s*=\s*""(?<v>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex Package();

    [GeneratedRegex(@"<script[^>]+src\s*=\s*[""'](?<src>(https?:)?//[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex CdnScript();
}

/// <summary>
/// Security config: CORS, cookie, HTTPS/HSTS or authentication options changed in Program.cs/Startup.cs or
/// appsettings*.json. One flag per area per file, on its most telling line; other appsettings edits are not flagged.
/// </summary>
public sealed partial class SecurityConfigDetector : IRedFlagDetector
{
    private enum Area
    {
        Cors,
        Cookies,
        Https,
        Authentication,
    }

    /// <summary>Per area: keywords from the most telling (a loosening) to the merely relevant.</summary>
    private static readonly (Area Area, string[] Keywords)[] CodeKeywords =
    [
        (Area.Cors, ["AllowAnyOrigin", "SetIsOriginAllowed", "AllowCredentials", "WithOrigins", "AllowAnyMethod", "AllowAnyHeader", "WithMethods", "AddCors", "UseCors", "EnableCors"]),
        (Area.Cookies, ["SameSite", "SecurePolicy", "HttpOnly", "Cookie.Secure", "ExpireTimeSpan", "SlidingExpiration", "Cookie."]),
        (Area.Https, ["UseHsts", "UseHttpsRedirection", "AddHsts", "RequireHttpsMetadata", "MaxAge", "IncludeSubDomains"]),
        (Area.Authentication, ["ValidateIssuer", "ValidateAudience", "ValidateLifetime", "ValidateIssuerSigningKey", "RequireSignedTokens", "AddJwtBearer", "AddAuthentication", "AddCookie", "AddOpenIdConnect", "UseAuthentication"]),
    ];

    private static readonly (Area Area, string[] Keys)[] SettingKeys =
    [
        (Area.Cors, ["Cors", "AllowedOrigins", "Origins"]),
        (Area.Cookies, ["Cookie", "SameSite"]),
        (Area.Https, ["Https", "Hsts", "Kestrel", "Certificate"]),
        (Area.Authentication, ["Authentication", "Jwt", "Authority", "Audience", "Issuer", "OpenIdConnect", "AzureAd", "ClientSecret"]),
    ];

    public IEnumerable<RedFlag> Detect(FlagFile file, FlagContext context)
    {
        var name = Path.GetFileName(file.Path);
        var isStartup = name is "Program.cs" or "Startup.cs";
        var isSettings = name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        if (!isStartup && !isSettings)
        {
            yield break;
        }

        // Moved lines are not changes.
        var changed = file.AddedCode.Where(l => !file.Removed.Any(r => RedFlag.Normalise(r.Text) == RedFlag.Normalise(l.Text))).Select(l => (Line: l, Side: DiffSide.Right, Number: l.NewNumber!.Value))
            .Concat(file.Removed.Where(l => !file.StillAdded(l.Text) && !IsCommentLine(l.Text)).Select(l => (Line: l, Side: DiffSide.Left, Number: l.OldNumber!.Value)))
            .ToList();
        var areas = isStartup ? CodeKeywords.Select(a => (a.Area, Match: (Func<DiffLine, int>)(l => Rank(l.Text, a.Keywords))))
            : SettingKeys.Select(a => (a.Area, Match: (Func<DiffLine, int>)(l => SettingRank(file, l, a.Keys))));
        foreach (var (area, match) in areas)
        {
            var hits = changed.Select(c => (c.Line, c.Side, c.Number, Rank: match(c.Line))).Where(h => h.Rank >= 0).ToList();
            if (hits.Count == 0)
            {
                continue;
            }

            // The most telling line; head before base on a tie, so a change shows its new value.
            var best = hits.OrderBy(h => h.Rank).ThenBy(h => h.Side).ThenBy(h => h.Number).First();
            var also = hits.Where(h => h != best).Select(h => h.Number).Distinct().Order().ToList();
            var where = also.Count == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $" (also line{(also.Count == 1 ? "" : "s")} {string.Join(", ", also)})");
            var verb = best.Side == DiffSide.Right ? "changed" : "removed";
            yield return new RedFlag(FlagKind.SecurityConfig, file.Path, best.Side, best.Number, $"{Title(area)} {verb}: {Code(best.Line.Text)}{where}.");
        }
    }

    private static string Title(Area area) => area switch
    {
        Area.Cors => "CORS",
        Area.Cookies => "Cookie options",
        Area.Https => "HTTPS/HSTS",
        _ => "Authentication options",
    };

    private static int Rank(string text, string[] keywords)
    {
        for (var i = 0; i < keywords.Length; i++)
        {
            if (text.Contains(keywords[i], StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A settings line counts when its own key or an enclosing section's key names the area.</summary>
    private static int SettingRank(FlagFile file, DiffLine line, string[] keys)
    {
        var text = line.Kind == DiffLineKind.Added ? file.HeadText : file.BaseText;
        var number = line.Kind == DiffLineKind.Added ? line.NewNumber : line.OldNumber;
        var path = text is null || number is null ? [JsonKey(line.Text)] : JsonPath(text.Split('\n'), number.Value - 1);
        for (var i = 0; i < keys.Length; i++)
        {
            if (path.Any(k => k is not null && k.Contains(keys[i], StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The key on a line and the keys of the sections around it, found by indentation.</summary>
    private static List<string?> JsonPath(string[] lines, int index)
    {
        var path = new List<string?> { JsonKey(lines[index]) };
        var indent = Indent(lines[index]);
        for (var i = index - 1; i >= 0 && indent > 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]) || Indent(lines[i]) >= indent)
            {
                continue;
            }

            indent = Indent(lines[i]);
            path.Add(JsonKey(lines[i]));
        }

        return path;
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    private static string? JsonKey(string line) => Key().Match(line) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"^\s*""([^""]+)""\s*:")]
    private static partial Regex Key();
}
