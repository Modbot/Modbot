namespace Modbot.Cloud.Engine;

/// <summary>
/// Cloud's names for the events companions send: the same strings a Modbot server stores them
/// under (foundation 5.3.1), so a trend in Cloud and a chart on a server mean the same thing.
/// </summary>
/// <remarks>
/// A client type not listed is stored as <see cref="Unrecognised"/> with the client's word kept in
/// <c>type_raw</c> and its data whole: an older Cloud never throws away what a newer client reports.
/// Renaming one of these is a data migration. Adding one is not.
/// </remarks>
public static class EventTypes
{
    public const string Unrecognised = "modbot.unrecognised";

    public const string InstanceJoined = "vrchat.instance.join";
    public const string InstancePresenceObserved = "vrchat.instance.presence";
    public const string InstanceLeft = "vrchat.instance.leave";
    public const string AvatarChanged = "vrchat.avatar.change";
    public const string InstanceLogStopped = "vrchat.instance.log-stopped";

    /// <summary>The client protocol's event names, and what Cloud calls each.</summary>
    public static readonly IReadOnlyDictionary<string, string> FromClient = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["InstanceJoined"] = InstanceJoined,
        ["InstancePresenceObserved"] = InstancePresenceObserved,
        ["InstanceLeft"] = InstanceLeft,
        ["AvatarChanged"] = AvatarChanged,
        ["LogStopped"] = InstanceLogStopped,
    };

    /// <summary>Cloud's type and, when it is unrecognised, the raw name to keep beside it.</summary>
    public static (string Type, string? TypeRaw) Classify(string clientType)
    {
        ArgumentNullException.ThrowIfNull(clientType);

        if (FromClient.TryGetValue(clientType, out var known))
            return (known, null);

        var raw = clientType.Length > StoredEvent.MaxTypeLength ? clientType[..StoredEvent.MaxTypeLength] : clientType;
        return (Unrecognised, raw);
    }
}
