using Modbot.Api.Auth;
using Modbot.Api.Features.Events;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Live.Stream;

/// <summary>
/// What one live connection may be sent (live updates design §3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A person</strong> sees by permission, decided per event from what they hold at that
/// moment: presence and instances need "See live instances" (the Live page's own permission, M3
/// §7.4), alerts need "See analytics" (the card's own permission), reviews need "Review tickets"
/// (the sidebar count's). A permission removed mid-connection stops the next event.
/// </para>
/// <para>
/// <strong>A companion device</strong> sees presence in the one instance it named, and nothing
/// else: its token is ingest-scoped and reads one group's roster context, and this stream is that
/// roster as it changes. It is not sent alerts, reviews or instance events, and it is not sent
/// presence anywhere it is not standing -- the same boundary <c>DeviceLocations</c> draws for the
/// old alert long poll.
/// </para>
/// </remarks>
public sealed class LiveScope
{
    private LiveScope(ModbotPermissions permissions, Guid? deviceId, string? instanceId)
    {
        Permissions = permissions;
        DeviceId = deviceId;
        InstanceId = instanceId;
    }

    public ModbotPermissions Permissions { get; }

    public Guid? DeviceId { get; }

    /// <summary>For a device: the instance it is standing in. Null for a person.</summary>
    public string? InstanceId { get; }

    public bool IsDevice => DeviceId is not null;

    public static LiveScope ForPerson(ModbotPermissions permissions) => new(permissions, null, null);

    public static LiveScope ForDevice(Guid deviceId, string? instanceId) => new(ModbotPermissions.None, deviceId, instanceId);

    /// <summary>The same device, standing somewhere else now.</summary>
    public LiveScope InInstance(string? instanceId) => new(Permissions, DeviceId, instanceId);

    /// <summary>Whether this caller could be sent anything at all.</summary>
    public bool SeesAnything => IsDevice
        || EventVisibility.SeesAnything(Permissions)
        || ModbotAuth.Allows(Permissions, ModbotPermissions.ViewLiveInstances)
        || ModbotAuth.Allows(Permissions, ModbotPermissions.ViewAnalytics)
        || ModbotAuth.Allows(Permissions, ModbotPermissions.ReviewTickets);

    /// <summary>
    /// Whether an event of this kind and fact type may be sent. A named kind is seen by the
    /// permission of the screen that shows it, or by the audit log's rules for the fact behind
    /// it -- whichever the caller holds. Any other fact follows the audit log's rules alone
    /// (API keys design §4.3), presence's narrowing to "See live instances" included.
    /// </summary>
    public bool CanSee(string kind, string type)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(type);

        if (IsDevice)
            return LiveKinds.IsPresence(kind);

        if (EventVisibility.CanSee(Permissions, type))
            return true;

        if (LiveKinds.IsPresence(kind) || LiveKinds.IsInstance(kind))
            return ModbotAuth.Allows(Permissions, ModbotPermissions.ViewLiveInstances);

        if (kind == LiveKinds.Alert)
            return ModbotAuth.Allows(Permissions, ModbotPermissions.ViewAnalytics);

        if (LiveKinds.IsReview(kind))
            return ModbotAuth.Allows(Permissions, ModbotPermissions.ReviewTickets);

        return false;
    }

    public bool Wants(LiveEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (!CanSee(@event.Kind, @event.Type))
            return false;

        // A device that has not said where it is gets nothing, the same as the alert hub: the
        // failure worth designing against is context reaching somewhere it was not needed.
        return !IsDevice || (InstanceId is { Length: > 0 } && string.Equals(@event.InstanceId, InstanceId, StringComparison.Ordinal));
    }
}
