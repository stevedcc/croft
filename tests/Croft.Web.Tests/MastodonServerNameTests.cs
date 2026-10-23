using AwesomeAssertions;
using Croft.Web.Services;

namespace Croft.Web.Tests;

/// <summary>Brief 12b item 3: what a reader types for "Your Mastodon server" becomes a DNS host name, or is refused.</summary>
public class MastodonServerNameTests
{
    [Theory]
    [InlineData("mastodon.example", "mastodon.example")]
    [InlineData("  Mastodon.Example  ", "mastodon.example")]
    [InlineData("https://mastodon.example", "mastodon.example")]
    [InlineData("http://mastodon.example/", "mastodon.example")]
    [InlineData("HTTPS://Mastodon.Example/", "mastodon.example")]
    [InlineData("@reader@mastodon.example", "mastodon.example")]
    [InlineData("reader@mastodon.example", "mastodon.example")]
    [InlineData("https://reader@mastodon.example/", "mastodon.example")]
    [InlineData("social.my-town.example", "social.my-town.example")]
    [InlineData("xn--mnchen-3ya.example", "xn--mnchen-3ya.example")]
    public void AValidInput_IsNormalised(string input, string expected)
    {
        MastodonServerName.TryNormalise(input, out var host).Should().BeTrue();
        host.Should().Be(expected);
    }

    [Fact]
    public void ANameOf253Characters_WithLabelsOf63_IsValid()
    {
        var name = string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61));
        name.Length.Should().Be(253);

        MastodonServerName.TryNormalise(name, out var host).Should().BeTrue();
        host.Should().Be(name);
    }

    [Fact]
    public void ANameOf254Characters_IsInvalid()
    {
        var name = string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 62));
        name.Length.Should().Be(254);

        MastodonServerName.TryNormalise(name, out _).Should().BeFalse();
    }

    [Fact]
    public void ALabelOf64Characters_IsInvalid() =>
        MastodonServerName.TryNormalise(new string('a', 64) + ".example", out _).Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mastodon")]
    [InlineData("-bad.example")]
    [InlineData("bad-.example")]
    [InlineData("a..b")]
    [InlineData(".example.com")]
    [InlineData("example.com.")]
    [InlineData("host:8080")]
    [InlineData("host.example:8080")]
    [InlineData("host/path")]
    [InlineData("host.example/path")]
    [InlineData("host.example?x=1")]
    [InlineData("host.example#top")]
    [InlineData("user:pass@host")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://mastodon.example/@reader")]
    [InlineData("ex ample.com")]
    [InlineData("ex_ample.com")]
    [InlineData("m\u00fcnchen.example")]
    [InlineData("127.0.0.1")]
    [InlineData("<script>.example")]
    public void AnInvalidInput_IsRefused(string? input)
    {
        MastodonServerName.TryNormalise(input, out var host).Should().BeFalse();
        host.Should().BeNull();
    }
}
