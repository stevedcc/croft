using System.Globalization;
using AwesomeAssertions;
using Croft.Web.Services;

namespace Croft.Web.Tests;

/// <summary>Brief 17e item 5: the compact count on the overlay pill.</summary>
public class SocialCountFormatTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1000, "1k")]
    [InlineData(1049, "1k")]
    [InlineData(1050, "1.1k")]
    [InlineData(1200, "1.2k")]
    [InlineData(63724, "63.7k")]
    [InlineData(999949, "999.9k")]
    [InlineData(999950, "1M")]
    [InlineData(1000000, "1M")]
    [InlineData(1200000, "1.2M")]
    [InlineData(int.MaxValue, "2147.5M")]
    public void Compact_ShortensTo_OneDecimal_DroppingTrailingZero(int count, string expected) =>
        SocialCountFormat.Compact(count).Should().Be(expected);

    [Fact]
    public void Compact_UsesTheInvariantCulture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            SocialCountFormat.Compact(63724).Should().Be("63.7k", "a German decimal comma must not leak into the pill");
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
