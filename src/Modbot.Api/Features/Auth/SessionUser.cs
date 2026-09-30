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
/// <param name="Rank">
/// The position of the account's highest role, first at 0 (accounts and access design §3.5). Null
/// with no role. The SPA greys what the server would refuse by comparing it with a row's own;
/// enforcement is always server-side.
/// </param>
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
    bool ChatOn,
    int? Rank = null)
{
    public static SessionUser From(ModbotUser user, bool chatOn)
    {
        ArgumentNullException.ThrowIfNull(user);

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
            chatOn,
            user.Roles.Count == 0 ? null : RoleRank.Of(user));
    }
}
