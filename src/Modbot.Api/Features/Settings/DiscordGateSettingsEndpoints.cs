using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Settings;

/// <summary>The join gate's settings, as stored, with what the card needs to say about them.</summary>
/// <param name="Mode"><c>off</c>, <c>watch</c> or <c>on</c>.</param>
/// <param name="RemoveAfterMinutes">Null is never.</param>
/// <param name="RemoveChoices">The lengths Remove after may be, in minutes.</param>
/// <param name="HeldSince">Since when new joiners are held, or null.</param>
/// <param name="LinkingReady">Account linking is set up, so the link step can be asked for.</param>
/// <param name="InviteUrl">The bot invite link, asking for what the gate needs. Null without a client id.</param>
public sealed record DiscordGateSettingsResponse(
    string Mode,
    string? MemberRoleId,
    string? ChannelId,
    string? Message,
    bool NeedsLink,
    bool NeedsEighteenPlus,
    int? RemoveAfterMinutes,
    IReadOnlyList<int> RemoveChoices,
    bool HoldOnSpike,
    bool PauseInvites,
    DateTimeOffset? HeldSince,
    bool LinkingReady,
    string? InviteUrl);

/// <param name="Mode"><c>off</c>, <c>watch</c> or <c>on</c>.</param>
/// <param name="MemberRoleId">Needed unless the mode is off.</param>
/// <param name="ChannelId">Needed when the mode is on.</param>
/// <param name="Message">At most 2,000 characters. Null or empty uses the server's name.</param>
/// <param name="RemoveAfterMinutes">One of the choices, or null for never.</param>
public sealed record DiscordGateSettingsUpdate(
    string Mode,
    string? MemberRoleId,
    string? ChannelId,
    string? Message,
    bool NeedsLink,
    bool NeedsEighteenPlus,
    int? RemoveAfterMinutes,
    bool HoldOnSpike,
    bool PauseInvites);

/// <summary>
/// Settings → Discord → Join gate (join gate design §3).
/// </summary>
/// <remarks>
/// <para>
/// A role the bot has read and cannot assign is refused here, as the linking card does, rather than
/// found out on the first joiner. So is the link step on a server where linking is not set up: a
/// step nobody can finish would only ever remove people.
/// </para>
/// <para>
/// Changing the mode ends the rows made under the old one (§9), so Watch only never turns into
/// removals, and starts the "who is gated" clock again: only people who join from now on are gated
/// by themselves.
/// </para>
/// </remarks>
public static class DiscordGateSettingsEndpoints
{
    public const int MaxMessageLength = 2000;

    /// <summary>The lengths Remove after may be, in minutes (join gate design §3).</summary>
    public static readonly IReadOnlyList<int> RemoveChoices =
        [30, 60, 6 * 60, 12 * 60, 24 * 60, 2 * 24 * 60, 3 * 24 * 60, 7 * 24 * 60];

    public static IEndpointRouteBuilder MapDiscordGateSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/discord-gate").WithTags("Settings");

        group.MapGet("", async ([FromServices] ModbotContext db, CancellationToken ct)
                => Results.Ok(View(await db.GetSettingsAsync(ct))))
            .WithName("GetDiscordGateSettings")
            .WithSummary("Get join gate settings")
            .WithDescription("The Discord join gate's settings: its mode, the member role, the gate channel and message, the steps, the removal time and join spike protection.")
            .Produces<DiscordGateSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] DiscordGateSettingsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] IJoinGateActions gate,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var mode = body.Mode?.Trim().ToLowerInvariant();
                if (!DiscordGateModes.IsKnown(mode))
                    return Results.BadRequest(new { error = "`mode` is off, watch or on." });

                // While no pass runs: a pass that started before this save must not go on removing
                // people under a removal time or a mode that has just changed.
                return await gate.RunAloneAsync(() => SaveAsync(http, body, mode!, db, clock, ct), ct);
            })
            .WithName("SetDiscordGateSettings")
            .WithSummary("Update join gate settings")
            .WithDescription(
                "Save the join gate's settings. `mode` is off, watch (record what would happen, do "
                + "nothing in Discord) or on. Changing the mode ends everybody's current time at the "
                + "gate and gates only people who join from then on. `removeAfterMinutes` is one of "
                + "`removeChoices`, or null for never; changing it, or the steps, starts everybody "
                + "waiting no later than halfway again, with a new warning. `needsEighteenPlus` "
                + "needs `needsLink`, and `needsLink` needs account linking to be set up.")
            .Produces<DiscordGateSettingsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .RequiresFlag(ModbotPermissions.ManageSettings);

        return app;
    }

    private static async Task<IResult> SaveAsync(
        HttpContext http, DiscordGateSettingsUpdate body, string mode, ModbotContext db, IModbotClock clock, CancellationToken ct)
    {
        var settings = await db.GetSettingsAsync(ct);

        var role = Blank(body.MemberRoleId);
        var channel = Blank(body.ChannelId);
        var message = Blank(body.Message);

        if (mode != DiscordGateModes.Off && role is null)
            return Results.BadRequest(new { error = "Pick the member role." });

        if (mode == DiscordGateModes.On && channel is null)
            return Results.BadRequest(new { error = "Pick the gate channel." });

        if (message is { Length: > MaxMessageLength })
            return Results.BadRequest(new { error = $"The message is longer than {MaxMessageLength} characters." });

        if (body.RemoveAfterMinutes is { } minutes && !RemoveChoices.Contains(minutes))
            return Results.BadRequest(new { error = "`removeAfterMinutes` is one of the choices, or null for never." });

        if (body.NeedsEighteenPlus && !body.NeedsLink)
            return Results.BadRequest(new { error = "18+ on VRChat needs the Link VRChat account step." });

        var linkingReady = LinkingReady(settings);
        if (body.NeedsLink && !linkingReady)
            return Results.BadRequest(new { error = "Account linking is not set up." });

        if (role is not null)
        {
            var guild = settings.DiscordGuildId ?? string.Empty;
            var known = await db.DiscordRoles.AsNoTracking()
                .FirstOrDefaultAsync(r => r.GuildId == guild && r.RoleId == role && r.RemovedAt == null, ct);

            if (known is { BotCanAssign: false })
                return Results.BadRequest(new { error = $"The bot cannot assign {known.Name}." });
        }

        var change = new SettingsChange("discordGate")
            .Field("mode", settings.DiscordGateMode, mode)
            .Field("memberRoleId", settings.DiscordGateMemberRoleId, role)
            .Field("channelId", settings.DiscordGateChannelId, channel)
            .Field("message", settings.DiscordGateMessage, message)
            .Field("needsLink", settings.DiscordGateNeedsLink, body.NeedsLink)
            .Field("needsEighteenPlus", settings.DiscordGateNeedsEighteenPlus, body.NeedsEighteenPlus)
            .Field("removeAfterMinutes", settings.DiscordGateRemoveAfterMinutes, body.RemoveAfterMinutes)
            .Field("holdOnSpike", settings.DiscordGateHoldOnSpike, body.HoldOnSpike)
            .Field("pauseInvites", settings.DiscordGatePauseInvites, body.PauseInvites);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var now = clock.UtcNow;
        var modeChanged = !string.Equals(settings.DiscordGateMode, mode, StringComparison.Ordinal);

        if (modeChanged)
        {
            // The rows of the old mode end here, and the gate counts joiners from now.
            await db.DiscordGateEntries
                .Where(e => e.ClosedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.ClosedAt, now)
                    .SetProperty(e => e.Outcome, DiscordGateOutcomes.GateChanged), ct);

            settings.DiscordGateStartedAt = mode == DiscordGateModes.Off ? null : now;
            settings.DiscordGateSpikeSeenAt = mode == DiscordGateModes.Off ? null : now;

            // A hold belongs to the gate that was on; a new mode starts without one.
            settings.DiscordGateHeldAt = null;
        }
        else if (settings.DiscordGateRemoveAfterMinutes != body.RemoveAfterMinutes
                 || settings.DiscordGateNeedsLink != body.NeedsLink
                 || settings.DiscordGateNeedsEighteenPlus != body.NeedsEighteenPlus)
        {
            // A new removal time, or new steps somebody waiting now has to do as well: nobody is past
            // the deadline at once, and everybody is warned again with what is asked of them now.
            await RestartClocksAsync(db, body.RemoveAfterMinutes, ct);
        }

        settings.DiscordGateMode = mode!;
        settings.DiscordGateMemberRoleId = role;
        settings.DiscordGateChannelId = channel;
        settings.DiscordGateMessage = message;
        settings.DiscordGateNeedsLink = body.NeedsLink;
        settings.DiscordGateNeedsEighteenPlus = body.NeedsEighteenPlus;
        settings.DiscordGateRemoveAfterMinutes = body.RemoveAfterMinutes;
        settings.DiscordGateHoldOnSpike = body.HoldOnSpike;
        settings.DiscordGatePauseInvites = body.PauseInvites;

        await db.SaveChangesAsync(ct);
        await change.RecordAsync(http, ct);
        await transaction.CommitAsync(ct);

        return Results.Ok(View(settings));
    }

    /// <summary>
    /// A new removal time, or new steps, must not put anybody past the deadline at once (join gate
    /// design §6). Everybody waiting goes back to no later than halfway and loses the warning they had, so the next pass
    /// warns them again with the new deadline and the removal comes at least the warning window
    /// after that. Removal turned off clears the time counted altogether.
    /// </summary>
    public static Task RestartClocksAsync(ModbotContext db, int? removeAfterMinutes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var open = db.DiscordGateEntries.Where(e => e.ClosedAt == null);

        if (removeAfterMinutes is not { } minutes)
        {
            return open.ExecuteUpdateAsync(s => s
                .SetProperty(e => e.MinutesCounted, 0)
                .SetProperty(e => e.WarnedAt, (DateTimeOffset?)null)
                .SetProperty(e => e.WouldRemoveAt, (DateTimeOffset?)null), ct);
        }

        var half = minutes / 2;

        return open.ExecuteUpdateAsync(s => s
            .SetProperty(e => e.MinutesCounted, e => e.MinutesCounted > half ? half : e.MinutesCounted)
            .SetProperty(e => e.WarnedAt, (DateTimeOffset?)null)
            .SetProperty(e => e.WouldRemoveAt, (DateTimeOffset?)null), ct);
    }

    private static DiscordGateSettingsResponse View(Core.Data.Entities.Settings settings) => new(
        DiscordGateModes.IsKnown(settings.DiscordGateMode) ? settings.DiscordGateMode : DiscordGateModes.Off,
        settings.DiscordGateMemberRoleId,
        settings.DiscordGateChannelId,
        settings.DiscordGateMessage,
        settings.DiscordGateNeedsLink,
        settings.DiscordGateNeedsEighteenPlus,
        settings.DiscordGateRemoveAfterMinutes,
        RemoveChoices,
        settings.DiscordGateHoldOnSpike,
        settings.DiscordGatePauseInvites,
        settings.DiscordGateHeldAt,
        LinkingReady(settings),
        DiscordInvite.LinkFor(settings));

    private static bool LinkingReady(Core.Data.Entities.Settings settings)
        => !string.IsNullOrWhiteSpace(settings.DiscordOAuthClientId)
           && settings.DiscordOAuthClientSecretEncrypted is not null
           && DiscordInvite.RedirectUrlFor(settings.PublicAddress) is not null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
