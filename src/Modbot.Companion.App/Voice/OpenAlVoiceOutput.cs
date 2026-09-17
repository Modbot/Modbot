using Modbot.Companion.Voice;
using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.Enumeration;

namespace Modbot.Companion.App.Voice;

/// <summary>
/// Sound out on Linux: OpenAL Soft, through the Silk.NET binding.
/// </summary>
/// <remarks>
/// <para><strong>Output only.</strong> OpenAL has a capture side; none of it is referenced here,
/// and the source guard (<c>CompanionSourceGuardTests</c>) fails the build if it ever is.</para>
/// <para><strong>The default follows the desktop.</strong> A clip is played on a device opened
/// for that clip and closed after it. Opening OpenAL's default device hands the sound to
/// PipeWire or PulseAudio, which put it on whatever the desktop currently calls the default
/// output, so "System default" here moves when the desktop's default moves. Devices are named
/// by OpenAL's own strings, which is what the setting remembers.</para>
/// <para>Nothing here reads a file or touches the network. The library it loads
/// (<c>libopenal.so</c>, LGPL, see THIRD-PARTY-NOTICES.md) is the one shipped beside the
/// companion.</para>
/// </remarks>
internal sealed unsafe class OpenAlVoiceOutput : IOutputDevices, IVoicePlayer, IDisposable
{
    private readonly ALContext _alc;
    private readonly AL _al;
    private readonly Enumeration? _enumeration;

    public OpenAlVoiceOutput()
    {
        _alc = ALContext.GetApi(true);
        _al = AL.GetApi(true);
        _enumeration = _alc.TryGetExtension<Enumeration>(null, out var enumeration) ? enumeration : null;
    }

    public IReadOnlyList<OutputDevice> List()
    {
        if (_enumeration is null)
            return [];

        return [.. _enumeration
            .GetStringList(GetEnumerationContextStringList.DeviceSpecifiers)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => new OutputDevice(name, name))];
    }

    public OutputDevice? Default()
    {
        var name = _enumeration?.GetString(null, GetEnumerationContextString.DefaultDeviceSpecifier);
        return string.IsNullOrWhiteSpace(name) ? null : new OutputDevice(name, name);
    }

    public Task PlayAsync(VoiceClip clip, OutputDevice? device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clip);

        // OpenAL blocks between calls and polls to know when a clip has finished, so it lives on
        // a worker thread and the caller awaits it.
        return Task.Run(() => Play(clip, device?.Id, cancellationToken), cancellationToken);
    }

    private void Play(VoiceClip clip, string? deviceName, CancellationToken cancellationToken)
    {
        var device = _alc.OpenDevice(deviceName);
        if (device is null)
            throw new InvalidOperationException("OpenAL could not open the output device.");

        Context* context = null;
        uint buffer = 0;
        uint source = 0;

        try
        {
            context = _alc.CreateContext(device, null);
            if (context is null)
                throw new InvalidOperationException("OpenAL could not make a context.");

            _alc.MakeContextCurrent(context);

            var pcm = new short[clip.Samples.Length];
            for (var i = 0; i < pcm.Length; i++)
                pcm[i] = (short)Math.Round(Math.Clamp(clip.Samples[i], -1f, 1f) * short.MaxValue);

            buffer = _al.GenBuffer();
            _al.BufferData(buffer, BufferFormat.Mono16, pcm, clip.SampleRate);

            source = _al.GenSource();
            _al.SetSourceProperty(source, SourceInteger.Buffer, (int)buffer);
            _al.SourcePlay(source);

            while (!cancellationToken.IsCancellationRequested)
            {
                _al.GetSourceProperty(source, GetSourceInteger.SourceState, out int state);
                if ((SourceState)state != SourceState.Playing)
                    break;

                Thread.Sleep(20);
            }

            _al.SourceStop(source);
        }
        finally
        {
            if (source != 0)
                _al.DeleteSource(source);

            if (buffer != 0)
                _al.DeleteBuffer(buffer);

            _alc.MakeContextCurrent(null);

            if (context is not null)
                _alc.DestroyContext(context);

            _alc.CloseDevice(device);
        }
    }

    public void Dispose()
    {
        _enumeration?.Dispose();
        _al.Dispose();
        _alc.Dispose();
    }
}
