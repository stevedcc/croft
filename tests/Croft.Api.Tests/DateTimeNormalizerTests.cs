using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using AwesomeAssertions;

namespace Croft.Api.Tests;

public class DateTimeNormalizerTests
{
    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void NormalizeToUtc_ReturnsUtc(DateTimeKind kind)
    {
        var dt = new DateTime(2026, 10, 4, 12, 0, 0, kind);

        var normalized = new DateTimeNormalizer(new FakeTimeProvider(DateTimeOffset.UtcNow)).NormalizeToUtc(dt);

        normalized.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void NormalizeToUtc_Local_ConvertsValue()
    {
        var local = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Local);

        var normalized = new DateTimeNormalizer(new FakeTimeProvider(DateTimeOffset.UtcNow)).NormalizeToUtc(local);

        normalized.Should().Be(local.ToUniversalTime());
    }

    [Fact]
    public void NormalizeToUtc_Null_ReturnsUtcNow()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);

        var normalized = new DateTimeNormalizer(new FakeTimeProvider(DateTimeOffset.UtcNow)).NormalizeToUtc(null);

        var after = DateTime.UtcNow.AddSeconds(1);
        normalized.Kind.Should().Be(DateTimeKind.Utc);
        normalized.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }
}
