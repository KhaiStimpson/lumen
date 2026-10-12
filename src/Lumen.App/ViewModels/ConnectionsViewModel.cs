using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using Lumen.App.Services;
using Lumen.Contracts;

namespace Lumen.App.ViewModels;

/// <summary>
/// The Connections panel (TDD §39): each service with its own status and billing. Keys and tokens pass straight
/// through to the engine's credential store; this view model drops them as soon as they have been handed over.
/// </summary>
public sealed partial class ConnectionsViewModel(IReviewSource source) : ObservableObject
{
    // Replies can overtake each other when switches flip quickly; only the newest request's reply is shown.
    private int _sequence;

    // Set while a reply is copied into the switches, so that copying isn't sent back as a change.
    private bool _applying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsLoaded), nameof(JevState), nameof(JevStatus), nameof(JevModel), nameof(JevSends), nameof(IsKeyStored), nameof(KeyLabel),
        nameof(CanStoreKey), nameof(ClaudeState), nameof(ClaudeStatus), nameof(ClaudeBilling), nameof(InvestigationsStatus),
        nameof(SettingsPath), nameof(FastModelState), nameof(FastModelStatus), nameof(FastModelName), nameof(FastModelSends),
        nameof(IsCloudflareStored), nameof(CanStoreCloudflare), nameof(RequestsTodayLabel), nameof(CloudflareLabel))]
    public partial Connections? Status { get; private set; }

    // Fast model (plan v2 M7) ------------------------------------------------------------------------

    /// <summary>privacy.allowCodeToFastModel: the diff goes to the fast model for the walkthrough.</summary>
    [ObservableProperty]
    public partial bool AllowCodeToFastModel { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCloudflareCommand))]
    public partial string CloudflareAccountInput { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCloudflareCommand))]
    public partial string CloudflareTokenInput { get; set; } = "";

    /// <summary>Saved on Enter or when the box loses focus.</summary>
    [ObservableProperty]
    public partial string FastModelInput { get; set; } = "";

    [ObservableProperty]
    public partial string FastModelGatewayInput { get; set; } = "";

    [ObservableProperty]
    public partial decimal FastModelTimeoutSeconds { get; set; } = 30;

    [ObservableProperty]
    public partial decimal FastModelMaxDiffKb { get; set; } = 512;

    [ObservableProperty]
    public partial decimal FastModelDailyCap { get; set; } = 500;

    /// <summary>JEV is legacy (plan v2): its controls sit behind a disclosure.</summary>
    [ObservableProperty]
    public partial bool ShowLegacy { get; set; }

    public ConnectionState FastModelState => Status?.FastModel?.State ?? ConnectionState.Off;

    public string FastModelStatus => Status?.FastModel is not { } fast
        ? "Checking…"
        : fast.State == ConnectionState.Connected ? $"Cloudflare Workers AI · {fast.Detail}" : fast.Detail;

    public string FastModelName => Status?.FastModel?.Model ?? "";

    public string FastModelSends => Status?.FastModel?.Sends ?? "";

    public bool IsCloudflareStored => Status?.FastModel?.CredentialsStored ?? false;

    public bool CanStoreCloudflare => Status?.FastModel?.CanStoreCredentials ?? false;

    public string CloudflareLabel => IsCloudflareStored ? "REPLACE CLOUDFLARE CREDENTIALS" : "CLOUDFLARE ACCOUNT ID AND API TOKEN";

    public string RequestsTodayLabel => Status?.FastModel is { } fast ? $"{fast.RequestsToday} requests today" : "";

    [RelayCommand(CanExecute = nameof(CanSaveCloudflare))]
    private async Task SaveCloudflareAsync()
    {
        var (account, token) = (CloudflareAccountInput.Trim(), CloudflareTokenInput.Trim());
        CloudflareTokenInput = "";
        await RunAsync(ct => source.SetCloudflareCredentialsAsync(account, token, ct)).ConfigureAwait(true);
        if (Error is null)
        {
            CloudflareAccountInput = "";
        }
    }

    private bool CanSaveCloudflare() => !IsBusy && CloudflareAccountInput.Trim().Length > 0 && CloudflareTokenInput.Trim().Length > 0;

    [RelayCommand]
    private Task RemoveCloudflareAsync() => RunAsync(source.RemoveCloudflareCredentialsAsync);

    partial void OnAllowCodeToFastModelChanged(bool value) => SaveSettings();

    [RelayCommand]
    private void ToggleLegacy() => ShowLegacy = !ShowLegacy;

    partial void OnFastModelTimeoutSecondsChanged(decimal value) => SaveSettings();

    partial void OnFastModelMaxDiffKbChanged(decimal value) => SaveSettings();

    partial void OnFastModelDailyCapChanged(decimal value) => SaveSettings();

    partial void OnFastModelGatewayInputChanged(string value)
    {
        if (value.Trim() != Status?.Settings?.FastModelGateway)
        {
            SaveSettings();
        }
    }

    partial void OnFastModelInputChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (Status?.Settings is { } saved)
            {
                FastModelInput = saved.FastModelId;
            }
        }
        else if (value.Trim() != Status?.Settings?.FastModelId)
        {
            SaveSettings();
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveKeyCommand))]
    public partial string KeyInput { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveKeyCommand), nameof(RemoveKeyCommand), nameof(SaveCloudflareCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>privacy.allowCloudReasoning: off means nothing goes to any AI service.</summary>
    [ObservableProperty]
    public partial bool AllowCloudReasoning { get; set; }

    [ObservableProperty]
    public partial bool JevEnabled { get; set; }

    [ObservableProperty]
    public partial bool AllowCodeSnippetsToJev { get; set; }

    /// <summary>agents.enabled and privacy.allowCodeToAgents together.</summary>
    [ObservableProperty]
    public partial bool InvestigationsEnabled { get; set; }

    // Cloud AI limits (the Settings overlay's Cloud AI limits section).

    /// <summary>Saved on Enter or when the box loses focus, never per keystroke.</summary>
    [ObservableProperty]
    public partial string JevModelInput { get; set; } = "";

    [ObservableProperty]
    public partial decimal JevTimeoutSeconds { get; set; } = 12;

    [ObservableProperty]
    public partial bool JevRequireZeroDataRetention { get; set; } = true;

    [ObservableProperty]
    public partial decimal MaxInvestigationsPerPullRequest { get; set; } = 3;

    [ObservableProperty]
    public partial decimal MaxConcurrentInvestigations { get; set; } = 1;

    [ObservableProperty]
    public partial bool AllowMeteredUsage { get; set; }

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

    partial void OnAllowCloudReasoningChanged(bool value) => SaveSettings();

    partial void OnJevEnabledChanged(bool value) => SaveSettings();

    partial void OnAllowCodeSnippetsToJevChanged(bool value) => SaveSettings();

    partial void OnInvestigationsEnabledChanged(bool value) => SaveSettings();

    partial void OnJevModelInputChanged(string value)
    {
        // The box commits on leaving it, so a blank here is a finished edit: put the saved model back.
        if (string.IsNullOrWhiteSpace(value))
        {
            if (Status?.Settings is { } saved)
            {
                JevModelInput = saved.JevModel;
            }
        }
        else if (value.Trim() != Status?.Settings?.JevModel)
        {
            SaveSettings();
        }
    }

    partial void OnJevTimeoutSecondsChanged(decimal value) => SaveSettings();

    partial void OnJevRequireZeroDataRetentionChanged(bool value) => SaveSettings();

    partial void OnMaxInvestigationsPerPullRequestChanged(decimal value) => SaveSettings();

    partial void OnMaxConcurrentInvestigationsChanged(decimal value) => SaveSettings();

    partial void OnAllowMeteredUsageChanged(bool value) => SaveSettings();

    partial void OnStatusChanged(Connections? value)
    {
        if (value?.Settings is not { } settings)
        {
            return;
        }

        _applying = true;
        try
        {
            AllowCloudReasoning = settings.AllowCloudReasoning;
            JevEnabled = settings.JevEnabled;
            AllowCodeSnippetsToJev = settings.AllowCodeSnippetsToJev;
            InvestigationsEnabled = settings.InvestigationsEnabled;
            JevModelInput = settings.JevModel;
            JevTimeoutSeconds = (decimal)settings.JevTimeoutSeconds;
            JevRequireZeroDataRetention = settings.JevRequireZeroDataRetention;
            MaxInvestigationsPerPullRequest = settings.MaxInvestigationsPerPullRequest;
            MaxConcurrentInvestigations = settings.MaxConcurrentInvestigations;
            AllowMeteredUsage = settings.AllowMeteredUsage;
            AllowCodeToFastModel = settings.AllowCodeToFastModel;
            FastModelInput = settings.FastModelId;
            FastModelGatewayInput = settings.FastModelGateway;
            FastModelTimeoutSeconds = (decimal)settings.FastModelTimeoutSeconds;
            FastModelMaxDiffKb = settings.FastModelMaxDiffKb;
            FastModelDailyCap = settings.FastModelDailyRequestCap;
        }
        finally
        {
            _applying = false;
        }
    }

    private async void SaveSettings()
    {
        if (_applying || Status is null)
        {
            return;
        }

        var settings = new ConnectionSettings
        {
            AllowCloudReasoning = AllowCloudReasoning,
            JevEnabled = JevEnabled,
            AllowCodeSnippetsToJev = AllowCodeSnippetsToJev,
            InvestigationsEnabled = InvestigationsEnabled,
            JevTimeoutSeconds = (double)JevTimeoutSeconds,
            JevRequireZeroDataRetention = JevRequireZeroDataRetention,
            MaxInvestigationsPerPullRequest = (int)MaxInvestigationsPerPullRequest,
            MaxConcurrentInvestigations = (int)MaxConcurrentInvestigations,
            AllowMeteredUsage = AllowMeteredUsage,
            AllowCodeToFastModel = AllowCodeToFastModel,
            FastModelTimeoutSeconds = (double)FastModelTimeoutSeconds,
            FastModelMaxDiffKb = (int)FastModelMaxDiffKb,
            FastModelDailyRequestCap = (int)FastModelDailyCap,
            FastModelGateway = FastModelGatewayInput.Trim(),
        };

        if (!string.IsNullOrWhiteSpace(FastModelInput))
        {
            settings.FastModelId = FastModelInput.Trim();
        }

        // A blank model would be refused; leave the saved one alone until a real name is entered.
        if (!string.IsNullOrWhiteSpace(JevModelInput))
        {
            settings.JevModel = JevModelInput.Trim();
        }

        await RunAsync(ct => source.UpdateConnectionSettingsAsync(settings, ct)).ConfigureAwait(true);
    }

    private async Task RunAsync(Func<CancellationToken, Task<Connections>> call)
    {
        var sequence = ++_sequence;
        IsBusy = true;
        Error = null;
        try
        {
            var status = await call(CancellationToken.None).ConfigureAwait(true);
            if (sequence == _sequence)
            {
                Status = status;
            }
        }
        catch (RpcException ex)
        {
            Error = ex.Status.Detail;
            RevertSwitches();
        }
        catch (EngineUnavailableException ex)
        {
            Error = ex.Message;
            RevertSwitches();
        }
        finally
        {
            if (sequence == _sequence)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>After a failed save, the switches go back to what the engine last reported.</summary>
    private void RevertSwitches() => OnStatusChanged(Status);
}
