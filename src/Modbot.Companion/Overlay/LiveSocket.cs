using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Modbot.Companion.Ingest;
using Modbot.Shared.Clips;

namespace Modbot.Companion.Overlay;

/// <summary>How an attempt to open the live WebSocket ended.</summary>
public enum LiveConnectOutcome
{
    Connected,

    /// <summary>Could not connect, or something in the path refused the upgrade. Try again later, or poll.</summary>
    Unreachable,

    /// <summary>The token was rejected. Terminal for this pairing.</summary>
    Unauthorised,
}

public sealed record LiveConnect(LiveConnectOutcome Outcome, ILiveSocket? Socket = null);

/// <summary>An open live WebSocket, as the link drives it. Messages arrive parsed.</summary>
public interface ILiveSocket : IDisposable
{
    /// <summary>The next message, or null once the connection has closed.</summary>
    Task<LiveSocketMessage?> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>Tells the server the instance the moderator is standing in now: its number, and its world.</summary>
    Task SubscribeAsync(string instanceId, string? worldId, CancellationToken cancellationToken);

    Task CloseAsync();
}

/// <summary>Opens the live WebSocket to one paired server.</summary>
public interface ILiveSocketFactory
{
    /// <summary>Opens the socket. With no instance named (the clip link), the server sends clip requests and no presence.</summary>
    Task<LiveConnect> ConnectAsync(ServerPairing pairing, string? instanceId, string? worldId, string? after, CancellationToken cancellationToken);
}

/// <summary>
/// The live WebSocket to a paired server, over <see cref="ClientWebSocket"/>.
/// </summary>
/// <remarks>
/// <para><strong>What this sends.</strong> One WebSocket connection to one paired server, opened
/// with the device token as a bearer header, naming the instance the moderator is standing in (its
/// world and its number) and the cursor of the last event received -- which that server already knows, because it is the
/// group's own instance and this client has been reporting presence for it. Afterwards the only
/// things sent are the instance (world and number) the moderator walked into, when they change instances, and the four Clips
/// settings (<see cref="ClipSettingsReport"/>), once on connecting and again when one changes, and nothing else:
/// no log lines, nothing about the machine, nothing about instances belonging to any other
/// group.</para>
/// <para><strong>What comes back is data to display</strong> -- who joined or left the instance --
/// and, since 2026-10-24, one request: <c>clip_request</c>, which asks this companion to send the
/// clip of an action, and only when the moderator ticked "Send clips for my actions automatically".
/// Nothing else that comes back is a command, and nothing here acts on the request: it is handed
/// on as a message, and the clip code decides whether there is anything to send.</para>
/// <para><strong>With no instance named</strong> (the clip link, open while that switch is on) the
/// socket opens with only the device token and the cursor, and the server sends it clip requests
/// and no presence.</para>
/// <para><strong>Why a WebSocket now.</strong> The protocol first chose a long poll here so that a
/// proxy that breaks WebSockets degraded it to a slow poll rather than to nothing. That is still
/// the rule: the link opens this socket first and, when it cannot connect or keeps dropping, falls
/// back to the same events by long polling, then tries the socket again later.</para>
/// </remarks>
public sealed class ClientLiveSocketFactory : ILiveSocketFactory
{
    private readonly ClipSettingsFeed? _clips;

    public ClientLiveSocketFactory(ClipSettingsFeed? clips = null) => _clips = clips;

    /// <summary>How long a connect may take before it counts as unreachable.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    public async Task<LiveConnect> ConnectAsync(
        ServerPairing pairing,
        string? instanceId,
        string? worldId,
        string? after,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pairing);

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {pairing.DeviceToken}");
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);

        try
        {
            await socket.ConnectAsync(pairing.LiveSocketEndpoint(instanceId, after, worldId), timeout.Token).ConfigureAwait(false);
            return new LiveConnect(LiveConnectOutcome.Connected, new ClientLiveSocket(socket, _clips));
        }
        catch (WebSocketException) when (socket.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            socket.Dispose();
            return new LiveConnect(LiveConnectOutcome.Unauthorised);
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException
            || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            socket.Dispose();
            return new LiveConnect(LiveConnectOutcome.Unreachable);
        }
    }

    private sealed class ClientLiveSocket : ILiveSocket
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly ClientWebSocket _socket;
        private readonly byte[] _buffer = new byte[16 * 1024];

        // One send at a time: the instance and the Clips settings are sent from different threads.
        private readonly SemaphoreSlim _sending = new(1, 1);
        private readonly ClipSettingsFeed? _clips;

        public ClientLiveSocket(ClientWebSocket socket, ClipSettingsFeed? clips)
        {
            _socket = socket;
            _clips = clips;

            if (clips is null)
                return;

            clips.Changed += SendClips;

            if (clips.Current is { } current)
                SendClips(current);
        }

        /// <summary>Tells the server the Clips settings. Fire and forget: a failed send is the connection closing.</summary>
        private void SendClips(ClipSettingsReport report)
            => _ = Task.Run(async () =>
            {
                try
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(
                        new { op = ClipSettingsReport.Op, on = report.On, recording = report.Recording, minutes = report.Minutes, sendForActions = report.SendForActions },
                        Json);
                    await SendAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException or IOException)
                {
                }
            });

        private async Task SendAsync(byte[] bytes, CancellationToken cancellationToken)
        {
            await _sending.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sending.Release();
            }
        }

        public async Task<LiveSocketMessage?> ReceiveAsync(CancellationToken cancellationToken)
        {
            using var message = new MemoryStream();

            try
            {
                while (true)
                {
                    var result = await _socket.ReceiveAsync(_buffer, cancellationToken).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                        return null;

                    message.Write(_buffer, 0, result.Count);

                    if (!result.EndOfMessage)
                        continue;

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        message.SetLength(0);
                        continue;
                    }

                    var parsed = ParseMessage(message.ToArray());
                    message.SetLength(0);

                    if (parsed is not null)
                        return parsed;
                }
            }
            catch (WebSocketException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        public Task SubscribeAsync(string instanceId, string? worldId, CancellationToken cancellationToken)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { op = "subscribe", instanceId, worldId }, Json);
            return SendAsync(bytes, cancellationToken);
        }

        public async Task CloseAsync()
        {
            if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
                return;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            try
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
            {
                _socket.Abort();
            }
        }

        public void Dispose()
        {
            if (_clips is not null)
                _clips.Changed -= SendClips;

            _socket.Dispose();
            _sending.Dispose();
        }
    }

    /// <summary>
    /// One text message from the server, parsed. A message the client does not understand is skipped
    /// (null), never fatal.
    /// </summary>
    public static LiveSocketMessage? ParseMessage(byte[] bytes)
    {
        try
        {
            var message = JsonSerializer.Deserialize<LiveSocketMessage>(bytes, MessageJson);

            // A clip request is the whole message, not a field of it.
            return message is { Kind: ClipRequestWire.Kind }
                ? message with { ClipRequest = JsonSerializer.Deserialize<ClipRequestMessage>(bytes, MessageJson) }
                : message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions MessageJson = new(JsonSerializerDefaults.Web);
}
