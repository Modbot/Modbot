using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Modbot.Companion.Ingest;

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

    /// <summary>Tells the server the instance the moderator is standing in now.</summary>
    Task SubscribeAsync(string instanceId, CancellationToken cancellationToken);

    Task CloseAsync();
}

/// <summary>Opens the live WebSocket to one paired server.</summary>
public interface ILiveSocketFactory
{
    Task<LiveConnect> ConnectAsync(ServerPairing pairing, string instanceId, string? after, CancellationToken cancellationToken);
}

/// <summary>
/// The live WebSocket to a paired server, over <see cref="ClientWebSocket"/>.
/// </summary>
/// <remarks>
/// <para><strong>What this sends.</strong> One WebSocket connection to one paired server, opened
/// with the device token as a bearer header, naming the instance the moderator is standing in and
/// the cursor of the last event received -- which that server already knows, because it is the
/// group's own instance and this client has been reporting presence for it. Afterwards the only
/// thing sent is the instance the moderator walked into, when they change rooms, and nothing else:
/// no log lines, nothing about the machine, nothing about instances belonging to any other
/// group.</para>
/// <para><strong>What comes back is data to display</strong> -- who joined or left the instance --
/// and never a command. There is nothing in this client that would act on one.</para>
/// <para><strong>Why a WebSocket now.</strong> The protocol first chose a long poll here so that a
/// proxy that breaks WebSockets degraded it to a slow poll rather than to nothing. That is still
/// the rule: the link opens this socket first and, when it cannot connect or keeps dropping, falls
/// back to the same events by long polling, then tries the socket again later.</para>
/// </remarks>
public sealed class ClientLiveSocketFactory : ILiveSocketFactory
{
    /// <summary>How long a connect may take before it counts as unreachable.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    public async Task<LiveConnect> ConnectAsync(
        ServerPairing pairing,
        string instanceId,
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
            await socket.ConnectAsync(pairing.LiveSocketEndpoint(instanceId, after), timeout.Token).ConfigureAwait(false);
            return new LiveConnect(LiveConnectOutcome.Connected, new ClientLiveSocket(socket));
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

        public ClientLiveSocket(ClientWebSocket socket) => _socket = socket;

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

                    var parsed = Parse(message.ToArray());
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

        public Task SubscribeAsync(string instanceId, CancellationToken cancellationToken)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { op = "subscribe", instanceId }, Json);
            return _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
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

        public void Dispose() => _socket.Dispose();

        /// <summary>A message the client does not understand is skipped, never fatal.</summary>
        private static LiveSocketMessage? Parse(byte[] bytes)
        {
            try
            {
                return JsonSerializer.Deserialize<LiveSocketMessage>(bytes, Json);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
