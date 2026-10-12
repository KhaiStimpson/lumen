using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using Lumen.App.Services;
using Lumen.Contracts;

namespace Lumen.App.ViewModels;

/// <summary>Settings → Inbox: the repositories whose open pull requests the inbox lists (plan v2 M1).</summary>
public sealed partial class InboxSettingsViewModel(IReviewSource source) : ObservableObject
{
    public ObservableCollection<string> WatchedRepositories { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string NewRepository { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial bool IsLoaded { get; private set; }

    /// <summary>Raised after a save, so the inbox can refresh at once.</summary>
    public event EventHandler? Saved;

    public async Task LoadAsync()
    {
        try
        {
            Show(await source.GetInboxSettingsAsync(CancellationToken.None).ConfigureAwait(true));
        }
        catch (RpcException ex)
        {
            Error = ex.Status.Detail;
        }
        catch (EngineUnavailableException ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        var repository = Normalise(NewRepository);
        if (WatchedRepositories.Contains(repository, StringComparer.OrdinalIgnoreCase))
        {
            NewRepository = "";
            return;
        }

        if (await SaveAsync([.. WatchedRepositories, repository]).ConfigureAwait(true))
        {
            NewRepository = "";
        }
    }

    private bool CanAdd() => NewRepository.Trim().Length > 0;

    [RelayCommand]
    private async Task RemoveAsync(string repository) =>
        await SaveAsync([.. WatchedRepositories.Where(r => !r.Equals(repository, StringComparison.OrdinalIgnoreCase))]).ConfigureAwait(true);

    /// <summary>Accepts "owner/name", a github.com repository URL, or a pull request reference in it.</summary>
    internal static string Normalise(string text)
    {
        var value = text.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
        {
            value = uri.AbsolutePath.Trim('/');
        }

        var hash = value.IndexOf('#', StringComparison.Ordinal);
        if (hash > 0)
        {
            value = value[..hash];
        }

        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : value;
    }

    private async Task<bool> SaveAsync(IEnumerable<string> repositories)
    {
        Error = null;
        try
        {
            var request = new InboxSettings();
            request.WatchedRepositories.AddRange(repositories);
            Show(await source.UpdateInboxSettingsAsync(request, CancellationToken.None).ConfigureAwait(true));
            Saved?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (RpcException ex)
        {
            Error = ex.Status.Detail;
        }
        catch (EngineUnavailableException ex)
        {
            Error = ex.Message;
        }

        return false;
    }

    private void Show(InboxSettings settings)
    {
        WatchedRepositories.Clear();
        foreach (var repository in settings.WatchedRepositories)
        {
            WatchedRepositories.Add(repository);
        }

        IsLoaded = true;
    }
}
