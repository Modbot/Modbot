using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Modbot.Core.Net;

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
    /// <summary>See <see cref="PublicAddresses.IsBlocked"/>. Kept here for the webhook code that already names it.</summary>
    public static bool IsBlocked(IPAddress address) => PublicAddresses.IsBlocked(address);

    /// <summary>See <see cref="PublicAddresses.IsBlockedHost"/>.</summary>
    public static bool IsBlockedHost(string host) => PublicAddresses.IsBlockedHost(host);

    /// <summary>
    /// The <see cref="SocketsHttpHandler.ConnectCallback"/> for the guarded client: resolves the
    /// name, drops every blocked address, and connects to what is left.
    /// </summary>
    public static ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct) =>
        PublicAddresses.ConnectAsync(context, host => new WebhookAddressBlockedException(host), ct);
}