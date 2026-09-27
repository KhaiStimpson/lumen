using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace Lumen.App.Diff;

/// <summary>TextMate syntax highlighting that follows the app's light/dark variant.</summary>
public static class SyntaxThemes
{
    private static readonly RegistryOptions Registry = new(ThemeName.LightPlus);

    public static TextMate.Installation Install(TextEditor editor, string? path)
    {
        var installation = editor.InstallTextMate(Registry);
        Apply(installation, editor.ActualThemeVariant);
        SetLanguage(installation, path);

        editor.ActualThemeVariantChanged += (_, _) => Apply(installation, editor.ActualThemeVariant);
        return installation;
    }

    public static void SetLanguage(TextMate.Installation installation, string? path)
    {
        var extension = Path.GetExtension(path ?? "");
        var language = string.IsNullOrEmpty(extension) ? null : Registry.GetLanguageByExtension(extension);
        installation.SetGrammar(language is null ? null : Registry.GetScopeByLanguageId(language.Id));
    }

    private static void Apply(TextMate.Installation installation, ThemeVariant variant) =>
        installation.SetTheme(Registry.LoadTheme(variant == ThemeVariant.Dark ? ThemeName.DarkPlus : ThemeName.LightPlus));
}

internal static class ThemeResources
{
    public static IBrush Brush(StyledElement element, string key) =>
        element.TryFindResource(key, element.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.Transparent;
}
