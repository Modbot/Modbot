using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modbot.AI.Insights;
using Modbot.Api.Auth;
using Modbot.Api.Features.Posts;
using Modbot.Api.Features.Spotlight;
using Modbot.Api.Features.Twitch;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;
using Modbot.Core.Time;
using Modbot.Core.Twitch;

namespace Modbot.Api.Features.Settings;

/// <summary>Settings → Spotlight, as stored.</summary>
/// <param name="On">Whether Modbot makes a spotlight draft each week.</param>
/// <param name="Day">The day the draft is made, 0 (Sunday) to 6.</param>
/// <param name="Hour">The hour of that day, 0 to 23, in <paramref name="TimeZone"/>.</param>
/// <param name="TimeZone">The zone the day and hour are in, or null for UTC.</param>
/// <param name="Mode"><c>random</c> (Modbot picks) or <c>staff</c> (staff pick on the Marketing page).</param>
/// <param name="Places">Where the post goes: the sites ticked, with their own choices. Nothing is ticked to start.</param>
/// <param name="VRChatRoles">The VRChat group's roles as the last group read found them, for choosing who a VRChat post is for.</param>
public sealed record SpotlightSettingsView(
    bool On,
    int Day,
    int Hour,
    string? TimeZone,
    string Mode,
    TwitchPostPlaces Places,
    IReadOnlyList<PostRoleChoice> VRChatRoles);

/// <summary>Changes to Settings → Spotlight. Every part left out keeps what is stored.</summary>
/// <param name="On">Turn the weekly draft on or off.</param>
/// <param name="Day">0 (Sunday) to 6.</param>
/// <param name="Hour">0 to 23.</param>
/// <param name="TimeZone">An IANA zone name. Empty is UTC.</param>
/// <param name="Mode"><c>random</c> or <c>staff</c>.</param>
/// <param name="Places">Where the post goes. Each site ticked is checked as the Marketing tab checks a post.</param>
public sealed record SpotlightSettingsUpdate(
    bool? On = null,
    int? Day = null,
    int? Hour = null,
    string? TimeZone = null,
    string? Mode = null,
    TwitchPostPlaces? Places = null);

/// <summary>
/// Settings → Spotlight (spotlight design §3): whether the weekly member spotlight is on, the day and
/// hour its draft is made, how the member is picked, and the sites it goes to. Needs Manage settings,
/// as the Posts and Twitch settings do. Every change is recorded as a settings change.
/// </summary>
public static class SpotlightSettingsEndpoints
{
    public static IEndpointRouteBuilder MapSpotlightSettings(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/settings/spotlight")
            .WithTags("Settings")
            .RequiresFlag(ModbotPermissions.ManageSettings);

        group.MapGet("", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) => Results.Ok(View(await db.GetSettingsAsync(ct))))
            .WithName("GetSpotlightSettings")
            .WithSummary("Get spotlight settings")
            .WithDescription(
                "Whether the weekly member spotlight is on, the day and hour its draft is made and the "
                + "time zone they are in, how the member is picked, and the sites the post goes to.")
            .Produces<SpotlightSettingsView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("", async (
                HttpContext http,
                [FromBody] SpotlightSettingsUpdate body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var problems = new List<string>();

                if (body.Day is < 0 or > 6)
                    problems.Add("The day must be between Sunday and Saturday.");

                if (body.Hour is < 0 or > 23)
                    problems.Add("The hour must be between 0 and 23.");

                var zone = body.TimeZone?.Trim();
                if (!string.IsNullOrEmpty(zone) && InsightTimes.FindTimeZone(zone) is null)
                    problems.Add("That time zone is not known.");

                if (body.Mode is not null && !SpotlightModes.IsKnown(body.Mode))
                    problems.Add("Pick who chooses the member: Modbot or staff.");

                var now = clock.UtcNow;

                // Each site ticked is checked the way the Marketing tab checks a post: the channel
                // is in the server, the role may be mentioned, the group and the account are set up.
                if (body.Places is { } places && problems.Count == 0)
                    problems.AddRange(await TwitchPosts.CheckPlacesAsync(db, places, now, ct));

                if (problems.Count > 0)
                    return Results.BadRequest(new { error = string.Join(" ", problems), problems });

                var settings = await db.GetSettingsAsync(ct);

                var onAfter = body.On ?? settings.SpotlightOn;
                var dayAfter = body.Day ?? settings.SpotlightDay;
                var hourAfter = body.Hour ?? settings.SpotlightHour;
                var zoneAfter = body.TimeZone is null ? settings.SpotlightTimeZone : (zone is { Length: > 0 } ? zone : null);
                var modeAfter = body.Mode ?? settings.SpotlightMode;
                var placesBefore = settings.SpotlightPlaces;
                var placesAfter = body.Places is { } given ? given.Write() : placesBefore;

                var change = new SettingsChange("spotlight")
                    .Field("spotlightOn", settings.SpotlightOn, onAfter)
                    .Field("day", settings.SpotlightDay, dayAfter)
                    .Field("hour", settings.SpotlightHour, hourAfter)
                    .Field("timeZone", settings.SpotlightTimeZone, zoneAfter)
                    .Field("mode", settings.SpotlightMode, modeAfter)
                    .Document(placesBefore, placesAfter);

                if (change.IsEmpty)
                    return Results.Ok(View(settings));

                var scheduleChanged = dayAfter != settings.SpotlightDay
                    || hourAfter != settings.SpotlightHour
                    || !string.Equals(zoneAfter, settings.SpotlightTimeZone, StringComparison.Ordinal);

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                var turnedOn = onAfter && !settings.SpotlightOn;

                settings.SpotlightOn = onAfter;
                settings.SpotlightDay = dayAfter;
                settings.SpotlightHour = hourAfter;
                settings.SpotlightTimeZone = zoneAfter;
                settings.SpotlightMode = modeAfter;
                settings.SpotlightPlaces = placesAfter;

                // A time that went by before this was turned on, or moved, never makes a draft
                // straight away: the first one is the next time the settings name.
                if (onAfter && (turnedOn || scheduleChanged))
                    settings.SpotlightHandledThrough = SpotlightPass.LatestMoment(settings, now);

                await db.SaveChangesAsync(ct);
                await change.RecordAsync(http, ct);
                await transaction.CommitAsync(ct);

                return Results.Ok(View(settings));
            })
            .WithName("SetSpotlightSettings")
            .WithSummary("Update spotlight settings")
            .WithDescription(
                "Turn the weekly member spotlight on or off, or change the day and hour of its draft "
                + "(`day` 0 is Sunday; `hour` 0 to 23 in `timeZone`, an IANA name, empty for UTC), "
                + "who picks the member (`mode`: `random` or `staff`) or the sites it goes to. Each "
                + "site ticked is checked as the Marketing tab checks a post. Turning it on, or moving "
                + "the time, never makes a draft for a time already past. A refusal (400) lists "
                + "everything wrong at once, in `problems`.")
            .Accepts<SpotlightSettingsUpdate>("application/json")
            .Produces<SpotlightSettingsView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    internal static SpotlightSettingsView View(Core.Data.Entities.Settings settings) => new(
        settings.SpotlightOn,
        settings.SpotlightDay,
        settings.SpotlightHour,
        settings.SpotlightTimeZone,
        settings.SpotlightMode,
        TwitchPostPlaces.Parse(settings.SpotlightPlaces),
        PostRequests.VRChatRoles(settings));
}
