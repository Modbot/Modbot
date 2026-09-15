using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.AI.Usage;

/// <summary>What a limit is counted in.</summary>
public static class AiLimitUnits
{
    /// <summary>US dollars.</summary>
    public const string Money = "money";

    /// <summary>Input plus output tokens. Only the token limits kept from before prices.</summary>
    public const string Tokens = "tokens";
}

/// <summary>A spend limit something has reached.</summary>
/// <param name="Key">Which limit, for whom and which period, e.g. <c>feature:moderation:month</c>. Unique per period.</param>
/// <param name="AppliesTo"><c>everyone</c>, <c>feature</c>, <c>role</c>, <c>user</c> or <c>tokens</c>.</param>
/// <param name="Feature">The feature the stopped call was for.</param>
/// <param name="Name">The role's name, for a role limit.</param>
/// <param name="Period"><c>day</c> or <c>month</c>.</param>
/// <param name="PeriodStart">The start of the UTC day or month.</param>
/// <param name="Unit"><see cref="AiLimitUnits.Money"/> or <see cref="AiLimitUnits.Tokens"/>.</param>
/// <param name="Message">The short sentence to show, naming the limit.</param>
public sealed record AiLimitReached(
    string Key,
    string AppliesTo,
    string Feature,
    Guid? UserId,
    string? Name,
    string Period,
    DateTimeOffset PeriodStart,
    string Unit,
    decimal Limit,
    decimal Spent,
    string Message)
{
    public const string TokenLimit = "tokens";
}

/// <summary>
/// Whether an AI call may go ahead, by every spend limit that applies to it (AI chat design §10).
/// </summary>
/// <remarks>
/// <para>
/// A call is stopped by the tightest limit that applies: the one for everyone (every feature's
/// spend together), the feature's own, a token limit kept from before prices, and -- for Chat only --
/// one set on the person or on any role they hold, compared with their own Chat spend.
/// </para>
/// <para>
/// <see cref="ModbotPermissions.UseAiPastLimits"/> takes the person and role limits away and leaves
/// the others: the limit for everyone is the operator's ceiling on the bill, and a feature limit is
/// the operator's share of it for that feature.
/// </para>
/// <para>
/// Spend is priced when it is read (<see cref="AiSpending"/>). Days and months are UTC, from
/// <see cref="IModbotClock"/>. The check runs before a call, so a call that starts just under a
/// limit can finish a little over it; the next one is refused. Spend of a model with no price is
/// unknown and cannot reach a money limit.
/// </para>
/// </remarks>
public sealed class AiSpendLimits
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly AiLimitNotices _notices;

    public AiSpendLimits(ModbotContext db, IModbotClock clock, AiLimitNotices notices)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(notices);

        _db = db;
        _clock = clock;
        _notices = notices;
    }

    /// <summary>The start of today and of this month, in UTC.</summary>
    public static (DateTimeOffset Day, DateTimeOffset Month) PeriodsAt(DateTimeOffset now)
        => (AiPeriods.DayOf(now), AiPeriods.MonthOf(now));

    /// <summary>
    /// The limit that stops a call for <paramref name="feature"/>, or null when it may go ahead.
    /// Reaching a limit is recorded the first time it stops a call in its day or month.
    /// </summary>
    /// <param name="userId">The person the call is for, when their own limits apply.</param>
    /// <param name="held">That person's permissions.</param>
    public async Task<AiLimitReached?> CheckAsync(string feature, Guid? userId, ModbotPermissions held, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);

        var limits = await _db.AiSpendLimits.AsNoTracking()
            .Select(l => new
            {
                l.AppliesTo,
                l.Feature,
                l.RoleId,
                l.UserId,
                l.PerDay,
                l.PerMonth,
                RoleName = l.RoleRow == null ? null : l.RoleRow.Name,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var tokenLimit = await _db.AiFeatureLimits.AsNoTracking()
            .Where(l => l.Feature == feature && l.MonthlyTokenLimit != null)
            .Select(l => l.MonthlyTokenLimit)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (limits.Count == 0 && tokenLimit is null)
            return null;

        var personal = userId is not null
                       && AiFeatures.HasPersonLimits(feature)
                       && !held.HasFlag(ModbotPermissions.Administrator)
                       && !held.HasFlag(ModbotPermissions.UseAiPastLimits);

        var roles = personal && limits.Any(l => l.AppliesTo == AiSpendLimit.Role)
            ? await _db.UserRoles.AsNoTracking().Where(r => r.UserId == userId).Select(r => r.RoleId).ToListAsync(ct).ConfigureAwait(false)
            : [];

        var applicable = limits.Where(l => l.AppliesTo switch
        {
            AiSpendLimit.Everyone => true,
            AiSpendLimit.ForFeature => l.Feature == feature,
            AiSpendLimit.User => personal && l.UserId == userId,
            AiSpendLimit.Role => personal && l.RoleId is { } role && roles.Contains(role),
            _ => false,
        }).ToList();

        if (applicable.Count == 0 && tokenLimit is null)
            return null;

        var now = _clock.UtcNow;
        var (day, month) = PeriodsAt(now);

        var everyone = applicable.Any(l => l.AppliesTo == AiSpendLimit.Everyone)
            ? await AiSpending.TodayAndMonthAsync(_db, now, null, null, ct).ConfigureAwait(false)
            : (AiSpent.None, AiSpent.None);

        var ofFeature = applicable.Any(l => l.AppliesTo == AiSpendLimit.ForFeature) || tokenLimit is not null
            ? await AiSpending.TodayAndMonthAsync(_db, now, feature, null, ct).ConfigureAwait(false)
            : (AiSpent.None, AiSpent.None);

        var mine = applicable.Any(l => l.AppliesTo is AiSpendLimit.User or AiSpendLimit.Role)
            ? await AiSpending.TodayAndMonthAsync(_db, now, feature, userId, ct).ConfigureAwait(false)
            : (AiSpent.None, AiSpent.None);

        var label = AiFeatures.LabelOf(feature);
        var candidates = new List<AiLimitReached>();

        foreach (var l in applicable)
        {
            var (today, thisMonth) = l.AppliesTo switch
            {
                AiSpendLimit.Everyone => everyone,
                AiSpendLimit.ForFeature => ofFeature,
                _ => mine,
            };

            var who = l.AppliesTo switch
            {
                AiSpendLimit.Everyone => "everyone",
                AiSpendLimit.ForFeature => $"feature:{feature}",
                AiSpendLimit.User => $"user:{userId}",
                _ => $"role:{l.RoleId}:user:{userId}",
            };

            foreach (var (period, start, amount, spent) in new[] { ("day", day, l.PerDay, today.Cost), ("month", month, l.PerMonth, thisMonth.Cost) })
            {
                if (amount is not { } limit || spent < limit)
                    continue;

                var every = period == "day" ? "daily" : "monthly";
                var message = l.AppliesTo switch
                {
                    AiSpendLimit.User => $"Your {every} AI spend limit is reached.",
                    AiSpendLimit.Role => $"The {l.RoleName} role's {every} AI spend limit is reached.",
                    AiSpendLimit.ForFeature => $"The {every} AI spend limit for {label} is reached.",
                    _ => $"This Modbot's {every} AI spend limit is reached.",
                };

                candidates.Add(new AiLimitReached(
                    $"{who}:{period}", l.AppliesTo, feature, l.AppliesTo is AiSpendLimit.User or AiSpendLimit.Role ? userId : null,
                    l.RoleName, period, start, AiLimitUnits.Money, limit, spent, message));
            }
        }

        // The tightest money limit first; a token limit only when no money limit is reached.
        var reached = candidates.OrderBy(c => c.Limit).FirstOrDefault();

        if (reached is null && tokenLimit is { } tokens && ofFeature.Item2.Tokens >= tokens)
        {
            reached = new AiLimitReached(
                $"tokens:{feature}:month", AiLimitReached.TokenLimit, feature, null, null, "month", month, AiLimitUnits.Tokens,
                tokens, ofFeature.Item2.Tokens, $"The monthly AI token limit for {label} is reached.");
        }

        if (reached is not null)
            await _notices.RecordOnceAsync(reached, ct).ConfigureAwait(false);

        return reached;
    }
}
