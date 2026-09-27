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

public enum ReviewPreset
{
    Quiet,
    Balanced,
    Thorough,
    Custom,
}

/// <summary>
/// The Review and Ignored &amp; files sections (docs/design/settings-overlay.md). Edits one layer at a time: the
/// global defaults, or the open repository's overrides. A repository either inherits the sensitivity or overrides
/// it whole; its lists add to the global ones. Every change saves at once; the open PR is re-analysed only on request.
/// </summary>
public sealed partial class ReviewSettingsViewModel(IReviewSource source) : ObservableObject
{
    private int _sequence;

    // Set while a reply is copied into the fields, so that copying isn't sent back as a change.
    private bool _applying;

    private PullRequestRef? _repository;

    /// <summary>Raised after a change has been saved; the open pull request may now be out of date.</summary>
    public event EventHandler? Saved;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoaded), nameof(IsOverriding), nameof(OverrideNote), nameof(SettingsFilePath))]
    public partial ReviewSettingsReply? Reply { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepositoryScope), nameof(IsOverriding), nameof(ScopeCaption), nameof(SettingsFilePath))]
    public partial SettingsScope Scope { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preset), nameof(IsCustom))]
    public partial decimal MinimumPeers { get; set; } = 3;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preset), nameof(IsCustom))]
    public partial decimal SupportPercent { get; set; } = 75;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preset), nameof(IsCustom))]
    public partial decimal MinimumLift { get; set; } = 1.5m;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preset), nameof(IsCustom))]
    public partial decimal PointsPerType { get; set; } = 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preset), nameof(IsCustom))]
    public partial decimal Examples { get; set; } = 4;

    [ObservableProperty]
    public partial bool IsAdvancedOpen { get; set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddIgnoredNameCommand))]
    public partial string NewIgnoredName { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddMechanicalPathCommand))]
    public partial string NewMechanicalPath { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSkippedPathCommand))]
    public partial string NewSkippedPath { get; set; } = "";

    /// <summary>The layer being edited.</summary>
    public ObservableCollection<string> IgnoredNames { get; } = [];

    public ObservableCollection<string> MechanicalPaths { get; } = [];

    public ObservableCollection<string> SkippedPaths { get; } = [];

    /// <summary>The global entries, shown read-only while a repository is being edited.</summary>
    public ObservableCollection<string> InheritedIgnoredNames { get; } = [];

    public ObservableCollection<string> InheritedMechanicalPaths { get; } = [];

    public ObservableCollection<string> InheritedSkippedPaths { get; } = [];

    public ObservableCollection<string> BuiltInIgnoredNames { get; } = [];

    public ObservableCollection<string> BuiltInMechanicalPaths { get; } = [];

    public bool IsLoaded => Reply is not null;

    public bool HasRepository => _repository is not null;

    public string RepositoryName => _repository?.Name ?? "";

    public string RepositoryFullName => _repository is { } r ? $"{r.Owner}/{r.Name}" : "";

    public bool IsRepositoryScope => Scope == SettingsScope.Repository && HasRepository;

    public bool IsOverriding => IsRepositoryScope && Reply?.Repository?.Sensitivity is not null;

    public string OverrideNote => Reply is null ? "" : $"Overrides All repositories ({PresetOf(Reply.Global.Sensitivity)})";

    public string ScopeCaption => IsRepositoryScope
        ? $"Only {RepositoryFullName}. Its lists add to the ones for all repositories."
        : "Every repository, unless one overrides it.";

    public string SettingsFilePath => Reply is null ? "" : IsRepositoryScope ? Reply.RepositorySettingsPath : Reply.SettingsPath;

    /// <summary>The preset the current numbers match. Setting it copies that preset's numbers in and saves.</summary>
    public ReviewPreset Preset
    {
        get => PresetOf(Numbers());
        set
        {
            if (value == ReviewPreset.Custom || value == Preset || Reply is null || _applying)
            {
                return;
            }

            SetNumbers(value switch
            {
                ReviewPreset.Quiet => Reply.Quiet,
                ReviewPreset.Thorough => Reply.Thorough,
                _ => Reply.Balanced,
            });
            OnPropertyChanged();
            _ = SaveAsync(sensitivityFromNumbers: true);
        }
    }

    public bool IsCustom => Preset == ReviewPreset.Custom;

    /// <summary>Shows the repository's layer when one is open, otherwise the global one.</summary>
    public Task LoadAsync(PullRequestRef? repository)
    {
        _repository = repository is null ? null : new PullRequestRef { Owner = repository.Owner, Name = repository.Name };
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

    [RelayCommand]
    private Task ResetToGlobalAsync() => SaveAsync(sensitivityFromNumbers: false, inheritSensitivity: true);

    [RelayCommand(CanExecute = nameof(CanAddIgnoredName))]
    private Task AddIgnoredNameAsync() => AddAsync(IgnoredNames, NewIgnoredName, () => NewIgnoredName = "");

    private bool CanAddIgnoredName() => !string.IsNullOrWhiteSpace(NewIgnoredName);

    [RelayCommand(CanExecute = nameof(CanAddMechanicalPath))]
    private Task AddMechanicalPathAsync() => AddAsync(MechanicalPaths, NewMechanicalPath, () => NewMechanicalPath = "");

    private bool CanAddMechanicalPath() => !string.IsNullOrWhiteSpace(NewMechanicalPath);

    [RelayCommand(CanExecute = nameof(CanAddSkippedPath))]
    private Task AddSkippedPathAsync() => AddAsync(SkippedPaths, NewSkippedPath, () => NewSkippedPath = "");

    private bool CanAddSkippedPath() => !string.IsNullOrWhiteSpace(NewSkippedPath);

    [RelayCommand]
    private Task RemoveIgnoredNameAsync(string entry) => RemoveAsync(IgnoredNames, entry);

    [RelayCommand]
    private Task RemoveMechanicalPathAsync(string entry) => RemoveAsync(MechanicalPaths, entry);

    [RelayCommand]
    private Task RemoveSkippedPathAsync(string entry) => RemoveAsync(SkippedPaths, entry);

    [RelayCommand]
    private void ToggleAdvanced() => IsAdvancedOpen = !IsAdvancedOpen;

    partial void OnScopeChanged(SettingsScope value)
    {
        if (!_applying)
        {
            Apply();
        }
    }

    partial void OnMinimumPeersChanged(decimal value) => NumbersChanged();

    partial void OnSupportPercentChanged(decimal value) => NumbersChanged();

    partial void OnMinimumLiftChanged(decimal value) => NumbersChanged();

    partial void OnPointsPerTypeChanged(decimal value) => NumbersChanged();

    partial void OnExamplesChanged(decimal value) => NumbersChanged();

    partial void OnReplyChanged(ReviewSettingsReply? value) => Apply();

    private void NumbersChanged()
    {
        if (!_applying && Reply is not null)
        {
            _ = SaveAsync(sensitivityFromNumbers: true);
        }
    }

    private async Task AddAsync(ObservableCollection<string> list, string entry, Action clear)
    {
        var trimmed = entry.Trim();
        clear();
        if (trimmed.Length == 0 || list.Contains(trimmed))
        {
            return;
        }

        list.Add(trimmed);
        await SaveAsync(sensitivityFromNumbers: false).ConfigureAwait(true);
    }

    private async Task RemoveAsync(ObservableCollection<string> list, string entry)
    {
        if (list.Remove(entry))
        {
            await SaveAsync(sensitivityFromNumbers: false).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Saves the layer in view. Editing numbers while a repository inherits starts an override; list edits leave its
    /// sensitivity as it was.
    /// </summary>
    private Task SaveAsync(bool sensitivityFromNumbers, bool inheritSensitivity = false)
    {
        if (Reply is null)
        {
            return Task.CompletedTask;
        }

        var repository = IsRepositoryScope ? _repository : null;
        var rules = new ReviewRules
        {
            Sensitivity = repository is null ? Numbers()
                : inheritSensitivity ? null
                : sensitivityFromNumbers ? Numbers()
                : Reply.Repository?.Sensitivity?.Clone(),
        };
        rules.IgnoredNames.AddRange(IgnoredNames);
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
            var own = IsRepositoryScope ? reply.Repository ?? new ReviewRules() : reply.Global;
            SetNumbers(own.Sensitivity ?? reply.Global.Sensitivity ?? reply.Balanced);
            Fill(IgnoredNames, own.IgnoredNames);
            Fill(MechanicalPaths, own.MechanicalPaths);
            Fill(SkippedPaths, own.SkippedPaths);
            Fill(InheritedIgnoredNames, IsRepositoryScope ? reply.Global.IgnoredNames : []);
            Fill(InheritedMechanicalPaths, IsRepositoryScope ? reply.Global.MechanicalPaths : []);
            Fill(InheritedSkippedPaths, IsRepositoryScope ? reply.Global.SkippedPaths : []);
            Fill(BuiltInIgnoredNames, reply.BuiltInIgnoredNames);
            Fill(BuiltInMechanicalPaths, reply.BuiltInMechanicalPaths);
            if (Preset == ReviewPreset.Custom)
            {
                IsAdvancedOpen = true;
            }
        }
        finally
        {
            _applying = false;
        }

        OnPropertyChanged(nameof(IsOverriding));
        OnPropertyChanged(nameof(Preset));
        OnPropertyChanged(nameof(SettingsFilePath));
    }

    private void SetNumbers(ReviewSensitivity? sensitivity)
    {
        if (sensitivity is null)
        {
            return;
        }

        var wasApplying = _applying;
        _applying = true;
        try
        {
            MinimumPeers = sensitivity.MinimumPeers;
            SupportPercent = Math.Round((decimal)sensitivity.MinimumSupport * 100);
            MinimumLift = Math.Round((decimal)sensitivity.MinimumLift, 1);
            PointsPerType = sensitivity.MaxPointsPerType;
            Examples = sensitivity.MaxExamples;
        }
        finally
        {
            _applying = wasApplying;
        }
    }

    private ReviewSensitivity Numbers() => new()
    {
        MinimumPeers = (int)MinimumPeers,
        MinimumSupport = (double)(SupportPercent / 100),
        MinimumLift = (double)MinimumLift,
        MaxPointsPerType = (int)PointsPerType,
        MaxExamples = (int)Examples,
    };

    private ReviewPreset PresetOf(ReviewSensitivity? sensitivity) =>
        Reply is null || sensitivity is null ? ReviewPreset.Balanced :
        Same(sensitivity, Reply.Quiet) ? ReviewPreset.Quiet :
        Same(sensitivity, Reply.Balanced) ? ReviewPreset.Balanced :
        Same(sensitivity, Reply.Thorough) ? ReviewPreset.Thorough :
        ReviewPreset.Custom;

    private static bool Same(ReviewSensitivity a, ReviewSensitivity? b) =>
        b is not null &&
        a.MinimumPeers == b.MinimumPeers &&
        Math.Abs(a.MinimumSupport - b.MinimumSupport) < 0.001 &&
        Math.Abs(a.MinimumLift - b.MinimumLift) < 0.001 &&
        a.MaxPointsPerType == b.MaxPointsPerType &&
        a.MaxExamples == b.MaxExamples;

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> entries)
    {
        target.Clear();
        foreach (var entry in entries)
        {
            target.Add(entry);
        }
    }
}
