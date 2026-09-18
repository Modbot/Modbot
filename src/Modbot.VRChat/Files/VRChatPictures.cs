using Modbot.Core.Files;

namespace Modbot.VRChat.Files;

/// <summary>
/// The one way anything outside <c>Modbot.VRChat</c> gets the bytes behind a VRChat picture
/// address.
/// </summary>
/// <remarks>
/// A thin wrapper on <see cref="IVRChatGate.FetchFileAsync"/> that turns every way VRChat can say
/// no into "no picture". The caller is a Discord card or something like it: there is nothing it
/// could do differently about a missing file, and a card with no picture is a far better answer
/// than a post that did not happen.
/// </remarks>
public sealed class VRChatPictures : IPictures
{
    private readonly IVRChatGate _gate;

    public VRChatPictures(IVRChatGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _gate = gate;
    }

    public async Task<PictureBytes?> FetchAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var address))
            return null;

        if (!VRChatFiles.IsVRChatAddress(address))
            return null;

        var fetched = await _gate.FetchFileAsync(address, ct).ConfigureAwait(false);

        return fetched is { Outcome: VRChatFileOutcome.Fetched, File: { } file }
            ? new PictureBytes(file.Bytes, file.ContentType)
            : null;
    }
}
