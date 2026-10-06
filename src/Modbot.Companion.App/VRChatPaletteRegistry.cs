using System.Runtime.Versioning;
using Microsoft.Win32;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.App;

/// <summary>What one look at VRChat's palette found: the palette, or nothing, or why it could not look.</summary>
/// <param name="Palette">The selected palette, or null when there is none to use.</param>
/// <param name="Problem">Why Windows would not let the client look, or null when it looked.</param>
internal readonly record struct PaletteRead(VRChatPalette? Palette, string? Problem = null);

/// <summary>
/// The Windows side of colouring the desktop overlay from the palette selected in VRChat.
/// </summary>
/// <remarks>
/// <para><strong>What this reads.</strong> One value, and only one, from the current user's own
/// <c>HKEY_CURRENT_USER\Software\VRChat\VRChat</c> key, where VRChat keeps its settings: the colour
/// palette selected for the person using this PC, found by the user id the client already learned
/// from VRChat's log. To find that value's name (VRChat adds a number to it) this lists the names of
/// the values in that key, which is a list of names and nothing else, and then reads the one that
/// matches. Every other value in the key, including every other account's palette, is never read.
/// It does not list sub-keys and it opens no other key.</para>
/// <para><strong>What this writes: nothing.</strong> The key is opened for reading only; there is no
/// call here that creates, changes or deletes anything in the registry.</para>
/// <para><strong>What leaves the machine: nothing.</strong> The six colours are held in memory while
/// the overlay is up, to colour it, and are not stored, logged or sent.</para>
/// <para><c>CompanionSourceGuardTests</c> holds this to being the only file in the client that
/// reads VRChat's registry key, and to opening it read-only.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class VRChatPaletteRegistry
{
    public const string KeyPath = @"Software\VRChat\VRChat";

    /// <summary>
    /// Looks once. Nothing when this is not Windows, VRChat has never run here, no user id is known
    /// yet, the person has no selected palette, or what is stored is not a whole palette.
    /// </summary>
    public static PaletteRead Read(string? userId)
    {
        if (!VRChatPaletteValue.IsUsableId(userId))
            return new PaletteRead(null);

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);

            if (key is null)
                return new PaletteRead(null);

            if (VRChatPaletteValue.Pick(key.GetValueNames(), userId) is not { } name)
                return new PaletteRead(null);

            // Unity stores its text settings as binary; anything else under that name is not ours to use.
            if (key.GetValueKind(name) != RegistryValueKind.Binary)
                return new PaletteRead(null);

            return new PaletteRead(key.GetValue(name) is byte[] data ? VRChatPalette.Parse(data) : null);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new PaletteRead(null, ex.GetType().Name);
        }
    }
}
