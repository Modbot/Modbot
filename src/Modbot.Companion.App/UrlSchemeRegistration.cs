using System.Runtime.Versioning;
using Modbot.Companion.Pairing;

namespace Modbot.Companion.App;

/// <summary>
/// Tells Windows that <c>modbot-companion://</c> links open this program.
/// </summary>
/// <remarks>
/// <para><strong>What this writes, and where.</strong> One key under the current user's own
/// registry hive, <c>HKEY_CURRENT_USER\Software\Classes\modbot-companion</c>, holding the path of
/// this executable. That is the standard, documented way a program claims a URL scheme for one
/// user; it needs no administrator rights and touches nothing outside that key. It is written on
/// every start only if it differs from what is there, so a client that has been moved or updated
/// points Windows at the right file.</para>
/// <para><strong>What this reads.</strong> The same key, to see whether it already says the right
/// thing. Nothing else in the registry is opened — not Steam's keys, not VRChat's, not anybody
/// else's. The client's source guard (<c>ClientSourceGuardTests</c>) holds this to being the only
/// file in the client that mentions the registry at all.</para>
/// <para><strong>Why.</strong> Pairing starts in the browser: the moderator presses "Open in
/// Modbot" on their group's pairing page, and the browser asks Windows what opens a
/// <c>modbot-companion://</c> link. Without this key the answer is nothing, and the moderator is back
/// to copying and pasting.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class UrlSchemeRegistration
{
    /// <summary>Under <c>HKEY_CURRENT_USER</c>. Per-user, so no elevation and no effect on anybody else's account.</summary>
    public const string KeyPath = @"Software\Classes\" + PairingToken.Scheme;

    /// <summary>
    /// Makes the registration current. Returns false, and changes nothing, if the registry
    /// refuses — a locked-down machine still runs the client; it just pairs by pasting.
    /// </summary>
    public static bool Register(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var command = $"\"{executablePath}\" \"%1\"";
        var icon = $"\"{executablePath}\",0";

        try
        {
            using var existing = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath + @"\shell\open\command");
            if (existing?.GetValue(null) is string current && string.Equals(current, command, StringComparison.Ordinal))
                return true;

            using var root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath);
            root.SetValue(null, "URL:Modbot pairing link");
            root.SetValue("URL Protocol", string.Empty);

            using var defaultIcon = root.CreateSubKey("DefaultIcon");
            defaultIcon.SetValue(null, icon);

            using var open = root.CreateSubKey(@"shell\open\command");
            open.SetValue(null, command);

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
                                       or UnauthorizedAccessException
                                       or IOException)
        {
            return false;
        }
    }
}
