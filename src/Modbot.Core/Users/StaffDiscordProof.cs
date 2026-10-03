using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Npgsql;

namespace Modbot.Core.Users;

/// <summary>How putting a proven Discord account on a staff account went.</summary>
public enum StaffDiscordProofOutcome
{
    /// <summary>Saved: the account now holds the id as proven.</summary>
    Proven,

    /// <summary>The account already held this id, proven, under this name. Nothing changed.</summary>
    AlreadyProven,

    /// <summary>Another account proved this Discord account. Nothing changed.</summary>
    Taken,
}

/// <summary>What <see cref="StaffDiscordProof.ProveAsync"/> did.</summary>
/// <param name="Replaced">The Discord id the account held before, when it held a different one.</param>
/// <param name="TypedElsewhere">Other accounts that had only typed this id in, and lost it.</param>
public sealed record StaffDiscordProofResult(
    StaffDiscordProofOutcome Outcome,
    string? Replaced,
    IReadOnlyList<ModbotUser> TypedElsewhere)
{
    public static StaffDiscordProofResult Unchanged(StaffDiscordProofOutcome outcome) => new(outcome, null, []);
}

/// <summary>
/// Puts a Discord account on a staff account as proven (accounts and access design §4.6), the same
/// way whichever proof brought it: Discord sign-in from the account page, or <c>/verify</c> with a
/// code from it (Discord account linking design §14).
/// </summary>
/// <remarks>
/// <para>
/// <strong>One proven Discord account, one Modbot account.</strong> A Discord account another
/// Modbot account has proven is refused, never moved. One that other accounts only typed in, before
/// proving existed, comes off them: whoever proves it is the person it belongs to.
/// </para>
/// <para>
/// What both-ways linked roles agreed about these accounts' Discord accounts no longer counts
/// (staff roles from Discord design §3.1), so it is forgotten here too.
/// </para>
/// <para>
/// The caller writes the facts, so each proof can say how it was made, and owns the transaction:
/// it begins one before calling, and commits it once the facts are written.
/// </para>
/// </remarks>
public static class StaffDiscordProof
{
    /// <summary>
    /// Proves <paramref name="discordUserId"/> on <paramref name="user"/>, which must be tracked by
    /// <paramref name="db"/>. Call inside a transaction. On <see cref="StaffDiscordProofOutcome.Taken"/>
    /// the caller rolls back: nothing of the attempt is kept, and nothing of it is left tracked.
    /// </summary>
    public static async Task<StaffDiscordProofResult> ProveAsync(
        ModbotContext db,
        ModbotUser user,
        string discordUserId,
        string discordUsername,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrEmpty(discordUserId);
        ArgumentNullException.ThrowIfNull(discordUsername);

        if (user.IsDiscordProven && user.DiscordUserId == discordUserId && user.DiscordUsername == discordUsername)
            return StaffDiscordProofResult.Unchanged(StaffDiscordProofOutcome.AlreadyProven);

        var taken = await db.Users.AsNoTracking()
            .AnyAsync(u => u.Id != user.Id && u.DiscordUserId == discordUserId && u.DiscordVerifiedAt != null, ct)
            .ConfigureAwait(false);
        if (taken)
            return StaffDiscordProofResult.Unchanged(StaffDiscordProofOutcome.Taken);

        var replaced = user.DiscordUserId != discordUserId ? user.DiscordUserId : null;

        // Typed in on other accounts, never proven: it is this person's, so it comes off them.
        var typedElsewhere = await db.Users
            .Where(u => u.Id != user.Id && u.DiscordUserId == discordUserId && u.DiscordVerifiedAt == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var other in typedElsewhere)
            Clear(other);

        user.DiscordUserId = discordUserId;
        user.DiscordUsername = Fit(discordUsername);
        user.DiscordVerifiedAt = now;

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another account proved it between the check above and here. The caller rolls back;
            // the typed ids taken off other accounts go back on with it.
            db.ChangeTracker.Clear();
            return StaffDiscordProofResult.Unchanged(StaffDiscordProofOutcome.Taken);
        }

        await ForgetAgreementsAsync(db, [user.Id, .. typedElsewhere.Select(o => o.Id)], ct).ConfigureAwait(false);

        return new StaffDiscordProofResult(StaffDiscordProofOutcome.Proven, replaced, typedElsewhere);
    }

    /// <summary>
    /// Forgets what both-ways linked roles agreed about these accounts' Discord accounts (staff
    /// roles from Discord design §3.1).
    /// </summary>
    public static Task ForgetAgreementsAsync(ModbotContext db, IReadOnlyList<Guid> userIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.DiscordStaffRoleStates.Where(s => userIds.Contains(s.UserId)).ExecuteDeleteAsync(ct);
    }

    /// <summary>Takes the Discord account off <paramref name="user"/>. Not saved.</summary>
    public static void Clear(ModbotUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        user.DiscordUserId = null;
        user.DiscordUsername = null;
        user.DiscordVerifiedAt = null;
    }

    /// <summary>Discord usernames are 32 characters at most; the column takes 64, in case that changes.</summary>
    private static string Fit(string username) => username.Length <= 64 ? username : username[..64];
}
