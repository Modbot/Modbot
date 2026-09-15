using Modbot.Api.Auth;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Users;

public sealed record RoleRef(Guid Id, string Name);

/// <summary>One row of the users page.</summary>
/// <param name="PermissionNames">The union of the roles, as names. What the person can actually do.</param>
/// <param name="VRChatLinked">Whether they have linked their VRChat account yet (design §4.3).</param>
public sealed record UserSummary(
    Guid Id,
    string Username,
    IReadOnlyList<RoleRef> Roles,
    IReadOnlyList<string> PermissionNames,
    bool IsDisabled,
    bool VRChatLinked,
    string? VRChatUserId,
    string? VRChatDisplayName,
    string? Email,
    string? DiscordUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt)
{
    public static UserSummary From(ModbotUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new UserSummary(
            user.Id,
            user.Username,
            user.Roles.Select(r => new RoleRef(r.Role.Id, r.Role.Name)).OrderBy(r => r.Name).ToList(),
            PermissionCatalog.NamesOf(user.EffectivePermissions),
            user.IsDisabled,
            user.IsVRChatLinked,
            user.VRChatUserId,
            user.VRChatDisplayName,
            user.Email,
            user.DiscordUserId,
            user.CreatedAt,
            user.LastLoginAt);
    }
}

/// <summary>Create an account with a temporary password the administrator will pass on.</summary>
/// <param name="RoleIds">May be empty: an account with no roles can sign in and do nothing.</param>
public sealed record CreateUserRequest(
    string Username,
    string Password,
    string? ConfirmPassword,
    IReadOnlyList<Guid> RoleIds,
    string? Email = null,
    string? DiscordUserId = null);

public sealed record SetRolesRequest(IReadOnlyList<Guid> RoleIds);

/// <summary>
/// Null leaves a field alone; an empty string clears it. The same rule the integrations step
/// uses, so a form that only shows one field cannot wipe the other.
/// </summary>
public sealed record ContactRequest(string? Email = null, string? DiscordUserId = null);

/// <summary>A link that was just made. Shown once; the server keeps only its hash.</summary>
/// <param name="Path">Relative to wherever Modbot lives. The browser showing it knows its own address.</param>
/// <param name="Url">
/// The full address, built from the saved public address. Null until one is saved -- never from
/// the request, which anyone can forge.
/// </param>
public sealed record LinkCreated(Guid Id, string Path, string? Url, DateTimeOffset ExpiresAt);

public sealed record CreateInviteRequest(IReadOnlyList<Guid> RoleIds);

/// <summary>An invite that has not been used yet, for the list on the users page.</summary>
public sealed record PendingInvite(
    Guid Id,
    string CreatedBy,
    IReadOnlyList<string> Roles,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>What the person opening an invite link sees before they fill anything in.</summary>
/// <param name="Reason">Why it cannot be used, when it cannot. Plain words for the page.</param>
public sealed record InviteView(
    bool Usable,
    string? Reason,
    string? InvitedBy,
    IReadOnlyList<string> Roles,
    DateTimeOffset? ExpiresAt);

public sealed record JoinRequest(string Username, string Password, string? ConfirmPassword);

/// <summary>What the person opening a reset link sees.</summary>
public sealed record ResetView(bool Usable, string? Reason, string? Username);

public sealed record ResetRequest(string Password, string? ConfirmPassword);

/// <summary>Which ways this deployment can send a reset link somebody asks for (design §4.2).</summary>
/// <param name="Ways">"email", "discord", or both, in order of preference. Empty means neither is set up.</param>
/// <param name="Reason">Why nothing can be sent, when nothing can, in words for the sign-in page.</param>
public sealed record ForgotPasswordWays(bool Available, IReadOnlyList<string> Ways, string? Reason);

public sealed record ForgotPasswordRequest(string Username);

/// <summary>
/// The same sentence whoever asked and whatever happened (design §4.2). It changes only with the
/// deployment: while account email is being held under the daily limit (design §4.4), it says so.
/// </summary>
public sealed record ForgotPasswordResponse(string Message)
{
    public static ForgotPasswordResponse Standard { get; } =
        new("If that account can be reached, a reset link is on its way.");

    public static ForgotPasswordResponse Delayed { get; } =
        new("If that account can be reached, a reset link is on its way. Email is running behind, so it may be late.");
}
