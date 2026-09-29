using Lumen.Analysis;
using Lumen.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lumen.Roslyn.Triage;

/// <summary>
/// Pulls the dangerous operations out of an otherwise-skippable EF Core migration: in <c>Up</c>, dropping or renaming
/// a table or column, narrowing a column, deleting data and raw SQL become Critical, each with what it does in words.
/// The rest of the migration stays Skip. Runs before the file-level mechanical rule, which would hide the whole file.
/// </summary>
public sealed class DestructiveMigrationClassifier : IHunkClassifier
{
    private const string MigrationReason = "Database migration";

    public HunkVerdict? Classify(HunkContext context)
    {
        if (!IsMigration(context.File.Path) ||
            context.Workspace.GetOrAdd("migration:" + context.File.Path, () => Operations(HeadText(context))) is not { Count: > 0 } operations)
        {
            return null;
        }

        var changed = context.Hunk.Lines.Where(l => l.Kind != DiffLineKind.Context).ToList();
        var parts = new List<HunkPart>();
        var rest = new List<DiffLine>();
        foreach (var group in changed.GroupBy(l => l.Kind == DiffLineKind.Added ? operations.FirstOrDefault(o => o.Covers(l.NewNumber)) : null))
        {
            if (group.Key is { } op)
            {
                parts.Add(new HunkPart(ChangeClass.BehaviourChange, TriageTier.Critical, [op.Description], null, group.ToList()));
            }
            else
            {
                rest.AddRange(group);
            }
        }

        if (parts.Count == 0)
        {
            return null;
        }

        if (rest.Count > 0)
        {
            parts.Add(new HunkPart(ChangeClass.Generated, TriageTier.Skip, [MigrationReason], null, rest));
        }

        return new(ChangeClass.BehaviourChange, TriageTier.Critical, parts.SelectMany(p => p.Reasons).Distinct().ToList()) { Parts = parts };
    }

    public static bool IsMigration(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal) &&
        path.Split('/').Any(s => s.Equals("Migrations", StringComparison.OrdinalIgnoreCase));

    /// <summary>The head text, or for an added file the file rebuilt from its only hunk (so replayed diffs work too).</summary>
    private static string? HeadText(HunkContext context)
    {
        if (context.HeadText is { } head)
        {
            return head;
        }

        return context.File.Kind == FileChangeKind.Added
            ? string.Join("\n", context.File.Hunks.SelectMany(h => h.Lines).Where(l => l.Kind == DiffLineKind.Added).OrderBy(l => l.NewNumber).Select(l => l.Text))
            : null;
    }

    private static List<Operation> Operations(string? text)
    {
        if (text is null)
        {
            return [];
        }

        var root = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();
        var up = root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.ValueText == "Up");
        if (up is null)
        {
            return [];
        }

        var result = new List<Operation>();
        foreach (var call in up.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax access || Describe(access.Name, call.ArgumentList) is not { } description)
            {
                continue;
            }

            var statement = (SyntaxNode?)call.FirstAncestorOrSelf<StatementSyntax>() ?? call;
            var span = statement.SyntaxTree.GetLineSpan(statement.Span);
            result.Add(new Operation(span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, description));
        }

        return result;
    }

    private static string? Describe(SimpleNameSyntax name, ArgumentListSyntax args)
    {
        string? Arg(string argName, int position) =>
            (args.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == argName)
             ?? (position >= 0 && position < args.Arguments.Count && args.Arguments[position].NameColon is null ? args.Arguments[position] : null))
            ?.Expression switch
            {
                LiteralExpressionSyntax l => l.Token.ValueText,
                { } e => e.ToString(),
                null => null,
            };

        string Column() => Arg("table", 1) is { } table ? $"{table}.{Arg("name", 0)}" : Arg("name", 0) ?? "?";

        return name.Identifier.ValueText switch
        {
            "DropColumn" => $"drops column `{Column()}`",
            "DropTable" => $"drops table `{Arg("name", 0)}`",
            "RenameColumn" => $"renames column `{Column()}` to `{Arg("newName", 2)}`",
            "RenameTable" => $"renames table `{Arg("name", 0)}` to `{Arg("newName", 2)}`",
            "DeleteData" => $"deletes rows from `{Arg("table", 0)}`",
            "Sql" => "runs raw SQL",
            "AlterColumn" => Narrowing(Column(), Arg),
            _ => null,
        };
    }

    private static string? Narrowing(string column, Func<string, int, string?> arg)
    {
        if (arg("type", -1) is { } type && arg("oldType", -1) is { } oldType && type != oldType)
        {
            return $"changes column `{column}` from `{oldType}` to `{type}`";
        }

        if (int.TryParse(arg("maxLength", -1), out var max) &&
            (arg("oldMaxLength", -1) is not { } oldText || (int.TryParse(oldText, out var oldMax) && max < oldMax)))
        {
            return $"narrows column `{column}` to {max} characters";
        }

        return arg("nullable", -1) == "false" && arg("oldNullable", -1) == "true"
            ? $"makes column `{column}` required"
            : null;
    }

    private sealed record Operation(int StartLine, int EndLine, string Description)
    {
        public bool Covers(int? line) => line is { } n && n >= StartLine && n <= EndLine;
    }
}
