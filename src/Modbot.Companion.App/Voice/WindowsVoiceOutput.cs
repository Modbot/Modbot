using System.Runtime.Versioning;
using Modbot.Companion.Voice;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Modbot.Companion.App.Voice;

/// <summary>
/// Sound out on Windows: every output device the machine has, which one is the default, and one
/// clip played on one of them.
/// </summary>
/// <remarks>
/// <para><strong>Output only.</strong> This asks Windows for its <em>render</em> endpoints — the
/// things sound comes out of — and never for capture ones. It opens no microphone, no line-in
/// and no loopback; the source guard (<c>CompanionSourceGuardTests</c>) fails the build if any
/// recording API appears anywhere in the client.</para>
/// <para><strong>The default follows Windows.</strong> When the moderator chose "Windows
/// default", a line is played as a stream Windows itself routes to the default device, so when
/// Windows moves the default — a headset switched on, a dock plugged in — the sound moves with
/// it, even mid-sentence, the way a voice chat program follows it. Each line opens its own
/// stream and closes it after, so nothing is held open between sentences. Windows also tells
/// this class when a device comes, goes or becomes the default, through its own device
/// notifications, which is how the settings page's list stays current; the notification itself
/// is answered with nothing more than a flag.</para>
/// <para>Nothing here reads a file or touches the network.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsVoiceOutput : IOutputDevices, IVoicePlayer, IDisposable
{
    /// <summary>Shared mode, event-driven, with a buffer this long. Announcements are not latency-critical.</summary>
    private const int LatencyMilliseconds = 100;

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient _notifications;

    /// <summary>
    /// Raised — on Windows' audio thread, so do nothing heavy — when a device comes, goes, or
    /// becomes the default.
    /// </summary>
    public event Action? Changed;

    public WindowsVoiceOutput()
    {
        _notifications = _enumerator.CreateNotificationClient(true);
        _notifications.DefaultDeviceChanged += (_, e) =>
        {
            if (e.Flow is DataFlow.Render)
                Changed?.Invoke();
        };
        _notifications.DeviceAdded += (_, _) => Changed?.Invoke();
        _notifications.DeviceRemoved += (_, _) => Changed?.Invoke();
        _notifications.DeviceStateChanged += (_, _) => Changed?.Invoke();
    }

    public IReadOnlyList<OutputDevice> List()
    {
        var devices = new List<OutputDevice>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
                devices.Add(new OutputDevice(device.ID, device.FriendlyName));
        }

        return devices;
    }

    public OutputDevice? Default()
    {
        if (!_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            return null;

        using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return new OutputDevice(device.ID, device.FriendlyName);
    }

    public async Task PlayAsync(VoiceClip clip, OutputDevice? device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clip);

        var builder = new WasapiPlayerBuilder()
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(LatencyMilliseconds);

        // "Windows default" is asked for as a stream that Windows itself routes to whatever the
        // default is, so a default that moves while a line is playing takes the line with it;
        // a chosen device is opened by its id.
        MMDevice? chosen = null;
        if (device is null)
            builder = builder.WithDefaultDeviceStreamRouting();
        else
            builder = builder.WithDevice(chosen = _enumerator.GetDevice(device.Id));

        try
        {
            using var player = await builder.BuildAsync().ConfigureAwait(false);

            // Handed over in the shape the mixer already runs at — stereo, at the device's own
            // rate — so nothing has to be converted on the way in.
            ISampleProvider source = new ClipSampleProvider(clip);
            if (player.DeviceMixFormat?.SampleRate is { } rate && rate != clip.SampleRate)
                source = new WdlResamplingSampleProvider(source, rate);

            source = new MonoToStereoSampleProvider(source);

            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is { } failure)
                    stopped.TrySetException(failure);
                else
                    stopped.TrySetResult();
            };

            player.Init(new SampleToWaveProvider(source));
            player.Play();

            using var stop = cancellationToken.Register(() =>
            {
                try
                {
                    player.Stop();
                }
                catch (Exception)
                {
                    // Stopping on the way out; the player is being thrown away either way.
                }
            });

            await stopped.Task.ConfigureAwait(false);
        }
        finally
        {
            chosen?.Dispose();
        }
    }

    public void Dispose()
    {
        _notifications.Dispose();
        _enumerator.Dispose();
    }

    /// <summary>A clip read out as mono float samples, once.</summary>
    private sealed class ClipSampleProvider(VoiceClip clip) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(clip.SampleRate, 1);

        public int Read(Span<float> buffer)
        {
            var available = Math.Min(buffer.Length, clip.Samples.Length - _position);
            if (available <= 0)
                return 0;

            clip.Samples.AsSpan(_position, available).CopyTo(buffer);
            _position += available;
            return available;
        }
    }
}
