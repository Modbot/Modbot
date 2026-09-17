using Modbot.Overlay.Interaction;

namespace Modbot.Overlay.OpenXr;

/// <summary>
/// The controllers as the frame thread last saw them, for the UI thread to read.
/// </summary>
/// <remarks>
/// OpenXR's actions are synced on the frame thread, once a frame, because that is where the
/// frame's predicted display time is; the host asks for tracking on the UI thread, thirty times a
/// second. The two meet here. A publish copies the struct in under a lock and a read copies it out
/// under the same lock, so a read never sees half of one frame and half of the next, and neither
/// side allocates: the struct is a value, and the copy is a hundred-odd bytes.
/// </remarks>
public sealed class LatestTracking
{
    private readonly object _lock = new();
    private OverlayTracking _latest = OverlayTracking.None;

    /// <summary>Replaces what a read will answer with.</summary>
    public void Publish(in OverlayTracking tracking)
    {
        lock (_lock)
            _latest = tracking;
    }

    /// <summary>What was last published; <see cref="OverlayTracking.None"/> before anything was.</summary>
    public OverlayTracking Read()
    {
        lock (_lock)
            return _latest;
    }

    /// <summary>Back to nobody tracked, for when the session is gone.</summary>
    public void Clear() => Publish(OverlayTracking.None);
}
