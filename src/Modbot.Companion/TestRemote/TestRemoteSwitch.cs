using Modbot.Companion.Startup;

namespace Modbot.Companion.TestRemote;

/// <summary>
/// Whether this copy has a test remote at all: only a test copy that was also started in debug
/// mode.
/// </summary>
/// <remarks>
/// <para><strong>Two switches, both needed.</strong> <c>MODBOT_DATA_FOLDER</c> makes a test copy
/// with its own folder (<see cref="DataFolder"/>), and <c>MODBOT_DEBUG_MODE=1</c> adds the Debug
/// page. The remote exists only with both: the real copy never has one, and neither does a test
/// copy started without debug mode. Without it nothing listens and none of its code runs.</para>
/// <para><strong>Local to this Windows account.</strong> The remote is a named pipe, named after
/// the test copy's folder, that only the same account can open (a Unix socket only the same user
/// can open on Linux). A pipe never crosses the network; nothing leaves the machine through it.</para>
/// </remarks>
public static class TestRemoteSwitch
{
    /// <summary>The environment variable that adds the Debug page.</summary>
    public const string DebugModeVariable = "MODBOT_DEBUG_MODE";

    /// <summary>How the remote's pipe name starts; the rest is worked out from the folder.</summary>
    public const string PipePrefix = "modbot-companion-remote-";

    /// <summary>Whether a value of <see cref="DebugModeVariable"/> turns debug mode on: <c>1</c> or <c>true</c>.</summary>
    public static bool DebugModeOn(string? value)
        => value is { } set && (set == "1" || set.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>The pipe the remote listens on, or null when this copy has no remote.</summary>
    public static string? PipeName(DataFolder data, bool debugMode)
    {
        ArgumentNullException.ThrowIfNull(data);

        return data.IsTestCopy && debugMode ? data.RemotePipeName : null;
    }

    /// <summary>
    /// The same answer worked out from the environment, the way the companion reads it at start.
    /// Null when either variable is missing, or when the folder is one a test copy may not use.
    /// </summary>
    public static string? PipeName(Func<string, string?> environment, string applicationData)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var choice = DataFolder.Choose(environment, applicationData);
        return choice.Folder is { } folder ? PipeName(folder, DebugModeOn(environment(DebugModeVariable))) : null;
    }

    /// <summary>
    /// The pipe for a test copy's folder, as the command line tool works it out: the folder given
    /// as if it were <c>MODBOT_DATA_FOLDER</c>. Null, with why, for the real folder or a bad one.
    /// </summary>
    public static (string? Pipe, string? Refusal) PipeForFolder(string folder, string applicationData)
    {
        var choice = DataFolder.Choose(
            name => name == DataFolder.Variable ? folder : null,
            applicationData);

        if (choice.Folder is not { } data)
            return (null, choice.Refusal);

        return data.RemotePipeName is { } pipe
            ? (pipe, null)
            : (null, "That is not a test copy's folder.");
    }
}
