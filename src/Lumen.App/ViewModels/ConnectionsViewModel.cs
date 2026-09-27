using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using Lumen.App.Services;
using Lumen.Contracts;

namespace Lumen.App.ViewModels;

/// <summary>
/// The Connections panel (TDD §39): each service with its own status and billing. The OpenRouter key passes straight
/// through to the engine's credential store; this view model drops it as soon as it has been handed over.
/// </summary>
public sealed partial class ConnectionsViewModel(IReviewSource source) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsLoaded), nameof(JevState), nameof(JevStatus), nameof(JevModel), nameof(JevSends), nameof(IsKeyStored), nameof(KeyLabel),
        nameof(CanStoreKey), nameof(ClaudeState), nameof(ClaudeStatus), nameof(ClaudeBilling), nameof(InvestigationsStatus),
        nameof(SettingsPath))]
    public partial Connections? Status { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveKeyCommand))]
    public partial string KeyInput { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveKeyCommand), nameof(RemoveKeyCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    public bool IsLoaded => Status is not null;

    public ConnectionState JevState => Status?.Jev.State ?? ConnectionState.Off;

    public string JevStatus => Status?.Jev is not { } jev
        ? "Checking…"
        : jev.State == ConnectionState.Connected ? $"OpenRouter API · {jev.Detail}" : jev.Detail;

    public string JevModel => Status?.Jev.Model ?? "";

    public string JevSends => Status?.Jev.Sends ?? "";

    public bool IsKeyStored => Status?.Jev.KeyStored ?? false;

    public string KeyLabel => IsKeyStored ? "REPLACE OPENROUTER KEY" : "OPENROUTER API KEY";

    public bool CanStoreKey => Status?.Jev.CanStoreKey ?? false;

    public ConnectionState ClaudeState => Status?.Claude.State ?? ConnectionState.Off;

    public string ClaudeStatus => Status?.Claude is not { } claude
        ? "Checking…"
        : claude.Version.Length == 0 ? claude.Detail : $"{claude.Detail} · {claude.Version}";

    public string ClaudeBilling => Status?.Claude.Billing switch
    {
        ConnectionBilling.Subscription => "Subscription",
        ConnectionBilling.Metered => "Metered",
        _ => "",
    };

    public string InvestigationsStatus => Status?.Claude.InvestigationsDetail ?? "";

    public string SettingsPath => Status?.SettingsPath ?? "";

    public Task LoadAsync() => RunAsync(source.GetConnectionsAsync);

    [RelayCommand(CanExecute = nameof(CanSaveKey))]
    private async Task SaveKeyAsync()
    {
        var key = KeyInput.Trim();
        KeyInput = "";
        await RunAsync(ct => source.SetOpenRouterKeyAsync(key, ct)).ConfigureAwait(true);
    }

    private bool CanSaveKey() => !IsBusy && !string.IsNullOrWhiteSpace(KeyInput);

    [RelayCommand(CanExecute = nameof(CanRemoveKey))]
    private Task RemoveKeyAsync() => RunAsync(source.RemoveOpenRouterKeyAsync);

    private bool CanRemoveKey() => !IsBusy;

    private async Task RunAsync(Func<CancellationToken, Task<Connections>> call)
    {
        IsBusy = true;
        Error = null;
        try
        {
            Status = await call(CancellationToken.None).ConfigureAwait(true);
        }
        catch (RpcException ex)
        {
            Error = ex.Status.Detail;
        }
        catch (EngineUnavailableException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
