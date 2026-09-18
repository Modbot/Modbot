using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Giveaways;

namespace Modbot.Analytics.Giveaways;

/// <summary>
/// How far back the facts still reach, and which rules that makes unanswerable.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the quiet correctness failure M7 §6 names, and the whole of the fix.</strong> An
/// operator who sets a retention window keeps the daily totals but loses the facts below it. A
/// rule that reaches further back than the surviving facts then has an answer that looks complete
/// and is not — "12 people have spent 50 hours here" when the real figure is however many did so
/// in the ninety days nobody pruned. Modbot refuses the rule instead, and says which one and why.
/// </para>
/// <para>
/// <strong>Nothing is pruned by default</strong> (foundation §5.5), so on a deployment nobody has
/// configured every rule is answerable and none of this fires. That is the reason to test the
/// configured case deliberately rather than to assume it is rare: the failure only exists where
/// nobody is looking for it.
/// </para>
/// <para>
/// Rules answered from daily totals are never refused. Daily totals are never aged out, whatever
/// retention is set to, and they outlive the facts they were computed from — which is exactly why
/// §5 says to prefer them.
/// </para>
/// </remarks>
/// <param name="ModerationDays">Days of moderation facts kept. 0 keeps them forever.</param>
/// <param name="PresenceDays">Days of presence facts kept. 0 keeps them forever.</param>
public sealed record GiveawayCoverage(int ModerationDays, int PresenceDays)
{
    /// <summary>Everything kept forever — the default configuration, where nothing is unanswerable.</summary>
    public static GiveawayCoverage Everything { get; } = new(0, 0);

    public static async Task<GiveawayCoverage> ReadAsync(ModbotContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        return settings is null
            ? Everything
            : new GiveawayCoverage(settings.ModerationFactRetentionDays, settings.PresenceFactRetentionDays);
    }

    /// <summary>
    /// Why this rule cannot be answered, in plain words, or null when it can.
    /// </summary>
    /// <remarks>
    /// The first unanswerable rule wins. Listing every one of them would be a wall of text about a
    /// tree the person is still building; the one that has to change is enough to change it.
    /// </remarks>
    public string? WhyUnanswerable(GiveawayRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (GiveawayRuleKinds.IsCombining(rule.Kind))
        {
            foreach (var inner in rule.Rules)
            {
                if (WhyUnanswerable(inner) is { } reason)
                    return reason;
            }

            return null;
        }

        var kept = rule.Kind switch
        {
            GiveawayRuleKinds.InstanceHours or GiveawayRuleKinds.OneInstanceHours
                or GiveawayRuleKinds.SeenWithinDays => PresenceDays,
            GiveawayRuleKinds.NoTrouble => ModerationDays,
            _ => 0,
        };

        if (kept <= 0)
            return null;

        // "Seen in the last N days" carries its window in the amount, not in WithinDays.
        var asked = rule.Kind == GiveawayRuleKinds.SeenWithinDays
            ? (int?)(rule.Amount is { } amount ? (int)Math.Ceiling(amount) : 0)
            : rule.WithinDays;

        var what = GiveawayRules.Describe(new GiveawayRule
        {
            Kind = rule.Kind,
            Amount = rule.Amount,
            WithinDays = rule.WithinDays,
            Id = rule.Id,
        });

        var history = rule.Kind == GiveawayRuleKinds.NoTrouble ? "Moderation history" : "Presence history";

        if (asked is null)
            return $"Modbot cannot answer “{what}”. {history} is kept for {kept} days, and the rule asks about all of it.";

        return asked > kept
            ? $"Modbot cannot answer “{what}”. {history} is kept for {kept} days, and the rule asks about {asked}."
            : null;
    }

    /// <summary>Why this weighting cannot be counted, or null when it can.</summary>
    public string? WhyWeightUnanswerable(string weighting)
    {
        if (PresenceDays <= 0 || !GiveawayWeights.IsApproximate(weighting))
            return null;

        return $"Modbot cannot weight by “{GiveawayWeights.Label(weighting)}”. "
            + $"Presence history is kept for {PresenceDays} days, and a weight counts all of it.";
    }
}
