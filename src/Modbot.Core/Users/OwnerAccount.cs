using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Users;

/// <summary>
/// Which account is "the person who runs this Modbot", and what their email address is.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The rule:</strong> the <em>oldest enabled account holding the Administrator permission
/// that has an email address</em>. Oldest by <see cref="ModbotUser.CreatedAt"/>, ties broken by id
/// so the answer never depends on row order.
/// </para>
/// <para>
/// In practice that is the account the setup wizard created, because the wizard requires an email
/// for it and nothing older can exist. The rule is written in terms of age and permission rather
/// than "the account onboarding made" because Modbot stores no "this one was first" marker, and a
/// marker would be one more thing to keep true through disabling, role changes and imports. Age
/// and permission are already true.
/// </para>
/// <para>
/// Disabled accounts are skipped: an owner who has been disabled is not somebody to write to. So is
/// an administrator with no address, which is why the answer can be null on a deployment set up
/// before an email was required (design §4.5).
/// </para>
/// <para>
/// Two readers: the User-Agent the gate sends to VRChat, which needs a person to write to before
/// blocking (foundation §4.1), and <c>GET /api/server</c>, which my.modbot.co reads so somebody
/// adding a server can see whose it is. They must agree, so they read the same rule.
/// </para>
/// </remarks>
public static class OwnerAccount
{
    /// <summary>The owner's email address, or null when no enabled administrator has one.</summary>
    public static async Task<string?> EmailAsync(ModbotContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        return Pick(await CandidatesAsync(db).ToListAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The same answer, read synchronously. For <c>IOperatorContact.Email</c>, which is a property
    /// read from inside the VRChat client factory's synchronous build.
    /// </summary>
    public static string? Email(ModbotContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        return Pick(CandidatesAsync(db).ToList());
    }

    /// <summary>
    /// Every enabled account with an address, oldest first. Permissions come back as the roles'
    /// values rather than a union, because the union is computed in memory (spec 7.3 checks
    /// Administrator as a flag and never expands it).
    /// </summary>
    private static IQueryable<Candidate> CandidatesAsync(ModbotContext db)
        => db.Users
            .AsNoTracking()
            .Where(u => !u.IsDisabled && u.Email != null && u.Email != "")
            .OrderBy(u => u.CreatedAt)
            .ThenBy(u => u.Id)
            .Select(u => new Candidate(u.Email!, u.Roles.Select(r => r.Role.Permissions).ToList()));

    private static string? Pick(List<Candidate> candidates)
        => candidates
            .FirstOrDefault(c => ModbotRole.Union(c.Permissions).HasFlag(ModbotPermissions.Administrator))
            ?.Email;

    private sealed record Candidate(string Email, List<ModbotPermissions> Permissions);
}
