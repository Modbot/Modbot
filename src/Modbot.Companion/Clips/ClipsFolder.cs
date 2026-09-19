namespace Modbot.Companion.Clips;

/// <summary>Whether the clips folder can actually be used, and what to say when it cannot.</summary>
/// <param name="Path">The folder that was checked.</param>
/// <param name="Problem">
/// One line naming what failed, or null when the folder is ready. Shown on the settings screen; it
/// never stops the client reporting presence.
/// </param>
public readonly record struct ClipsFolderCheck(string Path, string? Problem)
{
    public bool IsUsable => Problem is null;
}

/// <summary>
/// Where saved clips go: the folder a moderator named, or the usual place.
/// </summary>
/// <remarks>
/// <para><strong>What this reads and writes.</strong> One folder on this PC — the one named on the
/// settings screen, or the machine's own Videos folder plus <c>Modbot Clips</c>. It creates that
/// folder if it is missing and writes one small test file into it to find out whether writing works
/// at all, then deletes it again. It reads nothing else, and it never looks at what is already in
/// the folder beyond the clips Modbot itself wrote there (<see cref="ClipLibrary"/>).</para>
/// <para><strong>Nothing leaves the machine.</strong> The folder's address is never sent anywhere.
/// No server is told where clips are kept, or that any exist.</para>
/// <para><strong>Why the Videos folder.</strong> Windows' own Videos location rather than a path
/// spelled out in code, so a machine whose profile lives on another drive, or a localised Windows,
/// lands in the right place. On a machine that has no such folder the client falls back to its own
/// folder under the user profile rather than refusing to work.</para>
/// <para><strong>A folder that cannot be written to is a message, never a crash.</strong> A read-only
/// drive, a removed USB stick or a path with a typo all come back as one plain sentence on the
/// settings screen, and everything else in the client carries on.</para>
/// </remarks>
public static class ClipsFolder
{
    /// <summary>The folder Modbot makes inside the Videos folder.</summary>
    public const string FolderName = "Modbot Clips";

    /// <summary>The name that goes on a test file, and is removed again straight away.</summary>
    private const string WriteTestFileName = ".modbot-write-test";

    /// <summary>
    /// The folder saved clips go into: the one named in settings, or the usual place.
    /// </summary>
    /// <param name="configured">What the settings screen holds; blank means the usual place.</param>
    /// <param name="videos">
    /// The machine's Videos folder. Null asks the operating system for its own. A blank answer —
    /// which is what a machine with no such folder gives — falls back to <paramref name="fallback"/>.
    /// </param>
    /// <param name="fallback">Modbot's own folder under the user profile, used when there is no Videos folder.</param>
    public static string Resolve(string? configured, string? videos = null, string? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        videos ??= VideosFolder();

        if (!string.IsNullOrWhiteSpace(videos))
            return Path.Combine(videos.Trim(), FolderName);

        // No Videos folder on this machine. Modbot's own folder is somewhere it is already allowed
        // to write, which beats refusing to record at all.
        return string.IsNullOrWhiteSpace(fallback)
            ? Path.Combine(Path.GetTempPath(), FolderName)
            : Path.Combine(fallback.Trim(), FolderName);
    }

    /// <summary>
    /// Windows' own Videos location, or the same place on a Linux machine. Empty when the machine
    /// has no such folder, which is an ordinary state on a server-style install.
    /// </summary>
    /// <remarks>
    /// This is the one place in the whole client that names a folder of the moderator's own media.
    /// It names it to <em>write</em> clips the moderator asked for, and never to read what is
    /// already there; <c>CompanionSourceGuardTests</c> fails the build if any other file names it,
    /// and the Pictures, Documents and Desktop folders and VRChat's own screenshot folder stay
    /// banned everywhere, this file included.
    /// </remarks>
    private static string VideosFolder()
    {
        try
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Makes the folder if it is missing and finds out whether a file can actually be written into
    /// it, by writing one and removing it. Never throws.
    /// </summary>
    public static ClipsFolderCheck Check(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return new ClipsFolderCheck(folder ?? string.Empty, "No folder has been chosen.");

        var path = folder.Trim();

        if (!Path.IsPathFullyQualified(path))
            return new ClipsFolderCheck(path, "That is not a full folder path.");

        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new ClipsFolderCheck(path, $"That folder could not be made: {ex.Message}");
        }

        var test = Path.Combine(path, WriteTestFileName);

        try
        {
            // Written and removed rather than merely asked about: the answer that matters is
            // whether a file lands, and a permission check that says yes on a full or read-only
            // drive would be the wrong answer arriving too late to be useful.
            File.WriteAllBytes(test, []);
            File.Delete(test);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new ClipsFolderCheck(path, $"Nothing can be written to that folder: {ex.Message}");
        }

        return new ClipsFolderCheck(path, null);
    }
}
