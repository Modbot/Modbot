using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.AutomaticRoles;

/// <summary>
/// Remembers which automatic roles Modbot gave and when it last took one back. The times come from
/// the caller, so this reads no clock. Every method saves its own change.
/// </summary>
public sealed class AutomaticRoleGrantStore
{
    private readonly ModbotContext _db;

    public AutomaticRoleGrantStore(ModbotContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>The grant that is still open for this person and role: Modbot gave it and has not taken it back.</summary>
    public Task<AutomaticRoleGrant?> FindOpenAsync(Guid userId, AutomaticRoleKind kind, string roleId, CancellationToken ct = default)
        => _db.AutomaticRoleGrants.SingleOrDefaultAsync(
            g => g.ModbotUserId == userId && g.Kind == kind && g.RoleId == roleId && g.TakenAt == null, ct);

    /// <summary>
    /// Notes that Modbot gave the role. If a grant is already open for this person and role (a second
    /// rule gave the same role, or Modbot gave it again after someone took it off by hand), that one
    /// is kept, its given time moved to now, and returned. The giver's wait for the stored roles to
    /// catch up counts from the given time, so a give again must restart it.
    /// </summary>
    public async Task<AutomaticRoleGrant> OpenAsync(
        Guid ruleId, Guid userId, AutomaticRoleKind kind, string roleId, DateTimeOffset givenAt, CancellationToken ct = default)
    {
        var open = await FindOpenAsync(userId, kind, roleId, ct);
        if (open is not null)
        {
            open.GivenAt = givenAt;
            await _db.SaveChangesAsync(ct);
            return open;
        }

        var grant = new AutomaticRoleGrant
        {
            RuleId = ruleId,
            ModbotUserId = userId,
            Kind = kind,
            RoleId = roleId,
            GivenAt = givenAt,
        };
        _db.AutomaticRoleGrants.Add(grant);
        await _db.SaveChangesAsync(ct);
        return grant;
    }

    /// <summary>
    /// Notes that Modbot took the role. Closes the open grant; when there is none (the role was given
    /// by hand and the person is on a take-back list) a row is added whose given-at is the take time,
    /// so the give-back wait still knows when the role was taken.
    /// </summary>
    public async Task RecordTakeAsync(
        Guid ruleId, Guid userId, AutomaticRoleKind kind, string roleId, DateTimeOffset takenAt, string reason, CancellationToken ct = default)
    {
        var grant = await FindOpenAsync(userId, kind, roleId, ct);
        if (grant is null)
        {
            grant = new AutomaticRoleGrant
            {
                RuleId = ruleId,
                ModbotUserId = userId,
                Kind = kind,
                RoleId = roleId,
                GivenAt = takenAt,
            };
            _db.AutomaticRoleGrants.Add(grant);
        }

        grant.TakenAt = takenAt;
        grant.TakeReason = reason;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>The last time Modbot took this role from this person, or null if it never did.</summary>
    public Task<DateTimeOffset?> LastTakenAsync(Guid userId, AutomaticRoleKind kind, string roleId, CancellationToken ct = default)
        => _db.AutomaticRoleGrants
            .Where(g => g.ModbotUserId == userId && g.Kind == kind && g.RoleId == roleId)
            .MaxAsync(g => g.TakenAt, ct);
}
