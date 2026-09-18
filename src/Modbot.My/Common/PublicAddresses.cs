using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Modbot.My.Common;

/// <summary>
/// Connects only to addresses out on the internet.
/// </summary>
/// <remarks>
/// <para>
/// my.modbot.co makes one request to somewhere a stranger chose: it asks a Modbot address what
/// group it moderates before saving anything about it (register details spec 2.2). Anybody can put
/// any address in a register link, so that request must not be usable to reach anything that is not
/// on the public internet — a service on the same host, a neighbour on the private network, or a
/// cloud host's metadata service, which is the classic way a URL somebody supplied turns into a
/// credential somebody else's.
/// </para>
/// <para>
/// The check is on the address actually connected to, not on the name, because a name can resolve
/// to whatever its owner likes and can resolve to something different a second time. The socket is
/// opened here, to one of the addresses this class allowed.
/// </para>
/// </remarks>
public static class PublicAddresses
{
    /// <summary>
    /// The ranges that are not out on the internet: this host, the private networks, carrier-grade
    /// NAT, link-local — which is where cloud metadata services live — multicast and reserved.
    /// </summary>
    private static readonly IPNetwork[] Refused =
    [
        .. new[]
        {
            "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16",
            "172.16.0.0/12", "192.0.0.0/24", "192.168.0.0/16", "198.18.0.0/15", "224.0.0.0/4",
            "240.0.0.0/4",
            "::/128", "::1/128", "fc00::/7", "fe80::/10", "ff00::/8",
        }.Select(IPNetwork.Parse),
    ];

    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // An IPv4 address written as IPv6 is the same address, and has to be checked as one.
        var plain = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        return !Array.Exists(Refused, range => range.Contains(plain));
    }

    /// <summary>
    /// Opens the connection for an <see cref="HttpClient"/>, or refuses when the name leads nowhere
    /// on the public internet.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
        var allowed = Array.FindAll(addresses, IsPublic);

        if (allowed.Length == 0)
            throw new HttpRequestException("That address is not on the public internet.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
