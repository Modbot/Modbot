using Modbot.Api.Auth;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Auth;

/// <summary>
/// The signed-in account, as the SPA sees it. Returned by sign-in, by <c>/api/auth/me</c> and by
/// the wizard's create-administrator step, so all three agree.
/// </summary>
/// <param name="Permissions">
/// The bitfield. Kept for callers that compose it; the SPA must not gate on it, because
/// <c>Administrator</c> is bit 62 and JavaScript numbers round above 2^53.
/// </param>
/// <param name="PermissionNames">The same permissions as names. This is what the SPA gates on.</param>
/// <param name="Roles">Role names, for the account page. Enforcement is always server-side.</param>
/// <param name="VRChatLinked">
/// False until the person has linked their VRChat account (design §4.3). While false the SPA
/// shows the link page and nothing else, and the server refuses everything but the link.
/// </param>
/// <param name="ChatOn">
/// Whether Chat answers: AI is on and Chat is on (<see cref="Features.Chat.ChatSwitch"/>). The SPA
/// offers Chat in its page list only while this is true and the person holds <c>UseAiChat</c>;
/// the page itself still opens from a direct link and says Chat is off.
/// </param>
/// <param name="BriefsOn">
/// Whether the instance and person popups offer an AI brief: Chat answers and briefs are on
/// (<see cref="Features.Chat.ChatSwitch.BriefsOn"/>). The SPA shows the buttons only while this is
/// true and the person holds <c>UseAiChat</c>; the server refuses a brief otherwise either way.
/// </param>
/// <param name="Rank">
/// The position of the account's highest role, first at 0 (accounts and access design §3.5). Null
/// with no role. The SPA greys what the server would refuse by comparing it with a row's own;
/// enforcement is always server-side.
/// </param>
/// <param name="DiscordUsername">The Discord username when the account was proven. Null for a typed id.</param>
/// <param name="DiscordProven">The person signed in to Discord from the account page to prove the id (design §4.6).</param>
/// <param name="DiscordWorksUntil">For a typed, unproven id: the day it stops counting. Null otherwise.</param>
/// <param name="ApiKey">
/// The key the request was made with, and what it may do now; null for a session. Only
/// <c>/api/auth/me</c> fills it in (API conventions design §7): a program holding a key had no
/// other way to learn its own permissions over REST.
/// </param>
/// <param name="GetsEventInvites">Whether events that name this account as host or staff invite it.</param>
public sealed record SessionUser(
    Guid Id,
    string Username,
    ModbotPermissions Permissions,
    IReadOnlyList<string> PermissionNames,
    IReadOnlyList<string> Roles,
    bool VRChatLinked,
    string? VRChatUserId,
    string? VRChatDisplayName,
    string? Email,
    string? DiscordUserId,
    string? DiscordUsername,
    bool DiscordProven,
    DateTimeOffset? DiscordWorksUntil,
    bool ChatOn,
    int? Rank = null,
    bool BriefsOn = false,
    KeyInUse? ApiKey = null,
    bool GetsEventInvites = true)
{
    public static SessionUser From(ModbotUser user, Features.Chat.AiSwitches on)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(on);

        var permissions = user.EffectivePermissions;

        return new SessionUser(
            user.Id,
            user.Username,
            permissions,
            PermissionCatalog.NamesOf(permissions),
            user.Roles.Select(r => r.Role.Name).Order().ToList(),
            user.IsVRChatLinked,
            user.VRChatUserId,
            user.VRChatDisplayName,
            user.Email,
            user.DiscordUserId,
            user.IsDiscordProven ? user.DiscordUsername : null,
            user.IsDiscordProven,
            StaffDiscord.TypedIdWorksUntil(user),
            on.ChatOn,
            user.Roles.Count == 0 ? null : RoleRank.Of(user),
            on.BriefsOn,
            GetsEventInvites: user.GetsEventInvites);
    }
}

/// <summary>The API key a request was made with, as <c>/api/auth/me</c> describes it.</summary>
/// <param name="Id">The key's id, as the API keys list shows it.</param>
/// <param name="Name">The name it was given.</param>
/// <param name="Start">Its first characters, to tell keys apart. Never the whole key.</param>
/// <param name="PermissionNames">
/// What the key may do right now: its own permissions, never more than the account holds now (API
/// keys design §3.2). This, not the account's <c>permissionNames</c>, is what a request with the
/// key is checked against.
/// </param>
/// <param name="ExpiresAt">When it stops working, or null for never.</param>
public sealed record KeyInUse(
    Guid Id,
    string Name,
    string Start,
    IReadOnlyList<string> PermissionNames,
    DateTimeOffset? ExpiresAt);
