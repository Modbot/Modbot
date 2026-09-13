using Modbot.Api.Auth;
using Modbot.Core.Data.Entities;

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
    string? DiscordUserId)
{
    public static SessionUser From(ModbotUser user)
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
            user.DiscordUserId);
    }
}
