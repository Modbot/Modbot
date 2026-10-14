using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.Analytics.Calendar;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Settings;

/// <summary>Settings → Discord → Member milestones, as stored.</summary>
/// <param name="FirstAt">The number of events at which a person's first moment is marked.</param>
/// <param name="RegularAt">The number of events at which a person is a Regular.</param>
/// <param name="BackDays">How many days away make a return a welcome back.</param>
/// <param name="ChannelId">The channel the posts go in. Null for none.</param>
/// <param name="PostFirst">Whether the first moment gets a post.</param>
/// <param name="PostRegular">Whether becoming a Regular gets a post.</param>
/// <param name="PostBack">Whether a return gets a post.</param>
/// <param name="RegularRoleId">The role given to a Regular with a linked account. Null for none.</param>
/// <param name="Problem">What stopped the last post or role. Null when all is well.</param>
public sealed record DiscordMilestoneSettingsResponse(
    int FirstAt,
    int RegularAt,
    int BackDays,
    string? ChannelId,
    bool PostFirst,
    bool PostRegular,
    bool PostBack,
    string? RegularRoleId,
    string? Problem);

/// <summary>Changes to Settings → Discord → Member milestones. Every part left out keeps what is stored.</summary>
/// <param name="FirstAt">The number of events for the first moment.</param>
/// <param name="RegularAt">The number of events for a Regular.</param>
/// <param name="BackDays">The days away for a welcome back.</param>
/// <param name="ChannelId">The channel. Empty for none.</param>
/// <param name="PostFirst">Post for the first moment.</param>
/// <param name="PostRegular">Post for a new Regular.</param>
/// <param name="PostBack">Post for a return.</param>
/// <param name="RegularRoleId">The Regular role. Empty for none.</param>
public sealed record DiscordMilestoneSettingsUpdate(
    int? FirstAt = null,
    int? RegularAt = null,
    int? BackDays = null,
    string? ChannelId = null,
    bool? PostFirst = null,
    bool? PostRegular = null,
    bool? PostBack = null,
    string? RegularRoleId = null);

/// <summary>
/// Settings → Discord → Member milestones: the numbers behind the first-event, Regular and welcome-back
/// moments, which of them get a post and where, and the role given to a Regular. The marks on a
/// profile need none of this. Needs Manage settings. Every change is recorded as a settings change.
/// </summary>
public static class DiscordMilestoneSettingsEndpoints
{
    /// <summary>The most events a number can name.</summary>
    public const int MostEvents = 1000;

    /// <summary>The most days away a welcome back can name.</summary>
    public const int MostDays = 3650;

    public static IEndpointRouteBuilder MapDiscordMilestoneSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/discord-milestones")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("", async ([FromServices] ModbotContext db, CancellationToken ct)
                => Results.Ok(await ViewAsync(db, await db.GetSettingsAsync(ct), ct)))
            .WithName("GetDiscordMilestoneSettings")
            .WithSummary("Get member milestone settings")
            .WithDescription(
                "The numbers behind the first-event, Regular and welcome-back moments, which of them get a "
                + "post in the channel, and the role given to a Regular who has a linked account. `problem` "
                + "says what stopped the last post or role, or that Modbot cannot give the role.")
            .Produces<DiscordMilestoneSettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] DiscordMilestoneSettingsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);

                var firstAfter = body.FirstAt ?? settings.MilestoneFirstAt;
                var regularAfter = body.RegularAt ?? settings.MilestoneRegularAt;
                var daysAfter = body.BackDays ?? settings.MilestoneBackDays;
                var channelAfter = body.ChannelId is null ? settings.MilestoneChannelId : Clean(body.ChannelId);
                var postFirstAfter = body.PostFirst ?? settings.MilestonePostFirst;
                var postRegularAfter = body.PostRegular ?? settings.MilestonePostRegular;
                var postBackAfter = body.PostBack ?? settings.MilestonePostBack;
                var roleAfter = body.RegularRoleId is null ? settings.MilestoneRegularRoleId : Clean(body.RegularRoleId);

                var problems = new List<string>();

                if (firstAfter is < 1 or > MostEvents)
                    problems.Add($"The first moment is at an event number from 1 to {MostEvents}.");

                if (regularAfter is < 1 or > MostEvents)
                    problems.Add($"A Regular is at an event number from 1 to {MostEvents}.");

                if (daysAfter is < 1 or > MostDays)
                    problems.Add($"A welcome back is after 1 to {MostDays} days.");

                if ((postFirstAfter || postRegularAfter || postBackAfter) && channelAfter is null)
                    problems.Add("Pick a channel to post in.");

                if (roleAfter is not null
                    && roleAfter != settings.MilestoneRegularRoleId
                    && await MilestoneRoleCheck.RefusalAsync(db, roleAfter, ct) is { } refusal)
                {
                    problems.Add(refusal);
                }

                if (problems.Count > 0)
                    return Results.BadRequest(new { error = string.Join(" ", problems), problems });

                var change = new SettingsChange("discordMilestones")
                    .Field("firstAt", settings.MilestoneFirstAt, firstAfter)
                    .Field("regularAt", settings.MilestoneRegularAt, regularAfter)
                    .Field("backDays", settings.MilestoneBackDays, daysAfter)
                    .Field("channelId", settings.MilestoneChannelId, channelAfter)
                    .Field("postFirst", settings.MilestonePostFirst, postFirstAfter)
                    .Field("postRegular", settings.MilestonePostRegular, postRegularAfter)
                    .Field("postBack", settings.MilestonePostBack, postBackAfter)
                    .Field("regularRoleId", settings.MilestoneRegularRoleId, roleAfter);

                if (change.IsEmpty)
                    return Results.Ok(await ViewAsync(db, settings, ct));

                var wasOn = IsOn(settings.MilestonePostFirst, settings.MilestonePostRegular, settings.MilestonePostBack, settings.MilestoneChannelId, settings.MilestoneRegularRoleId);
                var isOn = IsOn(postFirstAfter, postRegularAfter, postBackAfter, channelAfter, roleAfter);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                // Only moments reached from now on are acted on: turning posts or the role on never
                // reaches back to people who got there long ago.
                if (isOn && !wasOn)
                    settings.MilestonesFrom = clock.UtcNow;

                settings.MilestoneFirstAt = firstAfter;
                settings.MilestoneRegularAt = regularAfter;
                settings.MilestoneBackDays = daysAfter;
                settings.MilestoneChannelId = channelAfter;
                settings.MilestonePostFirst = postFirstAfter;
                settings.MilestonePostRegular = postRegularAfter;
                settings.MilestonePostBack = postBackAfter;
                settings.MilestoneRegularRoleId = roleAfter;
                settings.MilestoneProblem = null;

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(db, settings, ct));
            })
            .WithName("SetDiscordMilestoneSettings")
            .WithSummary("Update member milestone settings")
            .WithDescription(
                "Change the event numbers for the first moment (`firstAt`) and for a Regular (`regularAt`), the "
                + "days away for a welcome back (`backDays`), the channel (empty for none), which moments get a "
                + "post, and the Regular role (empty for none). A role that holds a dangerous permission is "
                + "refused. Turning a post or the role on never acts for a moment reached before. A refusal "
                + "(400) lists everything wrong at once, in `problems`.")
            .Accepts<DiscordMilestoneSettingsUpdate>("application/json")
            .Produces<DiscordMilestoneSettingsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static bool IsOn(bool first, bool regular, bool back, string? channelId, string? roleId)
        => roleId is not null || (channelId is not null && (first || regular || back));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<DiscordMilestoneSettingsResponse> ViewAsync(
        ModbotContext db, Core.Data.Entities.Settings settings, CancellationToken ct)
    {
        var problem = settings.MilestoneRegularRoleId is { } roleId
            ? await MilestoneRoleCheck.CannotGiveAsync(db, roleId, ct) ?? settings.MilestoneProblem
            : settings.MilestoneProblem;

        return new DiscordMilestoneSettingsResponse(
            settings.MilestoneFirstAt,
            settings.MilestoneRegularAt,
            settings.MilestoneBackDays,
            settings.MilestoneChannelId,
            settings.MilestonePostFirst,
            settings.MilestonePostRegular,
            settings.MilestonePostBack,
            settings.MilestoneRegularRoleId,
            problem);
    }
}
