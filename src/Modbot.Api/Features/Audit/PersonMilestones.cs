using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Audit;

/// <summary>What the milestones know about one person, for the profile.</summary>
/// <param name="EventsCame">How many dates of events they came to.</param>
/// <param name="FirstAt">The event number the settings call the first moment.</param>
/// <param name="FirstDate">The date of that event, or null when they have not come to that many.</param>
/// <param name="RegularAt">The event number at which a person is a Regular.</param>
/// <param name="RegularSince">The date of that event, or null when they have not come to that many.</param>
/// <param name="BackAt">The visit that ended their latest long gap, or null when there has been none.</param>
/// <param name="BackAfterDays">How many days that gap was.</param>
public sealed record PersonMilestonesResponse(
    int EventsCame,
    int FirstAt,
    DateTimeOffset? FirstDate,
    int RegularAt,
    DateTimeOffset? RegularSince,
    DateTimeOffset? BackAt,
    int? BackAfterDays);

/// <summary>
/// The marks on a person's profile: how many events they came to, the date of their first and of
/// the one that made them a Regular, and when they came back after a long time. Always available;
/// the settings only change the numbers.
/// </summary>
public static class PersonMilestones
{
    public static IEndpointRouteBuilder MapPersonMilestones(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/audit/milestones", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromQuery] string? person,
                CancellationToken ct) =>
            {
                var held = ModbotAuth.PermissionsOf(http.User);

                if (!AuditVisibility.CanSeeType(held, FactType.InstanceJoined))
                    return Results.Forbid();

                if (string.IsNullOrWhiteSpace(person))
                    return Results.BadRequest(new { error = "person is required." });

                return Results.Ok(await ReadAsync(db, person.Trim(), ct));
            })
            .RequireAuthorization()
            .WithTags("Audit")
            .WithName("GetPersonMilestones")
            .WithSummary("Get a person's milestones")
            .WithDescription(
                "How many dates of calendar events one VRChat user came to, the date of the event Modbot calls "
                + "their first moment (`firstAt`) and of the one that made them a Regular (`regularAt`), and the "
                + "visit that ended their latest gap of the welcome-back length or more. Counted from the "
                + "companion's presence reports, so only people in an instance where a moderator's app was "
                + "present are counted.\n\n"
                + "Needs ViewAuditLog, the permission that reads the same arrivals in the audit log.")
            .Produces<PersonMilestonesResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    public static async Task<PersonMilestonesResponse> ReadAsync(ModbotContext db, string userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var settings = await db.GetSettingsAsync(ct);

        var came = await db.EventAttendees.AsNoTracking().CountAsync(a => a.UserId == userId, ct);

        var dates = came == 0
            ? []
            : await db.EventAttendees.AsNoTracking()
                .Where(a => a.UserId == userId)
                .OrderBy(a => a.OccurrenceStartsAt)
                .ThenBy(a => a.EventId)
                .Select(a => a.OccurrenceStartsAt)
                .Take(Math.Max(settings.MilestoneFirstAt, settings.MilestoneRegularAt))
                .ToListAsync(ct);

        var row = await db.MemberMilestones.AsNoTracking().FirstOrDefaultAsync(m => m.UserId == userId, ct);

        return new PersonMilestonesResponse(
            came,
            settings.MilestoneFirstAt,
            DateOf(dates, settings.MilestoneFirstAt),
            settings.MilestoneRegularAt,
            DateOf(dates, settings.MilestoneRegularAt),
            row?.BackAt,
            row?.BackAfterDays);
    }

    private static DateTimeOffset? DateOf(List<DateTimeOffset> dates, int number)
        => number >= 1 && number <= dates.Count ? dates[number - 1] : null;
}
