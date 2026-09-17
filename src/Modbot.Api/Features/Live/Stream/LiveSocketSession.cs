using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Events;
using Modbot.Core.Data;
using Modbot.Core.Time;
using Modbot.VRChat.Scheduling;
using Serilog;

namespace Modbot.Api.Features.Live.Stream;

/// <summary>
/// Reads a connection's access again, or returns null when it has none left.
/// </summary>
public delegate Task<LiveScope?> LiveScopeRefresh(IServiceProvider services, LiveScope current, CancellationToken ct);

/// <summary>
/// One live connection: hello, then every event after the cursor as it is written, with a
/// heartbeat that re-checks access (live updates design §4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Subscribed on connect.</strong> Unlike the general event socket there is nothing to
/// choose: the caller's scope decides what it is sent, and the address carries the cursor. A
/// client may still send <c>subscribe</c> to move the cursor, or -- a companion -- to name the
/// instance it has walked into, so a moderator changing instances does not reconnect.
/// </para>
/// <para>
/// <strong>Pull, not push.</strong> The next page is read only after the last one went out, so a
/// slow client makes the server hold nothing: the fact log is the buffer. Each read gets its own
/// scope, so a connection open all evening does not hold a database context all evening.
/// </para>
/// </remarks>
internal sealed class LiveSocketSession
{
    private readonly WebSocket _socket;
    private readonly IServiceScopeFactory _scopes;
    private readonly IModbotClock _clock;
    private readonly IMonotonicClock _elapsed;
    private readonly EventSocketOptions _options;
    private readonly LiveScopeRefresh _refresh;
    private readonly FactSignal _signal;
    private readonly Action<string>? _instanceNamed;
    private readonly string _caller;
    private readonly ILogger _log;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0);
    private readonly ConcurrentQueue<Change> _changes = new();

    private LiveScope _scope;
    private long? _startAt;
    private int _closing;

    /// <param name="scope">What this connection may be sent, as first resolved.</param>
    /// <param name="refresh">Re-resolves that at each heartbeat.</param>
    /// <param name="startAfter">The cursor from the address, or null for "from now".</param>
    /// <param name="instanceNamed">Told each time a companion names the instance it is in.</param>
    public LiveSocketSession(
        WebSocket socket,
        IServiceScopeFactory scopes,
        IModbotClock clock,
        IMonotonicClock elapsed,
        EventSocketOptions options,
        FactSignal signal,
        LiveScope scope,
        LiveScopeRefresh refresh,
        long? startAfter,
        string caller,
        Action<string>? instanceNamed = null)
    {
        _socket = socket;
        _scopes = scopes;
        _clock = clock;
        _elapsed = elapsed;
        _options = options;
        _signal = signal;
        _scope = scope;
        _refresh = refresh;
        _startAt = startAfter;
        _caller = caller;
        _instanceNamed = instanceNamed;
        _log = Log.Logger.ForContext<LiveSocketSession>();
    }

    private sealed record Change(long? Cursor, string? InstanceId);

    public async Task RunAsync(CancellationToken requestAborted)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var ct = session.Token;
        var receiving = ReceiveLoopAsync(session);
        var reason = "ended";

        try
        {
            reason = await StreamAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            reason = "client went away";
        }
        catch (WebSocketException)
        {
            reason = "connection lost";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Closed with a code rather than dropped: a client that sees 1011 reconnects with its
            // cursor, where a connection that simply vanishes leaves it waiting.
            _log.Warning(e, "Live connection for {Caller} failed", _caller);
            reason = "server error";
            await CloseAsync((int)WebSocketCloseStatus.InternalServerError, Describe(e));
        }
        finally
        {
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

            _log.Information("Live connection for {Caller} closed: {Reason}", _caller, reason);
        }
    }

    private async Task<string> StreamAsync(CancellationToken ct)
    {
        var cursor = await StartAsync(_startAt, ct);

        await SendAsync(new LiveHello("hello", LiveJson.Version, (int)_options.HeartbeatInterval.TotalSeconds, LiveJson.Text(cursor)), ct);

        var lastHeartbeat = _elapsed.Elapsed;

        while (!ct.IsCancellationRequested)
        {
            while (_changes.TryDequeue(out var change))
            {
                if (change.InstanceId is { } instance && _scope.IsDevice)
                {
                    _scope = _scope.InInstance(instance);
                    _instanceNamed?.Invoke(instance);
                }

                if (change.Cursor is { } moved)
                    cursor = await StartAsync(moved, ct);
            }

            if (_elapsed.Elapsed - lastHeartbeat >= _options.HeartbeatInterval)
            {
                LiveScope? refreshed;
                using (var scope = _scopes.CreateScope())
                    refreshed = await _refresh(scope.ServiceProvider, _scope, ct);

                if (refreshed is null || !refreshed.SeesAnything)
                {
                    await CloseAsync(EventCloseCodes.NoAccess, "Access removed");
                    return "access removed";
                }

                _scope = refreshed;

                if (!await SendAsync(new LiveHeartbeat("heartbeat", LiveJson.Text(cursor)), ct))
                    return await TooSlowAsync();

                lastHeartbeat = _elapsed.Elapsed;
            }

            // Taken before the read, so a fact written between the read and the wait still wakes it.
            var written = _signal.Next();

            LivePage page;
            using (var scope = _scopes.CreateScope())
            {
                var reader = new LiveReader(scope.ServiceProvider.GetRequiredService<ModbotContext>(), _options.GapWait);
                page = await reader.ReadAsync(cursor, _options.PageSize, _scope, _clock.UtcNow, _options.PageSize, maxPages: 1, ct);
            }

            foreach (var @event in page.Events)
            {
                if (!await SendAsync(new LiveEventMessage("event", @event), ct))
                    return await TooSlowAsync();
            }

            cursor = page.Cursor;

            // More to read means read on without waiting.
            if (!page.More)
                await WaitAsync(written, ct);
        }

        return "client went away";
    }

    /// <summary>Where reading starts: from now without a cursor, and never before the oldest kept fact.</summary>
    private async Task<long> StartAsync(long? asked, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var reader = new LiveReader(scope.ServiceProvider.GetRequiredService<ModbotContext>(), _options.GapWait);

        if (asked is not { } from)
            return await reader.NewestIdAsync(ct);

        if (from > 0 && await reader.OldestIdAsync(ct) is { } oldest && from < oldest - 1)
        {
            await SendAsync(LiveJson.HistoryTrimmed, ct);
            return oldest - 1;
        }

        return from;
    }

    private async Task WaitAsync(Task written, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var changed = _wake.WaitAsync(_options.PollInterval, stop.Token);

        await Task.WhenAny(written, changed);
        await stop.CancelAsync();

        try
        {
            await changed;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Woken by the signal; the change wait was only cancelled.
        }
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
            await SendAsync(new LiveError("error", "Messages must be JSON."), ct);
            return;
        }

        var op = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String
            ? o.GetString()
            : null;

        switch (op)
        {
            case "ping":
                await SendAsync(new LivePong("pong"), ct);
                break;

            case "subscribe":
                if (ParseChange(root, out var change, out var error))
                {
                    _changes.Enqueue(change!);
                    _wake.Release();
                }
                else
                {
                    await SendAsync(new LiveError("error", error!), ct);
                }

                break;

            default:
                await SendAsync(new LiveError("error", "Unknown op. Send subscribe or ping."), ct);
                break;
        }
    }

    private static bool ParseChange(JsonElement root, out Change? change, out string? error)
    {
        change = null;
        error = null;

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

        string? instance = null;
        if (root.TryGetProperty("instanceId", out var i) && i.ValueKind != JsonValueKind.Null)
        {
            if (i.ValueKind != JsonValueKind.String || i.GetString() is not { Length: > 0 and <= 256 } named)
            {
                error = "'instanceId' must be text.";
                return false;
            }

            instance = named;
        }

        change = new Change(cursor, instance);
        return true;
    }

    /// <returns>False when the send did not complete: too slow, or the connection is gone.</returns>
    private async Task<bool> SendAsync<T>(T message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, LiveJson.Options);

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
        await CloseAsync(EventCloseCodes.TooSlow, "Too slow");
        return "too slow";
    }

    /// <summary>The close reason for a failure: the exception's name, which a close frame has room for.</summary>
    private static string Describe(Exception e)
    {
        var text = e.GetType().Name + ": " + e.Message;
        return text.Length <= 120 ? text : text[..120];
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
}
