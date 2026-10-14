using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Settings;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;

namespace Modbot.Api.Features.DiscordLink;

/// <summary>The one-time link reminder, as the Account links tab shows it.</summary>
/// <param name="Running">Staff started it and it has not reached everyone yet.</param>
/// <param name="StartedAt">When it was started, while it runs.</param>
/// <param name="Waiting">
/// Members still to be sent it: those it began with while it runs, and those in the server now before
/// it is started.
/// </param>
/// <param name="Sent">Members it has been sent to, ever.</param>
/// <param name="NotReached">Members Discord would not let it reach, ever. They are not asked again.</param>
/// <param name="LastSentAt">When the latest one went out.</param>
/// <param name="SecondsBetween">The wait between two messages.</param>
public sealed record DiscordLinkReminderView(
    bool Running,
    DateTimeOffset? StartedAt,
    int Waiting,
    int Sent,
    int NotReached,
    DateTimeOffset? LastSentAt,
    int SecondsBetween);

/// <summary>What share of the VRChat group and of the Discord server has linked, and the reminder.</summary>
/// <param name="GroupMembers">People in the VRChat group now.</param>
/// <param name="GroupMembersLinked">Of them, those with a Discord account linked.</param>
/// <param name="DiscordMembers">People (not bots) in the Discord server now.</param>
/// <param name="DiscordMembersLinked">Of them, those with a VRChat account linked.</param>
/// <param name="LinkingSetUp">The OAuth client, its secret and the public address are saved, so the link page works.</param>
public sealed record DiscordLinkShareResponse(
    int GroupMembers,
    int GroupMembersLinked,
    int DiscordMembers,
    int DiscordMembersLinked,
    bool LinkingSetUp,
    DiscordLinkReminderView Reminder);

/// <summary>
/// The Account links tab on the Discord page: how many of the VRChat group's members and of the
/// Discord server's members have linked, and the one-time reminder to the ones who have not.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Seeing is analytics, starting is settings.</strong> The numbers are read under
/// <see cref="ModbotPermissions.ViewAnalytics"/>, like the rest of the Discord page; starting and
/// stopping the reminder need <see cref="ModbotPermissions.ManageSettings"/>.
/// </para>
/// <para>
/// The numbers count people, never name them. The reminder itself is sent by the bot, one direct
/// message at a time (the bot's reminder job).
/// </para>
/// </remarks>
public static class DiscordLinkShareEndpoints
{
    public static IEndpointRouteBuilder MapDiscordLinkShare(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/discord/account-links").WithTags("Discord");

        group.MapGet("", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) => Results.Ok(await ViewAsync(db, clock, ct)))
            .WithName("GetDiscordLinkShare")
            .WithSummary("What share of members have linked")
            .WithDescription(
                "How many people in the VRChat group have a Discord account linked, how many in the Discord "
                + "server have a VRChat account linked, and the state of the one-time link reminder. Counts "
                + "only; nobody is named.")
            .Produces<DiscordLinkShareResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ViewAnalytics);

        group.MapPost("/reminder/start", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);

                if (DiscordInvite.LinkPageWhenSetUp(settings) is null)
                    return Results.BadRequest(new { error = "Account linking is not set up." });

                if (string.IsNullOrWhiteSpace(settings.DiscordGuildId))
                    return Results.BadRequest(new { error = "No Discord server is saved." });

                if (settings.DiscordLinkNudgeStartedAt is null)
                {
                    var now = clock.UtcNow;

                    if (!await LinkNudgeQueue.Waiting(db, settings.DiscordGuildId, now).AnyAsync(ct))
                        return Results.BadRequest(new { error = "Nobody is waiting for the reminder." });

                    var change = new SettingsChange("discordLinkReminder").Field("running", false, true);

                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    settings.DiscordLinkNudgeStartedAt = now;
                    await db.SaveChangesAsync(ct);
                    await change.RecordAsync(http, ct);
                    await transaction.CommitAsync(ct);
                }

                return Results.Ok(await ViewAsync(db, clock, ct));
            })
            .WithName("StartDiscordLinkReminder")
            .WithSummary("Start the link reminder")
            .WithDescription(
                "Start sending the one-time reminder to link a VRChat account to the Discord members who "
                + "were in the server just now and have not linked. One direct message goes out every 30 "
                + "seconds, and nobody is sent it twice, ever. It stops by itself when it has reached them "
                + "all. Needs account linking set up. Does nothing if it is already running.")
            .Produces<DiscordLinkShareResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPost("/reminder/stop", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);

                if (settings.DiscordLinkNudgeStartedAt is not null)
                {
                    var change = new SettingsChange("discordLinkReminder").Field("running", true, false);

                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    settings.DiscordLinkNudgeStartedAt = null;
                    await db.SaveChangesAsync(ct);
                    await change.RecordAsync(http, ct);
                    await transaction.CommitAsync(ct);
                }

                return Results.Ok(await ViewAsync(db, clock, ct));
            })
            .WithName("StopDiscordLinkReminder")
            .WithSummary("Stop the link reminder")
            .WithDescription(
                "Stop sending the reminder. Whoever was already sent it is not sent it again if it is started again.")
            .Produces<DiscordLinkShareResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    private static async Task<DiscordLinkShareResponse> ViewAsync(ModbotContext db, IModbotClock clock, CancellationToken ct)
    {
        var settings = await db.GetSettingsAsync(ct);
        var links = db.ActiveAccountLinks();

        var groupMembers = 0;
        var groupLinked = 0;

        if (!string.IsNullOrWhiteSpace(settings.ManagedGroupId))
        {
            var here = db.GroupMembers.AsNoTracking().Where(m => m.GroupId == settings.ManagedGroupId && m.LeftAt == null);
            groupMembers = await here.CountAsync(ct);
            groupLinked = await here.CountAsync(m => links.Any(l => l.VRChatUserId == m.UserId), ct);
        }

        var discordMembers = 0;
        var discordLinked = 0;
        var waiting = 0;

        if (!string.IsNullOrWhiteSpace(settings.DiscordGuildId))
        {
            var guild = settings.DiscordGuildId;
            var here = db.DiscordMembers.AsNoTracking().Where(m => m.GuildId == guild && !m.IsBot && m.LeftAt == null);
            discordMembers = await here.CountAsync(ct);
            discordLinked = await here.CountAsync(m => links.Any(l => l.DiscordUserId == m.UserId), ct);

            // Before it is started: how many it would reach now. While it runs: how many are left of those it began with.
            waiting = await LinkNudgeQueue.Waiting(db, guild, settings.DiscordLinkNudgeStartedAt ?? clock.UtcNow).CountAsync(ct);
        }

        var sent = await db.DiscordLinkNudges.AsNoTracking().CountAsync(n => n.State == DiscordLinkNudgeStates.Sent, ct);
        var notReached = await db.DiscordLinkNudges.AsNoTracking().CountAsync(n => n.State == DiscordLinkNudgeStates.NotReached, ct);
        var lastSent = await db.DiscordLinkNudges.AsNoTracking().MaxAsync(n => n.SentAt, ct);

        return new DiscordLinkShareResponse(
            groupMembers,
            groupLinked,
            discordMembers,
            discordLinked,
            DiscordInvite.LinkPageWhenSetUp(settings) is not null,
            new DiscordLinkReminderView(
                settings.DiscordLinkNudgeStartedAt is not null,
                settings.DiscordLinkNudgeStartedAt,
                waiting,
                sent,
                notReached,
                lastSent,
                (int)LinkNudgeQueue.Gap.TotalSeconds));
    }
}
