using System.Net;
using System.Net.Sockets;
using VRC.OSCQuery;

namespace Modbot.Companion.Talk;

/// <summary>
/// Taps VRChat's voice button and hears back whether the microphone is muted, over OSC and OSCQuery.
/// </summary>
/// <remarks>
/// <para>What this does: it listens on one free UDP port of this PC's loopback address for the
/// <c>/avatar/parameters/MuteSelf</c> message VRChat sends whenever its microphone changes, and
/// sends VRChat the <c>/input/Voice</c> button (1, a moment, then 0) to tap mute. Nothing else is
/// read, and every other message is dropped unread. What it learns is kept in memory only; nothing
/// is written to your disk.</para>
/// <para>What leaves the machine: nothing over the internet. The packets go to this PC's own
/// loopback address, never to another address. The one thing that does leave the PC is the
/// OSCQuery announcement VRChat looks for: a multicast DNS notice on the local network saying a
/// service named Modbot exists, with two port numbers and the loopback address, so VRChat knows where
/// to send. Nothing on the network can connect to those ports, because they are bound to loopback.
/// The announcement is withdrawn when this is disposed.</para>
/// <para>It takes no fixed port, so other OSC programs (face tracking, chatbox tools) keep their
/// own. If the service cannot start, <see cref="Problem"/> says so and the taps still go out; only
/// hearing back is lost.</para>
/// </remarks>
public sealed class VRChatOsc : IVRChatMic, IDisposable
{
    /// <summary>The name VRChat shows its user when it first sees Modbot.</summary>
    public const string ServiceName = "Modbot";

    /// <summary>VRChat's documented receive port, for when it has not announced another.</summary>
    public const int VRChatDefaultPort = 9000;

    private const string MuteSelfAddress = "/avatar/parameters/MuteSelf";

    private const string VoiceAddress = "/input/Voice";

    private const string VRChatServicePrefix = "VRChat-Client-";

    private readonly TimeSpan _hold;
    private readonly TimeSpan _wait;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();

    private OSCQueryService? _query;
    private UdpClient? _listener;
    private Task? _listening;
    private TaskCompletionSource<bool>? _waiter;
    private string? _problem;
    private int _vrchatPort = VRChatDefaultPort;
    private int _muted = -1;
    private bool _started;
    private bool _disposed;

    /// <param name="hold">How long the voice button is held down in a tap.</param>
    /// <param name="wait">How long a tap waits for VRChat to say what the microphone is now.</param>
    public VRChatOsc(TimeSpan hold, TimeSpan wait)
    {
        _hold = hold;
        _wait = wait;
    }

    /// <summary>The usual hold (about a sixth of a second) and wait (a second and a half).</summary>
    public VRChatOsc()
        : this(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(1500))
    {
    }

    /// <inheritdoc />
    public bool? Muted => Volatile.Read(ref _muted) switch { 0 => false, 1 => true, _ => null };

    /// <inheritdoc />
    public string? Problem
    {
        get
        {
            lock (_gate)
                return _problem;
        }
    }

    /// <summary>Starts listening and announcing. Never throws: a failure is left in <see cref="Problem"/>.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
                return;

            _started = true;
        }

        try
        {
            // Port 0 lets the system hand out a free one; the announcement then names the port that
            // is really open, so there is no gap in which another program could take it.
            var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var udpPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

            _listener = listener;
            _listening = Task.Run(() => ListenAsync(listener, _stopping.Token));

            _query = new OSCQueryServiceBuilder()
                .WithServiceName(ServiceName)
                .WithHostIP(IPAddress.Loopback)
                .WithUdpPort(udpPort)
                .WithTcpPort(Extensions.GetAvailableTcpPort())
                .WithDiscovery(new MeaModDiscovery(null!))
                .StartHttpServer()
                .AdvertiseOSC()
                .AdvertiseOSCQuery()
                .Build();

            _query.AddEndpoint<string>("/avatar/change", Attributes.AccessValues.WriteOnly);
            _query.AddEndpoint<bool>(MuteSelfAddress, Attributes.AccessValues.WriteOnly);
            _query.OnOscServiceAdded += OnServiceAdded;
            RememberVRChatPort(_query.GetOSCServices());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_gate)
                _problem = $"Modbot could not start listening for VRChat: {ex.Message}";
        }
    }

    /// <inheritdoc />
    public async Task<MicTap> TapAsync(CancellationToken cancellationToken)
    {
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
            _waiter = waiter;

        try
        {
            RefreshVRChatPort();

            using var sender = new UdpClient(AddressFamily.InterNetwork);
            var target = new IPEndPoint(IPAddress.Loopback, Volatile.Read(ref _vrchatPort));

            try
            {
                await sender.SendAsync(OscCodec.Write(VoiceAddress, 1), target, cancellationToken).ConfigureAwait(false);
                await Task.Delay(_hold, cancellationToken).ConfigureAwait(false);
                await sender.SendAsync(OscCodec.Write(VoiceAddress, 0), target, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return new MicTap(false, Muted);
            }

            // VRChat says nothing when the tap changed nothing, so waiting out the time is a normal
            // end, and what it last said is the answer.
            await Task.WhenAny(waiter.Task, Task.Delay(_wait, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return new MicTap(true, Muted);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_waiter, waiter))
                    _waiter = null;
            }
        }
    }

    /// <summary>Stops listening and withdraws the announcement.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        _stopping.Cancel();

        if (_query is { } query)
        {
            query.OnOscServiceAdded -= OnServiceAdded;
            try
            {
                query.Dispose();
            }
            catch (Exception)
            {
                // Withdrawing an announcement that is already gone is not worth a message.
            }
        }

        _listener?.Dispose();

        try
        {
            _listening?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // The listening task ends by being cancelled or by its socket closing.
        }

        _stopping.Dispose();
    }

    private async Task ListenAsync(UdpClient listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await listener.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // A reply from an address that was closed (Windows reports it on the next read).
                continue;
            }

            foreach (var message in OscCodec.Read(packet.Buffer))
            {
                if (message.Address == MuteSelfAddress && message.FirstAsBool is { } muted)
                    HearMuteSelf(muted);
            }
        }
    }

    private void HearMuteSelf(bool muted)
    {
        Volatile.Write(ref _muted, muted ? 1 : 0);

        TaskCompletionSource<bool>? waiter;
        lock (_gate)
            waiter = _waiter;

        waiter?.TrySetResult(muted);
    }

    private void OnServiceAdded(OSCQueryServiceProfile profile) => RememberVRChatPort([profile]);

    private void RefreshVRChatPort()
    {
        if (_query is null)
            return;

        try
        {
            RememberVRChatPort(_query.GetOSCServices());
        }
        catch (Exception)
        {
            // The last known port stays.
        }
    }

    private void RememberVRChatPort(IEnumerable<OSCQueryServiceProfile> services)
    {
        foreach (var service in services)
        {
            if (service.name.StartsWith(VRChatServicePrefix, StringComparison.OrdinalIgnoreCase) && service.port > 0)
                Volatile.Write(ref _vrchatPort, service.port);
        }
    }
}
