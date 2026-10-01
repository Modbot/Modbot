namespace Modbot.Core.Data.Entities;

/// <summary>The words stored in <see cref="VRChatFriend.LearnedFrom"/>.</summary>
public static class VRChatFriendSources
{
    /// <summary>VRChat accepted an invite to them, which it only does for friends.</summary>
    public const string Invite = "invite";

    /// <summary>VRChat refused an invite to them with a 403: not friends.</summary>
    public const string Refused = "refused";

    /// <summary>The friends list VRChat sent with the account when Modbot signed in.</summary>
    public const string SignIn = "signIn";
}

/// <summary>
/// Whether one person is a friend of the group's Modbot VRChat account, as far as Modbot has
/// learned. The table is <c>vrchat_friend</c> (calendar auto-invite design §3.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Remembered, not read.</strong> VRChat does not hand over the whole friends list
/// cheaply, and reading it would be an endpoint nobody has answered a rate limit for. So Modbot
/// learns as it goes: an invite VRChat accepts means a friend, a 403 means not, and the friends ids
/// VRChat sends with the account at sign-in add friends (never remove them: that list may be
/// incomplete).
/// </para>
/// <para>
/// Used only to decide whether an event invite goes through VRChat or straight to a Discord
/// message. A person marked not a friend is tried on VRChat again after a while, in case they
/// added the account since. A purge deletes the person's row.
/// </para>
/// </remarks>
public class VRChatFriend
{
    /// <summary>VRChat's id for the person. Opaque; never validated (foundation §3.1.1).</summary>
    public string UserId { get; set; } = string.Empty;

    public bool IsFriend { get; set; }

    /// <summary>When Modbot last learned this, from <c>IModbotClock</c>.</summary>
    public DateTimeOffset CheckedAt { get; set; }

    /// <summary>One of <see cref="VRChatFriendSources"/>.</summary>
    public string LearnedFrom { get; set; } = VRChatFriendSources.Invite;
}
