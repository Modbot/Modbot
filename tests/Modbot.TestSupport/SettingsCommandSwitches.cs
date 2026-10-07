using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;

namespace Modbot.TestSupport;

/// <summary>Setting a command's switch on a settings row, as the Commands card does (Discord commands design §3.8).</summary>
public static class SettingsCommandSwitches
{
    /// <summary>Switches a command on or off, leaving the others as they were.</summary>
    public static void SwitchCommand(this Settings settings, string command, bool on)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.DiscordCommands = DiscordCommandSwitches.With(settings.DiscordCommands, command, on);
    }
}
