namespace Modbot.Client.Pairing;

/// <summary>
/// The one rule about where this client will talk to: HTTPS, or plain HTTP only to this machine.
/// </summary>
/// <remarks>
/// <para>Plain HTTP to anywhere else is refused rather than warned about. Presence data crossing a
/// home network, a café, or a captive portal in clear text is not a risk worth a checkbox, and a
/// warning that can be clicked past is a warning that will be.</para>
/// <para>Loopback is the exception because nothing on it crosses a network: a developer or a
/// tester running Modbot on their own PC at <c>http://localhost:8080</c> is not exposing anything
/// to anyone, and refusing them teaches nothing. <c>localhost</c>, <c>127.0.0.1</c> and <c>::1</c>
/// all count; a LAN address does not.</para>
/// <para>One place, so the check cannot drift between the address a token names, the pairing
/// request, and the stored pairing.</para>
/// </remarks>
public static class ServerAddresses
{
    public static bool IsAllowed(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (!address.IsAbsoluteUri || address.Host.Length == 0)
            return false;

        if (address.Scheme == Uri.UriSchemeHttps)
            return true;

        return address.Scheme == Uri.UriSchemeHttp && address.IsLoopback;
    }

    /// <summary>The sentence shown when <see cref="IsAllowed"/> is false.</summary>
    public static string Refusal(Uri address)
        => $"Modbot servers must be reached over HTTPS; '{address}' is not.";
}
