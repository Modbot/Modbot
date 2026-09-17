namespace Modbot.Companion.LogReading;

/// <summary>
/// Where VRChat's log folder is on this machine.
/// </summary>
/// <remarks>
/// <para><strong>What this reads.</strong> Whether each well-known folder exists, and nothing in
/// it: the log itself is read by <see cref="VRChatLogTail"/>, and which folder was picked is shown
/// in the window. Nothing here leaves the machine.</para>
/// <para><strong>Well-known places, never another program's configuration.</strong> On Windows
/// VRChat writes under the user's profile. On Linux it runs under Proton, and Proton keeps each
/// game's Windows profile inside the Steam library, under the game's own id (438100), so the same
/// folder is there -- inside a prefix, inside whichever Steam install this machine has: the
/// ordinary one, the Flatpak, the Snap, or the older <c>~/.steam</c> layout. A plain Wine prefix is
/// tried last. Reading Steam's <c>libraryfolders.vdf</c> to find a library on another drive would
/// find more, and is exactly the behaviour that makes a tool like this look like something worse
/// than it is (M3 2.3.1); a library somewhere else is what the setting is for.</para>
/// <para><strong>The setting wins.</strong> A folder named in <c>settings.json</c> is used as
/// written, whether or not it exists yet: a person who typed a path meant that path, and a
/// folder that appears once VRChat starts is the ordinary case, not a mistake.</para>
/// </remarks>
public static class VRChatLogFolders
{
    /// <summary>VRChat's Steam app id, which names its Proton prefix.</summary>
    public const string SteamAppId = "438100";

    private static readonly string[] WindowsTail = ["AppData", "LocalLow", "VRChat", "VRChat"];

    /// <summary>
    /// The folders VRChat is known to write to, most likely first, for a machine with this home
    /// folder. Pure, so the list can be checked without a machine to check it on.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string home, bool windows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);

        if (windows)
            return [Path.Combine([home, .. WindowsTail])];

        string[] steamRoots =
        [
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam", "root"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
            Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"),
        ];

        var candidates = new List<string>(steamRoots.Length + 1);
        foreach (var root in steamRoots)
        {
            candidates.Add(Path.Combine(
                [root, "steamapps", "compatdata", SteamAppId, "pfx", "drive_c", "users", "steamuser", .. WindowsTail]));
        }

        // A VRChat installed straight into Wine rather than through Steam. Wine names the user
        // after the account, not "steamuser".
        candidates.Add(Path.Combine([home, ".wine", "drive_c", "users", Path.GetFileName(home), .. WindowsTail]));

        return candidates;
    }

    /// <summary>The candidates for this machine.</summary>
    public static IReadOnlyList<string> Candidates()
        => Candidates(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), OperatingSystem.IsWindows());

    /// <summary>
    /// The folder to watch: the configured one when there is one, otherwise the first well-known
    /// folder that exists, otherwise the first well-known folder -- so the window can name the
    /// place it is waiting for VRChat to write to.
    /// </summary>
    public static string Resolve(string? configured, IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        return candidates.Count > 0 ? candidates[0] : string.Empty;
    }

    public static string Resolve(string? configured) => Resolve(configured, Candidates());
}
