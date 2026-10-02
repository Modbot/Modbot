using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.DiscordMembers;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.DiscordReports;

/// <summary>
/// The Discord page's Roles and Channels tabs: two read-only reports for tidying the server
/// (Discord tidy-up design).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing is asked of Discord.</strong> Both are read from what the bot already keeps: the
/// role and channel lists it reads on sign-in and keeps current from events, the member list, and the
/// message store, which holds every message the bot can read, read back to each channel's first and
/// kept current as messages arrive. So the reports cost no request and are never older than those.
/// </para>
/// <para>
/// <strong>Nothing changes.</strong> Neither report deletes, renames or archives anything; tidying up
/// is done in Discord.
/// </para>
/// <para>
/// Gated on <see cref="ModbotPermissions.ViewAnalytics"/>, which the Discord page itself needs. Both
/// are counts and dates about the server -- how many hold a role, when a channel was last written in --
/// and name nobody.
/// </para>
/// </remarks>
public static class DiscordReportEndpoints
{
    /// <summary>The kinds of channel people write in that the quiet channels list covers.</summary>
    public static readonly IReadOnlyList<string> QuietChannelTypes =
    [
        DiscordChannelTypes.Text,
        DiscordChannelTypes.Announcement,
        DiscordChannelTypes.Forum,
    ];

    public static IEndpointRouteBuilder MapDiscordReports(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/discord/reports")
            .WithTags("Discord")
            .RequiresFlag(ModbotPermissions.ViewAnalytics);

        group.MapGet("/roles", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
                Results.Ok(await RolesAsync(db, ct)))
            .WithName("GetDiscordRoleReport")
            .WithSummary("Get the Discord roles report")
            .WithDescription(
                "Every role in the Discord server but @everyone, with how many members hold it and "
                + "what the report marks on it: `no-members`, `same-name` (another role has the same "
                + "name), `same-permissions-and-colour` (another role has the same permissions and "
                + "colour; roles with no colour and nothing beyond @everyone's permissions, and bot "
                + "roles, are left out of this one) and `bot-role` (owned by a bot or an integration). "
                + "Marked roles come first, the most marks first, then the fewest members, then "
                + "Discord's order. Counts are null until the member list has been read. Read from "
                + "what the bot already stores; nothing is asked of Discord and nothing is changed.")
            .Produces<RoleReport>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/quiet-channels", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromQuery] bool? hideStaffOnly,
                CancellationToken ct) =>
                Results.Ok(await QuietChannelsAsync(db, clock, hideStaffOnly ?? false, ct)))
            .WithName("GetDiscordQuietChannels")
            .WithSummary("Get the Discord quiet channels list")
            .WithDescription(
                "The server's text, announcement and forum channels with when anybody last wrote in "
                + "each (in the channel or any of its threads), quietest first: channels with no "
                + "message at all, then the longest since the last message, then channels still being "
                + "read back with nothing found yet, then channels the bot cannot read. "
                + "`hideStaffOnly=true` leaves out channels @everyone cannot see. Read from the "
                + "messages Modbot stores; nothing is asked of Discord and nothing is changed.")
            .Produces<QuietChannelList>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    internal static async Task<RoleReport> RolesAsync(ModbotContext db, CancellationToken ct)
    {
        var guildId = await DiscordMemberEndpoints.GuildIdAsync(db, ct);
        if (guildId is null)
            return new RoleReport(null, null, null, []);

        var server = await db.DiscordServers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.GuildId == guildId, ct);

        var stored = await db.DiscordRoles.AsNoTracking()
            .Where(r => r.GuildId == guildId && r.RemovedAt == null)
            .ToListAsync(ct);

        var everyone = stored.FirstOrDefault(r => r.Everyone);

        // A count from a member list that was never read whole would mark half the server's roles
        // "no members".
        var members = server?.MembersListedAt is null
            ? null
            : await DiscordMemberEndpoints.RoleCountsAsync(
                db.DiscordMembers.AsNoTracking().Where(m => m.GuildId == guildId), ct);

        var rows = DiscordTidyUp.Roles(
            stored
                .Where(r => !r.Everyone)
                .Select(r => new DiscordTidyUp.RoleFacts(r.RoleId, r.Name, r.Color, r.Position, r.Managed, r.Permissions))
                .ToList(),
            everyone?.Permissions,
            members);

        return new RoleReport(guildId, server?.MembersListedAt, server?.RefreshedAt, rows);
    }

    internal static async Task<QuietChannelList> QuietChannelsAsync(
        ModbotContext db, IModbotClock clock, bool hideStaffOnly, CancellationToken ct)
    {
        var now = clock.UtcNow;

        var guildId = await DiscordMemberEndpoints.GuildIdAsync(db, ct);
        if (guildId is null)
            return new QuietChannelList(null, now, []);

        var categories = await db.DiscordChannels.AsNoTracking()
            .Where(c => c.GuildId == guildId && c.Type == DiscordChannelTypes.Category)
            .ToDictionaryAsync(c => c.ChannelId, c => c.Name, StringComparer.Ordinal, ct);

        var types = QuietChannelTypes.ToList();

        // One look per channel down the (channel, sent) index for its newest message, which counts
        // its threads too: a thread's messages carry the channel they sit in. Deleted messages count:
        // somebody still wrote there then.
        var channels = await db.DiscordChannels.AsNoTracking()
            .Where(c => c.GuildId == guildId && c.RemovedAt == null && types.Contains(c.Type))
            .Select(c => new
            {
                c.ChannelId,
                c.Name,
                c.Type,
                c.CategoryId,
                c.Position,
                c.EveryoneCanView,
                CanRead = c.BotCanView && c.BotCanReadHistory,
                LastMessageAt = db.DiscordMessages
                    .Where(m => m.ChannelId == c.ChannelId)
                    .OrderByDescending(m => m.SentAt)
                    .Select(m => (DateTimeOffset?)m.SentAt)
                    .FirstOrDefault(),
                StillReading = db.DiscordReadBacks
                    .Any(r => (r.ChannelId == c.ChannelId || r.ParentChannelId == c.ChannelId) && r.FinishedAt == null),
            })
            .ToListAsync(ct);

        var rows = DiscordTidyUp.Channels(
            channels
                .Select(c => new DiscordTidyUp.ChannelFacts(
                    c.ChannelId,
                    c.Name,
                    c.Type,
                    c.CategoryId is { } category ? categories.GetValueOrDefault(category) : null,
                    c.Position,
                    c.EveryoneCanView,
                    c.CanRead,
                    c.StillReading,
                    c.LastMessageAt))
                .ToList(),
            hideStaffOnly);

        return new QuietChannelList(guildId, now, rows);
    }
}
