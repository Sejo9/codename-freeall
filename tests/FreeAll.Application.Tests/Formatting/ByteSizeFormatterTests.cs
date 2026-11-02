using System.Globalization;
using FreeAll.Application.Formatting;

namespace FreeAll.Application.Tests.Formatting;

public sealed class ByteSizeFormatterTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(0L, "0 bytes")]
    [InlineData(1L, "1 byte")]
    [InlineData(1023L, "1,023 bytes")]
    [InlineData(1024L, "1.00 KB")]
    [InlineData(1536L, "1.50 KB")]
    [InlineData(10L * 1024, "10.0 KB")]
    [InlineData(100L * 1024, "100 KB")]
    [InlineData(1024L * 1024, "1.00 MB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.00 GB")]
    [InlineData(2L * 1024 * 1024 * 1024 * 1024, "2.00 TB")]
    public void Formats_with_binary_units_and_three_significant_digits(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSizeFormatter.Format(bytes, Invariant));
    }

    [Fact]
    public void Truncates_instead_of_rounding_up_to_the_next_unit()
    {
        // 1023.99 MB must not display as "1024 MB" or "1.00 GB".
        Assert.Equal("1023 MB", ByteSizeFormatter.Format((1024L * 1024 * 1024) - 1, Invariant));
    }

    [Fact]
    public void Unknown_size_is_labelled()
    {
        Assert.Equal("Unknown", ByteSizeFormatter.Format(null, Invariant));
    }

    [Fact]
    public void Negative_sizes_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ByteSizeFormatter.Format(-1, Invariant));
    }

    [Fact]
    public void Uses_the_supplied_culture()
    {
        Assert.Equal("1,50 KB", ByteSizeFormatter.Format(1536, CultureInfo.GetCultureInfo("de-DE")));
    }
}
