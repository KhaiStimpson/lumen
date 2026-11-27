using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Lumen.App.Views;

/// <summary>
/// Reasons and flag messages name code in backticks ("`EditModel` injects `IOrderService`"). <c>CodeText.Text</c> on a
/// TextBlock shows those names in the code font, without the backticks.
/// </summary>
public static class CodeText
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(CodeText));

    static CodeText()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((block, args) => Apply(block, args.NewValue as string));
    }

    public static string? GetText(TextBlock element) => element.GetValue(TextProperty);

    public static void SetText(TextBlock element, string? value) => element.SetValue(TextProperty, value);

    /// <summary>Splits on backticks: odd segments are code.</summary>
    public static IReadOnlyList<(string Text, bool IsCode)> Segments(string? text)
    {
        var parts = (text ?? "").Split('`');
        var segments = new List<(string, bool)>();
        for (var i = 0; i < parts.Length; i++)
        {
            // An even number of parts means the last backtick is unmatched: what follows it is plain text, backtick included.
            var unmatched = parts.Length % 2 == 0 && i == parts.Length - 1;
            var segment = unmatched ? "`" + parts[i] : parts[i];
            if (segment.Length > 0)
            {
                segments.Add((segment, i % 2 == 1 && !unmatched));
            }
        }

        return segments;
    }

    private static void Apply(TextBlock block, string? text)
    {
        var inlines = new InlineCollection();
        foreach (var (segment, isCode) in Segments(text))
        {
            var run = new Run(segment);
            if (isCode && Application.Current?.TryFindResource("CodeFont", out var font) == true && font is FontFamily family)
            {
                run.FontFamily = family;
                run.FontSize = Math.Max(1, block.FontSize - 0.5);
            }

            inlines.Add(run);
        }

        block.Inlines = inlines;
    }
}
