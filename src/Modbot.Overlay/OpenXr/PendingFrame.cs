namespace Modbot.Overlay.OpenXr;

/// <summary>
/// The picture waiting to go into the headset: written by the UI thread whenever the compositor
/// draws, taken by the frame thread on its next frame.
/// </summary>
/// <remarks>
/// <para>OpenXR has no "set texture once" as OpenVR does; a layer is submitted every frame by a
/// thread of the runtime's own. The two threads meet here and nowhere else. <see cref="Offer"/>
/// copies the pixels in under a lock and returns, so the UI thread never waits on the headset,
/// and <see cref="TryTake"/> copies them out under the same lock, so the frame thread never
/// reads a half-written picture.</para>
/// <para>Only the latest picture is kept. Two offers before a frame upload once, because the
/// first was never shown and there is nothing to be gained by showing it now. An offer made
/// before the runtime has started is kept too, and goes up on the first frame.</para>
/// </remarks>
public sealed class PendingFrame
{
    private readonly object _lock = new();
    private byte[]? _pixels;
    private int _length;
    private bool _pending;

    /// <summary>Whether a picture is waiting to be uploaded.</summary>
    public bool HasPending
    {
        get
        {
            lock (_lock)
                return _pending;
        }
    }

    /// <summary>Replaces whatever was waiting with this picture.</summary>
    public void Offer(ReadOnlySpan<byte> pixels)
    {
        lock (_lock)
        {
            if (_pixels is null || _pixels.Length < pixels.Length)
                _pixels = new byte[pixels.Length];

            pixels.CopyTo(_pixels);
            _length = pixels.Length;
            _pending = true;
        }
    }

    /// <summary>
    /// Copies the waiting picture into <paramref name="destination"/> and clears it. False when
    /// nothing is waiting, or when what is waiting is not the size the destination expects — a
    /// picture of the wrong size is dropped rather than uploaded torn.
    /// </summary>
    public bool TryTake(Span<byte> destination)
    {
        lock (_lock)
        {
            if (!_pending || _pixels is null)
                return false;

            _pending = false;
            if (_length != destination.Length)
                return false;

            _pixels.AsSpan(0, _length).CopyTo(destination);
            return true;
        }
    }
}
