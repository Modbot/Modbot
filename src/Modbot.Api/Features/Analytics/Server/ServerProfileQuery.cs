using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Core.Data;
using Modbot.Core.Discord;

namespace Modbot.Api.Features.Analytics.Server;

/// <summary>
/// The top of the Discord analytics page: the server as Discord's own server profile shows it.
/// The Discord member list draws the same header, from <c>GET /api/discord/server</c>.
/// </summary>
/// <remarks>
/// Nearly all of it is what the bot left behind. The name, pictures and boosts are on the server's
/// row, written whenever the bot reads the server; the member count is the newest reading of
/// Discord's own count, whatever the page's window; the day the server was made is in its id.
/// The online count is the one thing asked of Discord, through <paramref name="online"/>, which
/// keeps each answer five minutes; with none given, as for the AI chat, nothing is asked.
/// </remarks>
public sealed class ServerProfileQuery(ModbotContext db, IDiscordOnlineCount? online = null)
{
    /// <summary>The first moment of 2015, in milliseconds: where Discord starts counting its ids from.</summary>
    public const long DiscordEpochMs = 1_420_070_400_000;

    public async Task<ServerProfile> RunAsync(string? guildId, CancellationToken ct = default)
    {
        if (guildId is null)
            return new ServerProfile(null, null, null, null, null, null, null, null, null);

        var row = await db.DiscordServers.AsNoTracking()
            .Where(s => s.GuildId == guildId)
            .Select(s => new { s.Name, s.IconUrl, s.BannerUrl, s.BoostCount, s.BoostLevel })
            .FirstOrDefaultAsync(ct);

        var members = await db.DailyTotals.AsNoTracking()
            .Where(t => t.Metric == DailyTotalMetrics.DiscordMembersCount && t.Dimension == "")
            .OrderByDescending(t => t.Day)
            .Select(t => (decimal?)t.Value)
            .FirstOrDefaultAsync(ct);

        var onlineCount = online is null ? null : await online.ReadAsync(guildId, ct);

        return new ServerProfile(
            guildId,
            string.IsNullOrWhiteSpace(row?.Name) ? null : row.Name,
            row?.IconUrl,
            row?.BannerUrl,
            CreatedAt(guildId),
            members is { } count ? (int)count : null,
            onlineCount,
            row?.BoostCount,
            row?.BoostLevel);
    }

    /// <summary>
    /// When a Discord id was made. The id's top 42 bits are milliseconds since the start of 2015, so
    /// a server's id says when the server was made. Null for an id that is not a number.
    /// </summary>
    /// <remarks>
    /// Modbot stores Discord ids as text and never checks their shape; this reads one only to show a
    /// date, and an id it cannot read shows no date rather than failing the page.
    /// </remarks>
    public static DateTimeOffset? CreatedAt(string? id)
    {
        if (!ulong.TryParse(id?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            return null;

        return DateTimeOffset.FromUnixTimeMilliseconds((long)(value >> 22) + DiscordEpochMs);
    }
}
