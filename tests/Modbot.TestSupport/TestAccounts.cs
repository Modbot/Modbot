using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;

namespace Modbot.TestSupport;

/// <summary>
/// Creates staff accounts for tests the way the tests want to describe them: "an account with
/// these permissions", already linked to a VRChat account so it can reach every endpoint.
/// </summary>
/// <remarks>
/// Permissions are roles now (accounts and access design §3), so a flat set of flags becomes a
/// role named for those flags, created once per distinct set and shared across the assembly.
/// The VRChat link is required everywhere (design §4.3), so the default is linked; a test about
/// the link itself asks for an unlinked account.
/// </remarks>
public static class TestAccounts
{
    public const string Password = "hunter2";

    public static async Task<ModbotUser> CreateAsync(
        ModbotContext db,
        string username,
        string password,
        ModbotPermissions permissions,
        bool linked = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var service = new UserAccountService(db, new PasswordHasher<ModbotUser>(), new FakeClock());
        var roles = permissions == ModbotPermissions.None
            ? Array.Empty<Guid>()
            : [await RoleForAsync(db, permissions, ct)];

        var user = await service.CreateAsync(username, password, roles, ct);

        if (linked)
        {
            user.VRChatUserId = $"usr_test_{user.Id:N}";
            user.VRChatDisplayName = username;
            user.VRChatLinkedAt = new FakeClock().UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return user;
    }

    /// <summary>The role holding exactly these flags, created on first use.</summary>
    public static async Task<Guid> RoleForAsync(ModbotContext db, ModbotPermissions permissions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var name = $"test:{(long)permissions}";
        var normalized = ModbotRole.Normalize(name);

        var existing = await db.Roles.Where(r => r.NameNormalized == normalized).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
        if (existing is { } id)
            return id;

        var role = new ModbotRole
        {
            Name = name,
            NameNormalized = normalized,
            Description = "Created by a test.",
            Permissions = permissions,
            CreatedAt = new FakeClock().UtcNow,
        };

        db.Roles.Add(role);

        try
        {
            await db.SaveChangesAsync(ct);
            return role.Id;
        }
        catch (DbUpdateException)
        {
            // Another test in the same assembly won the race. Theirs is as good as ours.
            db.Entry(role).State = EntityState.Detached;
            return await db.Roles.Where(r => r.NameNormalized == normalized).Select(r => r.Id).FirstAsync(ct);
        }
    }

    /// <summary>Links an existing account directly, skipping the bio check the link tests cover.</summary>
    public static async Task LinkAsync(ModbotContext db, Guid userId, string vrchatUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        user.VRChatUserId = vrchatUserId;
        user.VRChatDisplayName = user.Username;
        user.VRChatLinkedAt = new FakeClock().UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
