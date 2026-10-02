using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Conventions;
using Modbot.Api.Features.DiscordLink;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;

namespace Modbot.Api.Features.DiscordGate;

/// <summary>One person at the join gate.</summary>
/// <param name="Agreed">They pressed I agree.</param>
/// <param name="Linked">They have a linked VRChat account. Null without See profiles.</param>
/// <param name="EighteenPlus">Their linked VRChat account is 18+ verified. Null without See profiles.</param>
/// <param name="MinutesCounted">The minutes counted against them so far.</param>
/// <param name="RemovedAt">
/// The earliest they can be removed if they do nothing and time keeps counting, and never sooner than
/// the warning window after a warning that reached them; null in Watch only and when removal is Never.
/// </param>
/// <param name="WouldRemoveAt">Watch only: when they would have been removed.</param>
/// <param name="Problem">The last thing that went wrong for them, in a sentence.</param>
public sealed record DiscordGateRow(
    string DiscordUserId,
    string Username,
    string? DisplayName,
    string? AvatarUrl,
    DateTimeOffset JoinedAt,
    bool Agreed,
    bool? Linked,
    bool? EighteenPlus,
    int MinutesCounted,
    DateTimeOffset? WarnedAt,
    DateTimeOffset? RemovedAt,
    DateTimeOffset? WouldRemoveAt,
    string? Problem);

/// <summary>The join gate as staff see it: its mode, whether new joiners are held, and who is waiting.</summary>
/// <param name="Mode"><c>off</c>, <c>watch</c> or <c>on</c>.</param>
/// <param name="HeldSince">Since when new joiners are held, or null.</param>
/// <param name="PauseInvites">Pausing invites is allowed.</param>
/// <param name="Waiting">Everybody at the gate, oldest joiner first, at most 200.</param>
public sealed record DiscordGateResponse(
    string Mode,
    DateTimeOffset? HeldSince,
    bool PauseInvites,
    bool NeedsLink,
    bool NeedsEighteenPlus,
    int? RemoveAfterMinutes,
    IReadOnlyList<DiscordGateRow> Waiting);

/// <summary>What a join gate action did.</summary>
public sealed record DiscordGateDone(bool Done);

/// <summary>
/// The join gate for staff (join gate design §7 and §8): who is waiting, and Let in, Remove, Hold
/// new joiners, Lift hold and Pause invites.
/// </summary>
/// <remarks>
/// Seeing who is waiting needs See members, as the member list does; whether each has a linked VRChat
/// account needs See profiles as well, as it does there. Every action needs Manage the join gate,
/// goes through the bot's live session, is never retried, and writes a fact naming who asked.
/// </remarks>
public static class DiscordGateEndpoints
{
    public const int MaxRows = 200;

    public static IEndpointRouteBuilder MapDiscordGate(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/discord/gate").WithTags("Discord").RequireAuthorization();

        group.MapGet("/", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var settings = await db.Settings.AsNoTracking().FirstAsync(s => s.Id == 1, ct);
                var guild = settings.DiscordGuildId ?? string.Empty;
                var seesLinks = LinkFilter.SeesLinks(http);
                var now = clock.UtcNow;

                var rows = await db.DiscordGateEntries.AsNoTracking()
                    .Where(e => e.GuildId == guild && e.ClosedAt == null)
                    .OrderBy(e => e.JoinedAt)
                    .Take(MaxRows)
                    .Select(e => new
                    {
                        Entry = e,
                        Member = db.DiscordMembers
                            .Where(m => m.GuildId == e.GuildId && m.UserId == e.DiscordUserId)
                            .Select(m => new { m.DisplayName, m.AvatarUrl })
                            .FirstOrDefault(),
                        Linked = db.DiscordAccountLinks.Any(l => l.DiscordUserId == e.DiscordUserId && l.UnlinkedAt == null),
                        EighteenPlus = db.DiscordAccountLinks.Any(l => l.DiscordUserId == e.DiscordUserId && l.UnlinkedAt == null
                            && db.VRChatUsers.Any(u => u.UserId == l.VRChatUserId && u.Is18PlusVerified)),
                    })
                    .ToListAsync(ct);

                var removeAfter = settings.DiscordGateRemoveAfterMinutes;

                var waiting = rows.Select(r => new DiscordGateRow(
                        r.Entry.DiscordUserId,
                        r.Entry.DiscordUsername,
                        r.Member?.DisplayName,
                        r.Member?.AvatarUrl,
                        r.Entry.JoinedAt,
                        r.Entry.AgreedAt is not null,
                        seesLinks ? r.Linked : null,
                        seesLinks ? r.EighteenPlus : null,
                        r.Entry.MinutesCounted,
                        r.Entry.WarnedAt,
                        DiscordGateTimes.EarliestRemoval(r.Entry, removeAfter, now),
                        r.Entry.WouldRemoveAt,
                        r.Entry.Problem))
                    .ToList();

                return Results.Ok(new DiscordGateResponse(
                    DiscordGateModes.IsKnown(settings.DiscordGateMode) ? settings.DiscordGateMode : DiscordGateModes.Off,
                    settings.DiscordGateHeldAt,
                    settings.DiscordGatePauseInvites,
                    settings.DiscordGateNeedsLink,
                    settings.DiscordGateNeedsEighteenPlus,
                    removeAfter,
                    waiting));
            })
            .RequiresFlag(ModbotPermissions.ViewMembers)
            .WithName("GetDiscordGate")
            .WithSummary("Who is at the join gate")
            .WithDescription(
                "The Discord join gate's mode, whether new joiners are held, and everybody waiting at "
                + "it, oldest joiner first (at most 200). `linked` and `eighteenPlus` need See profiles "
                + "and are null without it. `removedAt` is the earliest somebody can be removed if "
                + "nothing changes, never sooner than the warning window after their warning reached "
                + "them, and null in Watch only or when removal is Never; `wouldRemoveAt` is when Watch "
                + "only would have removed them.")
            .Produces<DiscordGateResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/{id}/let-in", (
                string id,
                HttpContext http,
                [FromServices] IJoinGateActions gate,
                CancellationToken ct) =>
                ActAsync(http, by => gate.LetInAsync(id, by, ct)))
            .RequiresFlag(ModbotPermissions.ManageJoinGate)
            .WithName("LetInAtDiscordGate")
            .WithSummary("Let somebody in at the join gate")
            .WithDescription(
                "Give one person waiting at the join gate the member role now, whatever steps they "
                + "have left. The address names their Discord id. 404 when they are not at the gate.")
            .Produces<DiscordGateDone>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/{id}/remove", (
                string id,
                HttpContext http,
                [FromServices] IJoinGateActions gate,
                CancellationToken ct) =>
                ActAsync(http, by => gate.RemoveAsync(id, by, http.User.Identity?.Name ?? "a Modbot account", ct)))
            .RequiresFlag(ModbotPermissions.ManageJoinGate)
            .WithName("RemoveAtDiscordGate")
            .WithSummary("Remove somebody at the join gate")
            .WithDescription(
                "Remove one person waiting at the join gate from the Discord server. They can join "
                + "again. The address names their Discord id. 404 when they are not at the gate.")
            .Produces<DiscordGateDone>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/hold", (HttpContext http, [FromServices] IJoinGateActions gate, CancellationToken ct) =>
                ActAsync(http, by => gate.HoldAsync(by, ct)))
            .RequiresFlag(ModbotPermissions.ManageJoinGate)
            .WithName("HoldDiscordGate")
            .WithSummary("Hold new joiners")
            .WithDescription("Nobody gets the member role by themselves until the hold is lifted. Needs the gate to be on.")
            .Produces<DiscordGateDone>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/lift", (HttpContext http, [FromServices] IJoinGateActions gate, CancellationToken ct) =>
                ActAsync(http, by => gate.LiftHoldAsync(by, ct)))
            .RequiresFlag(ModbotPermissions.ManageJoinGate)
            .WithName("LiftDiscordGateHold")
            .WithSummary("Lift the hold")
            .WithDescription("Let everybody who has done the steps in on the next pass.")
            .Produces<DiscordGateDone>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/pause-invites", (HttpContext http, [FromServices] IJoinGateActions gate, CancellationToken ct) =>
                ActAsync(http, by => gate.PauseInvitesAsync(by, ct)))
            .RequiresFlag(ModbotPermissions.ManageJoinGate)
            .WithName("PauseDiscordInvites")
            .WithSummary("Pause invites")
            .WithDescription(
                "Pause the Discord server's invites for 24 hours, the most Discord allows. Needs the "
                + "gate to be on, Allow pausing invites, and Manage Server for the bot.")
            .Produces<DiscordGateDone>()
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static async Task<IResult> ActAsync(HttpContext http, Func<Guid, Task<JoinGateOutcome>> act)
    {
        if (ModbotAuth.UserIdOf(http.User) is not { } by)
            return Results.Forbid();

        var outcome = await act(by);

        if (outcome.Done)
            return Results.Ok(new DiscordGateDone(true));

        if (outcome.BotOffline)
            return Problems.Of(StatusCodes.Status503ServiceUnavailable, outcome.Error, Problems.Unavailable);

        if (outcome.NotWaiting)
            return Problems.Of(StatusCodes.Status404NotFound, outcome.Error, Problems.NotFound);

        return Problems.Of(StatusCodes.Status409Conflict, outcome.Error, Problems.Conflict);
    }
}
