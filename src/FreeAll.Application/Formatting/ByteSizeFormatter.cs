using System.Globalization;

namespace FreeAll.Application.Formatting;

/// <summary>
/// Formats byte counts with binary (1024-based) units, labelled as Windows Explorer does (KB, MB, GB).
/// </summary>
public static class ByteSizeFormatter
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB", "PB", "EB"];

    public static string Format(long? bytes, IFormatProvider? provider = null)
    {
        if (bytes is not { } value)
        {
            return "Unknown";
        }

        provider ??= CultureInfo.CurrentCulture;

        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), "Byte counts cannot be negative.");
        }

        if (value < 1024)
        {
            return string.Format(provider, "{0:N0} {1}", value, value == 1 ? "byte" : "bytes");
        }

        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        // Three significant digits, like Explorer: 1.23 GB, 12.3 GB, 123 GB.
        var format = size switch
        {
            < 10 => "{0:0.00} {1}",
            < 100 => "{0:0.0} {1}",
            _ => "{0:0} {1}",
        };

        // Truncate rather than round so 1023.9 MB never displays as "1024 MB".
        var digits = size < 10 ? 100 : size < 100 ? 10 : 1;
        var truncated = Math.Floor(size * digits) / digits;

        return string.Format(provider, format, truncated, Units[unit]);
    }
}
