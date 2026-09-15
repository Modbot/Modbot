using System.Net;
using Microsoft.Extensions.Primitives;

namespace Modbot.Cloud.Common;

/// <summary>
/// The address of the person or server that made a request, through Cloudflare and Railway's edge.
/// </summary>
/// <remarks>
/// <para>
/// The chain in production is visitor → Cloudflare → Railway's edge → this app, so the connection
/// address is always Railway's proxy and never the visitor. Railway's edge adds the address that
/// connected to it as the right-most <c>X-Forwarded-For</c> entry. Everything to the left of that
/// entry was sent by the client and can say anything.
/// </para>
/// <para>The rule, in order:</para>
/// <list type="number">
/// <item>When the right-most <c>X-Forwarded-For</c> entry is a Cloudflare address, the request really
/// came through Cloudflare, which sets <c>CF-Connecting-IP</c> to the visitor. Use that.</item>
/// <item>Otherwise the right-most <c>X-Forwarded-For</c> entry is whoever connected to Railway. Use it.
/// This is the case for anyone calling the Railway address directly, and a <c>CF-Connecting-IP</c>
/// they sent themselves is ignored.</item>
/// <item>With no usable <c>X-Forwarded-For</c>, use the connection address.</item>
/// </list>
/// </remarks>
public static class ClientAddress
{
    public const string ForwardedForHeader = "X-Forwarded-For";
    public const string CloudflareHeader = "CF-Connecting-IP";

    /// <summary>The longest an address is as text: a full IPv6 address with an IPv4 tail.</summary>
    public const int MaxLength = 45;

    /// <summary>
    /// Cloudflare's published ranges, from https://www.cloudflare.com/ips-v4 and /ips-v6, read
    /// 2026-09-14. A Cloudflare edge missing from here makes its visitors look like that edge, which
    /// is wrong but never lets a spoofed header through.
    /// </summary>
    internal static readonly IPNetwork[] CloudflareRanges =
    [
        .. new[]
        {
            "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18",
            "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17",
            "162.158.0.0/15", "104.16.0.0/13", "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
            "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32", "2405:8100::/32",
            "2a06:98c0::/29", "2c0f:f248::/32",
        }.Select(range => IPNetwork.Parse(range)),
    ];

    public static IPAddress? From(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        return Resolve(
            http.Request.Headers[ForwardedForHeader],
            http.Request.Headers[CloudflareHeader],
            http.Connection.RemoteIpAddress);
    }

    public static IPAddress? Resolve(StringValues forwardedFor, StringValues cloudflare, IPAddress? connection)
    {
        if (RightMost(forwardedFor) is { } edge)
        {
            // Cloudflare replaces CF-Connecting-IP with exactly one address, so two values or a
            // malformed one did not come from Cloudflare.
            if (IsCloudflare(edge) && cloudflare.Count == 1 && TryParse(cloudflare[0], out var visitor))
                return visitor;

            return edge;
        }

        return connection is null ? null : Normalise(connection);
    }

    public static bool IsCloudflare(IPAddress address) => CloudflareRanges.Any(range => range.Contains(address));

    /// <summary>The last entry across every <c>X-Forwarded-For</c> header, or null when it is not an address.</summary>
    private static IPAddress? RightMost(StringValues forwardedFor)
    {
        for (var i = forwardedFor.Count - 1; i >= 0; i--)
        {
            var entries = forwardedFor[i]?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (entries is { Length: > 0 })
                return TryParse(entries[^1], out var address) ? address : null;
        }

        return null;
    }

    private static bool TryParse(string? value, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();

        // "203.0.113.9:4711" carries a port some proxies add. IPAddress.TryParse already accepts
        // the bracketed IPv6 form with a port.
        if (!IPAddress.TryParse(text, out var parsed))
        {
            var colon = text.LastIndexOf(':');
            if (colon <= 0 || text.IndexOf(':') != colon || !IPAddress.TryParse(text[..colon], out parsed))
                return false;
        }

        address = Normalise(parsed);
        return true;
    }

    /// <summary>An IPv4 address written as IPv6 (<c>::ffff:1.2.3.4</c>) is kept as the IPv4 address.</summary>
    private static IPAddress Normalise(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
