using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumen.App.Services;

namespace Lumen.App.ViewModels;

public enum ThemeChoice
{
    System,
    Light,
    Dark,
}

public enum MotionChoice
{
    System,
    Reduced,
    Full,
}

/// <summary>App-only preferences (app.json, never the engine's settings.json). Both apply at once.</summary>
public sealed partial class AppearanceViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly string _settingsPath;

    public AppearanceViewModel(AppSettings settings, string settingsPath)
    {
        _settings = settings;
        _settingsPath = settingsPath;
        Theme = settings.Theme switch
        {
            "Light" => ThemeChoice.Light,
            "Dark" => ThemeChoice.Dark,
            _ => ThemeChoice.System,
        };
        Motion = settings.ReducedMotion switch
        {
            true => MotionChoice.Reduced,
            false => MotionChoice.Full,
            null => MotionChoice.System,
        };
    }

    [ObservableProperty]
    public partial ThemeChoice Theme { get; set; }

    [ObservableProperty]
    public partial MotionChoice Motion { get; set; }

    partial void OnThemeChanged(ThemeChoice value)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = value switch
            {
                ThemeChoice.Light => ThemeVariant.Light,
                ThemeChoice.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }

        _settings.Theme = value.ToString();
        _settings.Save(_settingsPath);
    }

    partial void OnMotionChanged(MotionChoice value)
    {
        _settings.ReducedMotion = value switch
        {
            MotionChoice.Reduced => true,
            MotionChoice.Full => false,
            _ => null,
        };
        App.ReducedMotionPreference = _settings.ReducedMotion;
        _settings.Save(_settingsPath);
    }
}
