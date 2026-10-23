using System.Net;
using System.Net.Sockets;

namespace Croft.Api.Social;

/// <summary>The request was refused before any connection was made: the address is not one a public service may be asked for.</summary>
public sealed class SocialEgressRefusedException(string message) : HttpRequestException(message);

/// <summary>
/// The SSRF guard of the three social clients (brief 12a). Avatar URLs come from remote servers, so a hostile account could point one at the
/// cluster's own network (the metadata service, a database, another pod). The guard is the <c>SocketsHttpHandler.ConnectCallback</c>: it
/// resolves the host itself, refuses the whole host if any answer is loopback, private (RFC 1918, ULA), link-local, CGNAT, multicast, reserved or
/// unspecified, and connects to an address it checked: there is no second resolution a DNS rebind could change. Only https, only port 443.
/// </summary>
public sealed class SocialEgressGuard(Func<string, CancellationToken, ValueTask<IPAddress[]>>? resolver = null)
{
    private readonly Func<string, CancellationToken, ValueTask<IPAddress[]>> _resolve = resolver ?? ResolveWithDnsAsync;

    private static async ValueTask<IPAddress[]> ResolveWithDnsAsync(string host, CancellationToken ct) => await Dns.GetHostAddressesAsync(host, ct);

    /// <summary>True for every address a public service must never be asked at: anything that is not a routable unicast address of the open internet.</summary>
    public static bool IsBlocked(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsBlockedV4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsBlockedV6(address),
            _ => true,
        };
    }

    private static bool IsBlockedV4(byte[] b) =>
        b[0] == 0                                          // 0.0.0.0/8: unspecified and "this network"
        || b[0] == 10                                      // 10.0.0.0/8
        || b[0] == 127                                     // loopback
        || (b[0] == 100 && (b[1] & 0xC0) == 64)            // 100.64.0.0/10: carrier-grade NAT
        || (b[0] == 169 && b[1] == 254)                    // link-local, the cloud metadata address included
        || (b[0] == 172 && b[1] is >= 16 and <= 31)        // 172.16.0.0/12
        || (b[0] == 192 && b[1] == 168)                    // 192.168.0.0/16
        || b[0] >= 224;                                    // multicast, reserved and the broadcast address

    private static bool IsBlockedV6(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6None)
            || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
            || (b[0] & 0xFE) == 0xFC) // fc00::/7: unique local
            return true;

        // Addresses that carry an IPv4 address inside: judged by it. IPv4-compatible (::a.b.c.d), NAT64 (64:ff9b::/96), 6to4 (2002::/16).
        if (b.Take(12).All(x => x == 0))
            return IsBlockedV4(b[12..16]);
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b.Skip(4).Take(8).All(x => x == 0))
            return IsBlockedV4(b[12..16]);
        if (b[0] == 0x20 && b[1] == 0x02)
            return IsBlockedV4(b[2..6]);

        return false;
    }

    /// <summary>
    /// The addresses of <paramref name="host"/> that may be connected to, or a <see cref="SocialEgressRefusedException"/>: not https, not port 443,
    /// a host that resolves to nothing, or one with any non-public address among its answers.
    /// </summary>
    public async ValueTask<IPAddress[]> ResolveAllowedAsync(string host, int port, string scheme, CancellationToken ct)
    {
        if (!string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new SocialEgressRefusedException("Only https is allowed.");
        if (port != 443)
            throw new SocialEgressRefusedException("Only port 443 is allowed.");

        host = host.Trim('[', ']');
        IPAddress[] addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await _resolve(host, ct);

        if (addresses.Length == 0)
            throw new SocialEgressRefusedException("The host resolves to no address.");
        if (addresses.Any(IsBlocked))
            throw new SocialEgressRefusedException("The host resolves to an address that is not public.");

        return addresses;
    }

    /// <summary>The <c>ConnectCallback</c>: checks, then connects to a checked address and hands the stream to the handler (which does the TLS).</summary>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var endpoint = context.DnsEndPoint;
        var scheme = context.InitialRequestMessage?.RequestUri?.Scheme ?? string.Empty;
        var addresses = await ResolveAllowedAsync(endpoint.Host, endpoint.Port, scheme, ct);

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException("Could not connect to the host.", last);
    }
}
