namespace Modbot.Core.Files;

/// <summary>One picture, as bytes, with what the bytes are.</summary>
/// <param name="ContentType">What the host said the bytes are, e.g. <c>image/png</c>.</param>
public sealed record PictureBytes(byte[] Bytes, string ContentType);

/// <summary>
/// Fetches a VRChat picture as bytes, for somewhere that cannot use a VRChat address directly.
/// </summary>
/// <remarks>
/// <para>
/// VRChat's hosts refuse a request with no session, so the address in a stored row is an address
/// only Modbot can follow. The web app gets around that with <c>/api/files/vrchat</c>, which needs
/// a signed-in caller and a reachable server. Discord's servers are neither, so a Discord card
/// that wants a face has to send the bytes -- and this is where they come from.
/// </para>
/// <para>
/// <strong>Why this is an interface in Core rather than a call to the gate.</strong> The Discord
/// side is built out of rows that the VRChat side wrote and asks VRChat nothing; that is why
/// <c>Modbot.Discord</c> has never referenced <c>Modbot.VRChat</c>. Pictures are the one thing it
/// cannot get from a row, so the rule is narrowed rather than dropped: the Discord side asks for
/// a picture by address and knows nothing about sessions, hosts or cookies, and the one
/// implementation still goes through <c>IVRChatGate</c> like everything else that touches VRChat
/// (foundation design §4.1). VRChat's file addresses are not paced and belong to no bucket -- the
/// maintainer confirmed on 2026-09-17 that they are not rate limited -- so a card's picture costs
/// no API budget.
/// </para>
/// </remarks>
public interface IPictures
{
    /// <summary>
    /// The bytes behind one picture address, or null when there are none to be had: a blank
    /// address, an address VRChat does not serve, no VRChat session, a file that is gone, a file
    /// that is not a picture, or a picture too large to pass on. A caller that gets null shows no
    /// picture; none of these is worth failing a post over.
    /// </summary>
    Task<PictureBytes?> FetchAsync(string? url, CancellationToken ct);
}
