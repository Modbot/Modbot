using Modbot.Shared.Clips;

namespace Modbot.Api.Features.Companion.Alerts;

/// <summary>
/// The latest Clips settings each paired device told us, so a rule can ask whether a moderator's
/// companion is ready to send a clip for their actions.
/// </summary>
/// <remarks>
/// <para><strong>A by-product, like <see cref="DeviceLocations"/>.</strong> A report rides the live
/// connection the device already holds (a WebSocket message, or the query of a long poll); there is
/// no endpoint of its own. It is in memory only, lost on restart (which costs one report, because a
/// companion tells us again every time it connects), and is never written down.</para>
/// <para><strong>It says nothing about whether the device is still there.</strong> A device that
/// switched off or went away stops reporting and keeps its last report here. Whether the companion is
/// running is a separate question for the caller to ask of the live connection.</para>
/// </remarks>
public sealed class ClipSettingsReports
{
    private readonly Dictionary<Guid, ClipSettingsReport> _latest = [];
    private readonly Lock _gate = new();

    /// <summary>Keeps this report as the device's latest, replacing the one before.</summary>
    public void Record(Guid deviceId, ClipSettingsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        lock (_gate)
            _latest[deviceId] = report;
    }

    /// <summary>The device's latest report, or null when it has not sent one since the server started.</summary>
    public ClipSettingsReport? For(Guid deviceId)
    {
        lock (_gate)
            return _latest.GetValueOrDefault(deviceId);
    }

    /// <summary>
    /// Whether any of these devices (a moderator's paired companions) last reported Clips on,
    /// recording, the ten-minute setting and "Send clips for my actions automatically" ticked.
    /// </summary>
    public bool IsReady(IEnumerable<Guid> deviceIds)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);

        lock (_gate)
            return deviceIds.Any(id => _latest.TryGetValue(id, out var report) && report.IsReady);
    }
}
