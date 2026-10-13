using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.AI.Insights;
using Modbot.Api.Auth;
using Modbot.Api.Features.Birthdays;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Settings;

/// <summary>Settings → Discord → Birthdays, as stored.</summary>
/// <param name="On">Whether Modbot posts the day's birthdays.</param>
/// <param name="ChannelId">The channel the post goes in. Null for none.</param>
/// <param name="Hour">The hour of the day, 0 to 23, in <paramref name="TimeZone"/>.</param>
/// <param name="TimeZone">The zone the hour is in, or null for UTC.</param>
/// <param name="Message">The post's text. <c>{members}</c> stands for the members with a birthday.</param>
/// <param name="RoleId">The role members hold for their birthday. Null for none.</param>
/// <param name="Problem">What stopped the last post or role change, or why the role cannot be given now. Null when all is well.</param>
public sealed record DiscordBirthdaySettingsResponse(
    bool On,
    string? ChannelId,
    int Hour,
    string? TimeZone,
    string Message,
    string? RoleId,
    string? Problem);

/// <summary>Changes to Settings → Discord → Birthdays. Every part left out keeps what is stored.</summary>
/// <param name="On">Turn the daily post on or off.</param>
/// <param name="ChannelId">The channel. Empty for none.</param>
/// <param name="Hour">0 to 23.</param>
/// <param name="TimeZone">An IANA zone name. Empty is UTC.</param>
/// <param name="Message">The text. Empty for the built-in one.</param>
/// <param name="RoleId">The birthday role. Empty for none.</param>
public sealed record DiscordBirthdaySettingsUpdate(
    bool? On = null,
    string? ChannelId = null,
    int? Hour = null,
    string? TimeZone = null,
    string? Message = null,
    string? RoleId = null);

/// <summary>
/// Settings → Discord → Birthdays: whether Modbot posts the day's birthdays, where and when, what it
/// says, and the role members hold for the day. Needs Manage settings. Every change is recorded as a
/// settings change.
/// </summary>
public static class DiscordBirthdaySettingsEndpoints
{
    public static IEndpointRouteBuilder MapDiscordBirthdaySettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/discord-birthdays")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("", async ([FromServices] ModbotContext db, CancellationToken ct)
                => Results.Ok(await ViewAsync(db, await db.GetSettingsAsync(ct), ct)))
            .WithName("GetDiscordBirthdaySettings")
            .WithSummary("Get birthday settings")
            .WithDescription(
                "Whether Modbot posts the day's birthdays in Discord, the channel, the hour and time zone, the "
                + "text, and the role members hold for the day. `problem` says what stopped the last post or "
                + "role change, or why the role cannot be given now.")
            .Produces<DiscordBirthdaySettingsResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] DiscordBirthdaySettingsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);

                var onAfter = body.On ?? settings.BirthdaysOn;
                var channelAfter = body.ChannelId is null ? settings.BirthdayChannelId : Clean(body.ChannelId);
                var hourAfter = body.Hour ?? settings.BirthdayHour;
                var zone = body.TimeZone?.Trim();
                var zoneAfter = body.TimeZone is null ? settings.BirthdayTimeZone : (zone is { Length: > 0 } ? zone : null);
                var messageAfter = body.Message is null ? settings.BirthdayMessage : Clean(body.Message);
                var roleAfter = body.RoleId is null ? settings.BirthdayRoleId : Clean(body.RoleId);

                if (messageAfter == BirthdayRules.DefaultMessage)
                    messageAfter = null;

                var problems = new List<string>();

                if (hourAfter is < 0 or > 23)
                    problems.Add("The hour must be between 0 and 23.");

                if (zoneAfter is not null && InsightTimes.FindTimeZone(zoneAfter) is null)
                    problems.Add("That time zone is not known.");

                if (messageAfter is { Length: > BirthdayRules.MaxMessageLength })
                    problems.Add($"The text is at most {BirthdayRules.MaxMessageLength} characters.");

                if (onAfter && channelAfter is null)
                    problems.Add("Pick a channel to post in.");

                if (roleAfter is not null
                    && roleAfter != settings.BirthdayRoleId
                    && await BirthdayRoleCheck.RefusalAsync(db, roleAfter, ct) is { } refusal)
                {
                    problems.Add(refusal);
                }

                if (problems.Count > 0)
                    return Results.BadRequest(new { error = string.Join(" ", problems), problems });

                var change = new SettingsChange("discordBirthdays")
                    .Field("birthdaysOn", settings.BirthdaysOn, onAfter)
                    .Field("channelId", settings.BirthdayChannelId, channelAfter)
                    .Field("hour", settings.BirthdayHour, hourAfter)
                    .Field("timeZone", settings.BirthdayTimeZone, zoneAfter)
                    .Field("message", settings.BirthdayMessage, messageAfter)
                    .Field("roleId", settings.BirthdayRoleId, roleAfter);

                if (change.IsEmpty)
                    return Results.Ok(await ViewAsync(db, settings, ct));

                var scheduleChanged = hourAfter != settings.BirthdayHour
                    || !string.Equals(zoneAfter, settings.BirthdayTimeZone, StringComparison.Ordinal);
                var turnedOn = onAfter && !settings.BirthdaysOn;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.BirthdaysOn = onAfter;
                settings.BirthdayChannelId = channelAfter;
                settings.BirthdayHour = hourAfter;
                settings.BirthdayTimeZone = zoneAfter;
                settings.BirthdayMessage = messageAfter;
                settings.BirthdayRoleId = roleAfter;
                settings.BirthdayProblem = null;

                // A time that went by before this was turned on, or moved, never posts straight
                // away: the first post is the next time the settings name.
                if (onAfter && (turnedOn || scheduleChanged))
                    settings.BirthdayHandledThrough = BirthdayPass.LatestMoment(settings, clock.UtcNow);

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(await ViewAsync(db, settings, ct));
            })
            .WithName("SetDiscordBirthdaySettings")
            .WithSummary("Update birthday settings")
            .WithDescription(
                "Turn the daily birthday post on or off, or change its channel, its hour (`hour` 0 to 23 in "
                + "`timeZone`, an IANA name, empty for UTC), its text (`{members}` stands for the members; "
                + "empty for the built-in text) or the birthday role (empty for none). A role that holds a "
                + "dangerous permission is refused. Turning it on, or moving the hour, never posts for a time "
                + "already past. A refusal (400) lists everything wrong at once, in `problems`.")
            .Accepts<DiscordBirthdaySettingsUpdate>("application/json")
            .Produces<DiscordBirthdaySettingsResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<DiscordBirthdaySettingsResponse> ViewAsync(
        ModbotContext db, Core.Data.Entities.Settings settings, CancellationToken ct)
    {
        var problem = settings.BirthdayRoleId is { } roleId
            ? await BirthdayRoleCheck.CannotGiveAsync(db, roleId, ct) ?? settings.BirthdayProblem
            : settings.BirthdayProblem;

        return new DiscordBirthdaySettingsResponse(
            settings.BirthdaysOn,
            settings.BirthdayChannelId,
            settings.BirthdayHour,
            settings.BirthdayTimeZone,
            settings.BirthdayMessage ?? BirthdayRules.DefaultMessage,
            settings.BirthdayRoleId,
            problem);
    }
}
