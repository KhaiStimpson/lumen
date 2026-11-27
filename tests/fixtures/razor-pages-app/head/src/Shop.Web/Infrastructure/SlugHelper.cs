using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Shop.Web.Infrastructure;

public static partial class SlugHelper
{
    public static string ToSlug(string? value, int maxLength = 80)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalised = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalised.Length);
        foreach (var c in normalised)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        var slug = NonAlphanumeric().Replace(builder.ToString().ToLowerInvariant(), "-").Trim('-');
        return slug.Length <= maxLength ? slug : slug[..maxLength].TrimEnd('-');
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
