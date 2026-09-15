using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Auth;
using Modbot.Api.Features.Audit;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.VRChat.Scheduling;
using Serilog;

namespace Modbot.Api.Features.Events;

/// <param name="Category"><c>moderation</c> or <c>operational</c>.</param>
public sealed record EventTypeOption(string Type, string Label, string Category);

/// <param name="Ticket">Put in <c>?ticket=</c> on the WebSocket address. Works once.</param>
public sealed record EventTicketResponse(string Ticket, DateTimeOffset ExpiresAt);

/// <summary>
/// The live event WebSocket and the tickets browsers open it with (API keys design §5).
/// </summary>
/// <remarks>
/// <para>
/// The socket is mapped anonymous and authenticates itself, because what it accepts is narrower
/// than the rest of the API: a key in the <c>Authorization</c> header (already turned into a
/// principal by the key handler), or a ticket. <strong>Never a session cookie on its own</strong>
/// -- browsers send cookies with a WebSocket handshake from any site -- and never a key in the
/// query string.
/// </para>
/// <para>
/// A failure is reported after the upgrade, as a close code, because a browser shows a refused
/// handshake as 1006 with no reason at all.
/// </para>
/// </remarks>
public static class EventEndpoints
{
    public const string SocketPath = "/api/events/ws";

    public static IEndpointRouteBuilder MapEvents(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/events/types", (HttpContext http) =>
            {
                var held = ModbotAuth.PermissionsOf(http.User);

                return Results.Ok(FactType.All
                    .Where(t => EventVisibility.CanSee(held, t))
                    .Order(StringComparer.Ordinal)
                    .Select(t => new EventTypeOption(
                        t,
                        FactLabels.For(t),
                        AuditVisibility.CategoryOf(t) == AuditCategory.Moderation ? "moderation" : "operational"))
                    .ToList());
            })
            .WithTags("Events")
            .WithName("ListEventTypes")
            .WithSummary("The event types this caller may be sent")
            .WithDescription(
                "Every fact type this build knows and the caller's permissions reach. A type not "
                + "listed can still arrive -- an upstream event Modbot has no name for yet -- and a "
                + "prefix such as vrchat.group.* covers it.")
            .Produces<IReadOnlyList<EventTypeOption>>()
            .RequireAuthorization();

        app.MapPost("/api/events/tickets", (
                HttpContext http,
                [FromServices] EventTickets tickets,
                [FromServices] EventSocketOptions options,
                [FromServices] IModbotClock clock) =>
            {
                if (ModbotAuth.UserIdOf(http.User) is not { } userId)
                    return Results.Unauthorized();

                if (!EventVisibility.SeesAnything(ModbotAuth.PermissionsOf(http.User)))
                    return Results.Forbid();

                var (ticket, expires) = tickets.Issue(
                    new EventTicketHolder(userId, ApiKeyAuthentication.KeyIdOf(http.User)),
                    clock.UtcNow,
                    options.TicketLifetime);

                return Results.Ok(new EventTicketResponse(ticket, expires));
            })
            .WithTags("Events")
            .WithName("CreateEventTicket")
            .WithSummary("A one-use ticket for opening the event WebSocket from a browser")
            .WithDescription(
                "Lasts sixty seconds and works once. It stands for whoever asked for it: a session, "
                + "or the key in the Authorization header.")
            .Produces<EventTicketResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .RequireAuthorization();

        app.MapGet(SocketPath, async (
                HttpContext http,
                [FromQuery] string? ticket,
                [FromServices] EventTickets tickets,
                [FromServices] EventConnections connections,
                [FromServices] EventSocketOptions options,
                [FromServices] IServiceScopeFactory scopes,
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
                    await RefuseAsync(socket, EventCloseCodes.NotAuthenticated, "Not authenticated");
                    return Results.Empty;
                }

                if (!EventVisibility.SeesAnything(caller.Permissions))
                {
                    await RefuseAsync(socket, EventCloseCodes.NoAccess, "No events visible");
                    return Results.Empty;
                }

                var slot = EventConnections.KeyFor(holder.UserId, holder.ApiKeyId);
                if (!connections.TryOpen(slot, options.MaxConnectionsPerCaller))
                {
                    await RefuseAsync(socket, EventCloseCodes.TooManyConnections, "Too many connections");
                    return Results.Empty;
                }

                try
                {
                    Log.Information("Event connection opened for {Caller}", slot);

                    var elapsed = http.RequestServices.GetService<IMonotonicClock>() ?? new StopwatchMonotonicClock();
                    var session = new EventSocketSession(socket, scopes, clock, elapsed, options, holder, caller.Permissions);
                    await session.RunAsync(ct);
                }
                finally
                {
                    connections.Close(slot);
                }

                return Results.Empty;
            })
            .WithTags("Events")
            .WithName("EventSocket")
            .WithSummary("The live event WebSocket")
            .WithDescription(
                "Authenticate with Authorization: Bearer <key>, or ?ticket= from POST "
                + "/api/events/tickets. Send {\"op\":\"subscribe\",\"types\":[...],\"subjects\":[...],"
                + "\"cursor\":\"...\"} within ten seconds; events arrive as {\"kind\":\"event\","
                + "\"event\":{...}}. See docs/api.md.")
            .AllowAnonymous();

        return app;
    }

    private static async Task RefuseAsync(WebSocket socket, int code, string reason)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        try
        {
            await socket.CloseAsync((WebSocketCloseStatus)code, reason, timeout.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException)
        {
            socket.Abort();
        }
    }
}
