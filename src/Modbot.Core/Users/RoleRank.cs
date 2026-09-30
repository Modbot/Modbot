using Modbot.Core.Data.Entities;

namespace Modbot.Core.Users;

/// <summary>
/// Who outranks whom (accounts and access design §3.5).
/// </summary>
/// <remarks>
/// A person's rank is the position of their highest role. A smaller position is a higher role, so
/// "below" means a bigger number. A person holding no role sits at the bottom, below everybody who
/// holds one.
/// </remarks>
public static class RoleRank
{
    /// <summary>The rank of somebody who holds no role: below every role.</summary>
    public const int Bottom = int.MaxValue;

    /// <summary>Above every position a role can be given: what Administrator is worth.</summary>
    public const int Top = int.MinValue;

    /// <summary>
    /// Where a role counts as sitting. Administrator is always first, whatever number is stored, so
    /// the rule cannot be bent by a row that says otherwise.
    /// </summary>
    public static int PositionOf(ModbotRole role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return role.Id == BuiltInRoles.AdministratorId ? Top : role.Position;
    }

    /// <summary>The rank of this set of roles: the position of the highest. <see cref="Bottom"/> when empty.</summary>
    public static int Of(IEnumerable<ModbotRole> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        var rank = Bottom;
        foreach (var role in roles)
            rank = Math.Min(rank, PositionOf(role));

        return rank;
    }

    /// <summary>The rank of an account whose roles are loaded.</summary>
    public static int Of(ModbotUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Of(user.Roles.Select(r => r.Role));
    }

    /// <summary>Whether something at <paramref name="position"/> is strictly below somebody ranked <paramref name="caller"/>.</summary>
    public static bool IsBelow(int position, int caller) => position > caller;
}
