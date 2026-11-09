using System.Globalization;
using System.Windows;
using System.Windows.Data;
using FreeAll.Application.Formatting;

namespace FreeAll.Desktop.Converters;

/// <summary>True → Visible. Set <see cref="Invert"/> for the opposite.</summary>
internal sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Non-null, non-empty and non-zero → Visible. Set <see cref="Invert"/> for the opposite.
/// Counts are treated as "has value" only when greater than zero.
/// </summary>
internal sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value switch
        {
            null => false,
            string s => s.Length > 0,
            int count => count > 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };

        return hasValue ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the value's name equals the converter parameter, e.g. an enum state.</summary>
internal sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter as string, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

internal sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        long bytes => ByteSizeFormatter.Format(bytes, culture),
        null => ByteSizeFormatter.Format(null, culture),
        _ => DependencyProperty.UnsetValue,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A share of a length, for example a details card's MaxHeight as a share of its pane's height, so the list
/// below it always keeps the rest. The parameter is the share in invariant format ("0.45").
/// </summary>
internal sealed class ShareOfLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double length && double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var share) && length > 0
            ? length * share
            : double.PositiveInfinity;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

internal sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
