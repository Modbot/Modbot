using Microsoft.AspNetCore.Http;
using Modbot.Api.Auth;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;

namespace Modbot.Api.Features.Roles;

/// <summary>
/// The order of roles as a rule for callers (accounts and access design §3.5).
/// </summary>
/// <remarks>
/// <para>
/// Manage users and Manage roles only reach what is below the caller's highest role: accounts
/// whose highest role is below it, and roles that sit below it. The same rank is refused. An
/// account holding the Administrator permission is the one exception, and may act on anyone,
/// including other administrators.
/// </para>
/// <para>
/// The caller's rank is read from the database at each call, for a signed-in person and for an API
/// key alike: a key carries its owner's account id, so it follows its owner's rank whatever
/// permissions it was narrowed to.
/// </para>
/// <para>
/// Each check returns the 403 to send, or null when the caller may go ahead. Refusals are plain
/// sentences, with no fact recorded: the person was stopped, nothing happened.
/// </para>
/// </remarks>
public static class RoleOrder
{
    public const string AccountsBelow = "You can only change accounts below your highest role.";

    public const string RolesBelow = "You can only change roles below your highest role.";

    /// <summary>Whether the caller holds the Administrator permission, which is above every rank.</summary>
    public static bool IsAdministrator(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return ModbotAuth.PermissionsOf(http.User).HasFlag(ModbotPermissions.Administrator);
    }

    /// <summary>The caller's rank now. <see cref="RoleRank.Bottom"/> for somebody holding no role.</summary>
    public static async Task<int> CallerRankAsync(HttpContext http, UserAccountService accounts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(accounts);

        return ModbotAuth.UserIdOf(http.User) is { } id
            ? await accounts.RankOfAsync(id, ct)
            : RoleRank.Bottom;
    }

    /// <summary>
    /// Refuses a change to an account whose highest role is not below the caller's. The caller's
    /// own account passes: what may be done to it is settled by the endpoint's own rules.
    /// </summary>
    /// <param name="target">An account with its roles loaded.</param>
    public static async Task<IResult?> MayNotChangeAccountAsync(
        HttpContext http, UserAccountService accounts, ModbotUser target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (IsAdministrator(http) || ModbotAuth.UserIdOf(http.User) == target.Id)
            return null;

        var caller = await CallerRankAsync(http, accounts, ct);
        return RoleRank.IsBelow(RoleRank.Of(target), caller) ? null : Refuse(AccountsBelow);
    }

    /// <summary>The same check for an account named only by its id, such as a companion's owner.</summary>
    public static async Task<IResult?> MayNotChangeAccountAsync(
        HttpContext http, UserAccountService accounts, Guid targetId, CancellationToken ct)
    {
        if (IsAdministrator(http) || ModbotAuth.UserIdOf(http.User) == targetId)
            return null;

        var caller = await CallerRankAsync(http, accounts, ct);
        var target = await accounts.RankOfAsync(targetId, ct);
        return RoleRank.IsBelow(target, caller) ? null : Refuse(AccountsBelow);
    }

    /// <summary>
    /// Refuses when any of these roles is not below the caller's highest role. For handing roles
    /// out and for taking them away: pass every role that is being added or removed.
    /// </summary>
    public static async Task<IResult?> MayNotChangeRolesAsync(
        HttpContext http, UserAccountService accounts, IEnumerable<ModbotRole> roles, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roles);

        if (IsAdministrator(http))
            return null;

        var positions = roles.Select(RoleRank.PositionOf).ToList();
        if (positions.Count == 0)
            return null;

        var caller = await CallerRankAsync(http, accounts, ct);
        return positions.All(p => RoleRank.IsBelow(p, caller)) ? null : Refuse(RolesBelow);
    }

    /// <summary>
    /// The roles an account would gain or lose if it went from the roles it holds to the ones
    /// asked for: everything in one set and not the other.
    /// </summary>
    public static IReadOnlyList<ModbotRole> RolesChanged(
        IEnumerable<ModbotRole> held, IEnumerable<ModbotRole> wanted)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(wanted);

        var before = held.ToDictionary(r => r.Id);
        var after = wanted.ToDictionary(r => r.Id);

        return
        [
            .. before.Values.Where(r => !after.ContainsKey(r.Id)),
            .. after.Values.Where(r => !before.ContainsKey(r.Id)),
        ];
    }

    internal static IResult Refuse(string sentence)
        => Results.Json(new { error = sentence }, statusCode: StatusCodes.Status403Forbidden);
}
