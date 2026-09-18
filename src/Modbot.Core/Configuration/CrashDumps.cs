namespace Modbot.Core.Configuration;

/// <summary>
/// Whether the .NET runtime has been told to write a dump if this process dies outright, and
/// where it would put one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Some ways a process dies leave nothing for Serilog to write.</strong> A fatal runtime
/// error -- a read of memory that is not there, a stack that ran out, a heap the runtime no longer
/// believes -- prints a few lines to standard error and ends the process on the spot. No
/// <c>catch</c> runs, no sink is flushed, and nothing reaches the log files or the log table. An
/// operator reading the dashboard afterwards sees a restart and no reason for it, which is the
/// least useful thing a crash can leave behind.
/// </para>
/// <para>
/// The runtime can write a dump of the whole process at that moment instead, and it is switched on
/// by environment variables it owns rather than by anything Modbot configures. This type only
/// reads them, the way <see cref="HostPlatform"/> reads the host's own variables: nothing here
/// decides anything, and Modbot never sets one for the operator.
/// </para>
/// <para>
/// <strong>Off unless somebody asks for it.</strong> A dump is the size of the process's memory,
/// it is written at the worst possible moment, and on a host that throws the container filesystem
/// away it would be thrown away with it. That is a thing to turn on while chasing a crash, not a
/// thing to leave on.
/// </para>
/// </remarks>
public sealed record CrashDumps(bool On, string? Path, string? Kind)
{
    /// <summary>Set to 1 to have the runtime write a dump when the process dies outright.</summary>
    public const string OnVariable = "DOTNET_DbgEnableMiniDump";

    /// <summary>Where the dump goes. <c>%p</c> in it becomes the process id.</summary>
    public const string PathVariable = "DOTNET_DbgMiniDumpName";

    /// <summary>1 mini, 2 with the heap, 3 triage, 4 the whole process. 2 is the useful one.</summary>
    public const string KindVariable = "DOTNET_DbgMiniDumpType";

    /// <summary>What the runtime has been told, read from the variables it reads.</summary>
    public static CrashDumps Read(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;

        var on = read(OnVariable)?.Trim();

        return new CrashDumps(
            on is "1" or "true" or "True",
            Blank(read(PathVariable)),
            Blank(read(KindVariable)));

        static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>The folder the dump would be written into, or null when none was named.</summary>
    /// <remarks>
    /// The runtime writes the file and does not create the folder above it, so a path into a
    /// folder that does not exist is a crash with nothing to show for it -- exactly the outcome
    /// the setting was turned on to avoid. Modbot makes the folder at startup instead.
    /// </remarks>
    public string? Folder =>
        Path is null ? null : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));

    /// <summary>One sentence for the startup log.</summary>
    public string Explanation
    {
        get
        {
            if (!On)
                return $"Crash dumps are off. Set {OnVariable}=1 and {PathVariable} to collect one.";

            var where = Path ?? "the runtime's default location";
            var kind = Kind is null ? "" : $", type {Kind}";

            return $"Crash dumps are on and go to {where}{kind}.";
        }
    }
}
