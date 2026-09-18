using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.VRChat.Scheduling;
using Serilog;

namespace Modbot.Api.Features.Events;

public sealed record HelloMessage(string Kind, int Version, int HeartbeatSeconds, IReadOnlyList<string> Permissions);

public sealed record SubscribedMessage(string Kind, IReadOnlyList<string> Types, IReadOnlyList<string> Subjects, string Cursor);

public sealed record EventMessage(string Kind, EventEnvelope Event);

public sealed record HeartbeatMessage(string Kind, string Cursor);

public sealed record NoticeMessage(string Kind, string Code, string Message);

public sealed record ErrorMessage(string Kind, string Message);

public sealed record PongMessage(string Kind);

/// <summary>The close codes of API keys design §5.3.</summary>
public static class EventCloseCodes
{
    public const int NoSubscribe = 4000;
    public const int NotAuthenticated = 4001;
    public const int NoAccess = 4003;
    public const int TooSlow = 4008;
    public const int TooManyConnections = 4029;
}

/// <summary>
/// One live event connection: hello, subscribe, then every visible fact after the cursor as it is
/// written (API keys design §5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Pull, not push.</strong> The connection reads its next page of facts only after the last
/// page went out, so a slow client makes the server hold nothing: the fact log is the buffer. A
/// send that does not finish within <see cref="EventSocketOptions.SendTimeout"/> ends the
/// connection, and the client comes back with its cursor.
/// </para>
/// <para>
/// Each database read gets its own scope, so a connection that stays open for a day does not hold
/// a context for a day.
/// </para>
/// </remarks>
internal sealed class EventSocketSession
{
    private readonly WebSocket _socket;
    private readonly IServiceScopeFactory _scopes;
    private readonly IModbotClock _clock;
    private readonly IMonotonicClock _elapsed;
    private readonly EventSocketOptions _options;
    private readonly EventTicketHolder _caller;
    private readonly ILogger _log;
    private readonly FactSignal _signal;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0);
    private readonly ConcurrentQueue<Subscription> _subscriptions = new();

    private ModbotPermissions _permissions;
    private int _closing;

    public EventSocketSession(
        WebSocket socket,
        IServiceScopeFactory scopes,
        IModbotClock clock,
        IMonotonicClock elapsed,
        EventSocketOptions options,
        EventTicketHolder caller,
        ModbotPermissions permissions,
        FactSignal signal)
    {
        _signal = signal;
        _socket = socket;
        _scopes = scopes;
        _clock = clock;
        _elapsed = elapsed;
        _options = options;
        _caller = caller;
        _permissions = permissions;
        _log = Log.Logger.ForContext<EventSocketSession>();
    }

    private sealed record Subscription(EventFilter Filter, long? Cursor);

    public async Task RunAsync(CancellationToken requestAborted)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var ct = session.Token;
        var receiving = ReceiveLoopAsync(session);
        string reason = "ended";

        try
        {
            await SendAsync(
                new HelloMessage("hello", EventEnvelopes.Version, (int)_options.HeartbeatInterval.TotalSeconds, PermissionCatalog.NamesOf(_permissions)),
                ct);

            var first = await FirstSubscriptionAsync(ct);
            if (first is null)
            {
                if (!ct.IsCancellationRequested)
                {
                    reason = "no subscribe";
                    await CloseAsync(EventCloseCodes.NoSubscribe, "No subscribe in time");
                }

                return;
            }

            reason = await StreamAsync(first, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            reason = "client went away";
        }
        catch (WebSocketException)
        {
            reason = "connection lost";
        }
        finally
        {
            // After the server's own close frame, give the client a moment to answer it so the
            // close handshake completes rather than the connection being cut under it.
            if (Volatile.Read(ref _closing) == 1 && !receiving.IsCompleted)
                await Task.WhenAny(receiving, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));

            await session.CancelAsync();

            try
            {
                await receiving;
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException)
            {
                // The receive loop ends by cancellation or a dropped connection; either is the end.
            }
            catch (Exception e)
            {
                // Anything else -- a socket disposed or aborted under a receive that had not come
                // back yet is the one seen in the wild. It must not leave this block: an exception
                // thrown out of a finally replaces the reason the connection ended with nothing,
                // and the line below is the only record that the connection ended at all.
                _log.Warning(
                    e,
                    "The receive loop for {Caller} ended unexpectedly",
                    EventConnections.KeyFor(_caller.UserId, _caller.ApiKeyId));
            }

            _log.Information(
                "Event connection for {Caller} closed: {Reason}",
                EventConnections.KeyFor(_caller.UserId, _caller.ApiKeyId),
                reason);
        }
    }

    private async Task<Subscription?> FirstSubscriptionAsync(CancellationToken ct)
    {
        var deadline = _elapsed.Elapsed + _options.SubscribeTimeout;

        while (!ct.IsCancellationRequested)
        {
            if (_subscriptions.TryDequeue(out var subscription))
                return subscription;

            var left = deadline - _elapsed.Elapsed;
            if (left <= TimeSpan.Zero)
                return null;

            await _wake.WaitAsync(left, ct);
        }

        return null;
    }

    /// <returns>Why the stream ended, for the log.</returns>
    private async Task<string> StreamAsync(Subscription first, CancellationToken ct)
    {
        var (filter, cursor) = await ApplyAsync(first, null, ct);
        var lastHeartbeat = _elapsed.Elapsed;

        while (!ct.IsCancellationRequested)
        {
            while (_subscriptions.TryDequeue(out var next))
                (filter, cursor) = await ApplyAsync(next, cursor, ct);

            if (_elapsed.Elapsed - lastHeartbeat >= _options.HeartbeatInterval)
            {
                var caller = await ResolveAsync(ct);
                if (caller is null || !EventVisibility.SeesAnything(caller.Permissions))
                {
                    await CloseAsync(EventCloseCodes.NoAccess, "Access removed");
                    return "access removed";
                }

                _permissions = caller.Permissions;

                if (!await SendAsync(new HeartbeatMessage("heartbeat", Text(cursor)), ct))
                    return await TooSlowAsync();

                lastHeartbeat = _elapsed.Elapsed;
            }

            // Taken before the read, so a fact written between the read and the wait still wakes it.
            var written = _signal.Next();

            FactFeedPage page;
            using (var scope = _scopes.CreateScope())
            {
                var feed = new FactFeed(scope.ServiceProvider.GetRequiredService<ModbotContext>(), _options.GapWait);
                page = await feed.ReadAsync(cursor, _options.PageSize, _clock.UtcNow, ct);
            }

            foreach (var fact in page.Facts)
            {
                if (!filter.Matches(fact) || !EventVisibility.CanSee(_permissions, fact.Type))
                    continue;

                if (!await SendAsync(new EventMessage("event", EventEnvelopes.From(fact)), ct))
                    return await TooSlowAsync();
            }

            cursor = page.Through;

            // A full page means there is probably more: read on without waiting.
            if (page.Facts.Count < _options.PageSize)
                await WaitAsync(written, ct);
        }

        return "client went away";
    }

    /// <summary>
    /// Until a fact is written, a new subscribe arrives, or <see cref="EventSocketOptions.PollInterval"/>
    /// passes -- the last being the check that does not depend on anybody pulsing.
    /// </summary>
    private async Task WaitAsync(Task written, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var subscribed = _wake.WaitAsync(_options.PollInterval, stop.Token);

        await Task.WhenAny(written, subscribed);
        await stop.CancelAsync();

        try
        {
            await subscribed;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Woken by the signal; the subscribe wait was only cancelled.
        }
    }

    private async Task<(EventFilter Filter, long Cursor)> ApplyAsync(Subscription subscription, long? current, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var feed = new FactFeed(scope.ServiceProvider.GetRequiredService<ModbotContext>(), _options.GapWait);

        // No cursor: from now on a first subscribe, and from where it was on a later one.
        var cursor = subscription.Cursor ?? current ?? await feed.NewestIdAsync(ct);

        if (subscription.Cursor is > 0 && await feed.OldestIdAsync(ct) is { } oldest && cursor < oldest - 1)
        {
            await SendAsync(
                new NoticeMessage("notice", "history_trimmed", "Events before the oldest kept fact are gone."),
                ct);
            cursor = oldest - 1;
        }

        await SendAsync(new SubscribedMessage("subscribed", subscription.Filter.Types, subscription.Filter.Subjects, Text(cursor)), ct);

        return (subscription.Filter, cursor);
    }

    private async Task<ApiCaller?> ResolveAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var callers = scope.ServiceProvider.GetRequiredService<ApiCallers>();

        return _caller.ApiKeyId is { } key
            ? await callers.ForKeyIdAsync(key, ct)
            : await callers.ForUserAsync(_caller.UserId, ct);
    }

    private async Task ReceiveLoopAsync(CancellationTokenSource session)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();

        try
        {
            while (!session.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer.AsMemory(), session.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await CloseAsync((int)WebSocketCloseStatus.NormalClosure, string.Empty);
                    break;
                }

                message.Write(buffer, 0, result.Count);

                if (message.Length > _options.MaxClientMessageBytes)
                {
                    await CloseAsync((int)WebSocketCloseStatus.MessageTooBig, "Message too big");
                    break;
                }

                if (!result.EndOfMessage)
                    continue;

                if (result.MessageType == WebSocketMessageType.Text)
                    await HandleAsync(message.ToArray(), session.Token);

                message.SetLength(0);
            }
        }
        catch (OperationCanceledException)
        {
            // The session ended.
        }
        catch (WebSocketException)
        {
            // The client went away without a close frame.
        }
        finally
        {
            await session.CancelAsync();
            _wake.Release();
        }
    }

    private async Task HandleAsync(byte[] bytes, CancellationToken ct)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            await SendAsync(new ErrorMessage("error", "Messages must be JSON."), ct);
            return;
        }

        var op = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String
            ? o.GetString()
            : null;

        switch (op)
        {
            case "ping":
                await SendAsync(new PongMessage("pong"), ct);
                break;

            case "subscribe":
                if (ParseSubscription(root, out var subscription, out var error))
                {
                    _subscriptions.Enqueue(subscription!);
                    _wake.Release();
                }
                else
                {
                    await SendAsync(new ErrorMessage("error", error!), ct);
                }

                break;

            default:
                await SendAsync(new ErrorMessage("error", "Unknown op. Send subscribe or ping."), ct);
                break;
        }
    }

    private static bool ParseSubscription(JsonElement root, out Subscription? subscription, out string? error)
    {
        subscription = null;

        if (!StringList(root, "types", out var types, out error) || !StringList(root, "subjects", out var subjects, out error))
            return false;

        long? cursor = null;
        if (root.TryGetProperty("cursor", out var c) && c.ValueKind != JsonValueKind.Null)
        {
            var parsed = c.ValueKind switch
            {
                JsonValueKind.String when long.TryParse(c.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var s) => s,
                JsonValueKind.Number when c.TryGetInt64(out var n) && n >= 0 => n,
                _ => (long?)null,
            };

            if (parsed is null)
            {
                error = "The cursor is not valid.";
                return false;
            }

            cursor = parsed;
        }

        if (!EventFilter.TryCreate(types, subjects, out var filter, out error))
            return false;

        subscription = new Subscription(filter, cursor);
        return true;
    }

    private static bool StringList(JsonElement root, string name, out List<string> values, out string? error)
    {
        values = [];
        error = null;

        if (!root.TryGetProperty(name, out var array) || array.ValueKind == JsonValueKind.Null)
            return true;

        if (array.ValueKind != JsonValueKind.Array)
        {
            error = $"'{name}' must be a list.";
            return false;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                error = $"'{name}' must be a list of text.";
                return false;
            }

            values.Add(item.GetString()!);
        }

        return true;
    }

    /// <returns>False when the send did not complete: too slow, or the connection is gone.</returns>
    private async Task<bool> SendAsync<T>(T message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, EventEnvelopes.JsonOptions);

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_socket.State != WebSocketState.Open)
                return false;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.SendTimeout);

            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (WebSocketException)
        {
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<string> TooSlowAsync()
    {
        // A cancelled send has usually aborted the socket already, so the close frame is a best
        // effort: a client that is not reading will not read this either.
        await CloseAsync(EventCloseCodes.TooSlow, "Too slow");
        return "too slow";
    }

    private async Task CloseAsync(int code, string reason)
    {
        if (Interlocked.Exchange(ref _closing, 1) == 1)
            return;

        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        try
        {
            await _sendLock.WaitAsync(timeout.Token);
            try
            {
                await _socket.CloseOutputAsync((WebSocketCloseStatus)code, reason, timeout.Token);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException)
        {
            _socket.Abort();
        }
    }

    private static string Text(long cursor) => cursor.ToString(CultureInfo.InvariantCulture);
}
