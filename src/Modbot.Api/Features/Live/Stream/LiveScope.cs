using Modbot.Api.Auth;
using Modbot.Api.Features.Audit;
using Modbot.Api.Features.Events;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Live.Stream;

/// <summary>
/// What one live connection may be sent (live updates design §3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A person</strong> sees by permission, decided per event from what they hold at that
/// moment: presence, instances and Discord voice need "See live instances" (the Live page's own permission, M3
/// §7.4), alerts need "See analytics" (the card's own permission), reviews need "Review tickets"
/// (the sidebar count's). A permission removed mid-connection stops the next event.
/// </para>
/// <para>
/// One rule is about the fact and not only its type, so <see cref="CanSee"/> cannot hold it: a
/// member-report fact about a staff account is for those who hold Review tickets only. The reader
/// applies it to each fact it is about to send (<see cref="LiveReader"/>,
/// <c>MemberReportAccess.HidesAsync</c>).
/// </para>
/// <para>
/// <strong>A companion device</strong> sees presence in the one instance it named, and nothing
/// else: its token is ingest-scoped and reads one group's roster context, and this stream is that
/// roster as it changes. It is not sent alerts, reviews or instance events, and it is not sent
/// presence anywhere it is not standing -- the same boundary <c>DeviceLocations</c> draws for the
/// old alert long poll.
/// </para>
/// <para>
/// <strong>One widening, asked for and earned.</strong> A device that asks for moderation
/// (<c>moderation=true</c>) and whose owner holds "See the audit log" (the owner's permissions as
/// they are now, read again with every refresh) is also sent the audit log's facts about its own
/// instance, and about the people seen in it (<see cref="InstanceAudit"/>), each one filtered by the
/// audit log's own rules for the owner (<see cref="EventVisibility.CanSee"/>, evidence included).
/// An owner without that permission, or a device that did not ask, is sent presence only, as before.
/// </para>
/// </remarks>
public sealed class LiveScope
{
    private LiveScope(
        ModbotPermissions permissions, Guid? deviceId, string? instanceId, string? worldId, Guid? ownerId = null, bool moderation = false)
    {
        Permissions = permissions;
        DeviceId = deviceId;
        OwnerId = ownerId;
        AsksForModeration = moderation;
        InstanceId = instanceId;
        WorldId = worldId is { Length: > 0 } ? worldId : null;
    }

    /// <summary>For a person: what they hold. For a device: what its owner holds now, or none until that is known.</summary>
    public ModbotPermissions Permissions { get; }

    public Guid? DeviceId { get; }

    /// <summary>For a device: the account it was issued to. Null for a person.</summary>
    public Guid? OwnerId { get; }

    /// <summary>For a device: whether its client said it can show moderation events (<c>moderation=true</c>).</summary>
    public bool AsksForModeration { get; }

    /// <summary>For a device: the instance it is standing in. Null for a person.</summary>
    public string? InstanceId { get; }

    /// <summary>
    /// For a device: the world of that instance, when it said. An instance number is only unique
    /// inside one world, so a device that names its world is sent that world's instance and no
    /// other. A device that named none (an older client) is sent by the number alone, as before.
    /// </summary>
    public string? WorldId { get; }

    public bool IsDevice => DeviceId is not null;

    public static LiveScope ForPerson(ModbotPermissions permissions) => new(permissions, null, null, null);

    /// <param name="permissions">What the device's owner holds. Leave out for a device sent presence only.</param>
    /// <param name="ownerId">The account the device was issued to.</param>
    /// <param name="moderation">Whether the client asked for moderation events.</param>
    public static LiveScope ForDevice(
        Guid deviceId,
        string? instanceId,
        string? worldId = null,
        ModbotPermissions permissions = ModbotPermissions.None,
        Guid? ownerId = null,
        bool moderation = false)
        => new(permissions, deviceId, instanceId, worldId, ownerId, moderation);

    /// <summary>The same device, standing somewhere else now.</summary>
    public LiveScope InInstance(string? instanceId, string? worldId = null)
        => new(Permissions, DeviceId, instanceId, worldId, OwnerId, AsksForModeration);

    /// <summary>The same device with what its owner holds now.</summary>
    public LiveScope WithOwnerPermissions(ModbotPermissions permissions)
        => new(permissions, DeviceId, InstanceId, WorldId, OwnerId, AsksForModeration);

    /// <summary>
    /// Whether this device is sent the audit log's events: it asked, and its owner may read the
    /// moderation log.
    /// </summary>
    public bool CarriesModeration
        => IsDevice && AsksForModeration && AuditVisibility.CanSee(Permissions, AuditCategory.Moderation);

    /// <summary>Whether a fact placed in this instance (and world, when both sides know it) is the one this device stands in.</summary>
    public bool IsHere(string? instanceId, string? worldId)
        => InstanceId is { Length: > 0 }
            && string.Equals(instanceId, InstanceId, StringComparison.Ordinal)
            && (WorldId is null || string.Equals(worldId, WorldId, StringComparison.Ordinal));

    /// <summary>Whether this caller could be sent anything at all.</summary>
    public bool SeesAnything => IsDevice
        || EventVisibility.SeesAnything(Permissions)
        || ModbotAuth.Allows(Permissions, ModbotPermissions.ViewLiveInstances)
        || ModbotAuth.Allows(Permissions, ModbotPermissions.ViewAnalytics)
        || ModbotAuth.Allows(Permissions, ModbotPermissions.ReviewTickets)
        || ModbotAuth.Allows(Permissions, ModbotPermissions.ViewPosts);

    /// <summary>The posts' facts, which the Marketing tab redraws on (posts design §4.2).</summary>
    public const string PostTypes = "modbot.post.";

    /// <summary>The Twitch poll's facts, which the Live and Now pages' "Live on Twitch" card redraws on (Twitch design).</summary>
    public const string TwitchTypes = "modbot.twitch.";

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

        // A device: presence always; the audit log's moderation facts only when it carries them,
        // by the owner's permissions and the same narrowing a person gets (evidence, presence).
        if (IsDevice)
        {
            return LiveKinds.IsPresence(kind)
                || (CarriesModeration
                    && AuditVisibility.CategoryOf(type) == AuditCategory.Moderation
                    && EventVisibility.CanSee(Permissions, type));
        }

        if (EventVisibility.CanSee(Permissions, type))
            return true;

        if (LiveKinds.IsPresence(kind) || LiveKinds.IsInstance(kind) || LiveKinds.IsVoice(kind))
            return ModbotAuth.Allows(Permissions, ModbotPermissions.ViewLiveInstances);

        if (kind == LiveKinds.Alert)
            return ModbotAuth.Allows(Permissions, ModbotPermissions.ViewAnalytics);

        if (LiveKinds.IsReview(kind))
            return ModbotAuth.Allows(Permissions, ModbotPermissions.ReviewTickets);

        // A post's facts are seen by whoever may see the posts, as the Marketing tab shows them,
        // without the operational log they sit in.
        if (type.StartsWith(PostTypes, StringComparison.Ordinal))
            return ModbotAuth.Allows(Permissions, ModbotPermissions.ViewPosts);

        // The Twitch card is under the Live page's own rule, See live instances, without the
        // operational log the facts sit in.
        if (type.StartsWith(TwitchTypes, StringComparison.Ordinal))
            return ModbotAuth.Allows(Permissions, ModbotPermissions.ViewLiveInstances);

        return false;
    }

    /// <param name="aboutSomeoneHere">
    /// For a device: whether the fact is about a person seen in the instance it stands in
    /// (<see cref="InstanceAudit.AboutPeopleHereAsync"/>), which is how a group join or a Discord
    /// ban reaches a feed that has no instance of its own.
    /// </param>
    public bool Wants(LiveEvent @event, bool aboutSomeoneHere = false)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (!CanSee(@event.Kind, @event.Type))
            return false;

        // A device that has not said where it is gets nothing, the same as the alert hub: the
        // failure worth designing against is context reaching somewhere it was not needed.
        if (!IsDevice)
            return true;

        // The world counts when the device gave one: two instances in two worlds can share a
        // number, and a roster or a flagged card from the other one is somebody else's.
        if (IsHere(@event.InstanceId, @event.WorldId))
            return true;

        // Anything else a device is sent is about a person seen here, and presence never is: a
        // join or leave in another instance is somebody else's roster.
        return aboutSomeoneHere && CarriesModeration && !LiveKinds.IsPresence(@event.Kind);
    }
}
