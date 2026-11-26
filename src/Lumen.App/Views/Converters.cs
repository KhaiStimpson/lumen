using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Lumen.App.Views;

public static class Converters
{
    public static readonly IValueConverter IsPositive =
        new FuncValueConverter<int, bool>(n => n > 0);

    /// <summary>A share of changed lines → a star column width, for the plan's tier bar (0 collapses the segment).</summary>
    public static readonly IValueConverter LinesToStar =
        new FuncValueConverter<double, GridLength>(n => n > 0 ? new GridLength(n, GridUnitType.Star) : new GridLength(0));

    public static readonly IValueConverter IsZero =
        new FuncValueConverter<int, bool>(n => n == 0);

    /// <summary>Empty text → null, so an empty tooltip does not show.</summary>
    public static readonly IValueConverter EmptyToNull =
        new FuncValueConverter<string?, string?>(s => string.IsNullOrEmpty(s) ? null : s);

    public static readonly IValueConverter NotEmpty =
        new FuncValueConverter<string?, bool>(s => !string.IsNullOrEmpty(s));

    /// <summary>Check state ("success", "failure", "pending") → status dot. The tooltip and label carry it too.</summary>
    public static readonly IValueConverter CheckToBrush = new ResourceConverter(state => state switch
    {
        "success" or "Success" => "Marker.Verified",
        "failure" or "Failure" => "Severity.High",
        "pending" or "Pending" => "Severity.Medium",
        _ => "Border.Strong",
    });

    /// <summary>
    /// An enum property against the enum member named in the parameter. Two-way, so a radio button can set it;
    /// unchecking does nothing, since checking another option already moved the value.
    /// </summary>
    public static readonly IValueConverter EnumEquals = new EnumEqualsConverter();

    /// <summary>" ✓" after "Mechanical · Proven"; nothing for boilerplate, which is not verified.</summary>
    public static readonly IValueConverter CheckIf =
        new FuncValueConverter<bool, string>(proven => proven ? " ✓" : "");

    public static readonly IValueConverter DimIf =
        new FuncValueConverter<bool, double>(dim => dim ? 0.45 : 1);

    private sealed class EnumEqualsConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is not null && parameter is not null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true && parameter?.ToString() is { } name && targetType.IsEnum
                ? Enum.Parse(targetType, name)
                : Avalonia.Data.BindingOperations.DoNothing;
    }

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
