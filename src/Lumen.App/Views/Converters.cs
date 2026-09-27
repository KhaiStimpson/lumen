using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Lumen.App.Views;

public static class Converters
{
    public static readonly IValueConverter IsPositive =
        new FuncValueConverter<int, bool>(n => n > 0);

    public static readonly IValueConverter IsZero =
        new FuncValueConverter<int, bool>(n => n == 0);

    public static readonly IValueConverter NotEmpty =
        new FuncValueConverter<string?, bool>(s => !string.IsNullOrEmpty(s));

    /// <summary>Review point type → marker shape geometry (never colour alone, TDD §28).</summary>
    public static readonly IValueConverter KindToIcon = new ResourceConverter(kind => kind switch
    {
        "CorrectnessRisk" or "SecurityRisk" => "Icon.Correctness",
        "BehaviourChange" => "Icon.Behaviour",
        "ArchitectureDrift" => "Icon.Architecture",
        _ => "Icon.Deviation",
    });

    public static readonly IValueConverter KindToBrush = new ResourceConverter(kind => kind switch
    {
        "CorrectnessRisk" or "SecurityRisk" => "Marker.Correctness",
        "BehaviourChange" => "Marker.Behaviour",
        "ArchitectureDrift" => "Marker.Architecture",
        _ => "Marker.Deviation",
    });

    public static readonly IValueConverter SeverityToBrush = new ResourceConverter(severity => severity switch
    {
        "High" => "Severity.High",
        "Medium" => "Severity.Medium",
        _ => "Severity.Low",
    });

    /// <summary>Connection state → status dot. The status text always carries the meaning too.</summary>
    public static readonly IValueConverter ConnectionToBrush = new ResourceConverter(state => state switch
    {
        "Connected" => "Marker.Verified",
        "Error" => "Severity.High",
        "NotConnected" => "Text.Tertiary",
        _ => "Border.Strong",
    });

    public static readonly IValueConverter StrengthToWidth =
        new FuncValueConverter<double, double>(strength => Math.Round(strength * 56));

    public static readonly IValueConverter DimIf =
        new FuncValueConverter<bool, double>(dim => dim ? 0.45 : 1);

    private sealed class ResourceConverter(Func<string, string> key) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var resourceKey = key(value?.ToString() ?? "");
            return Avalonia.Application.Current is { } app && app.TryGetResource(resourceKey, app.ActualThemeVariant, out var resource)
                ? resource
                : targetType == typeof(IBrush) ? Brushes.Gray : null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
