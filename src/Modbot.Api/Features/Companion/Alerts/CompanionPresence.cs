using Modbot.Core.Time;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.Companion.Alerts;

/// <summary>
/// Tells the audit-log loop whether a moderator's companion is in an instance, from
/// <see cref="DeviceLocations"/>. Reads what is already known; asks nothing of any device.
/// </summary>
public sealed class CompanionPresence : ICompanionPresence
{
    private readonly DeviceLocations _locations;
    private readonly IModbotClock _clock;

    public CompanionPresence(DeviceLocations locations, IModbotClock clock)
    {
        _locations = locations;
        _clock = clock;
    }

    public bool AnyInInstance() => _locations.AnyIn(_clock.UtcNow);
}
