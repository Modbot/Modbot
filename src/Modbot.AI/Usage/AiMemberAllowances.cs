using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.AI.Usage;

/// <summary>One team member's monthly AI allowance: what they may use, in tokens and in US dollars.</summary>
/// <param name="Tokens">Input plus output tokens in a UTC month. Null means no token allowance.</param>
/// <param name="Money">US dollars in a UTC month. Null means no money allowance.</param>
/// <param name="Own">Set for this person on their own, not the default every member gets.</param>
public sealed record AiAllowance(long? Tokens, decimal? Money, bool Own = false)
{
    public static AiAllowance None { get; } = new(null, null);

    /// <summary>Some amount is set, so there is something to reach.</summary>
    public bool Any => Tokens is not null || Money is not null;
}

/// <summary>
/// Who has an allowance, what it is, and the plain sentence for a member who has used it (team
/// members' monthly AI allowances).
/// </summary>
/// <remarks>
/// <para>
/// The allowance is a limit on a person, like the person and role spend limits, so the same people
/// are past it: an Administrator, and anybody whose role says "Use AI in excess of usage limits".
/// What counts against it is that account's use of every feature this month, read from the same
/// <c>ai_usage</c> rows as the spend report and the spend limits (<see cref="AiSpending"/>). Use with
/// no account behind it counts against nobody's allowance and still counts against the limits for
/// everyone and for its feature.
/// </para>
/// <para>
/// Tokens are the measure that always works: every provider reports them, a model with no price has
/// them, and a local model is nothing but them. Money is counted only where a price is known, so a
/// money allowance never stops the use of a model with no price.
/// </para>
/// </remarks>
public static class AiMemberAllowances
{
    /// <summary>The most tokens an allowance may be. Dollars are held to <c>AiPriceRules.MaxAmount</c>, as spend limits are.</summary>
    public const long MaxTokens = 1_000_000_000_000;

    /// <summary>Whether these permissions are past every limit that is set on a person.</summary>
    public static bool PastLimits(ModbotPermissions held)
        => held.HasFlag(ModbotPermissions.Administrator) || held.HasFlag(ModbotPermissions.UseAiPastLimits);

    /// <summary>What every member gets unless they have an allowance of their own.</summary>
    public static async Task<AiAllowance> DefaultAsync(ModbotContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var row = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.AiMemberMonthlyTokens, s.AiMemberMonthlyMoney })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return row is null ? AiAllowance.None : new AiAllowance(row.AiMemberMonthlyTokens, row.AiMemberMonthlyMoney);
    }

    /// <summary>This member's allowance: their own when they have one, otherwise the default.</summary>
    public static async Task<AiAllowance> ForAsync(ModbotContext db, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var own = await db.AiMemberAllowances.AsNoTracking()
            .Where(a => a.UserId == userId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return own is not null
            ? new AiAllowance(own.MonthlyTokens, own.MonthlyMoney, Own: true)
            : await DefaultAsync(db, ct).ConfigureAwait(false);
    }

    /// <summary>The permissions this account holds through its roles. None for an account that has none.</summary>
    public static async Task<ModbotPermissions> HeldByAsync(ModbotContext db, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var sets = await db.UserRoles.AsNoTracking()
            .Where(r => r.UserId == userId)
            .Select(r => r.Role.Permissions)
            .ToListAsync(ct).ConfigureAwait(false);

        return ModbotRole.Union(sets);
    }

    /// <summary>The first of the month after the one starting at <paramref name="monthStart"/>, the day the allowance starts again.</summary>
    public static string ResetDay(DateTimeOffset monthStart)
        => monthStart.AddMonths(1).ToString("d MMMM", CultureInfo.InvariantCulture);

    public static string Message(bool tokens, decimal amount, DateTimeOffset monthStart)
    {
        var what = tokens
            ? $"{amount.ToString("N0", CultureInfo.InvariantCulture)} tokens"
            : $"${amount.ToString("0.00##", CultureInfo.InvariantCulture)}";

        return $"You have used your monthly AI allowance ({what}). It starts again on {ResetDay(monthStart)}.";
    }
}
