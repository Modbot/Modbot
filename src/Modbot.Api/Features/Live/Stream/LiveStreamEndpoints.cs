using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Auth;
using Modbot.Api.Features.Events;
using Modbot.Core.Data;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Live.Stream;

/// <summary>
/// The web app's live updates: a ticket, the WebSocket, and long polling as its backup (live
/// updates design §4, §5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The socket takes a ticket or a key, never a cookie on its own</strong>, for the reason
/// the event socket does not: browsers send cookies with a WebSocket handshake from any site. The
/// ticket comes from a POST the session cookie's <c>SameSite=Lax</c> does not send cross-site.
/// </para>
/// <para>
/// <strong>The poll takes the session</strong>, like every other GET the web app makes -- a
/// cross-site page cannot read the answer to a credentialed fetch -- and a key, for a program that
/// wants this stream rather than the general one.
/// </para>
/// </remarks>
public static class LiveStreamEndpoints
{
    public const string SocketPath = "/api/live/ws";

    public const string PollPath = "/api/live/poll";

    public const string TicketPath = "/api/live/tickets";

    public static IEndpointRouteBuilder MapLiveStream(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost(TicketPath, (
                HttpContext http,
                [FromServices] EventTickets tickets,
                [FromServices] EventSocketOptions options,
                [FromServices] IModbotClock clock) =>
            {
                if (ModbotAuth.UserIdOf(http.User) is not { } userId)
                    return Results.Unauthorized();

                if (!LiveScope.ForPerson(ModbotAuth.PermissionsOf(http.User)).SeesAnything)
                    return Results.Forbid();

                var (ticket, expires) = tickets.Issue(
                    new EventTicketHolder(userId, ApiKeyAuthentication.KeyIdOf(http.User)),
                    clock.UtcNow,
                    options.TicketLifetime);

                return Results.Ok(new EventTicketResponse(ticket, expires));
            })
            .WithTags("Live")
            .WithName("CreateLiveTicket")
            .WithSummary("A one-use ticket for opening the live updates WebSocket from a browser")
            .WithDescription("Lasts sixty seconds and works once. It stands for whoever asked for it.")
            .Produces<EventTicketResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .RequireAuthorization();

        app.MapGet(SocketPath, async (
                HttpContext http,
                [FromQuery] string? ticket,
                [FromQuery] string? after,
                [FromServices] EventTickets tickets,
                [FromServices] EventSocketOptions options,
                [FromServices] IModbotClock clock,
                [FromServices] ApiCallers callers) =>
            {
                if (!http.WebSockets.IsWebSocketRequest)
                    return Results.BadRequest(new { error = "Connect with a WebSocket." });

                var ct = http.RequestAborted;
                EventTicketHolder? holder = null;
                ApiCaller? caller = null;

                if (ApiKeyAuthentication.KeyIdOf(http.User) is { } keyId)
                {
                    holder = new EventTicketHolder(ModbotAuth.UserIdOf(http.User)!.Value, keyId);
                    caller = await callers.ForKeyIdAsync(keyId, ct);
                }
                else if (tickets.Redeem(ticket, clock.UtcNow) is { } redeemed)
                {
                    holder = redeemed;
                    caller = redeemed.ApiKeyId is { } k
                        ? await callers.ForKeyIdAsync(k, ct)
                        : await callers.ForUserAsync(redeemed.UserId, ct);
                }

                using var socket = await http.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
                {
                    KeepAliveInterval = options.PingInterval,
                    KeepAliveTimeout = options.PingTimeout,
                });

                if (holder is null || caller is null)
                {
                    await LiveStreams.RefuseAsync(socket, EventCloseCodes.NotAuthenticated, "Not authenticated");
                    return Results.Empty;
                }

                if (!LiveStreams.TryParseCursor(after, out var cursor))
                {
                    await LiveStreams.RefuseAsync(socket, EventCloseCodes.NoSubscribe, "The cursor is not valid");
                    return Results.Empty;
                }

                await LiveStreams.RunSocketAsync(
                    http,
                    socket,
                    LiveScope.ForPerson(caller.Permissions),
                    PersonRefresh(holder),
                    cursor,
                    "live:" + EventConnections.KeyFor(holder.UserId, holder.ApiKeyId));

                return Results.Empty;
            })
            .WithTags("Live")
            .WithName("LiveSocket")
            .WithSummary("The live updates WebSocket")
            .WithDescription(
                "Authenticate with `?ticket=` from POST /api/live/tickets, or `Authorization: Bearer mbk_...`. "
                + "`?after=` is the cursor to carry on from; without it the stream starts from now. "
                + "Events arrive as `{\"kind\":\"event\",\"event\":{...}}`. See https://docs.modbot.co/api/live-updates/.")
            .AllowAnonymous();

        app.MapGet(PollPath, (
                HttpContext http,
                [FromQuery] string? after,
                [FromQuery] int? wait,
                [FromQuery] int? limit,
                [FromServices] ModbotContext db) =>
            {
                if (ModbotAuth.UserIdOf(http.User) is not { } userId)
                    return Task.FromResult(Results.Unauthorized());

                var keyId = ApiKeyAuthentication.KeyIdOf(http.User);

                return LiveStreams.PollAsync(
                    http,
                    db,
                    LiveScope.ForPerson(ModbotAuth.PermissionsOf(http.User)),
                    PersonRefresh(new EventTicketHolder(userId, keyId)),
                    "live:" + EventConnections.KeyFor(userId, keyId),
                    after,
                    wait,
                    limit);
            })
            .WithTags("Live")
            .WithName("PollLive")
            .WithSummary("Long polling for live updates, the backup for the WebSocket")
            .WithDescription(
                "Returns events after `after` at once when there are any, up to `limit` (default 100, "
                + "at most 500); otherwise waits up to `wait` seconds (default 30, at most 60) for one "
                + "and returns an empty list. No `after` means from now. Send the returned `cursor` "
                + "back as `after`; `more` means poll again straight away. See https://docs.modbot.co/api/live-updates/.")
            .Produces<LivePollResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status429TooManyRequests)
            .RequireAuthorization();

        return app;
    }

    /// <summary>Reads a person's (or their key's) access again, from the database.</summary>
    private static LiveScopeRefresh PersonRefresh(EventTicketHolder holder) => async (services, _, ct) =>
    {
        var callers = services.GetRequiredService<ApiCallers>();
        var caller = holder.ApiKeyId is { } key
            ? await callers.ForKeyIdAsync(key, ct)
            : await callers.ForUserAsync(holder.UserId, ct);

        return caller is null ? null : LiveScope.ForPerson(caller.Permissions);
    };
}
