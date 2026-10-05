using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Modbot.Companion.Pairing;

namespace Modbot.Companion.Startup;

/// <summary>
/// Where this copy of the companion keeps everything it keeps: settings, pairings, logs, queues,
/// the sent journal, the cloud backup's batches and the downloaded voice and phrase models.
/// </summary>
/// <remarks>
/// <para><strong>Normally <c>%APPDATA%\Modbot</c>.</strong> Setting <c>MODBOT_DATA_FOLDER</c> to
/// another folder makes this a test copy: everything goes there instead, so somebody trying the
/// companion out on a PC that already runs a real one never touches its pairings or its
/// settings.</para>
/// <para><strong>A test copy keeps out of the real one's way.</strong> It claims no browser
/// links, adds no start-with-Windows entry, looks for no updates, and takes its own one-copy lock
/// and its own message pipe, named after its folder, so starting it never hands anything to the
/// installed copy and the installed copy never hands anything to it.</para>
/// <para><strong>It refuses the real folder however it is written.</strong> The same folder can
/// be named many ways: a <c>\\?\</c> prefix, an old short name like <c>MODBOT~1</c>, a link or
/// junction pointing at it, or a folder above or below it. Each name is turned into the one
/// folder it really is before the two are compared, and a test copy whose folder is the real one,
/// sits inside it, or holds it, does not start.</para>
/// <para><strong>A test copy sends nothing of its own.</strong> No event backup to Modbot Cloud,
/// no Cloud install, no Credits read, no voice or phrase model download and no update check. The
/// only requests it can make are to a server somebody paired in the test copy's own folder.</para>
/// <para><strong>What this reads.</strong> One environment variable, and, to compare folders, the
/// names on disk: which folders exist, where a link points and a short name's long form. It writes
/// nothing and sends nothing; the folder is not even created here.</para>
/// </remarks>
/// <param name="Path">The folder itself, as a full path.</param>
/// <param name="IsTestCopy">True when <see cref="Variable"/> chose it.</param>
public sealed record DataFolder(string Path, bool IsTestCopy)
{
    /// <summary>The environment variable that makes a test copy.</summary>
    public const string Variable = "MODBOT_DATA_FOLDER";

    /// <summary>The one-copy lock's name for the real copy. Under <c>Local\</c>, so each Windows account has its own.</summary>
    public const string RealSingleInstanceName = @"Local\Modbot.Companion";

    /// <summary>How many links in a row are followed before giving up on a name.</summary>
    private const int MostLinks = 16;

    /// <summary>The real copy's folder: <c>Modbot</c> under the roaming application data folder.</summary>
    public static string Real(string applicationData)
        => System.IO.Path.Combine(applicationData, "Modbot");

    /// <summary>
    /// The folder this copy uses, or why it must not start.
    /// </summary>
    /// <param name="environment">Reads one environment variable.</param>
    /// <param name="applicationData">The roaming application data folder.</param>
    public static DataFolderChoice Choose(Func<string, string?> environment, string applicationData)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationData);

        var real = Full(Real(applicationData));

        if (environment(Variable) is not { } asked || string.IsNullOrWhiteSpace(asked))
            return new DataFolderChoice(new DataFolder(real, IsTestCopy: false), null);

        string chosen;
        try
        {
            chosen = Full(asked.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new DataFolderChoice(null, $"{Variable} is not a folder Windows can use: {asked}");
        }

        var realAsItIs = TheFolderItIs(real);
        var chosenAsItIs = TheFolderItIs(chosen);

        if (Same(chosenAsItIs, realAsItIs) || Inside(chosenAsItIs, realAsItIs) || Inside(realAsItIs, chosenAsItIs))
        {
            return new DataFolderChoice(
                null,
                $"{Variable} is the real Modbot folder ({real}), or inside it, or holds it. Pick another folder for a test copy.");
        }

        return new DataFolderChoice(new DataFolder(chosen, IsTestCopy: true), null);
    }

    /// <summary>The one-copy lock: the real name, or one of the test copy's own.</summary>
    public string SingleInstanceName
        => IsTestCopy ? RealSingleInstanceName + ".Test." + Key : RealSingleInstanceName;

    /// <summary>The pipe a second copy hands a link or "show the window" through.</summary>
    public string PipeName
        => IsTestCopy ? PairingLinkInbox.DefaultPipeName + "-test-" + Key : PairingLinkInbox.DefaultPipeName;

    /// <summary>
    /// Whether this copy may talk to Modbot Cloud at all: the event backup, the Cloud install it
    /// registers and the Credits page's read. Never in a test copy, whatever <c>settings.json</c>
    /// or the environment say, so a copy being tried out never sends a real VRChat log's events
    /// anywhere.
    /// </summary>
    public bool MayUseCloud => !IsTestCopy;

    /// <summary>
    /// Whether this copy may download the voice or the phrase model. Never in a test copy, which
    /// makes no request of its own; only a server paired in the test copy's own folder is talked to.
    /// </summary>
    public bool MayDownload => !IsTestCopy;

    /// <summary>What a test copy says where a download would have started.</summary>
    public const string NoDownloadInATestCopy = "A test copy downloads nothing.";

    /// <summary>
    /// The Cloud settings this copy goes by: as read for the real copy, and switched off for a test
    /// copy as if <c>cloud.disabled</c> were true.
    /// </summary>
    public CloudBackup.CloudSettings CloudFor(CloudBackup.CloudSettings read)
    {
        ArgumentNullException.ThrowIfNull(read);

        return MayUseCloud ? read : read with { Disabled = true };
    }

    /// <summary>
    /// The test remote's pipe, named after the folder like the lock and the message pipe. Null for
    /// the real copy, which never has one (<see cref="TestRemote.TestRemoteSwitch"/>).
    /// </summary>
    public string? RemotePipeName
        => IsTestCopy ? TestRemote.TestRemoteSwitch.PipePrefix + Key : null;

    /// <summary>
    /// A short name worked out from the folder, so two test copies in two folders keep apart and
    /// the same folder always gets the same name.
    /// </summary>
    private string Key
    {
        get
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.ToUpperInvariant()));
            return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
        }
    }

    /// <summary>A full path with no <c>\\?\</c> prefix, no <c>..</c> and no separator at the end.</summary>
    private static string Full(string path)
        => System.IO.Path.TrimEndingDirectorySeparator(Unprefixed(System.IO.Path.GetFullPath(Unprefixed(path))));

    private static string Unprefixed(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            ? path[4..]
            : path;
    }

    /// <summary>
    /// The folder a full path really names: every link or junction along it followed, and on
    /// Windows every short name made long. The parts that do not exist yet are kept as written.
    /// </summary>
    internal static string TheFolderItIs(string full)
    {
        var path = full;

        for (var hop = 0; hop < MostLinks; hop++)
        {
            var followed = FollowFirstLink(path);
            if (followed is null)
                break;

            path = followed;
        }

        return LongName(path);
    }

    /// <summary>
    /// The path with its first link, from the root down, replaced by where it points; null when no
    /// part of it is a link.
    /// </summary>
    private static string? FollowFirstLink(string path)
    {
        var root = System.IO.Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return null;

        var parts = path[root.Length..].Split(
            [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            current = System.IO.Path.Combine(current, parts[i]);

            try
            {
                var info = new DirectoryInfo(current);
                if (info.LinkTarget is null)
                {
                    if (!info.Exists)
                        return null;

                    continue;
                }

                if (info.ResolveLinkTarget(returnFinalTarget: true) is not { } target)
                    return null;

                var rest = parts.Skip(i + 1).ToArray();
                return Full(rest.Length == 0 ? target.FullName : System.IO.Path.Combine([target.FullName, .. rest]));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>On Windows, the long form of the part of the path that exists, with the rest as written.</summary>
    private static string LongName(string path)
    {
        if (!OperatingSystem.IsWindows())
            return path;

        var existing = path;
        var rest = new Stack<string>();
        while (!System.IO.Path.Exists(existing))
        {
            var parent = System.IO.Path.GetDirectoryName(existing);
            if (parent is null)
                return path;

            rest.Push(System.IO.Path.GetFileName(existing));
            existing = parent;
        }

        var buffer = new StringBuilder(1024);
        var length = GetLongPathNameW(existing, buffer, (uint)buffer.Capacity);
        if (length == 0 || length >= buffer.Capacity)
            return path;

        var result = Unprefixed(buffer.ToString());
        while (rest.Count > 0)
            result = System.IO.Path.Combine(result, rest.Pop());

        return System.IO.Path.TrimEndingDirectorySeparator(result);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint size);

    /// <summary>Windows folder names ignore case; elsewhere they do not.</summary>
    private static StringComparison Case
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool Same(string a, string b) => string.Equals(a, b, Case);

    /// <summary>Whether <paramref name="inner"/> sits somewhere under <paramref name="outer"/>.</summary>
    private static bool Inside(string inner, string outer)
    {
        var withSeparator = System.IO.Path.EndsInDirectorySeparator(outer)
            ? outer
            : outer + System.IO.Path.DirectorySeparatorChar;

        return inner.StartsWith(withSeparator, Case);
    }
}

/// <param name="Folder">The folder to use, or null when this copy must not start.</param>
/// <param name="Refusal">Why it must not start, in a sentence, or null.</param>
public sealed record DataFolderChoice(DataFolder? Folder, string? Refusal);
