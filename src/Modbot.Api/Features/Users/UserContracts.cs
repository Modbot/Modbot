using Modbot.Api.Auth;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Users;

public sealed record RoleRef(Guid Id, string Name);

/// <summary>One row of the users page.</summary>
/// <param name="PermissionNames">The union of the roles, as names. What the person can actually do.</param>
/// <param name="VRChatLinked">Whether they have linked their VRChat account yet (design §4.3).</param>
/// <param name="Rank">
/// The position of their highest role, first at 0 (design §3.5). Null when they hold no role,
/// which is below every role. The users page compares it with the signed-in person's own.
/// </param>
/// <param name="DiscordUserId">
/// The Discord account on the account. Only its holder can set it, by proving it (design §4.6);
/// the users page shows it and cannot change it.
/// </param>
/// <param name="DiscordProven">Whether the holder proved it, or it was typed in before proving existed.</param>
/// <param name="DiscordWorksUntil">For a typed, unproven id: the day it stops counting. Null otherwise.</param>
/// <param name="RolesFromDiscord">
/// Roles that follow a Discord role on this account and so cannot be given or taken here (staff
/// roles from Discord design §5). Filled by the list; empty elsewhere.
/// </param>
public sealed record UserSummary(
    Guid Id,
    string Username,
    IReadOnlyList<RoleRef> Roles,
    IReadOnlyList<string> PermissionNames,
    bool IsDisabled,
    bool IsDeleted,
    bool VRChatLinked,
    string? VRChatUserId,
    string? VRChatDisplayName,
    string? Email,
    string? DiscordUserId,
    string? DiscordUsername,
    bool DiscordProven,
    DateTimeOffset? DiscordWorksUntil,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    int? Rank = null,
    IReadOnlyList<Guid>? RolesFromDiscord = null)
{
    public static UserSummary From(ModbotUser user) => From(user, null);

    public static UserSummary From(ModbotUser user, IReadOnlySet<Guid>? rolesFromDiscord)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new UserSummary(
            user.Id,
            user.Username,
            user.Roles.Select(r => new RoleRef(r.Role.Id, r.Role.Name)).OrderBy(r => r.Name).ToList(),
            PermissionCatalog.NamesOf(user.EffectivePermissions),
            user.IsDisabled,
            user.IsDeleted,
            user.IsVRChatLinked,
            user.VRChatUserId,
            user.VRChatDisplayName,
            user.Email,
            user.DiscordUserId,
            user.IsDiscordProven ? user.DiscordUsername : null,
            user.IsDiscordProven,
            StaffDiscord.TypedIdWorksUntil(user),
            user.CreatedAt,
            user.LastLoginAt,
            user.Roles.Count == 0 ? null : RoleRank.Of(user),
            rolesFromDiscord?.Order().ToList() ?? []);
    }
}

/// <summary>Create an account with a temporary password the administrator will pass on.</summary>
/// <param name="RoleIds">May be empty: an account with no roles can sign in and do nothing.</param>
/// <param name="Email">Required, and unique across accounts (server info and account email design §4).</param>
/// <remarks>
/// No Discord id: only the person can put one on their account, by proving it (accounts and access
/// design §4.6). The field this had until then is ignored if a caller still sends it.
/// </remarks>
public sealed record CreateUserRequest(
    string Username,
    string Password,
    string? ConfirmPassword,
    IReadOnlyList<Guid> RoleIds,
    string? Email = null);

public sealed record SetRolesRequest(IReadOnlyList<Guid> RoleIds);

/// <summary>
/// Delete this account. <paramref name="Username"/> is the account's own username, typed out by
/// the person doing it.
/// </summary>
/// <remarks>
/// Checked on the server and not only in the browser: a confirmation that lives in the page is a
/// confirmation anybody calling the API straight has already passed.
/// </remarks>
public sealed record DeleteUserRequest(string Username);

/// <summary>
/// Null leaves the address alone. The Discord user id this carried until accounts and access
/// design §4.6 is gone: a Discord account is proven by its holder from their account page, never
/// typed, and a caller that still sends the field has it ignored.
/// </summary>
public sealed record ContactRequest(string? Email = null);

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
/// <param name="CanSubscribeToUpdates">
/// Whether this server has a Modbot Cloud to ask, and therefore whether the form shows the updates
/// checkbox at all (server info and account email design §5).
/// </param>
public sealed record InviteView(
    bool Usable,
    string? Reason,
    string? InvitedBy,
    IReadOnlyList<string> Roles,
    DateTimeOffset? ExpiresAt,
    bool CanSubscribeToUpdates = false);

/// <param name="Email">
/// Required, like every account's (server info and account email design §4). It is where this
/// person's reset link goes and the other thing the sign-in form accepts.
/// </param>
/// <param name="SubscribeToUpdates">
/// The person ticked "Receive emails from Modbot about new features and updates".
/// </param>
public sealed record JoinRequest(
    string Username,
    string Password,
    string? ConfirmPassword,
    string? Email = null,
    bool SubscribeToUpdates = false);

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
