using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Api.Conventions;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;

namespace Modbot.Api.Features.DiscordMembers;

/// <summary>
/// Ban, unban, remove and time out one member of the Discord server (API conventions design §8):
/// what a group's own bot needs to act on Discord through Modbot rather than beside it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One permission per action.</strong> Discord's own are separate (Ban Members, Kick
/// Members, Moderate Members), and so are Modbot's: a group can let a helper time people out
/// without letting them ban anybody. The VRChat <c>Kick</c>, <c>Ban</c> and <c>Unban</c> are not
/// reused, because acting on one platform is not permission to act on the other.
/// </para>
/// <para>
/// <strong>One request, never retried, and nothing recorded unless Discord accepted.</strong> Then
/// a <c>modbot.action.discord.*</c> fact names the Modbot account that asked, beside Discord's own
/// audit entry, which can only name the bot. The reason goes to Discord's audit log as
/// "Modbot: banned by &lt;account&gt;: &lt;reason&gt;", the way a ban copied from VRChat does.
/// </para>
/// <para>
/// A Discord ban made here is a ban by Modbot's bot, so ban sync treats it as it treats any bot's
/// ban: it is not copied to VRChat unless the operator asked for bots' bans to be.
/// </para>
/// </remarks>
public static class DiscordMemberActionEndpoints
{
    /// <summary>Discord keeps at most this many characters of a reason in its audit log.</summary>
    public const int MaxReasonLength = 512;

    /// <summary>Discord's longest timeout: 28 days.</summary>
    public const int MaxTimeoutMinutes = 28 * 24 * 60;

    public static IEndpointRouteBuilder MapDiscordMemberActions(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var bans = app.MapGroup("/api/discord/bans").WithTags("Discord").RequireAuthorization();
        var members = app.MapGroup("/api/discord/members").WithTags("Discord").RequireAuthorization();

        bans.MapPost("/", (
                HttpContext http,
                [FromBody] DiscordBanRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IDiscordMemberActions discord,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (body.DeleteMessageDays is < 0 or > 7)
                    return Task.FromResult(Problems.Of(StatusCodes.Status400BadRequest, "`deleteMessageDays` is 0 to 7."));

                return ActAsync(
                    http, db, clock, discord, facts, partitions, body.UserId, body.Reason, "banned", FactType.ActionDiscordBan,
                    (guild, reason) => discord.BanAsync(guild, body.UserId, reason, body.DeleteMessageDays, ct),
                    new JsonObject { ["deleteMessageDays"] = body.DeleteMessageDays },
                    ct);
            })
            .RequiresFlag(ModbotPermissions.DiscordBan)
            .WithName("BanDiscordMember")
            .WithSummary("Ban on Discord")
            .WithDescription(
                "Ban one person from the Discord server. `userId` is their Discord id; `reason` goes "
                + "to Discord's audit log with your account's name; `deleteMessageDays` (0 to 7, "
                + "default 0) has Discord delete that many days of their messages too. One request, "
                + "never retried. Once Discord accepts, the audit log records who asked. `changed` is "
                + "false when they were already banned."
                + " Never the bot's own account, the server's owner or a Discord account linked to a Modbot staff account: those answer 403 `refused`.")
            .Produces<DiscordActionDone>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        bans.MapDelete("/{id}", (
                string id,
                [FromQuery] string? reason,
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IDiscordMemberActions discord,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
                ActAsync(
                    http, db, clock, discord, facts, partitions, id, reason, "unbanned", FactType.ActionDiscordUnban,
                    (guild, why) => discord.UnbanAsync(guild, id, why, ct),
                    [],
                    ct))
            .RequiresFlag(ModbotPermissions.DiscordUnban)
            .WithName("UnbanDiscordMember")
            .WithSummary("Unban on Discord")
            .WithDescription(
                "Lift one person's Discord ban. The address names their Discord id; `reason` goes to "
                + "Discord's audit log with your account's name. One request, never retried. Once "
                + "Discord accepts, the audit log records who asked. `changed` is false when they were "
                + "not banned."
                + " Never the bot's own account, the server's owner or a Discord account linked to a Modbot staff account: those answer 403 `refused`.")
            .Produces<DiscordActionDone>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        members.MapPost("/{id}/kick", (
                string id,
                [FromBody] DiscordActionRequest? body,
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IDiscordMemberActions discord,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
                ActAsync(
                    http, db, clock, discord, facts, partitions, id, body?.Reason, "removed", FactType.ActionDiscordKick,
                    (guild, why) => discord.KickAsync(guild, id, why, ct),
                    [],
                    ct))
            .RequiresFlag(ModbotPermissions.DiscordKick)
            .WithName("KickDiscordMember")
            .WithSummary("Remove from Discord")
            .WithDescription(
                "Remove one person from the Discord server without banning them. The address names "
                + "their Discord id; `reason` goes to Discord's audit log with your account's name. "
                + "One request, never retried. Once Discord accepts, the audit log records who asked. "
                + "`changed` is false when they were not in the server."
                + " Never the bot's own account, the server's owner or a Discord account linked to a Modbot staff account: those answer 403 `refused`.")
            .Produces<DiscordActionDone>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        members.MapPost("/{id}/timeout", (
                string id,
                [FromBody] DiscordTimeoutRequest body,
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IDiscordMemberActions discord,
                [FromServices] IFactWriter? facts,
                [FromServices] EventPartitionMaintainer? partitions,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (body.Minutes is < 1 or > MaxTimeoutMinutes)
                {
                    return Task.FromResult(Problems.Of(
                        StatusCodes.Status400BadRequest, $"`minutes` is 1 to {MaxTimeoutMinutes} (28 days)."));
                }

                var duration = TimeSpan.FromMinutes(body.Minutes);

                return ActAsync(
                    http, db, clock, discord, facts, partitions, id, body.Reason, "timed out", FactType.ActionDiscordTimeOut,
                    (guild, why) => discord.TimeOutAsync(guild, id, duration, why, ct),
                    new JsonObject { ["minutes"] = body.Minutes, ["until"] = clock.UtcNow + duration },
                    ct);
            })
            .RequiresFlag(ModbotPermissions.DiscordTimeOut)
            .WithName("TimeOutDiscordMember")
            .WithSummary("Time out on Discord")
            .WithDescription(
                "Time one member of the Discord server out for `minutes` (1 to 40320, which is 28 "
                + "days); a member already timed out gets the new length. The address names their "
                + "Discord id; `reason` goes to Discord's audit log with your account's name. One "
                + "request, never retried. Once Discord accepts, the audit log records who asked."
                + " Never the bot's own account, the server's owner or a Discord account linked to a Modbot staff account: those answer 403 `refused`.")
            .Produces<DiscordActionDone>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status502BadGateway)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    /// <summary>What every one of the four does the same way.</summary>
    private static async Task<IResult> ActAsync(
        HttpContext http,
        ModbotContext db,
        IModbotClock clock,
        IDiscordMemberActions discord,
        IFactWriter? facts,
        EventPartitionMaintainer? partitions,
        string? userId,
        string? why,
        string verb,
        string factType,
        Func<string, string, Task<DiscordMemberOutcome>> act,
        JsonObject data,
        CancellationToken ct)
    {
        if (ModbotAuth.UserIdOf(http.User) is not { } actor)
            return Results.Forbid();

        if (facts is null || partitions is null)
            return Problems.Of(StatusCodes.Status503ServiceUnavailable, "This deployment cannot record actions.", Problems.NotSetUp);

        if (string.IsNullOrWhiteSpace(userId))
            return Problems.Of(StatusCodes.Status400BadRequest, "Say who, by their Discord id.");

        var guildId = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordGuildId)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(guildId))
            return Problems.Of(StatusCodes.Status409Conflict, "No Discord server is set up yet.", Problems.NotSetUp);

        // Three accounts are never acted on from here, however the request is worded. Modbot's own
        // bot (taking it out of the server would end every Discord feature, from inside the thing
        // doing it, as ModerationActionService refuses for Modbot's VRChat account), the server's
        // owner (whom Discord refuses anyway, and a refusal here is plainer), and any Discord account
        // linked to a Modbot staff account (a key that may time people out is not a way to lock
        // another moderator out of the server).
        var refusal = await OffLimitsAnswerAsync(db, discord, guildId, userId, ct);
        if (refusal is not null)
            return refusal;

        var reason = Reason(verb, http.User.Identity?.Name ?? "a Modbot account", why);

        var outcome = await act(guildId, reason);

        if (outcome.BotOffline)
            return Problems.Of(StatusCodes.Status503ServiceUnavailable, outcome.Error, Problems.Unavailable);

        if (!outcome.Done)
            return Problems.Of(StatusCodes.Status502BadGateway, outcome.Error ?? "Discord refused.", Problems.DiscordRefused);

        if (!outcome.NothingToDo)
        {
            var now = clock.UtcNow;
            await partitions.EnsureForAsync(now, ct);

            data["reason"] = string.IsNullOrWhiteSpace(why) ? null : why.Trim();
            data["guildId"] = guildId;

            await facts.WriteAsync(
                new FactRecord
                {
                    Type = factType,
                    OccurredAt = now,
                    SubjectPlatform = FactPlatform.Discord,
                    SubjectId = userId,
                    ActorPlatform = FactPlatform.Modbot,
                    ActorId = actor.ToString(),
                    Source = FactSource.Manual,
                    Data = data,
                },
                ct);
        }

        return Results.Ok(new DiscordActionDone(!outcome.NothingToDo));
    }

    /// <summary>
    /// The answer for an account that may not be acted on, or null when it may. Ids are compared as
    /// the number they spell, because Discord reads "0123" as 123 and a text comparison would let
    /// that through (the id is otherwise opaque, foundation §3.1.1).
    /// </summary>
    private static async Task<IResult?> OffLimitsAnswerAsync(
        ModbotContext db, IDiscordMemberActions discord, string guildId, string userId, CancellationToken ct)
    {
        // Not a number: nothing here can match it, and Discord's side says it is not an id.
        if (!ulong.TryParse(userId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var target))
            return null;

        var off = await discord.OffLimitsAsync(guildId, ct);

        if (Spells(off.BotUserId, target))
            return Problems.Of(StatusCodes.Status403Forbidden, "That is the Discord account Modbot's bot runs as. Modbot will not act on itself.", Problems.Refused);

        if (Spells(off.OwnerId, target))
            return Problems.Of(StatusCodes.Status403Forbidden, "That is the owner of the Discord server. Modbot will not act on them.", Problems.Refused);

        // A staff account's Discord id, proven or typed in: either way it names a moderator.
        var staff = await db.Users.AsNoTracking()
            .Where(u => u.DeletedAt == null && u.DiscordUserId != null && u.DiscordUserId != string.Empty)
            .Select(u => u.DiscordUserId!)
            .ToListAsync(ct);

        if (staff.Any(id => Spells(id, target)))
            return Problems.Of(StatusCodes.Status403Forbidden, "That Discord account belongs to a Modbot staff account. Modbot will not act on staff.", Problems.Refused);

        return null;
    }

    private static bool Spells(string? id, ulong number)
        => id is not null
            && ulong.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed == number;

    /// <summary>What Discord's audit log shows: who asked, in Modbot, and why.</summary>
    internal static string Reason(string verb, string by, string? why)
    {
        var text = $"Modbot: {verb} by {by}" + (string.IsNullOrWhiteSpace(why) ? string.Empty : $": {why.Trim()}");
        return text.Length <= MaxReasonLength ? text : text[..(MaxReasonLength - 1)] + "…";
    }
}

/// <summary>A Discord ban to make.</summary>
/// <param name="UserId">Their Discord id.</param>
/// <param name="Reason">Why, for Discord's audit log and Modbot's.</param>
/// <param name="DeleteMessageDays">How many days of their messages Discord deletes too, 0 to 7.</param>
public sealed record DiscordBanRequest(string UserId, string? Reason = null, int DeleteMessageDays = 0);

/// <summary>Why, for Discord's audit log and Modbot's.</summary>
public sealed record DiscordActionRequest(string? Reason = null);

/// <summary>A Discord timeout to give.</summary>
/// <param name="Minutes">How long, 1 to 40320 (28 days).</param>
/// <param name="Reason">Why, for Discord's audit log and Modbot's.</param>
public sealed record DiscordTimeoutRequest(int Minutes, string? Reason = null);

/// <summary>What a Discord action did.</summary>
/// <param name="Changed">False when it was already so: not banned, already banned, not in the server.</param>
public sealed record DiscordActionDone(bool Changed);
