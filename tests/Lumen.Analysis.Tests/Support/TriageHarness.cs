using System.Text;
using Lumen.Domain;
using Lumen.Roslyn.Triage;

namespace Lumen.Analysis.Tests.Support;

/// <summary>A changed file for triage tests: base text (null = added) and head text (null = deleted).</summary>
internal sealed record TestFile(string Path, string? Base, string? Head);

/// <summary>Builds minimal unified diffs (LCS, three lines of context) from base/head texts and runs triage over them.</summary>
internal static class TriageHarness
{
    public static TriageResult Run(params TestFile[] files) => Run(RoslynTriage.CreatePipeline(), files);

    public static TriageResult Run(TriagePipeline pipeline, params TestFile[] files)
    {
        var diff = string.Concat(files.Select(f => Diff(f.Path, f.Base, f.Head)));
        var snapshot = TestDiffs.Snapshot(diff);
        var sources = new TriageSources(
            files.Where(f => f.Base is not null).ToDictionary(f => f.Path, f => f.Base!),
            files.Where(f => f.Head is not null).ToDictionary(f => f.Path, f => f.Head!));

        // A hunk that does not apply proves nothing, which would make every "not mechanical" test pass vacuously.
        foreach (var file in snapshot.Files.Where(f => sources.Base(f.Path) is not null))
        {
            Assert.All(file.Hunks, h => Assert.NotNull(HunkApplier.ApplyToBase(sources.Base(file.Path)!, h)));
        }

        return pipeline.Run(snapshot, sources);
    }

    /// <summary>The single hunk of a one-file change; fails if the diff produced more than one.</summary>
    public static HunkTriage Single(string baseText, string headText, string path = "src/A.cs") =>
        Assert.Single(Run(new TestFile(path, baseText, headText)).Hunks);

    public static string Diff(string path, string? baseText, string? headText, int context = 3)
    {
        var a = baseText is null ? [] : FileLines(baseText);
        var b = headText is null ? [] : FileLines(headText);
        var ops = Ops(a, b);

        var sb = new StringBuilder();
        sb.Append("diff --git a/").Append(path).Append(" b/").Append(path).Append('\n');
        if (baseText is null)
        {
            sb.Append("new file mode 100644\n");
        }
        else if (headText is null)
        {
            sb.Append("deleted file mode 100644\n");
        }

        sb.Append("index 1111111..2222222 100644\n");
        sb.Append(baseText is null ? "--- /dev/null\n" : $"--- a/{path}\n");
        sb.Append(headText is null ? "+++ /dev/null\n" : $"+++ b/{path}\n");

        // Group changed ops with surrounding context into hunks.
        var changed = ops.Select((op, i) => (op, i)).Where(x => x.op.Kind != ' ').Select(x => x.i).ToList();
        var index = 0;
        while (index < changed.Count)
        {
            var start = Math.Max(0, changed[index] - context);
            var end = changed[index];
            while (index + 1 < changed.Count && changed[index + 1] - end <= context * 2 + 1)
            {
                end = changed[++index];
            }

            end = Math.Min(ops.Count - 1, end + context);
            index++;

            var slice = ops.Skip(start).Take(end - start + 1).ToList();
            var oldStart = ops.Take(start).Count(o => o.Kind != '+') + 1;
            var newStart = ops.Take(start).Count(o => o.Kind != '-') + 1;
            var oldCount = slice.Count(o => o.Kind != '+');
            var newCount = slice.Count(o => o.Kind != '-');
            sb.Append("@@ -").Append(oldCount == 0 ? oldStart - 1 : oldStart).Append(',').Append(oldCount)
              .Append(" +").Append(newCount == 0 ? newStart - 1 : newStart).Append(',').Append(newCount).Append(" @@\n");
            foreach (var op in slice)
            {
                sb.Append(op.Kind).Append(op.Text).Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>Lines as git counts them: a final newline ends the last line rather than starting an empty one.</summary>
    private static string[] FileLines(string text)
    {
        var lines = TestDiffs.Lines(text);
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static List<(char Kind, string Text)> Ops(string[] a, string[] b)
    {
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var ops = new List<(char, string)>();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y])
            {
                ops.Add((' ', a[x++]));
                y++;
            }
            else if (y < b.Length && (x == a.Length || lcs[x, y + 1] >= lcs[x + 1, y]))
            {
                ops.Add(('+', b[y++]));
            }
            else
            {
                ops.Add(('-', a[x++]));
            }
        }

        // Removals before additions inside each change block, as git prints them.
        var ordered = new List<(char, string)>();
        var k = 0;
        while (k < ops.Count)
        {
            if (ops[k].Item1 == ' ')
            {
                ordered.Add(ops[k++]);
                continue;
            }

            var block = new List<(char, string)>();
            while (k < ops.Count && ops[k].Item1 != ' ')
            {
                block.Add(ops[k++]);
            }

            ordered.AddRange(block.Where(o => o.Item1 == '-'));
            ordered.AddRange(block.Where(o => o.Item1 == '+'));
        }

        return ordered;
    }
}
