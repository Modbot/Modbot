namespace Modbot.Cloud.Engine;

/// <summary>
/// Cloud's names for the events the desktop client parses out of VRChat's log.
/// </summary>
/// <remarks>
/// <para>
/// The client names its events after its own classes (<c>PlayerJoined</c>); Cloud stores them as
/// hierarchical strings, the way the server stores facts (foundation 5.3.1). A name the client sends
/// that is not in <see cref="FromClient"/> is kept as <see cref="Unrecognised"/>, with the client's
/// word in <c>type_raw</c>: an old Cloud never throws away what a newer client understood.
/// </para>
/// <para>
/// Renaming one of these is a data migration. Adding one is not.
/// </para>
/// </remarks>
public static class LogEventTypes
{
    public const string Unrecognised = "modbot.unrecognised";

    public const string PlayerJoined = "vrchat.log.player.joined";
    public const string PlayerLeft = "vrchat.log.player.left";
    public const string PlayerEnteredRoom = "vrchat.log.player.entered-room";
    public const string PlayerLeftRoom = "vrchat.log.player.left-room";
    public const string AvatarSwitched = "vrchat.log.avatar.switched";
    public const string LocalLeftRoom = "vrchat.log.local.left-room";
    public const string LocalDestinationSet = "vrchat.log.local.destination-set";
    public const string LocalJoining = "vrchat.log.local.joining";
    public const string LocalIdentified = "vrchat.log.local.identified";

    /// <summary>The client's event names, and what Cloud calls each.</summary>
    public static readonly IReadOnlyDictionary<string, string> FromClient = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PlayerJoined"] = PlayerJoined,
        ["PlayerLeft"] = PlayerLeft,
        ["RemotePlayerEnteredRoom"] = PlayerEnteredRoom,
        ["RemotePlayerLeftRoom"] = PlayerLeftRoom,
        ["AvatarSwitched"] = AvatarSwitched,
        ["LocalPlayerLeftRoom"] = LocalLeftRoom,
        ["DestinationSet"] = LocalDestinationSet,
        ["JoiningInstance"] = LocalJoining,
        ["LocalPlayerIdentified"] = LocalIdentified,
    };

    /// <summary>Cloud's type and, when it is unrecognised, the raw name to keep beside it.</summary>
    public static (string Type, string? TypeRaw) Classify(string clientType)
    {
        ArgumentNullException.ThrowIfNull(clientType);

        if (FromClient.TryGetValue(clientType, out var known))
            return (known, null);

        var raw = clientType.Length > LogEvent.MaxTypeLength ? clientType[..LogEvent.MaxTypeLength] : clientType;
        return (Unrecognised, raw);
    }
}
