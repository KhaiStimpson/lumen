using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using Lumen.App.Diff;
using Lumen.Contracts;

namespace Lumen.App.ViewModels;

public sealed partial class WalkthroughGroupViewModel(WalkthroughGroup group) : ObservableObject
{
    public WalkthroughGroup Model { get; } = group;

    public string Id => Model.Id;

    public string Title => Model.Title;

    public string Summary => Model.Summary;

    public bool HasSummary => Model.Summary.Length > 0;

    public IReadOnlyList<string> Paths => Model.Paths;

    public string OrderLabel => Model.Order.ToString(CultureInfo.InvariantCulture);

    public bool IsMechanical => Model.IsMechanical;

    public string FilesLabel => Model.Paths.Count == 1 ? "1 file" : string.Create(CultureInfo.InvariantCulture, $"{Model.Paths.Count} files");

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }
}

public sealed partial class RiskViewModel(WalkthroughRisk risk) : ObservableObject
{
    public WalkthroughRisk Model { get; } = risk;

    public string Id => Model.Id;

    public string MarkerId => $"risk:{Model.Id}";

    public string Path => Model.Location.Path;

    public int Line => Model.Location.StartLine;

    public string Reason => Model.Reason;

    public string Category => Model.Category.ToString();

    public string CategoryLabel => Model.Category switch
    {
        RiskCategory.Concurrency => "Concurrency",
        RiskCategory.ErrorHandling => "Error handling",
        RiskCategory.Security => "Security-sensitive",
        RiskCategory.BehaviourChange => "Behaviour change",
        RiskCategory.MissingTests => "Missing tests",
        _ => "Worth a look",
    };

    public string LocationLabel => string.Create(CultureInfo.InvariantCulture, $"{System.IO.Path.GetFileName(Model.Location.Path)}:{Model.Location.StartLine}");

    /// <summary>On your own pull request the risky areas are a checklist to go through before requesting review.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>The PR walkthrough as the panel shows it (plan v2 M8). Labelled as a model's reading, never evidence.</summary>
public sealed class WalkthroughViewModel(Walkthrough model)
{
    public Walkthrough Model { get; } = model;

    public bool IsRunning => Model.Status == WalkthroughStatus.Running;

    public bool IsComplete => Model.Status == WalkthroughStatus.Complete;

    public bool IsUnavailable => Model.Status == WalkthroughStatus.Unavailable;

    public bool IsFailed => Model.Status == WalkthroughStatus.Failed;

    public bool HasContent => Model.Intent.Length > 0 || Model.Groups.Count > 0;

    public string Intent => Model.Intent;

    public bool HasIntent => Model.Intent.Length > 0;

    public string Mismatch => Model.DescriptionMismatch;

    public bool HasMismatch => Model.DescriptionMismatch.Length > 0;

    public string Detail => Model.Detail;

    public bool HasDetail => Model.Detail.Length > 0;

    public IReadOnlyList<WalkthroughGroupViewModel> Groups { get; } = [.. model.Groups.OrderBy(g => g.Order).Select(g => new WalkthroughGroupViewModel(g))];

    public IReadOnlyList<RiskViewModel> Risks { get; } = [.. model.Risks.Select(r => new RiskViewModel(r))];

    public bool HasRisks => Risks.Count > 0;

    public string ProgressLabel => Model.FilesTotal == 0 ? Model.Detail
        : string.Create(CultureInfo.InvariantCulture, $"Reading the diff · {Model.FilesDone} of {Model.FilesTotal} files");

    /// <summary>Provenance (TDD §2.3): which model, which prompt version, and that it is a reading.</summary>
    public string Provenance => Model.Producer.Length == 0 ? "" : $"A model's reading · {Model.Producer}{(Model.FromCache ? " · cached" : "")}";

    public bool IsDelta => Model.Since is { FromSha.Length: > 0 };
}

/// <summary>The PR walkthrough (plan v2 M8): intent, change groups in reading order, and risky areas.</summary>
public sealed partial class PullRequestViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWalkthrough), nameof(WalkthroughTitle))]
    public partial WalkthroughViewModel? Walkthrough { get; private set; }

    /// <summary>"What changed since you last looked", when Since last review is on.</summary>
    [ObservableProperty]
    public partial WalkthroughViewModel? DeltaWalkthrough { get; private set; }

    public bool HasWalkthrough => Walkthrough is not null;

    /// <summary>On your own pull request the walkthrough is the before-review check (plan v2: no separate mode).</summary>
    public string WalkthroughTitle => ViewerIsAuthor ? "Before you request review" : "Walkthrough";

    [ObservableProperty]
    public partial WalkthroughGroupViewModel? CurrentGroup { get; private set; }

    [ObservableProperty]
    public partial RiskViewModel? CurrentRisk { get; private set; }

    /// <summary>Show the file tree in the walkthrough's reading order instead of folders.</summary>
    [ObservableProperty]
    public partial bool GroupOrder { get; set; }

    partial void OnGroupOrderChanged(bool value) => RebuildTree();

    public bool CanUseGroupOrder => Walkthrough?.Groups.Count > 0;

    private void ApplyWalkthrough(Walkthrough walkthrough)
    {
        var view = new WalkthroughViewModel(walkthrough);
        if (view.IsDelta)
        {
            DeltaWalkthrough = view;
            return;
        }

        // Keep the reader's ticks on the own-PR checklist across updates.
        var checkedRisks = Walkthrough?.Risks.Where(r => r.IsChecked).Select(r => r.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var risk in view.Risks.Where(r => checkedRisks.Contains(r.Id)))
        {
            risk.IsChecked = true;
        }

        var current = CurrentGroup?.Id;
        Walkthrough = view;
        CurrentGroup = view.Groups.FirstOrDefault(g => g.Id == current);
        if (CurrentGroup is not null)
        {
            CurrentGroup.IsCurrent = true;
        }

        OnPropertyChanged(nameof(CanUseGroupOrder));
        if (GroupOrder)
        {
            RebuildTree();
        }

        UpdateMarkers();
        RebuildMap();
    }

    private IEnumerable<GutterMarker> RiskMarkers(string path) =>
        Walkthrough?.Risks.Where(r => r.Path == path).Select(r => new GutterMarker(r.MarkerId, r.Line, "Risk", r == CurrentRisk, r.IsChecked, false)) ?? [];

    /// <summary>Folders, or (Group order) the walkthrough's groups in reading order.</summary>
    private IEnumerable<FileTreeNode> TreeNodes(IEnumerable<FileEntryViewModel> visible)
    {
        if (!GroupOrder || Walkthrough is not { Groups.Count: > 0 } walkthrough)
        {
            return FileTreeNode.Build(visible);
        }

        var files = visible.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var nodes = new List<FileTreeNode>();
        foreach (var group in walkthrough.Groups.Where(g => !g.IsMechanical))
        {
            var node = new FileTreeNode($"{group.OrderLabel}. {group.Title}", $"group:{group.Id}", null);
            foreach (var path in group.Paths.Where(files.ContainsKey))
            {
                node.Children.Add(new FileTreeNode(System.IO.Path.GetFileName(path), path, files[path]));
                files.Remove(path);
            }

            if (node.Children.Count > 0)
            {
                nodes.Add(node);
            }
        }

        if (files.Count > 0)
        {
            var rest = new FileTreeNode("Other", "group:other", null);
            foreach (var file in files.Values)
            {
                rest.Children.Add(new FileTreeNode(file.Name, file.Path, file));
            }

            nodes.Add(rest);
        }

        return nodes;
    }

    /// <summary>J / K walk the groups when there are no open review points (plan v2 M8).</summary>
    public bool WalksGroups => !OpenPoints.Any() && Walkthrough?.Groups.Count > 0;

    [RelayCommand]
    public Task NextGroupAsync() => StepGroupAsync(+1);

    [RelayCommand]
    public Task PreviousGroupAsync() => StepGroupAsync(-1);

    private Task StepGroupAsync(int direction)
    {
        if (Walkthrough is not { Groups.Count: > 0 } walkthrough)
        {
            return Task.CompletedTask;
        }

        var groups = walkthrough.Groups;
        var index = CurrentGroup is null ? -1 : groups.ToList().IndexOf(CurrentGroup);
        var next = index < 0 ? (direction > 0 ? groups[0] : groups[^1]) : groups[(index + direction + groups.Count) % groups.Count];
        return SelectGroupAsync(next);
    }

    [RelayCommand]
    public async Task SelectGroupAsync(WalkthroughGroupViewModel? group)
    {
        if (group is null)
        {
            return;
        }

        if (CurrentGroup is { } previous)
        {
            previous.IsCurrent = false;
        }

        group.IsCurrent = true;
        CurrentGroup = group;
        ShowTab(PullRequestTab.Files);
        var file = group.Paths.Select(p => Files.FirstOrDefault(f => f.Path == p)).FirstOrDefault(f => f is not null);
        if (file is not null)
        {
            await SelectFileAsync(file).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    public async Task SelectRiskAsync(RiskViewModel? risk)
    {
        if (risk is null)
        {
            return;
        }

        CurrentRisk = risk;
        await RevealAsync(risk.Path, LineSide.Right, risk.Line).ConfigureAwait(true);
        UpdateMarkers();
    }

    [RelayCommand]
    public void ToggleRiskChecked(RiskViewModel? risk)
    {
        if (risk is not null)
        {
            risk.IsChecked = !risk.IsChecked;
            UpdateMarkers();
        }
    }

    /// <summary>Runs the walkthrough again, ignoring the cache.</summary>
    [RelayCommand]
    public async Task RefreshWalkthroughAsync()
    {
        try
        {
            await _source.StartWalkthroughAsync(new StartWalkthroughRequest { PullRequest = Ref, Refresh = true }, _lifetime.Token).ConfigureAwait(true);
        }
        catch (RpcException ex)
        {
            Toast = $"Couldn't start the walkthrough: {ex.Status.Detail}";
        }
    }

    private async Task RequestDeltaWalkthroughAsync(string sinceSha)
    {
        if (Walkthrough is null || Walkthrough.IsUnavailable)
        {
            return;
        }

        try
        {
            await _source.StartWalkthroughAsync(
                new StartWalkthroughRequest { PullRequest = Ref, Since = new CommitRange { FromSha = sinceSha, ToSha = _headSha } },
                _lifetime.Token).ConfigureAwait(true);
        }
        catch (RpcException)
        {
            // The delta walkthrough is a nicety; the range diff is already showing.
        }
    }
}
