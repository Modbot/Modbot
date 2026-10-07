using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Conventions;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Availability;

/// <summary>
/// Availability: the hours of the week each person on the team is free, entered by each person for
/// themselves, and the team's hours together (availability design).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two permissions, two readers.</strong> Enter availability lets a person read and replace
/// <em>their own</em> week and nobody else's. See availability reads everyone's. Neither implies the
/// other, and the server refuses whatever the sidebar shows.
/// </para>
/// <para>
/// <strong>Hours are kept in the person's own time zone</strong>, as a day and an hour of their own
/// clock, and sent that way. Whoever is looking converts them for the dates they are looking at,
/// which is what keeps a daylight-saving change from moving anybody's evening.
/// </para>
/// <para>
/// <strong>The team is everyone who may enter times</strong> and whose account is neither disabled
/// nor deleted: Enter availability on one of their roles, or Administrator.
/// </para>
/// </remarks>
public static class AvailabilityEndpoints
{
    public static IEndpointRouteBuilder MapAvailability(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/availability").WithTags("Availability");

        group.MapGet("/mine", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                if (ModbotAuth.UserIdOf(http.User) is not { } me)
                    return Results.Forbid();

                return Results.Ok(await ViewOfAsync(db, me, ct));
            })
            .RequiresFlag(ModbotPermissions.EnterAvailability)
            .WithName("GetMyAvailability")
            .WithSummary("Get my availability")
            .WithDescription(
                "The signed-in person's own week: the time zone it is in and the hours they are free or "
                + "free if needed. An hour that is not listed is not free. Days count from Monday as 0.")
            .Produces<MyAvailabilityView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/mine", async (
                HttpContext http,
                [FromBody] MyAvailabilityRequest body,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                if (ModbotAuth.UserIdOf(http.User) is not { } me)
                    return Results.Forbid();

                if (Check(body, out var zone, out var cells) is { } problem)
                    return Results.BadRequest(new { error = problem });

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                try
                {
                    // Only this person's rows: the account comes from the session, never from the body.
                    await db.StaffAvailabilities.Where(a => a.UserId == me).ExecuteDeleteAsync(ct);

                    db.StaffAvailabilities.AddRange(cells.Select(c => new StaffAvailability
                    {
                        UserId = me,
                        Day = c.Day,
                        Hour = c.Hour,
                        State = c.State,
                    }));

                    var saved = await db.StaffAvailabilityZones.FirstOrDefaultAsync(z => z.UserId == me, ct);
                    var now = clock.UtcNow;

                    if (saved is null)
                        db.StaffAvailabilityZones.Add(new StaffAvailabilityZone { UserId = me, TimeZone = zone, SavedAt = now });
                    else
                    {
                        saved.TimeZone = zone;
                        saved.SavedAt = now;
                    }

                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                }
                catch (DbUpdateException)
                {
                    // Two saves from the same person at once: the later one finds the first one's rows.
                    return Problems.Of(StatusCodes.Status409Conflict, "Saved from somewhere else just now. Try again.");
                }

                return Results.Ok(await ViewOfAsync(db, me, ct));
            })
            .RequiresFlag(ModbotPermissions.EnterAvailability)
            .WithName("SaveMyAvailability")
            .WithSummary("Save my availability")
            .WithDescription(
                $"Replaces the signed-in person's whole week with the hours sent: at most {StaffAvailabilityRules.WeekHours}, one "
                + "for each hour at most, each `free` or `ifNeeded`, with the IANA time zone they are in. "
                + "Days count from Monday as 0, hours from 0 to 23 on the person's own clock. Only the "
                + "caller's own week is ever changed.")
            .Produces<MyAvailabilityView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);

        group.MapGet("/", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                // What somebody may do is the union over their roles, so who may enter times is
                // worked out here rather than in the query.
                var users = await db.Users.AsNoTracking()
                    .Where(u => u.DeletedAt == null && !u.IsDisabled)
                    .Include(u => u.Roles).ThenInclude(r => r.Role)
                    .OrderBy(u => u.Username)
                    .ToListAsync(ct);

                var team = users
                    .Where(u => ModbotAuth.Allows(u.EffectivePermissions, ModbotPermissions.EnterAvailability))
                    .ToList();

                var ids = team.Select(u => u.Id).ToList();

                var zones = await db.StaffAvailabilityZones.AsNoTracking()
                    .Where(z => ids.Contains(z.UserId))
                    .ToDictionaryAsync(z => z.UserId, z => z.TimeZone, ct);

                var rows = await db.StaffAvailabilities.AsNoTracking()
                    .Where(a => ids.Contains(a.UserId))
                    .OrderBy(a => a.Day).ThenBy(a => a.Hour)
                    .ToListAsync(ct);

                var byPerson = rows.ToLookup(a => a.UserId);

                return Results.Ok(new TeamAvailabilityView(
                    [.. team.Select(u => new TeamAvailabilityPerson(
                        u.Id,
                        u.Username,
                        [.. u.Roles.Select(r => r.Role).OrderBy(r => r.Position).ThenBy(r => r.CreatedAt).Select(r => r.Name)],
                        zones.GetValueOrDefault(u.Id),
                        [.. byPerson[u.Id].Select(a => new AvailabilityCell(a.Day, a.Hour, a.State))]))]));
            })
            .RequiresFlag(ModbotPermissions.ViewAvailability)
            .WithName("GetTeamAvailability")
            .WithSummary("Get the team's availability")
            .WithDescription(
                "Everyone who may enter availability and whose account is neither disabled nor deleted: "
                + "name, role names, the time zone their week is in and the hours they are free or free "
                + "if needed, each on that person's own clock. Someone who has saved nothing has no "
                + "zone and no hours.")
            .Produces<TeamAvailabilityView>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<MyAvailabilityView> ViewOfAsync(ModbotContext db, Guid user, CancellationToken ct)
    {
        var zone = await db.StaffAvailabilityZones.AsNoTracking().FirstOrDefaultAsync(z => z.UserId == user, ct);

        var cells = await db.StaffAvailabilities.AsNoTracking()
            .Where(a => a.UserId == user)
            .OrderBy(a => a.Day).ThenBy(a => a.Hour)
            .Select(a => new AvailabilityCell(a.Day, a.Hour, a.State))
            .ToListAsync(ct);

        return new MyAvailabilityView(zone?.TimeZone, cells, zone?.SavedAt);
    }

    /// <summary>
    /// What is wrong with a week as sent, in a sentence, or null. On success, the zone as the zone
    /// database spells it and the hours. A repeated hour is an error, not a choice between two.
    /// </summary>
    private static string? Check(MyAvailabilityRequest body, out string zone, out IReadOnlyList<AvailabilityCell> cells)
    {
        zone = string.Empty;
        cells = [];

        if (CalendarRepeat.FindZone(body.TimeZone) is not { } found)
            return string.IsNullOrWhiteSpace(body.TimeZone) ? "Choose a time zone." : "That time zone is not known.";

        var sent = body.Cells ?? [];

        if (sent.Count > StaffAvailabilityRules.WeekHours)
            return $"At most {StaffAvailabilityRules.WeekHours} hours.";

        var seen = new HashSet<(int, int)>();

        foreach (var cell in sent)
        {
            if (cell is null || cell.Day is < 0 or > 6)
                return "A day must be 0 to 6, Monday first.";

            if (cell.Hour is < 0 or > 23)
                return "An hour must be 0 to 23.";

            if (!AvailabilityStates.IsKnown(cell.State))
                return "A state must be free or ifNeeded.";

            if (!seen.Add((cell.Day, cell.Hour)))
                return "Each hour can be set once.";
        }

        zone = found.Id;
        cells = sent;
        return null;
    }
}
