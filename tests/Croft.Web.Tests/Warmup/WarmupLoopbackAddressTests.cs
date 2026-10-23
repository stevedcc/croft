using AwesomeAssertions;
using Croft.Shared.Warmup;

namespace Croft.Web.Tests.Warmup;

/// <summary>W-097: Kestrel reports wildcard addresses that are not URIs; the warm-up needs a loopback one.</summary>
public sealed class WarmupLoopbackAddressTests
{
    [Theory]
    [InlineData("http://+:8080", "http://127.0.0.1:8080/")]
    [InlineData("http://*:8080", "http://127.0.0.1:8080/")]
    [InlineData("http://[::]:8080", "http://127.0.0.1:8080/")]
    [InlineData("http://0.0.0.0:8080", "http://127.0.0.1:8080/")]
    [InlineData("http://localhost:5000", "http://127.0.0.1:5000/")]
    [InlineData("http://localhost:5000/", "http://127.0.0.1:5000/")]
    [InlineData("http://+", "http://127.0.0.1/")]
    public void EveryAddressForm_BecomesTheLoopbackAddressOnItsPort(string reported, string expected) =>
        WarmupRunner.LoopbackOf(reported).ToString().Should().Be(expected);

    [Fact]
    public void PrefersPlainHttp_WhenThereAreSeveralAddresses() =>
        WarmupRunner.LoopbackOf(["https://+:8443", "http://+:8080"]).ToString().Should().Be("http://127.0.0.1:8080/");
}
