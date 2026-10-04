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
/// installed copy and the installed copy never hands anything to it. It refuses to start on the
/// real folder, which would make it the real copy with half its manners missing.</para>
/// <para><strong>What this reads.</strong> One environment variable. It writes nothing and sends
/// nothing; the folder is not even created here.</para>
/// </remarks>
/// <param name="Path">The folder itself, as a full path.</param>
/// <param name="IsTestCopy">True when <see cref="Variable"/> chose it.</param>
public sealed record DataFolder(string Path, bool IsTestCopy)
{
    /// <summary>The environment variable that makes a test copy.</summary>
    public const string Variable = "MODBOT_DATA_FOLDER";

    /// <summary>The one-copy lock's name for the real copy. Under <c>Local\</c>, so each Windows account has its own.</summary>
    public const string RealSingleInstanceName = @"Local\Modbot.Companion";

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

        if (Same(chosen, real))
        {
            return new DataFolderChoice(
                null,
                $"{Variable} names the real Modbot folder ({real}). Pick another folder for a test copy.");
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

    private static string Full(string path)
        => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));

    /// <summary>Windows folder names ignore case; elsewhere they do not.</summary>
    private static bool Same(string a, string b)
        => string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

/// <param name="Folder">The folder to use, or null when this copy must not start.</param>
/// <param name="Refusal">Why it must not start, in a sentence, or null.</param>
public sealed record DataFolderChoice(DataFolder? Folder, string? Refusal);
