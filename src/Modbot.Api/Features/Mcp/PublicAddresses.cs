using System.Net;
using System.Net.Sockets;

namespace Modbot.Api.Features.Mcp;

/// <summary>
/// Which addresses a fetch chosen by a stranger may go to: the public internet, and nothing
/// that is this machine, its network, or the ranges nothing on the internet answers from.
/// </summary>
/// <remarks>
/// A client id metadata document is fetched at an address a caller supplies before signing in,
/// so without this the sign-in would fetch anything it was pointed at, including the private
/// services beside this server. The check is made on every address the name resolves to, and
/// again on the address actually connected to, so a name that resolves differently the second
/// time gains nothing.
/// </remarks>
public static class PublicAddresses
{
    /// <summary>Whether a fetch may connect to this address.</summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast))
            return false;

        var bytes = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] switch
            {
                0 => false,                                   // 0.0.0.0/8
                10 => false,                                  // 10/8
                127 => false,                                 // 127/8
                100 when bytes[1] is >= 64 and <= 127 => false, // 100.64/10, carrier NAT
                169 when bytes[1] == 254 => false,            // 169.254/16, link-local and cloud metadata
                172 when bytes[1] is >= 16 and <= 31 => false, // 172.16/12
                192 when bytes[1] == 168 => false,            // 192.168/16
                192 when bytes[1] == 0 && bytes[2] == 0 => false, // 192.0.0/24
                198 when bytes[1] is 18 or 19 => false,       // 198.18/15, benchmarking
                >= 224 => false,                              // multicast and reserved
                _ => true,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal)
                return false;

            // 64:ff9b::/96 and ::ffff:0:0/96 wrap an IPv4 address; judge the address inside.
            if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b)
                return IsPublic(new IPAddress(bytes[12..]));

            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves a host and answers its addresses, or none when any of them is not public: a
    /// name that mixes a public address with a private one is refused whole.
    /// </summary>
    public static async Task<IPAddress[]> ResolvePublicAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host.TrimEnd('.'), ct);
            }
            catch (SocketException)
            {
                return [];
            }
        }

        return addresses.Length > 0 && addresses.All(IsPublic) ? addresses : [];
    }
}
