using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using Lumen.App.Services;
using Lumen.Contracts;

namespace Lumen.App.ViewModels;

public enum SettingsScope
{
    AllRepositories,
    Repository,
}

/// <summary>
/// Settings → Files: paths treated as mechanical (folded as proven) and paths that never raise a red flag, for all
/// repositories or the open pull request's repository. The engine keeps them; the next analysis uses them.
/// </summary>
public sealed partial class ReviewSettingsViewModel(IReviewSource source) : ObservableObject
{
    private PullRequestRef? _repository;
    private bool _applying;
    private int _sequence;

    /// <summary>A save went through; the open pull request offers Re-analyse.</summary>
    public event EventHandler? Saved;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoaded), nameof(SettingsFilePath))]
    public partial ReviewSettingsReply? Reply { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepositoryScope), nameof(ScopeCaption), nameof(SettingsFilePath))]
    public partial SettingsScope Scope { get; set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddMechanicalPathCommand))]
    public partial string NewMechanicalPath { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSkippedPathCommand))]
    public partial string NewSkippedPath { get; set; } = "";

    public ObservableCollection<string> MechanicalPaths { get; } = [];

    public ObservableCollection<string> SkippedPaths { get; } = [];

    public ObservableCollection<string> InheritedMechanicalPaths { get; } = [];

    public ObservableCollection<string> InheritedSkippedPaths { get; } = [];

    public ObservableCollection<string> BuiltInMechanicalPaths { get; } = [];

    public bool IsLoaded => Reply is not null;

    public bool HasRepository => _repository is not null;

    public string RepositoryName => _repository?.Name ?? "";

    public string RepositoryFullName => _repository is { } r ? $"{r.Owner}/{r.Name}" : "";

    public bool IsRepositoryScope => Scope == SettingsScope.Repository && HasRepository;

    public string ScopeCaption => IsRepositoryScope
        ? $"Only {RepositoryFullName}. Its lists add to the ones for all repositories."
        : "Every repository, unless one adds its own.";

    public string SettingsFilePath => Reply is null ? "" : IsRepositoryScope ? Reply.RepositorySettingsPath : Reply.SettingsPath;

    /// <summary>Shows the repository's layer when a pull request is open (local branches have none), otherwise the global one.</summary>
    public Task LoadAsync(PullRequestRef? repository)
    {
        _repository = repository is null || repository.Owner == "local" ? null : new PullRequestRef { Owner = repository.Owner, Name = repository.Name };
        OnPropertyChanged(nameof(HasRepository));
        OnPropertyChanged(nameof(RepositoryName));
        OnPropertyChanged(nameof(RepositoryFullName));
        _applying = true;
        Scope = HasRepository ? SettingsScope.Repository : SettingsScope.AllRepositories;
        _applying = false;
        OnPropertyChanged(nameof(IsRepositoryScope));
        return RunAsync(ct => source.GetReviewSettingsAsync(
            new ReviewSettingsRequest { Owner = _repository?.Owner ?? "", Name = _repository?.Name ?? "" }, ct), announce: false);
    }

    [RelayCommand(CanExecute = nameof(CanAddMechanicalPath))]
    private Task AddMechanicalPathAsync() => AddAsync(MechanicalPaths, NewMechanicalPath, () => NewMechanicalPath = "");

    private bool CanAddMechanicalPath() => !string.IsNullOrWhiteSpace(NewMechanicalPath);

    [RelayCommand(CanExecute = nameof(CanAddSkippedPath))]
    private Task AddSkippedPathAsync() => AddAsync(SkippedPaths, NewSkippedPath, () => NewSkippedPath = "");

    private bool CanAddSkippedPath() => !string.IsNullOrWhiteSpace(NewSkippedPath);

    [RelayCommand]
    private Task RemoveMechanicalPathAsync(string entry) => RemoveAsync(MechanicalPaths, entry);

    [RelayCommand]
    private Task RemoveSkippedPathAsync(string entry) => RemoveAsync(SkippedPaths, entry);

    partial void OnScopeChanged(SettingsScope value)
    {
        if (!_applying)
        {
            Apply();
        }
    }

    partial void OnReplyChanged(ReviewSettingsReply? value) => Apply();

    private async Task AddAsync(ObservableCollection<string> list, string entry, Action clear)
    {
        var trimmed = entry.Trim();
        clear();
        if (trimmed.Length == 0 || list.Contains(trimmed))
        {
            return;
        }

        list.Add(trimmed);
        await SaveAsync().ConfigureAwait(true);
    }

    private async Task RemoveAsync(ObservableCollection<string> list, string entry)
    {
        if (list.Remove(entry))
        {
            await SaveAsync().ConfigureAwait(true);
        }
    }

    private Task SaveAsync()
    {
        if (Reply is null)
        {
            return Task.CompletedTask;
        }

        var repository = IsRepositoryScope ? _repository : null;
        var rules = new ReviewRules();
        rules.MechanicalPaths.AddRange(MechanicalPaths);
        rules.SkippedPaths.AddRange(SkippedPaths);
        var request = new UpdateReviewSettingsRequest { Owner = repository?.Owner ?? "", Name = repository?.Name ?? "", Rules = rules };
        return RunAsync(ct => source.UpdateReviewSettingsAsync(request, ct), announce: true);
    }

    private async Task RunAsync(Func<CancellationToken, Task<ReviewSettingsReply>> call, bool announce)
    {
        var sequence = ++_sequence;
        Error = null;
        try
        {
            var reply = await call(CancellationToken.None).ConfigureAwait(true);
            if (sequence == _sequence)
            {
                Reply = reply;
            }

            if (announce)
            {
                Saved?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (RpcException ex)
        {
            Error = ex.Status.Detail;
            Apply();
        }
        catch (EngineUnavailableException ex)
        {
            Error = ex.Message;
            Apply();
        }
    }

    /// <summary>Copies the layer in view out of the last reply; after a failed save, this also undoes the edit.</summary>
    private void Apply()
    {
        if (Reply is not { } reply)
        {
            return;
        }

        _applying = true;
        try
        {
            var own = IsRepositoryScope ? reply.Repository ?? new ReviewRules() : reply.Global ?? new ReviewRules();
            Fill(MechanicalPaths, own.MechanicalPaths);
            Fill(SkippedPaths, own.SkippedPaths);
            Fill(InheritedMechanicalPaths, IsRepositoryScope ? reply.Global?.MechanicalPaths ?? [] : []);
            Fill(InheritedSkippedPaths, IsRepositoryScope ? reply.Global?.SkippedPaths ?? [] : []);
            Fill(BuiltInMechanicalPaths, reply.BuiltInMechanicalPaths);
        }
        finally
        {
            _applying = false;
        }

        OnPropertyChanged(nameof(SettingsFilePath));
    }

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> entries)
    {
        target.Clear();
        foreach (var entry in entries)
        {
            target.Add(entry);
        }
    }
}
