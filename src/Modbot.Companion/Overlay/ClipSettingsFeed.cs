using Modbot.Shared.Clips;

namespace Modbot.Companion.Overlay;

/// <summary>
/// The companion's current Clips settings as it tells its server (automatic roles, 2026-10-24).
/// The clip code sets it when the settings or the recorder change; the live connection reads it
/// and sends it again when it changes.
/// </summary>
public sealed class ClipSettingsFeed
{
    private readonly object _lock = new();
    private ClipSettingsReport? _current;

    /// <summary>What the companion would tell its server now, or null before the first report.</summary>
    public ClipSettingsReport? Current
    {
        get
        {
            lock (_lock)
                return _current;
        }
    }

    /// <summary>Raised after <see cref="Set"/> stored a report that differs from the one before.</summary>
    public event Action<ClipSettingsReport>? Changed;

    /// <summary>Stores the report. Returns whether it differs from the one held before.</summary>
    public bool Set(ClipSettingsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        lock (_lock)
        {
            if (_current == report)
                return false;

            _current = report;
        }

        Changed?.Invoke(report);
        return true;
    }
}
