using System.Runtime.Versioning;
using Modbot.Companion.Startup;

namespace Modbot.Companion.App;

/// <summary>
/// The Windows side of "Start Modbot Companion when my computer starts".
/// </summary>
/// <remarks>
/// <para><strong>What this writes, and where.</strong> One value, <c>Modbot</c>, under the current
/// user's own <c>HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run</c> key, holding the
/// installed launcher's path and <c>--autostart</c>. That is the standard per-user way a program
/// starts at sign-in; it needs no administrator rights and affects nobody else's account.</para>
/// <para><strong>What this reads.</strong> That value, and the matching value under
/// <c>Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run</c>, which is where
/// Task Manager records that you turned an app off. That second key is only ever read, never
/// written: Modbot does not overrule Windows' switch.</para>
/// <para>Nothing here is sent anywhere. <c>CompanionSourceGuardTests</c> holds this and
/// <c>UrlSchemeRegistration</c> to being the only files in the client that touch the registry.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class StartupRegistration : IStartupRegistry
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public const string ValueName = "Modbot";

    public string? ReadCommand()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public void WriteCommand(string command)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key.SetValue(ValueName, command);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // A locked-down machine still runs the client; it just does not start with Windows.
        }
    }

    public void DeleteCommand() => Remove();

    /// <summary>
    /// Task Manager writes a small binary value here; an odd first byte means the user turned the app
    /// off. No value at all means Windows has no opinion, which counts as on.
    /// </summary>
    public bool IsTurnedOffInWindows()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ApprovedKeyPath);
            return key?.GetValue(ValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Removes the startup entry. Also called by the uninstaller.</summary>
    public static void Remove()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Nothing more to do: an entry that cannot be removed cannot be removed.
        }
    }
}
