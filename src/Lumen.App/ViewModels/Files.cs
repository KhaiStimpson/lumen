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

    [ObservableProperty]
    public partial bool IsViewed { get; set; }
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
