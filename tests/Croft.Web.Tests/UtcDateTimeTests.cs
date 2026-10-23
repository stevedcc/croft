using AwesomeAssertions;

namespace Croft.Web.Tests;

public class UtcDateTimeTests
{
    [Fact]
    public void Combine_WithDateAndTime_ReturnsUtcDateTime()
    {
        var date = new DateTime(2025, 6, 15);
        var time = new TimeSpan(14, 30, 0);

        var result = UtcDateTime.Combine(date, time);

        result.Should().NotBeNull();
        result!.Value.Kind.Should().Be(DateTimeKind.Utc);
        result.Value.Year.Should().Be(2025);
        result.Value.Month.Should().Be(6);
        result.Value.Day.Should().Be(15);
        result.Value.Hour.Should().Be(14);
        result.Value.Minute.Should().Be(30);
        result.Value.Second.Should().Be(0);
    }

    [Fact]
    public void Combine_WithDateOnly_DefaultsToMidnightUtc()
    {
        var date = new DateTime(2025, 6, 15);

        var result = UtcDateTime.Combine(date, null);

        result.Should().NotBeNull();
        result!.Value.Kind.Should().Be(DateTimeKind.Utc);
        result.Value.Hour.Should().Be(0);
        result.Value.Minute.Should().Be(0);
    }

    [Fact]
    public void Combine_WithNullDate_ReturnsNull()
    {
        var result = UtcDateTime.Combine(null, new TimeSpan(14, 30, 0));

        result.Should().BeNull();
    }

    [Fact]
    public void Combine_WithNullDateAndTime_ReturnsNull()
    {
        var result = UtcDateTime.Combine(null, null);

        result.Should().BeNull();
    }

    [Fact]
    public void Combine_IgnoresOriginalDateTimeKind_AlwaysReturnsUtc()
    {
        var date = new DateTime(2025, 6, 15, 10, 0, 0, DateTimeKind.Local);
        var time = new TimeSpan(8, 0, 0);

        var result = UtcDateTime.Combine(date, time);

        result.Should().NotBeNull();
        result!.Value.Kind.Should().Be(DateTimeKind.Utc);
        result.Value.Hour.Should().Be(8);
        result.Value.Minute.Should().Be(0);
    }
}
