using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Notifications;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Notifications;

/// <param name="Id">The notification.</param>
/// <param name="Kind">What happened, as a dotted name.</param>
/// <param name="Severity"><c>critical</c>, <c>warning</c> or <c>information</c>.</param>
/// <param name="Title">One line.</param>
/// <param name="Body">A sentence or two.</param>
/// <param name="Link">Where in Modbot to look, or null.</param>
/// <param name="At">When it last happened.</param>
/// <param name="Repeats">How many more times it happened while it was being kept quiet.</param>
/// <param name="Waiting">True when it is critical and reached this person on no channel.</param>
/// <param name="Seen">Whether this person has marked it seen.</param>
public sealed record NotificationView(
    Guid Id,
    string Kind,
    string Severity,
    string Title,
    string Body,
    string? Link,
    DateTimeOffset At,
    int Repeats,
    bool Waiting,
    bool Seen);

/// <param name="Waiting">Critical notifications that reached this person on no channel and are unseen.</param>
/// <param name="Recent">The latest notifications for this person, newest first.</param>
public sealed record NotificationsView(
    IReadOnlyList<NotificationView> Waiting,
    IReadOnlyList<NotificationView> Recent);

/// <param name="Channel"><c>email</c> or <c>discord</c>.</param>
/// <param name="Label">What to call it on the screen.</param>
/// <param name="Level">The least serious thing that goes out here.</param>
/// <param name="DailySummary">Whether a daily summary goes out here.</param>
/// <param name="CanReach">Whether this channel can reach this person at all right now.</param>
public sealed record NotificationChoiceView(
    string Channel,
    string Label,
    string Level,
    bool DailySummary,
    bool CanReach);

/// <param name="Channels">One for each channel this build has.</param>
public sealed record NotificationChoicesView(IReadOnlyList<NotificationChoiceView> Channels);

/// <param name="Channel">Which channel to set.</param>
/// <param name="Level"><c>off</c>, <c>critical</c>, <c>warning</c> or <c>everything</c>.</param>
/// <param name="DailySummary">Whether a daily summary goes out here.</param>
public sealed record NotificationChoiceUpdate(string Channel, string Level, bool DailySummary);

/// <param name="Channels">Every channel to set. Channels left out are unchanged.</param>
public sealed record NotificationChoicesUpdate(IReadOnlyList<NotificationChoiceUpdate> Channels);

/// <summary>
/// A person's own notifications and their own channel settings (foundation §4.5).
/// </summary>
/// <remarks>
/// <para>
/// No permission flag anywhere here. Everything answers about the account that is signed in and
/// nothing else, so there is nothing to grant: a moderator changing where their own alerts go is
/// the same kind of act as changing their own password.
/// </para>
/// <para>
/// <c>waiting</c> is the whole reason the read exists at all. A critical notification that reached
/// somebody on no channel is not lost — it is here, and the app shows it above every page until
/// they mark it seen (§4.5.3).
/// </para>
/// </remarks>
public static class NotificationEndpoints
{
    private const int RecentShown = 50;

    public static IEndpointRouteBuilder MapNotifications(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/notifications").WithTags("Notifications").RequireAuthorization();

        group.MapGet("", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                if (ModbotAuth.UserIdOf(http.User) is not { } userId)
                    return Results.Unauthorized();

                var mine = await db.NotificationsForPeople.AsNoTracking()
                    .Include(p => p.Notification)
                    .Where(p => p.UserId == userId)
                    .OrderByDescending(p => p.Notification.LastAt)
                    .Take(RecentShown)
                    .ToListAsync(ct);

                var views = mine.Select(View).ToList();

                return Results.Ok(new NotificationsView(
                    views.Where(v => v is { Waiting: true, Seen: false }).ToList(),
                    views));
            })
            .WithName("ListNotifications")
            .WithSummary("This account's notifications, and the critical ones still waiting to be seen")
            .Produces<NotificationsView>();

        group.MapPost("/{id:guid}/seen", async (
                Guid id,
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                if (ModbotAuth.UserIdOf(http.User) is not { } userId)
                    return Results.Unauthorized();

                var row = await db.NotificationsForPeople
                    .FirstOrDefaultAsync(p => p.NotificationId == id && p.UserId == userId, ct);

                if (row is null)
                    return Results.NotFound();

                // Seen, never cleared. What happened stays on the record: a warning that vanished
                // the moment somebody glanced at it is a warning nobody can go back and check.
                row.SeenAt ??= clock.UtcNow;
                await db.SaveChangesAsync(ct);

                return Results.NoContent();
            })
            .WithName("MarkNotificationSeen")
            .WithSummary("Marks one notification seen by this account")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/choices", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IEnumerable<INotificationChannel> channels,
                CancellationToken ct) =>
            {
                if (ModbotAuth.UserIdOf(http.User) is not { } userId)
                    return Results.Unauthorized();

                return Results.Ok(await ChoicesAsync(db, channels, userId, ct));
            })
            .WithName("GetNotificationChoices")
            .WithSummary("Where this account's notifications go")
            .Produces<NotificationChoicesView>();

        group.MapPut("/choices", async (
                [FromBody] NotificationChoicesUpdate body,
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IEnumerable<INotificationChannel> channels,
                CancellationToken ct) =>
            {
                if (ModbotAuth.UserIdOf(http.User) is not { } userId)
                    return Results.Unauthorized();

                ArgumentNullException.ThrowIfNull(body);

                foreach (var wanted in body.Channels ?? [])
                {
                    if (!NotificationChannels.IsKnown(wanted.Channel))
                        return Results.BadRequest(new { error = $"There is no {wanted.Channel} channel." });

                    if (!NotificationLevels.IsKnown(wanted.Level))
                        return Results.BadRequest(new { error = $"\"{wanted.Level}\" is not one of the choices." });
                }

                foreach (var wanted in body.Channels ?? [])
                {
                    var row = await db.NotificationChoices
                        .FirstOrDefaultAsync(c => c.UserId == userId && c.Channel == wanted.Channel, ct);

                    if (row is null)
                    {
                        db.NotificationChoices.Add(new NotificationChoice
                        {
                            UserId = userId,
                            Channel = wanted.Channel,
                            Level = wanted.Level,
                            DailySummary = wanted.DailySummary,
                        });

                        continue;
                    }

                    row.Level = wanted.Level;
                    row.DailySummary = wanted.DailySummary;
                }

                await db.SaveChangesAsync(ct);

                return Results.Ok(await ChoicesAsync(db, channels, userId, ct));
            })
            .WithName("SetNotificationChoices")
            .WithSummary("Sets where this account's notifications go")
            .Produces<NotificationChoicesView>()
            .Produces(StatusCodes.Status400BadRequest);

        return app;
    }

    private static async Task<NotificationChoicesView> ChoicesAsync(
        ModbotContext db, IEnumerable<INotificationChannel> channels, Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);

        var rows = await db.NotificationChoices.AsNoTracking()
            .Where(c => c.UserId == userId)
            .ToListAsync(ct);

        var byName = channels.ToDictionary(c => c.Name);
        var views = new List<NotificationChoiceView>();

        foreach (var name in NotificationChannels.All)
        {
            var row = rows.FirstOrDefault(c => c.Channel == name);
            var (level, summary) = row is null
                ? NotificationChannels.Default(name)
                : (row.Level, row.DailySummary);

            var canReach = user is not null
                           && byName.TryGetValue(name, out var channel)
                           && await channel.CanReachAsync(user, ct);

            views.Add(new NotificationChoiceView(name, NotificationChannels.Label(name), level, summary, canReach));
        }

        return new NotificationChoicesView(views);
    }

    private static NotificationView View(NotificationForPerson row) => new(
        row.NotificationId,
        row.Notification.Kind,
        row.Notification.Severity,
        row.Notification.Title,
        row.Notification.Body,
        row.Notification.Link,
        row.Notification.LastAt,
        row.Notification.Repeats,
        row.Waiting,
        row.SeenAt is not null);
}
