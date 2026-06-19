using System.Text;

namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Normalizes MAC addresses into comparable and Snipe-IT custom-field display forms.
/// </summary>
internal static class MacAddressNormalizer
{
    public static string? NormalizeComparable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(capacity: 12);
        foreach (var character in value)
        {
            if (character is ':' or '-' or '.' || char.IsWhiteSpace(character))
            {
                continue;
            }

            if (!Uri.IsHexDigit(character))
            {
                return null;
            }

            builder.Append(char.ToUpperInvariant(character));
        }

        return builder.Length == 12 ? builder.ToString() : null;
    }

    public static string? NormalizeDisplay(string? value)
    {
        var comparable = NormalizeComparable(value);
        if (comparable is null)
        {
            return null;
        }

        return $"{comparable[..2]}:{comparable[2..4]}:{comparable[4..6]}:{comparable[6..8]}:{comparable[8..10]}:{comparable[10..12]}";
    }
}
