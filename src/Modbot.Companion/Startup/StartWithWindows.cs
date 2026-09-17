namespace Modbot.Companion.Startup;

/// <summary>
/// The per-user startup entry, behind an interface so the rules can be tested without a registry.
/// </summary>
public interface IStartupRegistry
{
    /// <summary>This client's command in the current user's startup list, or null when there is none.</summary>
    string? ReadCommand();

    void WriteCommand(string command);

    void DeleteCommand();

    /// <summary>Whether the user turned Modbot off in Task Manager → Startup apps.</summary>
    bool IsTurnedOffInWindows();
}

/// <summary>How the "Start Modbot Companion when my computer starts" switch should look.</summary>
/// <param name="Visible">Only an installed copy shows the switch.</param>
/// <param name="On">Whether Modbot will start with Windows.</param>
/// <param name="TurnedOffInWindows">The user switched it off in Windows' own list; the switch shows off and cannot be turned on here.</param>
public sealed record StartupState(bool Visible, bool On, bool TurnedOffInWindows)
{
    public static StartupState Hidden { get; } = new(false, false, false);
}

/// <summary>
/// "Start Modbot Companion when my computer starts": on by default, for an installed copy only.
/// </summary>
/// <remarks>
/// <para><strong>What this writes.</strong> One value named <c>Modbot</c> under the current user's
/// <c>Software\Microsoft\Windows\CurrentVersion\Run</c> key: the installed launcher's path followed
/// by <see cref="StartArgument"/>, so Windows starts Modbot, in the tray, when you sign in. Per user,
/// no administrator rights. It points at the installer's stable <c>current</c> folder, which updates
/// replace in place, so it keeps working after an update.</para>
/// <para><strong>What this reads.</strong> That value, and Windows' own record of whether you turned
/// Modbot off in Task Manager → Startup apps. When you did, Modbot leaves it off and never turns it
/// back on; the switch shows off.</para>
/// <para><strong>Only an installed copy touches it.</strong> A copy run from source, a build folder
/// or a copied folder hides the switch and never reads or writes the registry, so it cannot leave an
/// entry pointing at a folder that will be deleted. An old entry from an earlier install is left
/// alone until the installed client runs again, which rewrites or removes it.</para>
/// <para>Nothing here is sent anywhere. Uninstalling removes the entry.</para>
/// </remarks>
public sealed class StartWithWindows(IStartupRegistry registry)
{
    /// <summary>What Windows starts Modbot with at sign-in: start in the tray, with no window.</summary>
    public const string StartArgument = "--autostart";

    public static string Command(string launcherPath) => $"\"{launcherPath}\" {StartArgument}";

    /// <summary>Whether this start came from the startup entry and should stay in the tray.</summary>
    public static bool StartsHidden(IEnumerable<string> args) =>
        args.Any(a => string.Equals(a, StartArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Makes the entry match the setting, on every start of an installed copy and whenever the
    /// switch changes, and says how to show the switch.
    /// </summary>
    /// <param name="installed">The installer's own answer that this copy was installed, never a guess from the path.</param>
    /// <param name="launcherPath">The installed launcher, or null when not installed.</param>
    /// <param name="wanted">The setting. On unless turned off.</param>
    public StartupState Apply(bool installed, string? launcherPath, bool wanted)
    {
        if (!installed || string.IsNullOrWhiteSpace(launcherPath))
            return StartupState.Hidden;

        var turnedOffInWindows = registry.IsTurnedOffInWindows();

        if (wanted)
        {
            // Turned off in Windows means left alone: not re-added, not rewritten.
            if (!turnedOffInWindows)
            {
                var command = Command(launcherPath);
                if (!string.Equals(registry.ReadCommand(), command, StringComparison.Ordinal))
                    registry.WriteCommand(command);
            }
        }
        else if (registry.ReadCommand() is not null)
        {
            registry.DeleteCommand();
        }

        return new StartupState(true, wanted && !turnedOffInWindows, turnedOffInWindows);
    }
}
