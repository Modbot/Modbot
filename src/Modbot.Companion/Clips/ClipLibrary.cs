using Modbot.Core.Time;

namespace Modbot.Companion.Clips;

/// <summary>One saved clip sitting in the clips folder.</summary>
public readonly record struct SavedClip(string Path, long Bytes, DateTimeOffset SavedAt);

/// <summary>
/// The clips folder's contents: what is in it, and what has to go when it gets too big.
/// </summary>
/// <remarks>
/// <para><strong>What this reads and writes.</strong> One folder — the clips folder chosen on the
/// settings screen — and inside it only the <c>.mp4</c> files Modbot itself wrote. It lists them to
/// add up how much room they take, and deletes the oldest of them when saving another would put the
/// folder over the limit in settings. It never opens a clip, never reads any other kind of file, and
/// never touches anything outside that one folder.</para>
/// <para><strong>Nothing leaves the machine.</strong> No clip, no file name and no count is sent
/// anywhere. Attaching a clip to a case is something a moderator does afterwards, in Modbot's web
/// interface, in a browser, by choosing the file — the client has no way to upload one and does not
/// gain one here (clips design spec, §6).</para>
/// <para><strong>Why anything is deleted at all.</strong> Video is large and a rolling recorder that
/// only ever adds will eventually fill a moderator's disk, which would be Modbot breaking the
/// machine it was installed to help. The limit is in settings, the oldest go first, and a clip is
/// never deleted to make room for itself.</para>
/// </remarks>
public sealed class ClipLibrary
{
    /// <summary>The only kind of file Modbot writes here, and the only kind it counts or deletes.</summary>
    public const string ClipExtension = ".mp4";

    private readonly IModbotClock _clock;

    public ClipLibrary(IModbotClock clock) => _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// The clips in the folder, oldest first. An unreadable or missing folder is empty, not an
    /// error: the moderator may have moved or deleted it while the client was running.
    /// </summary>
    public IReadOnlyList<SavedClip> List(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return [];

        var clips = new List<SavedClip>();

        try
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                if (!string.Equals(Path.GetExtension(path), ClipExtension, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var file = new FileInfo(path);
                    clips.Add(new SavedClip(path, file.Length, file.LastWriteTimeUtc));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Deleted or locked between listing and asking. Not worth a word.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        clips.Sort((a, b) => a.SavedAt.CompareTo(b.SavedAt));
        return clips;
    }

    /// <summary>How much room the clips in the folder take, in bytes.</summary>
    public long Bytes(string folder)
    {
        long total = 0;
        foreach (var clip in List(folder))
            total += clip.Bytes;

        return total;
    }

    /// <summary>
    /// Deletes the oldest clips until the folder, plus <paramref name="aboutToAdd"/> more bytes,
    /// fits inside the limit. Returns how many were deleted.
    /// </summary>
    /// <remarks>
    /// The newest clip is never deleted, however large it is. A limit smaller than one clip would
    /// otherwise delete the thing that was just saved, which is the one outcome a moderator who
    /// pressed Save must not get.
    /// </remarks>
    public int MakeRoom(string folder, long limitBytes, long aboutToAdd = 0)
    {
        if (limitBytes <= 0)
            return 0;

        var clips = List(folder);
        if (clips.Count == 0)
            return 0;

        long total = aboutToAdd;
        foreach (var clip in clips)
            total += clip.Bytes;

        var deleted = 0;

        // Oldest first, and never the last one standing.
        for (var index = 0; index < clips.Count - 1 && total > limitBytes; index++)
        {
            try
            {
                File.Delete(clips[index].Path);
                total -= clips[index].Bytes;
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Open in a video player, most likely. Leave it and try the next one.
            }
        }

        return deleted;
    }

    /// <summary>
    /// What to call a clip saved now: the date and time, and the instance it was saved in when one
    /// is known.
    /// </summary>
    /// <remarks>
    /// Sortable, readable, and made only of characters a Windows file name may hold — an instance id
    /// is VRChat's text and is never trusted to be a file name (foundation 3.1.1: ids follow no
    /// structure), so anything unusual in it becomes an underscore.
    /// </remarks>
    public string NameFor(string? about = null)
    {
        var moment = _clock.UtcNow.ToLocalTime();
        var name = moment.ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(about))
        {
            var safe = new string([.. about.Trim()
                .Take(60)
                .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_')]);

            if (safe.Trim().Length > 0)
                name = $"{name} {safe.Trim()}";
        }

        return name + ClipExtension;
    }
}
