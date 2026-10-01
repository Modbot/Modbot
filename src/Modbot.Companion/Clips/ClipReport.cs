using System.Security.Cryptography;

namespace Modbot.Companion.Clips;

/// <summary>
/// A clip the moderator saved, as a paired server may be told about it: where, when, by whom, and
/// the file's fingerprint. Never the file.
/// </summary>
/// <param name="ModeratorId">The moderator's own VRChat id, as VRChat's log gave it. Opaque.</param>
/// <param name="ModeratorName">Their display name at the time, when the log gave one.</param>
/// <param name="WorldId">The world they were in when they pressed Save.</param>
/// <param name="InstanceId">The instance, as VRChat named it. Hostile text; never trusted.</param>
/// <param name="GroupId">The group that owns the instance: the one server allowed to hear of it.</param>
/// <param name="SavedAt">When Save was pressed, from the companion's clock.</param>
/// <param name="Hash">SHA-256 of the saved file, lowercase hex.</param>
/// <param name="Bytes">The saved file's size.</param>
public sealed record ClipReport(
    string ModeratorId,
    string? ModeratorName,
    string WorldId,
    string InstanceId,
    string GroupId,
    DateTimeOffset SavedAt,
    string Hash,
    long Bytes);

/// <summary>
/// The fingerprint of a clip this program saved: its SHA-256 and its size.
/// </summary>
/// <remarks>
/// <para><strong>What this reads.</strong> One file: the clip the companion itself just wrote into
/// the clips folder, opened for reading once, start to end, to work out its SHA-256. Nothing else
/// on the disk, and nothing about the file but its bytes and its length.</para>
/// <para><strong>What leaves the machine.</strong> Nothing from here. The fingerprint is sent to the
/// group's paired server only when the moderator ticked <strong>Tell the group's Modbot when I save
/// a clip</strong>, and then as two fields of one event (<c>CompanionEvent</c>, type
/// <c>ClipSaved</c>). A fingerprint cannot be turned back into the video; it only lets the server
/// recognise the same file if the moderator later attaches it to a case in a browser (clips design
/// spec §16).</para>
/// </remarks>
public static class ClipFingerprint
{
    /// <summary>
    /// The SHA-256 and size of the file at <paramref name="path"/>, or null when it cannot be read
    /// — gone already, locked, or on a drive that went away. A clip that cannot be fingerprinted is
    /// still saved; the server is simply not told.
    /// </summary>
    public static async Task<(string Hash, long Bytes)?> OfAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16, useAsync: true);

            var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            return (Convert.ToHexStringLower(hash), stream.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
