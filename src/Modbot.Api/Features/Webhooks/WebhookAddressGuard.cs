using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Modbot.Api.Features.Webhooks;

/// <summary>Thrown when every address a webhook's host resolves to is one Modbot will not send to.</summary>
public sealed class WebhookAddressBlockedException(string host)
    : Exception($"{host} resolves to a private address.")
{
    public string Host { get; } = host;
}

/// <summary>
/// The addresses a webhook may not reach unless the operator allows it (API keys design §6.7).
/// </summary>
/// <remarks>
/// <para>
/// Without this, anyone who can manage webhooks could make Modbot's server call its own network --
/// the database's admin port, a cloud metadata address -- and read the answer's status in the
/// delivery log.
/// </para>
/// <para>
/// <strong>The check is made when the connection is made</strong>, on the address the name
/// actually resolved to, in <see cref="ConnectAsync"/>. A check at save time alone is beaten by a
/// name that resolves somewhere public on Monday and to <c>169.254.169.254</c> on Tuesday.
/// </para>
/// </remarks>
public static class WebhookAddressGuard
{
    public static bool IsBlocked(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        var b = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return IsBlockedV4(b);

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return true;

        if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None)
            || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
        {
            return true;
        }

        // fc00::/7 unique local.
        if ((b[0] & 0xfe) == 0xfc)
            return true;

        // ::/96 IPv4-compatible (deprecated) -- judge the IPv4 address inside.
        if (b.Take(12).All(x => x == 0))
            return IsBlockedV4(b[12..16]);

        // 64:ff9b::/96 NAT64 carries an IPv4 address in the last four bytes.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.Skip(4).Take(8).All(x => x == 0))
            return IsBlockedV4(b[12..16]);

        // 2002::/16 6to4 carries one in bytes 2-5.
        if (b[0] == 0x20 && b[1] == 0x02)
            return IsBlockedV4(b[2..6]);

        // 100::/64 discard, 2001:db8::/32 documentation.
        if (b[0] == 0x01 && b[1] == 0x00 && b.Skip(2).Take(6).All(x => x == 0))
            return true;

        return b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8;
    }

    private static bool IsBlockedV4(byte[] b) => b[0] switch
    {
        0 => true,                                  // "this network"
        10 => true,                                 // private
        127 => true,                                // loopback
        100 => b[1] is >= 64 and <= 127,            // carrier-grade NAT
        169 => b[1] == 254,                         // link-local, including cloud metadata
        172 => b[1] is >= 16 and <= 31,             // private
        192 => (b[1] == 168)                        // private
               || (b[1] == 0 && b[2] is 0 or 2),    // IETF protocol assignments, documentation
        198 => b[1] is 18 or 19                     // benchmarking
               || (b[1] == 51 && b[2] == 100),      // documentation
        203 => b[1] == 0 && b[2] == 113,            // documentation
        >= 224 => true,                             // multicast, reserved, broadcast
        _ => false,
    };

    /// <summary>
    /// Whether a host name is one Modbot refuses without resolving it: <c>localhost</c>, or an
    /// address literal in a blocked range.
    /// </summary>
    public static bool IsBlockedHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var trimmed = host.Trim('[', ']').TrimEnd('.');

        if (trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(trimmed, out var literal) && IsBlocked(literal);
    }

    /// <summary>
    /// The <see cref="SocketsHttpHandler.ConnectCallback"/> for the guarded client: resolves the
    /// name, drops every blocked address, and connects to what is left.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, ct);

        var allowed = addresses.Where(a => !IsBlocked(a)).ToArray();
        if (allowed.Length == 0)
            throw new WebhookAddressBlockedException(host);

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
