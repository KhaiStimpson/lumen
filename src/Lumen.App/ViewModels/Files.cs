using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumen.Contracts;

namespace Lumen.App.ViewModels;

public sealed partial class FileEntryViewModel(ChangedFileSummary file) : ObservableObject
{
    public ChangedFileSummary Model { get; } = file;

    public string Path => Model.Path;

    public string Name => System.IO.Path.GetFileName(Model.Path);

    public string Directory => System.IO.Path.GetDirectoryName(Model.Path)?.Replace('\\', '/') ?? "";

    public string AdditionsLabel => $"+{Model.Additions}";

    public string DeletionsLabel => $"−{Model.Deletions}";

    public bool IsMechanical => Model.IsMechanical;

    public string MechanicalReason => Model.MechanicalReason;

    public bool IsRenamed => Model.Kind == FileChangeKind.Renamed && Model.OldPath.Length > 0 && Model.OldPath != Model.Path;

    /// <summary>Moved or renamed with the content untouched: nothing to read line by line.</summary>
    public bool IsPureRename => IsRenamed && !Model.IsBinary && Model.Additions + Model.Deletions == 0;

    /// <summary>A pure rename shows "moved" instead of "+0 −0".</summary>
    public bool ShowCounts => !IsPureRename;

    /// <summary>
    /// Under the name in the files tree: "was Old.cs" (renamed in place), "from …/Old/" (moved, shown from where the
    /// paths part), or the old path when both changed.
    /// </summary>
    public string RenamedFromLabel
    {
        get
        {
            if (PathChange is not { } change)
            {
                return "";
            }

            var oldName = System.IO.Path.GetFileName(Model.OldPath);
            var oldDirectory = System.IO.Path.GetDirectoryName(Model.OldPath)?.Replace('\\', '/') ?? "";
            if (oldDirectory == Directory)
            {
                return $"was {oldName}";
            }

            if (oldName != Name)
            {
                return $"from {Model.OldPath}";
            }

            return change.OldMiddle.Length > 0 ? $"from {(change.Head.Length > 0 ? "…/" : "")}{change.OldMiddle}"
                : oldDirectory.Length > 0 ? $"from {oldDirectory}/"
                : "from the root";
        }
    }

    /// <summary>"94% similar" for a rename with edits; empty for a pure rename or anything else.</summary>
    public string SimilarityLabel => IsRenamed && Model.Similarity is > 0 and < 100
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Model.Similarity}% similar")
        : "";

    public bool HasSimilarity => SimilarityLabel.Length > 0;

    /// <summary>The old and new paths split around the segments that differ, for the header's "old → new" line.</summary>
    public PathChange? PathChange => IsRenamed ? PathChange.Between(Model.OldPath, Model.Path) : null;

    public string KindLabel => Model.Kind switch
    {
        FileChangeKind.Added => "Added",
        FileChangeKind.Deleted => "Deleted",
        FileChangeKind.Renamed => "Renamed",
        _ => "Modified",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReviewPoints))]
    public partial int ReviewPointCount { get; set; }

    public bool HasReviewPoints => ReviewPointCount > 0;

    /// <summary>Unresolved review threads on this file (plan v2 M4).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThreads), nameof(ThreadCountLabel))]
    public partial int ThreadCount { get; set; }

    public bool HasThreads => ThreadCount > 0;

    public string ThreadCountLabel => ThreadCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>GitHub's per-file "Viewed" checkbox: ticked by the reviewer, never by merely opening the file.</summary>
    [ObservableProperty]
    public partial bool IsViewed { get; set; } = file.IsViewed;
}

/// <summary>
/// A rename's two paths split into the shared head, the differing middle and the shared tail, by path segment:
/// <c>src/Retry/Policy.cs → src/Policies/Policy.cs</c> differs only in <c>Retry/</c> vs <c>Policies/</c>.
/// </summary>
public sealed record PathChange(string Head, string OldMiddle, string NewMiddle, string Tail)
{
    public static PathChange Between(string oldPath, string newPath)
    {
        var a = oldPath.Split('/');
        var b = newPath.Split('/');
        var head = 0;
        while (head < Math.Min(a.Length, b.Length) - 1 && a[head] == b[head])
        {
            head++;
        }

        var tail = 0;
        while (tail < Math.Min(a.Length, b.Length) - head && a[^(tail + 1)] == b[^(tail + 1)])
        {
            tail++;
        }

        static string Join(IEnumerable<string> parts, bool trailing)
        {
            var joined = string.Join('/', parts);
            return trailing && joined.Length > 0 ? joined + "/" : joined;
        }

        // The tail is the file name onwards; everything before it keeps its slash.
        return new PathChange(
            Join(a.Take(head), trailing: true),
            Join(a.Skip(head).Take(a.Length - head - tail), trailing: tail > 0),
            Join(b.Skip(head).Take(b.Length - head - tail), trailing: tail > 0),
            Join(a.Skip(a.Length - tail), trailing: false));
    }

    public string OldPath => Head + OldMiddle + Tail;

    public string NewPath => Head + NewMiddle + Tail;
}

/// <summary>A folder or file in the changed-files tree. Single-child folder chains are compacted ("src/Web/Services").</summary>
public sealed partial class FileTreeNode : ObservableObject
{
    public FileTreeNode(string name, string path, FileEntryViewModel? file)
    {
        Name = name;
        Path = path;
        File = file;
    }

    public string Name { get; private set; }

    public string Path { get; }

    public FileEntryViewModel? File { get; }

    public bool IsFolder => File is null;

    public ObservableCollection<FileTreeNode> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    public static IReadOnlyList<FileTreeNode> Build(IEnumerable<FileEntryViewModel> files)
    {
        var root = new FileTreeNode("", "", null);
        foreach (var file in files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var node = root;
            var parts = file.Path.Split('/');
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var folderPath = string.Join('/', parts[..(i + 1)]);
                var next = node.Children.FirstOrDefault(c => c.IsFolder && c.Path == folderPath);
                if (next is null)
                {
                    next = new FileTreeNode(parts[i], folderPath, null);
                    node.Children.Add(next);
                }

                node = next;
            }

            node.Children.Add(new FileTreeNode(parts[^1], file.Path, file));
        }

        foreach (var child in root.Children)
        {
            Compact(child);
        }

        // Folders before files at each level, like an editor's explorer.
        Sort(root);
        return [.. root.Children];
    }

    public IEnumerable<FileTreeNode> Descendants() =>
        Children.SelectMany(c => c.Descendants().Prepend(c));

    private static void Compact(FileTreeNode node)
    {
        while (node.IsFolder && node.Children.Count == 1 && node.Children[0].IsFolder)
        {
            var only = node.Children[0];
            node.Name = $"{node.Name}/{only.Name}";
            node.Children.Clear();
            foreach (var grandChild in only.Children)
            {
                node.Children.Add(grandChild);
            }
        }

        foreach (var child in node.Children)
        {
            Compact(child);
        }
    }

    private static void Sort(FileTreeNode node)
    {
        var sorted = node.Children.OrderBy(c => c.IsFolder ? 0 : 1).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        node.Children.Clear();
        foreach (var child in sorted)
        {
            node.Children.Add(child);
            Sort(child);
        }
    }
}

public sealed class ConventionViewModel(Convention convention)
{
    public string Statement => convention.Statement;

    public string Support => $"{convention.Supporting} of {convention.PeerCount}";

    public string ExamplesLabel => string.Join(", ", convention.Examples.Select(e => e.Symbol).Where(s => s.Length > 0).Take(3));

    public double Strength => convention.PeerCount == 0 ? 0 : (double)convention.Supporting / convention.PeerCount;
}
