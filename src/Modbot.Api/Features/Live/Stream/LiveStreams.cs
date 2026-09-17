using System.Globalization;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Events;
using Modbot.Core.Data;
using Modbot.Core.Time;
using Modbot.VRChat.Scheduling;
using Serilog;

namespace Modbot.Api.Features.Live.Stream;

/// <summary>
/// The two ways to read the live stream -- a WebSocket, and long polling as its backup -- shared
/// by the web's endpoints and the companion's (live updates design §4, §5).
/// </summary>
/// <remarks>
/// <para>
/// One implementation, two doors. The web and the companion authenticate differently and see
/// different things, and both of those are decided before this is called: what arrives here is a
/// <see cref="LiveScope"/> and a way to check it again. Everything after that -- the reader, the
/// gap rule, the wake-up, the heartbeat, the connection cap -- is the same code, so the two doors
/// cannot drift.
/// </para>
/// <para>
/// A poll and a socket from the same caller share that caller's connection places, as the event
/// stream's do: a client that may hold five sockets gains nothing by being allowed five polls too.
/// </para>
/// </remarks>
public static class LiveStreams
{
    public static bool TryParseCursor(string? text, out long? cursor)
    {
        cursor = null;

        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (!long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            return false;

        cursor = parsed;
        return true;
    }

    /// <summary>Runs one WebSocket connection to its end. The socket is already accepted.</summary>
    public static async Task RunSocketAsync(
        HttpContext http,
        WebSocket socket,
        LiveScope scope,
        LiveScopeRefresh refresh,
        long? after,
        string connectionKey,
        Action<string>? instanceNamed = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(scope);

        var services = http.RequestServices;
        var options = services.GetRequiredService<EventSocketOptions>();
        var connections = services.GetRequiredService<EventConnections>();

        if (!scope.SeesAnything)
        {
            await RefuseAsync(socket, EventCloseCodes.NoAccess, "No events visible");
            return;
        }

        if (!connections.TryOpen(connectionKey, options.MaxConnectionsPerCaller))
        {
            await RefuseAsync(socket, EventCloseCodes.TooManyConnections, "Too many connections");
            return;
        }

        try
        {
            Log.Information("Live connection opened for {Caller}", connectionKey);

            var session = new LiveSocketSession(
                socket,
                services.GetRequiredService<IServiceScopeFactory>(),
                services.GetRequiredService<IModbotClock>(),
                services.GetService<IMonotonicClock>() ?? new StopwatchMonotonicClock(),
                options,
                services.GetRequiredService<FactSignal>(),
                scope,
                refresh,
                after,
                connectionKey,
                instanceNamed);

            await session.RunAsync(http.RequestAborted);
        }
        finally
        {
            connections.Close(connectionKey);
        }
    }

    /// <summary>
    /// One long poll: events after <paramref name="after"/> at once, or a wait of up to
    /// <paramref name="wait"/> seconds for one, then an empty answer.
    /// </summary>
    /// <param name="db">The request's own context, used for the whole request.</param>
    public static async Task<IResult> PollAsync(
        HttpContext http,
        ModbotContext db,
        LiveScope scope,
        LiveScopeRefresh refresh,
        string connectionKey,
        string? after,
        int? wait,
        int? limit)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);

        http.Response.Headers.CacheControl = "no-store";

        var services = http.RequestServices;
        var options = services.GetRequiredService<EventSocketOptions>();
        var connections = services.GetRequiredService<EventConnections>();
        var signal = services.GetRequiredService<FactSignal>();
        var clock = services.GetRequiredService<IModbotClock>();

        if (!scope.SeesAnything)
            return Results.Json(new { error = "You may not see live updates." }, statusCode: StatusCodes.Status403Forbidden);

        if (!TryParseCursor(after, out var asked))
            return Results.BadRequest(new { error = "The cursor is not valid." });

        var waitFor = TimeSpan.FromSeconds(Math.Clamp(wait ?? options.LongPollDefaultWaitSeconds, 0, options.LongPollMaxWaitSeconds));
        var take = Math.Clamp(limit ?? options.LongPollDefaultLimit, 1, options.LongPollMaxLimit);

        if (!connections.TryOpen(connectionKey, options.MaxConnectionsPerCaller))
        {
            http.Response.Headers.RetryAfter = options.LongPollRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Results.Json(new { error = "Too many connections." }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        var ct = http.RequestAborted;

        try
        {
            var reader = new LiveReader(db, options.GapWait);
            var elapsed = services.GetService<IMonotonicClock>() ?? new StopwatchMonotonicClock();
            var deadline = elapsed.Elapsed + waitFor;

            LiveNotice? notice = null;
            long cursor;

            if (asked is not { } from)
            {
                cursor = await reader.NewestIdAsync(ct);
            }
            else
            {
                cursor = from;
                if (from > 0 && await reader.OldestIdAsync(ct) is { } oldest && from < oldest - 1)
                {
                    notice = LiveJson.HistoryTrimmed;
                    cursor = oldest - 1;
                }
            }

            while (true)
            {
                var written = signal.Next();
                var page = await reader.ReadAsync(cursor, take, scope, clock.UtcNow, options.PageSize, options.LongPollMaxPages, ct);
                var left = deadline - elapsed.Elapsed;

                if (page.Events.Count > 0 || page.More || left <= TimeSpan.Zero)
                    return Results.Json(new LivePollResponse(page.Events, LiveJson.Text(page.Cursor), page.More, notice), LiveJson.Options);

                cursor = page.Cursor;
                await WaitAsync(written, left < options.PollInterval ? left : options.PollInterval, ct);

                // Access may have been taken away while this waited.
                var refreshed = await refresh(services, scope, ct);
                if (refreshed is null || !refreshed.SeesAnything)
                    return Results.Json(new { error = "Access removed." }, statusCode: StatusCodes.Status401Unauthorized);

                scope = refreshed;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client went away. Nobody is listening for an answer.
            return Results.Empty;
        }
        finally
        {
            connections.Close(connectionKey);
        }
    }

    public static async Task RefuseAsync(WebSocket socket, int code, string reason)
    {
        ArgumentNullException.ThrowIfNull(socket);

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

    private static async Task WaitAsync(Task written, TimeSpan longest, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = Task.Delay(longest, stop.Token);

        await Task.WhenAny(written, timer);
        await stop.CancelAsync();

        ct.ThrowIfCancellationRequested();
    }
}
