using System.IO.Pipes;
using System.Text;

namespace Modbot.Companion.Pairing;

/// <summary>
/// Where a second copy of this program leaves a pairing link for the copy that is already running.
/// </summary>
/// <remarks>
/// <para><strong>Why there is a second copy at all.</strong> When the moderator presses "Open in
/// Modbot" in their browser, Windows starts a fresh <c>Modbot.exe</c> with the link as its
/// argument. The client is single-instance — one tray icon, one log reader — so that fresh copy
/// hands the link to the running one and exits, and the running one pairs.</para>
/// <para><strong>What this reads, and from where.</strong> One named pipe on this machine, opened
/// so that only the same Windows account can connect to it. What comes through it is a short piece
/// of text — a pairing link, or the word that means "show your window" — and it is bounded in size
/// and read once per connection. Nothing is read from disk here, and nothing is written to it.</para>
/// <para><strong>Nothing leaves the machine.</strong> A named pipe never crosses the network; the
/// text goes from one copy of this program to another on the same PC and nowhere else. The link is
/// then checked like any other untrusted input (<see cref="PairingToken"/>) before anything is
/// sent to any server.</para>
/// </remarks>
public sealed class PairingLinkInbox
{
    /// <summary>One name, so a second copy knows where to knock.</summary>
    public const string DefaultPipeName = "modbot-companion-pairing";

    /// <summary>
    /// A pairing link is a few hundred bytes. The bound means a misbehaving neighbour cannot make
    /// the running client buffer anything worth mentioning.
    /// </summary>
    public const int MaxMessageBytes = 8 * 1024;

    /// <summary>How long a connected sender gets to say its piece before it is dropped.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    private readonly string _pipeName;

    public PairingLinkInbox(string pipeName = DefaultPipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
    }

    /// <summary>
    /// Waits for messages until cancelled, handing each one to <paramref name="onMessage"/>.
    /// </summary>
    /// <remarks>
    /// Returns quietly if the pipe cannot be created — which on a shared PC means another Windows
    /// account's client already owns the name. The client then runs without an inbox: pairing by
    /// link falls back to pasting the token, and nothing else is affected.
    /// </remarks>
    public async Task ListenAsync(Func<string, Task> onMessage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onMessage);

        // One server instance for the whole life of the inbox, disconnected between callers rather
        // than disposed and recreated. On Windows that is merely the ordinary pattern. On Linux and
        // macOS a named pipe is a Unix socket, and a second copy of the client that connects while
        // the first message is still being read sits in the listen backlog -- its write even
        // succeeds -- until the server stream is disposed, which drops the backlog and loses the
        // link. Disconnect() closes the finished connection and keeps the listening socket.
        NamedPipeServerStream server;
        try
        {
            server = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.In,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another copy of Modbot owns the pipe. That copy is the one that should be listening.
            return;
        }

        await using (server.ConfigureAwait(false))
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // A caller that vanished between connecting and being accepted. Reset and go
                    // round again -- after a pause, so a fault that repeats cannot spin hot.
                    DisconnectQuietly(server);
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    var message = await ReadOneAsync(server, cancellationToken).ConfigureAwait(false);
                    if (message is { Length: > 0 })
                        await onMessage(message).ConfigureAwait(false);
                }
                finally
                {
                    DisconnectQuietly(server);
                }
            }
        }
    }

    private static void DisconnectQuietly(NamedPipeServerStream server)
    {
        // Not guarded by IsConnected on purpose. When the caller closes its end first -- which is
        // every send, since TrySendAsync writes and leaves -- the server's last read ends with a
        // broken pipe and .NET marks the stream Broken, not Connected. Disconnect() is the move
        // that takes a Broken stream back to listening; skipping it leaves every later
        // WaitForConnectionAsync throwing "pipe is broken" and the inbox deaf.
        try
        {
            server.Disconnect();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Already disconnected, or never connected; either way the next wait starts clean.
        }
    }

    public static async Task<bool> TrySendAsync(
        string message,
        string pipeName = DefaultPipeName,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var bytes = Encoding.UTF8.GetBytes(message);
        if (bytes.Length > MaxMessageBytes)
            return false;

        try
        {
            var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            await using (client.ConfigureAwait(false))
            {
                await client.ConnectAsync(
                    (int)(timeout ?? TimeSpan.FromSeconds(3)).TotalMilliseconds, cancellationToken).ConfigureAwait(false);
                await client.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await client.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// One connection, one message: everything the sender writes before it closes, bounded. A
    /// sender that exceeds the bound or stalls is dropped without being heard.
    /// </summary>
    private static async Task<string?> ReadOneAsync(Stream pipe, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);

        var buffer = new byte[MaxMessageBytes + 1];
        var total = 0;

        try
        {
            while (total < buffer.Length)
            {
                var read = await pipe.ReadAsync(buffer.AsMemory(total), timeout.Token).ConfigureAwait(false);
                if (read == 0)
                    break;

                total += read;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        if (total > MaxMessageBytes)
            return null;

        return Encoding.UTF8.GetString(buffer, 0, total).Trim();
    }
}
