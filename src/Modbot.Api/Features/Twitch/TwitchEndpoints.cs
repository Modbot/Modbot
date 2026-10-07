using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Core.Twitch;

namespace Modbot.Api.Features.Twitch;

/// <summary>
/// What staff read about Twitch (Twitch design, steps 1 and 3): whether the channel is live, the
/// recent streams, linking a stream to a calendar event, and the Health card. Settings → Twitch has
/// its own endpoints.
/// </summary>
public static class TwitchEndpoints
{
    /// <summary>How many recent streams the event link's list offers.</summary>
    public const int RecentStreams = 20;

    /// <summary>How long Twitch may stay silent before Health says so.</summary>
    public static readonly TimeSpan SilentAfter = TimeSpan.FromMinutes(5);

    public const string NoSuchStream = "That stream does not exist.";
    public const string NoSuchEvent = "That event does not exist.";

    public static IEndpointRouteBuilder MapTwitch(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/twitch").WithTags("Twitch");

        group.MapGet("/live", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

                if (settings is null || !settings.TwitchLiveOn || !TwitchRules.SetUp(settings))
                    return Results.Ok(new TwitchLiveView(false, null, null));

                var row = await db.TwitchStreams.AsNoTracking()
                    .Where(s => s.EndedAt == null)
                    .OrderByDescending(s => s.StartedAt)
                    .FirstOrDefaultAsync(ct);

                var views = row is null ? [] : await ViewsAsync(db, settings, [row], ct);

                return Results.Ok(new TwitchLiveView(true, settings.TwitchChannelName ?? settings.TwitchChannelLogin, views.FirstOrDefault()));
            })
            .RequiresFlag(ModbotPermissions.ViewLiveInstances)
            .WithName("GetTwitchLive")
            .WithSummary("Get whether the channel is live on Twitch")
            .WithDescription(
                "What the Live and Now pages' Live on Twitch card shows: `on` is false when the poll is "
                + "off or Twitch is not set up, and `stream` is null when the channel is not live. The "
                + "card redraws on the live stream's `modbot.twitch.` facts.")
            .Produces<TwitchLiveView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/streams", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

                var rows = await db.TwitchStreams.AsNoTracking()
                    .OrderByDescending(s => s.StartedAt)
                    .Take(RecentStreams)
                    .ToListAsync(ct);

                return Results.Ok(await ViewsAsync(db, settings, rows, ct));
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("ListTwitchStreams")
            .WithSummary("List recent Twitch streams")
            .WithDescription(
                "The channel's most recent streams as the poll saw them, newest first, each with the "
                + "calendar event it is linked to. For choosing a stream to link to an event.")
            .Produces<IReadOnlyList<TwitchStreamView>>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/streams/{id}/event", async (
                HttpContext http,
                [FromRoute] string id,
                [FromBody] TwitchStreamEventRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var stream = await db.TwitchStreams.FirstOrDefaultAsync(s => s.Id == id, ct);
                if (stream is null)
                    return Results.NotFound(new { error = NoSuchStream });

                if (body.EventId is { } eventId
                    && !await db.CalendarEvents.AnyAsync(e => e.Id == eventId && e.DeletedAt == null, ct))
                {
                    return Results.BadRequest(new { error = NoSuchEvent });
                }

                var before = stream.EventId;

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                // A person's say stays: the poll never links this stream again by itself.
                stream.EventId = body.EventId;
                stream.EventSetByStaff = true;
                await db.SaveChangesAsync(ct);

                if (before != body.EventId)
                {
                    await facts.RecordAsync(
                        FactType.TwitchLinked,
                        stream.Id,
                        Actor.Of(http),
                        new JsonObject
                        {
                            ["title"] = stream.Title,
                            ["eventId"] = body.EventId?.ToString(),
                            ["wasEventId"] = before?.ToString(),
                        },
                        ct);
                }

                await transaction.CommitAsync(ct);

                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
                return Results.Ok((await ViewsAsync(db, settings, [stream], ct))[0]);
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("LinkTwitchStreamToEvent")
            .WithSummary("Link a Twitch stream to a calendar event")
            .WithDescription(
                "Sets the calendar event a stream is shown under, or clears it with a null `eventId`. "
                + "Modbot links a stream to the one event on when it started and leaves it unlinked "
                + "when events overlap; a person's choice here is never changed by the poll.")
            .Produces<TwitchStreamView>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/health", async (
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
            {
                var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
                return Results.Ok(HealthOf(settings, clock.UtcNow));
            })
            .RequiresFlag(ModbotPermissions.ViewOperationalLog)
            .WithName("GetTwitchHealth")
            .WithSummary("Get Twitch health")
            .WithDescription(
                "What the Health page's Twitch card shows: the last Check's problem, the last poll's "
                + "problem, when Twitch last answered, and until when Twitch is limiting Modbot.")
            .Produces<TwitchHealth>()
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    public static TwitchHealth HealthOf(Core.Data.Entities.Settings? settings, DateTimeOffset now)
    {
        if (settings is null)
            return new TwitchHealth(false, false, null, null, null, null);

        var reference = settings.TwitchPolledAt ?? settings.TwitchCheckedAt;
        var limited = TwitchRules.Stopped(settings, now) ? settings.TwitchStoppedUntil : null;

        var silent = settings.TwitchLiveOn
            && TwitchRules.SetUp(settings)
            && limited is null
            && reference is { } at
            && now - at > SilentAfter;

        return new TwitchHealth(
            settings.TwitchLiveOn,
            silent,
            settings.TwitchProblem,
            settings.TwitchLiveOn ? settings.TwitchPollProblem : null,
            settings.TwitchPolledAt,
            limited);
    }

    /// <summary>The streams as the pages draw them, with the titles of the events they are linked to.</summary>
    internal static async Task<List<TwitchStreamView>> ViewsAsync(
        ModbotContext db,
        Core.Data.Entities.Settings? settings,
        IReadOnlyList<TwitchStream> streams,
        CancellationToken ct)
    {
        var eventIds = streams.Select(s => s.EventId).OfType<Guid>().Distinct().ToList();
        var titles = eventIds.Count == 0
            ? []
            : await db.CalendarEvents.AsNoTracking()
                .Where(e => eventIds.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, e => e.Title, ct);

        var link = settings?.TwitchChannelLogin is { Length: > 0 } login ? TwitchRules.ChannelLink(login) : null;

        return [.. streams.Select(s => new TwitchStreamView(
            s.Id,
            s.Title,
            s.Category,
            s.Viewers,
            s.PeakViewers,
            s.StartedAt,
            s.EndedAt,
            s.EventId,
            s.EventId is { } id && titles.TryGetValue(id, out var title) ? title : null,
            link))];
    }
}
