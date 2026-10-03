using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Modbot.Api.Auth;
using Modbot.Api.Features.Users;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Calendar;

/// <summary>
/// The calendar's public feed (calendar design §6.1, added 2026-10-03): the events visible to
/// everyone, at an address with no secret in it, for event directories, websites and calendar apps
/// that subscribe by address.
/// </summary>
/// <remarks>
/// <para>
/// Off until someone with Manage calendar turns it on; off, the address answers 404. The entries are
/// the secret feed's, written by the same <see cref="CalendarFeedWriter"/>, less what is only for
/// members: see <see cref="CalendarFeedWriter.WritePublic"/>.
/// </para>
/// <para>
/// Anybody can ask for it as often as they like, so it is kept for <see cref="PublicCalendarFeedCache.KeepFor"/>
/// once written, with an <c>ETag</c> that lets a calendar app ask "has it changed?" and get an empty
/// 304. Each request still reads the switch, so turning it off takes effect on the next one. A change
/// made in Modbot empties what is kept (<see cref="PublicCalendarFeedCache.ClearsPublicFeed{TBuilder}"/>),
/// so it shows on the next request; one read from VRChat, or an event finishing, within a minute.
/// </para>
/// <para>
/// <c>Cache-Control: no-cache</c>: a calendar app or a cache between may keep a copy, but asks again
/// every time, so the feed turned off is never served from a copy, and asking again is a cheap 304.
/// </para>
/// </remarks>
public static class PublicCalendarFeedEndpoints
{
    /// <summary>The public feed's path on this server.</summary>
    public const string FeedPath = "/api/calendar/public.ics";

    public static IEndpointRouteBuilder MapPublicCalendarFeed(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/calendar").WithTags("Calendar");

        group.MapGet("/public-feed", async (
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
            {
                var settings = await db.GetSettingsAsync(ct);
                return Results.Ok(View(settings));
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("GetPublicCalendarFeed")
            .WithSummary("Get public calendar feed")
            .WithDescription("Whether the public calendar feed is on, and its address.")
            .Produces<PublicCalendarFeedView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/public-feed", async (
                [FromBody] SetPublicCalendarFeedRequest body,
                [FromServices] ModbotContext db,
                [FromServices] AccountFacts facts,
                [FromServices] PublicCalendarFeedCache cache,
                HttpContext http,
                CancellationToken ct) =>
            {
                ArgumentNullException.ThrowIfNull(body);

                var settings = await db.GetSettingsAsync(ct);
                var before = settings.CalendarPublicFeed;

                if (before == body.On)
                    return Results.Ok(View(settings));

                await using var transaction = await db.Database.BeginTransactionAsync(ct);

                settings.CalendarPublicFeed = body.On;
                await db.SaveChangesAsync(ct);

                await facts.RecordAsync(
                    FactType.SettingsChanged,
                    "settings",
                    Actor.Of(http),
                    new JsonObject
                    {
                        ["setting"] = "calendarPublicFeed",
                        ["before"] = before,
                        ["after"] = body.On,
                    },
                    ct);

                await transaction.CommitAsync(ct);

                cache.Clear();

                return Results.Ok(View(settings));
            })
            .RequiresFlag(ModbotPermissions.ManageCalendar)
            .WithName("SetPublicCalendarFeed")
            .WithSummary("Set public calendar feed")
            .WithDescription("Turn the public calendar feed on or off. Off, its address answers 404.")
            .Produces<PublicCalendarFeedView>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/public.ics", async (
                HttpContext http,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                [FromServices] PublicCalendarFeedCache cache,
                CancellationToken ct) =>
            {
                // Read on every request, so the switch turned off is a 404 at once, whatever is kept.
                var on = await db.Settings.AsNoTracking()
                    .Where(s => s.Id == 1)
                    .Select(s => s.CalendarPublicFeed)
                    .FirstOrDefaultAsync(ct);

                if (!on)
                    return Results.NotFound();

                var feed = await cache.GetAsync(clock.UtcNow, () => WriteAsync(db, clock.UtcNow, ct), ct);

                http.Response.Headers[HeaderNames.ETag] = feed.ETag;
                http.Response.Headers[HeaderNames.CacheControl] = "no-cache";

                if (Matches(http.Request.Headers.IfNoneMatch, feed.ETag))
                    return Results.StatusCode(StatusCodes.Status304NotModified);

                return Results.Text(feed.Body, "text/calendar; charset=utf-8", Encoding.UTF8);
            })
            .AllowAnonymous()
            .WithName("GetPublicCalendarFeedFile")
            .WithSummary("Get public calendar feed file")
            .WithDescription(
                "The public calendar feed as iCalendar. No sign-in and no secret; 404 while it is off. "
                + "Holds the events visible to everyone that are scheduled or open, and finished and "
                + "cancelled ones for 30 days after. A join link only for an event anyone can join. "
                + "Send If-None-Match with the ETag to get 304 when it has not changed.")
            .Produces<string>(StatusCodes.Status200OK, "text/calendar")
            .Produces(StatusCodes.Status304NotModified)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>
    /// Whether an <c>If-None-Match</c> names <paramref name="etag"/>: in a list, weak (<c>W/</c>) or
    /// strong alike, as RFC 9110 §13.1.2 compares them for this header; <c>*</c> matches any.
    /// </summary>
    internal static bool Matches(Microsoft.Extensions.Primitives.StringValues ifNoneMatch, string etag)
    {
        foreach (var value in ifNoneMatch)
        {
            if (value is null)
                continue;

            foreach (var part in value.Split(','))
            {
                var tag = part.Trim();

                if (tag == "*")
                    return true;

                if (tag.StartsWith("W/", StringComparison.Ordinal))
                    tag = tag[2..];

                if (tag == etag)
                    return true;
            }
        }

        return false;
    }

    private static async Task<string> WriteAsync(ModbotContext db, DateTimeOffset now, CancellationToken ct)
    {
        var events = await CalendarEndpoints.FeedEventsAsync(db, now, publicOnly: true, ct);
        var names = await CalendarEndpoints.FeedWorldNamesAsync(db, events, ct);
        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        return CalendarFeedWriter.WritePublic(CalendarEndpoints.FeedName(settings), events, names, now, settings?.PublicAddress);
    }

    private static PublicCalendarFeedView View(Modbot.Core.Data.Entities.Settings settings)
    {
        var address = settings.PublicAddress;

        return new PublicCalendarFeedView(
            settings.CalendarPublicFeed,
            FeedPath,
            string.IsNullOrWhiteSpace(address) ? null : address.Trim().TrimEnd('/') + FeedPath);
    }
}

/// <summary>
/// The public feed as last written, kept for <see cref="KeepFor"/> so a crowd of requests reads the
/// database once a minute at most (calendar design §6.1). One per process: a singleton.
/// </summary>
/// <remarks>
/// Every change made to the calendar in Modbot empties it (<see cref="ClearsPublicFeed{TBuilder}"/>),
/// so an event saved, cancelled or deleted leaves the public feed on the next request, not a minute
/// later. A feed being written while a change is saved is handed to the request that wrote it but
/// not kept, so it cannot outlive the change.
/// </remarks>
public sealed class PublicCalendarFeedCache
{
    /// <summary>How long a written feed is served before it is written again.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _writing = new(1, 1);
    private Kept? _kept;
    private long _cleared;

    /// <summary>The kept feed while it is younger than <see cref="KeepFor"/>; otherwise <paramref name="write"/>'s, kept.</summary>
    public async Task<Kept> GetAsync(DateTimeOffset now, Func<Task<string>> write, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);

        if (Fresh(now) is { } kept)
            return kept;

        // One writer at a time: the requests that arrive while it writes get what it wrote.
        await _writing.WaitAsync(ct);

        try
        {
            if (Fresh(now) is { } written)
                return written;

            // Taken before reading the database: a clear while it reads means what it read may be
            // from before a change, so it is not kept.
            var clearedBefore = Interlocked.Read(ref _cleared);

            var body = await write();
            var made = new Kept(body, ETagOf(body), now);

            if (Interlocked.Read(ref _cleared) == clearedBefore)
                Volatile.Write(ref _kept, made);

            return made;
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <summary>Forgets the kept feed, so the next request writes it again.</summary>
    public void Clear()
    {
        Interlocked.Increment(ref _cleared);
        Volatile.Write(ref _kept, null);
    }

    /// <summary>
    /// Empties the public feed after every request to these endpoints that is not a read: an event
    /// saved, cancelled or deleted, a date changed or cancelled, a world picked again. The handler
    /// has committed by the time it returns, so the next feed request reads the change. A refused
    /// request empties it too; that only costs one more write of the feed.
    /// </summary>
    public static TBuilder ClearsPublicFeed<TBuilder>(TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);

            if (!HttpMethods.IsGet(context.HttpContext.Request.Method) && !HttpMethods.IsHead(context.HttpContext.Request.Method))
                context.HttpContext.RequestServices.GetService<PublicCalendarFeedCache>()?.Clear();

            return result;
        });

        return builder;
    }

    private Kept? Fresh(DateTimeOffset now) =>
        Volatile.Read(ref _kept) is { } kept && now >= kept.WrittenAt && now - kept.WrittenAt < KeepFor ? kept : null;

    private static string ETagOf(string body) =>
        "\"" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..32] + "\"";

    /// <param name="Body">The feed's text.</param>
    /// <param name="ETag">A quoted hash of the text.</param>
    /// <param name="WrittenAt">When it was written, from <c>IModbotClock</c>.</param>
    public sealed record Kept(string Body, string ETag, DateTimeOffset WrittenAt);
}
