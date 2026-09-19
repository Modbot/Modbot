using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.VRChat.Scheduling;

namespace Modbot.Api.Features.Events;

/// <param name="Events">In fact order, each in the version 1 envelope.</param>
/// <param name="Cursor">Send back as <c>cursor</c> to carry on. Moves past events this caller was not sent.</param>
/// <param name="More">Events are ready past <see cref="Events"/>: poll again straight away.</param>
/// <param name="Notice">The WebSocket's <c>history_trimmed</c> notice, when the cursor was older than retention kept.</param>
public sealed record EventPollResponse(
    IReadOnlyList<EventEnvelope> Events,
    string Cursor,
    bool More,
    NoticeMessage? Notice);

/// <param name="Cursor">The id reading got to.</param>
public sealed record EventPollRead(IReadOnlyList<ModbotEvent> Events, long Cursor, bool More);

/// <summary>
/// Long polling over the same event stream the WebSocket and webhooks use (API keys design §5.6),
/// for programs that can hold neither a socket open nor an address open to the internet.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A key in the header, and nothing else.</strong> Not a key in the query string, which
/// ends up in logs, and not a session cookie, for the same reason the WebSocket refuses one.
/// </para>
/// <para>
/// <strong>The same reader.</strong> <see cref="FactFeed"/> with its gap rule, <see cref="EventFilter"/>
/// and <see cref="EventVisibility"/>, so a poll can never be sent an event the WebSocket would not
/// send, nor miss one it would.
/// </para>
/// <para>
/// <strong>Waiting is woken, not timed.</strong> A poll with nothing to return waits on
/// <see cref="FactSignal"/>, with a database check every <see cref="EventSocketOptions.PollInterval"/>
/// as the fallback. A waiting poll holds one of its key's connection places, shared with the key's
/// WebSocket connections, and gives it back the moment the client goes away.
/// </para>
/// </remarks>
public static class EventPollEndpoint
{
    public const string PollPath = "/api/events/poll";

    public static IEndpointRouteBuilder MapEventPoll(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(PollPath, async (
                HttpContext http,
                [FromQuery] string? cursor,
                [FromQuery(Name = "types")] string[]? types,
                [FromQuery(Name = "subjects")] string[]? subjects,
                [FromQuery] int? wait,
                [FromQuery] int? limit,
                [FromServices] ModbotContext db,
                [FromServices] EventConnections connections,
                [FromServices] EventSocketOptions options,
                [FromServices] ApiCallers callers,
                [FromServices] FactSignal signal,
                [FromServices] IModbotClock clock) =>
            {
                http.Response.Headers.CacheControl = "no-store";

                if (ApiKeyAuthentication.KeyIdOf(http.User) is not { } keyId
                    || ModbotAuth.UserIdOf(http.User) is not { } userId)
                {
                    http.Response.Headers.WWWAuthenticate = "Bearer";
                    return Results.Json(new { error = "Send an API key in the Authorization header." }, statusCode: StatusCodes.Status401Unauthorized);
                }

                var held = ModbotAuth.PermissionsOf(http.User);
                if (!EventVisibility.SeesAnything(held))
                    return Results.Json(new { error = "This key can see no events." }, statusCode: StatusCodes.Status403Forbidden);

                if (!EventFilter.TryCreate(Split(types), Split(subjects), out var filter, out var filterError))
                    return Results.BadRequest(new { error = filterError });

                long? asked = null;
                if (!string.IsNullOrWhiteSpace(cursor))
                {
                    if (!long.TryParse(cursor.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                        return Results.BadRequest(new { error = "The cursor is not valid." });

                    asked = parsed;
                }

                var waitFor = TimeSpan.FromSeconds(Math.Clamp(wait ?? options.LongPollDefaultWaitSeconds, 0, options.LongPollMaxWaitSeconds));
                var take = Math.Clamp(limit ?? options.LongPollDefaultLimit, 1, options.LongPollMaxLimit);

                var slot = EventConnections.KeyFor(userId, keyId);
                if (!connections.TryOpen(slot, options.MaxConnectionsPerCaller))
                {
                    http.Response.Headers.RetryAfter = options.LongPollRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                    return Results.Json(new { error = "Too many connections for this key." }, statusCode: StatusCodes.Status429TooManyRequests);
                }

                var ct = http.RequestAborted;

                try
                {
                    var feed = new FactFeed(db, options.GapWait);
                    var elapsed = http.RequestServices.GetService<IMonotonicClock>() ?? new StopwatchMonotonicClock();
                    var deadline = elapsed.Elapsed + waitFor;

                    NoticeMessage? notice = null;
                    long after;

                    if (asked is not { } from)
                    {
                        after = await feed.NewestIdAsync(ct);
                    }
                    else
                    {
                        after = from;
                        if (from > 0 && await feed.OldestIdAsync(ct) is { } oldest && from < oldest - 1)
                        {
                            notice = new NoticeMessage("notice", "history_trimmed", "Events before the oldest kept fact are gone.");
                            after = oldest - 1;
                        }
                    }

                    while (true)
                    {
                        // Taken before the read, so a fact written between the read and the wait wakes it.
                        var written = signal.Next();
                        var read = await ReadAsync(feed, after, take, filter, held, clock.UtcNow, options.PageSize, options.LongPollMaxPages, ct);
                        var left = deadline - elapsed.Elapsed;

                        if (read.Events.Count > 0 || read.More || left <= TimeSpan.Zero)
                        {
                            return Results.Json(
                                new EventPollResponse(
                                    read.Events.Select(EventEnvelopes.From).ToList(),
                                    read.Cursor.ToString(CultureInfo.InvariantCulture),
                                    read.More,
                                    notice),
                                EventEnvelopes.JsonOptions);
                        }

                        after = read.Cursor;
                        await WaitAsync(written, left < options.PollInterval ? left : options.PollInterval, ct);

                        // The key may have been revoked, or its account changed, while this waited.
                        var caller = await callers.ForKeyIdAsync(keyId, ct);
                        if (caller is null || !EventVisibility.SeesAnything(caller.Permissions))
                            return Results.Json(new { error = "The API key is not valid." }, statusCode: StatusCodes.Status401Unauthorized);

                        held = caller.Permissions;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // The client went away. Nobody is listening for an answer.
                    return Results.Empty;
                }
                finally
                {
                    connections.Close(slot);
                }
            })
            .WithTags("Events")
            .WithName("PollEvents")
            .WithSummary("Event polling")
            .WithDescription(
                "Authenticate with `Authorization: Bearer mbk_...`. Returns events after `cursor` at once "
                + "when there are any, up to `limit` (default 100, at most 500); otherwise waits up to "
                + "`wait` seconds (default 30, at most 60) for one and returns an empty list. No cursor "
                + "means from now. Send the returned `cursor` back; `more` means poll again straight "
                + "away. `types` and `subjects` repeat or are comma-separated. See https://docs.modbot.co/api/long-polling/.")
            .Produces<EventPollResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status429TooManyRequests)
            .RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Up to <paramref name="limit"/> events after <paramref name="after"/> that match and may be
    /// seen, reading past the ones that do not for at most <paramref name="maxPages"/> pages.
    /// </summary>
    /// <remarks>
    /// The cursor stops just before the first wanted event that did not fit, so the next poll starts
    /// with it; everything before that was sent or is not for this caller. <c>More</c> is true when
    /// such an event was found, or when reading stopped at a full page -- there is more to look at,
    /// even if it turns out not to be for this caller.
    /// </remarks>
    public static async Task<EventPollRead> ReadAsync(
        FactFeed feed,
        long after,
        int limit,
        EventFilter filter,
        ModbotPermissions held,
        DateTimeOffset now,
        int pageSize,
        int maxPages,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(filter);

        var events = new List<ModbotEvent>();
        var cursor = after;

        for (var pages = 0; pages < maxPages; pages++)
        {
            var page = await feed.ReadAsync(cursor, pageSize, now, ct);

            foreach (var fact in page.Facts)
            {
                var wanted = filter.Matches(fact) && EventVisibility.CanSee(held, fact.Type);

                if (wanted && events.Count == limit)
                    return new EventPollRead(events, cursor, More: true);

                if (wanted)
                    events.Add(fact);

                cursor = fact.Id;
            }

            // Caught up, or stopped at a gap that is still being waited on.
            if (page.Facts.Count < pageSize)
                return new EventPollRead(events, cursor, More: false);

            if (events.Count == limit)
                return new EventPollRead(events, cursor, More: true);
        }

        return new EventPollRead(events, cursor, More: true);
    }

    private static async Task WaitAsync(Task written, TimeSpan longest, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = Task.Delay(longest, stop.Token);

        await Task.WhenAny(written, timer);
        await stop.CancelAsync();

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>Repeated parameters, each of which may also be comma-separated.</summary>
    private static IEnumerable<string> Split(string[]? values)
        => (values ?? []).SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
