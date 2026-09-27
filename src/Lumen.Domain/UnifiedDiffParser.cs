using System.Globalization;
using System.Text.RegularExpressions;

namespace Lumen.Domain;

/// <summary>Parses the output of <c>git diff</c> (unified format, one or many files).</summary>
public static partial class UnifiedDiffParser
{
    public sealed record ParsedFile(
        string Path,
        string? OldPath,
        FileChangeKind Kind,
        bool IsBinary,
        IReadOnlyList<DiffHunk> Hunks)
    {
        public int Additions => Hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Added));

        public int Deletions => Hunks.Sum(h => h.Lines.Count(l => l.Kind == DiffLineKind.Removed));
    }

    public static IReadOnlyList<ParsedFile> Parse(string diff)
    {
        var files = new List<ParsedFile>();
        var lines = diff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        FileBuilder? file = null;
        HunkBuilder? hunk = null;

        void FlushHunk()
        {
            if (hunk is not null && file is not null)
            {
                file.Hunks.Add(hunk.Build());
            }

            hunk = null;
        }

        void FlushFile()
        {
            FlushHunk();
            if (file is not null)
            {
                files.Add(file.Build());
            }

            file = null;
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FlushFile();
                file = new FileBuilder();
                var m = DiffGitHeader().Match(line);
                if (m.Success)
                {
                    file.OldPath = Unquote(m.Groups["a"].Value);
                    file.Path = Unquote(m.Groups["b"].Value);
                }

                continue;
            }

            if (file is null)
            {
                continue;
            }

            if (hunk is not null && IsHunkBody(line) && (hunk.IsOpen || line[0] == '\\'))
            {
                hunk.Add(line);
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                FlushHunk();
                var m = HunkHeader().Match(line);
                if (m.Success)
                {
                    hunk = new HunkBuilder(
                        Int(m.Groups["os"].Value),
                        m.Groups["oc"].Success ? Int(m.Groups["oc"].Value) : 1,
                        Int(m.Groups["ns"].Value),
                        m.Groups["nc"].Success ? Int(m.Groups["nc"].Value) : 1,
                        line);
                }

                continue;
            }

            if (hunk is null)
            {
                ParseFileHeader(file, line);
            }
        }

        FlushFile();
        return files;
    }

    private static bool IsHunkBody(string line) =>
        line.Length > 0 && line[0] is ' ' or '+' or '-' or '\\';

    private static void ParseFileHeader(FileBuilder file, string line)
    {
        if (line.StartsWith("new file mode", StringComparison.Ordinal))
        {
            file.Kind = FileChangeKind.Added;
        }
        else if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
        {
            file.Kind = FileChangeKind.Deleted;
        }
        else if (line.StartsWith("rename from ", StringComparison.Ordinal))
        {
            file.Kind = FileChangeKind.Renamed;
            file.OldPath = line["rename from ".Length..];
        }
        else if (line.StartsWith("rename to ", StringComparison.Ordinal))
        {
            file.Path = line["rename to ".Length..];
        }
        else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line.StartsWith("GIT binary patch", StringComparison.Ordinal))
        {
            file.IsBinary = true;
        }
        else if (line.StartsWith("+++ ", StringComparison.Ordinal) && line != "+++ /dev/null")
        {
            file.Path = StripPrefix(Unquote(line[4..]));
        }
        else if (line.StartsWith("--- ", StringComparison.Ordinal) && line != "--- /dev/null")
        {
            file.OldPath = StripPrefix(Unquote(line[4..]));
        }
    }

    private static string StripPrefix(string path) =>
        path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal) ? path[2..] : path;

    /// <summary>Decodes git's C-style quoting, e.g. <c>"caf\303\251.md"</c> → <c>café.md</c>.</summary>
    private static string Unquote(string path)
    {
        if (path.Length < 2 || path[0] != '"' || path[^1] != '"')
        {
            return path;
        }

        var bytes = new List<byte>(path.Length);
        for (var i = 1; i < path.Length - 1; i++)
        {
            var c = path[i];
            if (c != '\\' || i + 1 >= path.Length - 1)
            {
                bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(c.ToString()));
                continue;
            }

            var next = path[++i];
            if (next is >= '0' and <= '7' && i + 2 < path.Length - 1)
            {
                bytes.Add(Convert.ToByte(path.Substring(i, 3), 8));
                i += 2;
                continue;
            }

            bytes.Add((byte)(next switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                'a' => '\a',
                'b' => '\b',
                'f' => '\f',
                'v' => '\v',
                _ => next,
            }));
        }

        return System.Text.Encoding.UTF8.GetString([.. bytes]);
    }

    private static int Int(string s) => int.Parse(s, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^diff --git ""?a/(?<a>.+?)""? ""?b/(?<b>.+?)""?$")]
    private static partial Regex DiffGitHeader();

    [GeneratedRegex(@"^@@ -(?<os>\d+)(,(?<oc>\d+))? \+(?<ns>\d+)(,(?<nc>\d+))? @@")]
    private static partial Regex HunkHeader();

    private sealed class FileBuilder
    {
        public string Path { get; set; } = "";

        public string? OldPath { get; set; }

        public FileChangeKind Kind { get; set; } = FileChangeKind.Modified;

        public bool IsBinary { get; set; }

        public List<DiffHunk> Hunks { get; } = [];

        public ParsedFile Build() => new(
            Path,
            Kind is FileChangeKind.Renamed or FileChangeKind.Deleted ? OldPath : null,
            Kind,
            IsBinary,
            Hunks);
    }

    private sealed class HunkBuilder
    {
        private readonly int _oldStart;
        private readonly int _oldCount;
        private readonly int _newStart;
        private readonly int _newCount;
        private readonly string _header;
        private readonly List<DiffLine> _lines = [];
        private int _old;
        private int _new;

        public HunkBuilder(int oldStart, int oldCount, int newStart, int newCount, string header)
        {
            (_oldStart, _oldCount, _newStart, _newCount, _header) = (oldStart, oldCount, newStart, newCount, header);
            (_old, _new) = (oldStart, newStart);
        }

        /// <summary>True while the hunk still expects lines according to its header counts.</summary>
        public bool IsOpen => _old < _oldStart + _oldCount || _new < _newStart + _newCount;

        public void Add(string line)
        {
            switch (line[0])
            {
                case '+':
                    _lines.Add(new DiffLine(DiffLineKind.Added, null, _new++, line[1..]));
                    break;
                case '-':
                    _lines.Add(new DiffLine(DiffLineKind.Removed, _old++, null, line[1..]));
                    break;
                case ' ':
                    _lines.Add(new DiffLine(DiffLineKind.Context, _old++, _new++, line[1..]));
                    break;
                default:
                    // "\ No newline at end of file" carries no content.
                    break;
            }
        }

        public DiffHunk Build() => new(_oldStart, _oldCount, _newStart, _newCount, _header, _lines);
    }
}
