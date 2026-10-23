using Croft.Web.Options;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;

namespace Croft.Web.Tests;

public class AuthSettingsTests
{
    [Fact]
    public void Read_Unset_DefaultsToThirtyDays()
    {
        AuthSettings.Read(Configure(null)).SessionLifetime.Should().Be(TimeSpan.FromDays(30));
    }

    [Theory]
    [InlineData("7.00:00:00", 7 * 24)]
    [InlineData("365.00:00:00", 365 * 24)]
    [InlineData("12:00:00", 12)]
    public void Read_ValidLifetime_IsParsed(string raw, int hours)
    {
        AuthSettings.Read(Configure(raw)).SessionLifetime.Should().Be(TimeSpan.FromHours(hours));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("00:00:00")]
    [InlineData("-1.00:00:00")]
    [InlineData("366.00:00:00")]
    [InlineData("thirty days")]
    [InlineData("")]
    public void Read_InvalidLifetime_FailsNamingTheSetting(string raw)
    {
        var read = () => AuthSettings.Read(Configure(raw));

        read.Should().Throw<InvalidOperationException>().WithMessage("*Auth:SessionLifetime*");
    }

    private static IConfiguration Configure(string? lifetime) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            lifetime is null ? [] : new Dictionary<string, string?> { ["Auth:SessionLifetime"] = lifetime }).Build();
}
