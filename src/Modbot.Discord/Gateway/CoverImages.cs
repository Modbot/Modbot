using Modbot.Core.Files;
using Modbot.Core.Net;

namespace Modbot.Discord.Gateway;

/// <summary>
/// Fetches a calendar event's picture link for a Discord event cover (calendar design §3.2, §15.2).
/// </summary>
/// <remarks>
/// <para>
/// Discord wants the picture's bytes, not its address, so Modbot has to fetch what a moderator
/// typed. Since 2026-10-02 that goes the way the form's crop box gets it (<see cref="PictureLinks"/>):
/// https only, public addresses only, checked on the address the name resolved to when connecting,
/// each redirect checked again, a page read only as far as its own picture, and the bytes taken for
/// what they are rather than what the host called them. Before, a link to a page or a host that
/// called a picture something else gave no cover.
/// </para>
/// <para>
/// A VRChat file link is fetched through the VRChat side (<see cref="IPictures"/>), because VRChat
/// serves its files only to a signed-in session, and a link pasted from VRChat's site is the usual
/// way a group's picture arrives.
/// </para>
/// <para>
/// Discord takes PNG, JPEG, GIF and WebP. Modbot's server has no way to turn other kinds into one
/// of those, so a cover in another kind is left off; the form turns a picture into a PNG in the
/// browser before anything is uploaded. A cover that cannot be fetched is left off rather than
/// failing the event: the event matters, the picture does not.
/// </para>
/// </remarks>
internal static class CoverImages
{
    /// <summary>Discord's own limit for an event cover is 10 MB; less is plenty for a picture.</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    public static async Task<Cover?> FetchAsync(string? url, IPictures? pictures, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var address)
            || PictureLinks.Problem(address) is not null)
        {
            return null;
        }

        PictureBytes? picture;

        if (VRChatFileIds.IsVRChatFileHost(address.Host))
        {
            picture = pictures is null ? null : await pictures.FetchAsync(address.ToString(), ct).ConfigureAwait(false);
        }
        else
        {
            var fetched = await PictureLinks.Shared.FetchAsync(address.ToString(), ct).ConfigureAwait(false);
            picture = fetched.Picture;
        }

        if (picture is null
            || picture.Bytes.Length is 0 or > MaxBytes
            || !PictureFormats.DiscordTakes(PictureFormats.Sniff(picture.Bytes)))
        {
            return null;
        }

        return new Cover(new MemoryStream(picture.Bytes, writable: false));
    }

    /// <summary>The fetched picture, kept open until Discord has been sent it.</summary>
    public sealed class Cover(MemoryStream bytes) : IDisposable
    {
        public global::Discord.Image Image { get; } = new(bytes);

        public void Dispose() => bytes.Dispose();
    }
}
