using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Discord;

namespace Modbot.Discord.Commands;

/// <summary>Reads the <c>discord_commands</c> setting for the bot (Discord commands design §3.8).</summary>
public static class CommandSwitchSetting
{
    /// <summary>The setting as stored. Empty when there is no settings row yet, which means every default.</summary>
    public static async Task<string> ReadAsync(ModbotContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        return await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordCommands)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false) ?? DiscordCommandSwitches.Empty;
    }

    /// <summary>Whether a command is switched on right now.</summary>
    public static async Task<bool> IsOnAsync(ModbotContext db, string command, CancellationToken ct)
        => DiscordCommandSwitches.IsOn(await ReadAsync(db, ct).ConfigureAwait(false), command);

    /// <summary>What a late run of a switched-off command is told. Discord drops the command within a poll or two of the switch.</summary>
    public static string OffMessage(string command, bool menu)
        => menu ? $"\"{command}\" is turned off on this server." : $"/{command} is turned off on this server.";
}
