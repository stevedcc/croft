using System.Net;
using AwesomeAssertions;
using Croft.Api.Social;

namespace Croft.Api.Tests.Social;

/// <summary>
/// Brief 12a item 4, the SSRF guard: avatar URLs come from remote servers, so the three social clients resolve the host themselves, refuse
/// loopback, private, link-local, CGNAT, multicast and unspecified addresses, and connect to the address they checked. https on port 443 only.
/// </summary>
public class SocialEgressGuardTests
{
    private static readonly IPAddress[] Public = [IPAddress.Parse("93.184.216.34")];

    private static SocialEgressGuard GuardResolving(params string[] addresses) =>
        new((_, _) => ValueTask.FromResult(addresses.Select(IPAddress.Parse).ToArray()));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.255.255")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.255")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fd00::1")]
    [InlineData("fc00::1")]
    [InlineData("fdff:ffff::1")]
    [InlineData("fe80::1")]
    [InlineData("febf::1")]
    [InlineData("fec0::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::a00:1")]
    public void AnAddressThatIsNotPublic_IsBlocked(string address) =>
        SocialEgressGuard.IsBlocked(IPAddress.Parse(address)).Should().BeTrue();

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("169.253.255.255")]
    [InlineData("192.167.255.255")]
    [InlineData("223.255.255.255")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:93.184.216.34")]
    [InlineData("64:ff9b::5db8:d822")]
    public void APublicAddress_IsAllowed(string address) =>
        SocialEgressGuard.IsBlocked(IPAddress.Parse(address)).Should().BeFalse();

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("[::1]")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("[fd00::1]")]
    public async Task AnAddressLiteralThatIsNotPublic_IsRefused_WithoutAskingTheResolver(string host)
    {
        var resolved = false;
        var guard = new SocialEgressGuard((_, _) =>
        {
            resolved = true;
            return ValueTask.FromResult(Public);
        });

        var act = async () => await guard.ResolveAllowedAsync(host, 443, "https", CancellationToken.None);

        await act.Should().ThrowAsync<SocialEgressRefusedException>();
        resolved.Should().BeFalse("a literal needs no resolution");
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("192.168.1.10")]
    [InlineData("::1")]
    [InlineData("fd12:3456::1")]
    public async Task AHostnameResolvingToAPrivateAddress_IsRefused(string resolvedTo)
    {
        var act = async () => await GuardResolving(resolvedTo).ResolveAllowedAsync("avatars.example", 443, "https", CancellationToken.None);

        await act.Should().ThrowAsync<SocialEgressRefusedException>();
    }

    [Fact]
    public async Task AHostnameWithOneBadAddressAmongGoodOnes_IsRefused_ForTheWholeHost()
    {
        var act = async () => await GuardResolving("93.184.216.34", "10.0.0.7").ResolveAllowedAsync("avatars.example", 443, "https", CancellationToken.None);

        await act.Should().ThrowAsync<SocialEgressRefusedException>("DNS rebinding hides a private address among public ones");
    }

    [Fact]
    public async Task AHostnameThatResolvesToNothing_IsRefused()
    {
        var act = async () => await GuardResolving().ResolveAllowedAsync("nothing.example", 443, "https", CancellationToken.None);

        await act.Should().ThrowAsync<SocialEgressRefusedException>();
    }

    [Fact]
    public async Task APublicHostname_IsAllowed_AndTheCheckedAddressesAreReturned()
    {
        var addresses = await GuardResolving("93.184.216.34", "2606:4700:4700::1111")
            .ResolveAllowedAsync("avatars.example", 443, "https", CancellationToken.None);

        addresses.Select(a => a.ToString()).Should().Equal("93.184.216.34", "2606:4700:4700::1111");
    }

    [Fact]
    public async Task APublicAddressLiteral_IsAllowed()
    {
        var addresses = await new SocialEgressGuard().ResolveAllowedAsync("93.184.216.34", 443, "https", CancellationToken.None);

        addresses.Should().Equal(IPAddress.Parse("93.184.216.34"));
    }

    [Fact]
    public async Task Localhost_ResolvedByTheRealResolver_IsRefused()
    {
        var act = async () => await new SocialEgressGuard().ResolveAllowedAsync("localhost", 443, "https", CancellationToken.None);

        await act.Should().ThrowAsync<SocialEgressRefusedException>();
    }

    [Fact]
    public async Task Http_IsRefused_EvenForAPublicHost()
    {
        var act = async () => await GuardResolving("93.184.216.34").ResolveAllowedAsync("avatars.example", 80, "http", CancellationToken.None);

        (await act.Should().ThrowAsync<SocialEgressRefusedException>()).Which.Message.Should().Contain("https");
    }

    [Theory]
    [InlineData(80)]
    [InlineData(8443)]
    [InlineData(22)]
    public async Task AnyPortButFourFortyThree_IsRefused(int port)
    {
        var act = async () => await GuardResolving("93.184.216.34").ResolveAllowedAsync("avatars.example", port, "https", CancellationToken.None);

        await act.Should().ThrowAsync<SocialEgressRefusedException>();
    }

    [Theory]
    [InlineData("https://127.0.0.1/avatar.png")]
    [InlineData("https://10.0.0.1/avatar.png")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[::1]/avatar.png")]
    [InlineData("https://[fd00::1]/avatar.png")]
    [InlineData("http://93.184.216.34/avatar.png")]
    [InlineData("https://93.184.216.34:8443/avatar.png")]
    public async Task ThroughARealHandler_TheRequestIsRefusedBeforeAnyConnection(string url)
    {
        using var handler = new SocketsHttpHandler { ConnectCallback = new SocialEgressGuard().ConnectAsync, AllowAutoRedirect = false };
        using var client = new HttpClient(handler);

        var act = async () => await client.GetAsync(url);

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        Flatten(thrown.Which).Should().ContainSingle(e => e is SocialEgressRefusedException, "the guard, not a failed connection, said no");
    }

    [Fact]
    public async Task ThroughARealHandler_AHostnameResolvingToAPrivateAddress_IsRefused()
    {
        using var handler = new SocketsHttpHandler { ConnectCallback = GuardResolving("10.1.2.3").ConnectAsync, AllowAutoRedirect = false };
        using var client = new HttpClient(handler);

        var act = async () => await client.GetAsync("https://rebinder.example/avatar.png");

        var thrown = await act.Should().ThrowAsync<HttpRequestException>();
        Flatten(thrown.Which).Should().ContainSingle(e => e is SocialEgressRefusedException);
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            yield return current;
    }
}
