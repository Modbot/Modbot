using Modbot.Core.Data.Entities;

namespace Modbot.Analytics.Retention;

/// <summary>
/// How long a fact is worth keeping.
/// </summary>
/// <remarks>
/// Spec 5.5. The two classes exist because the volume profiles differ by three orders of
/// magnitude while the value profiles run the opposite way: a ban from three years ago is exactly
/// what a moderator needs, and an instance join from three weeks ago is not.
/// </remarks>
public enum RetentionClass
{
    /// <summary>
    /// Bans, kicks, role changes, membership changes, Modbot's own audit entries. Hundreds a day.
    /// Kept forever by default.
    /// </summary>
    Moderation,

    /// <summary>
    /// Instance joins and leaves, avatar changes, voice sessions, and Modbot's operational noise.
    /// Up to ~18k an hour at peak. Ninety days by default.
    /// </summary>
    Presence,
}

/// <summary>
/// Which retention class each fact type belongs to.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="FactType"/> values are already blocked by class, but the mapping is spelled out
/// per member rather than inferred from numeric ranges: retention decides what gets destroyed, and
/// "it was in the 200s" is not a reason anyone should have to reconstruct from a range check.
/// </para>
/// <para>
/// An unmapped type falls to <see cref="RetentionClass.Moderation"/> -- kept forever. Keeping data
/// longer than intended is a storage bill; deleting it early is unrecoverable, and a new fact type
/// added without a thought about retention should fail in the safe direction.
/// </para>
/// </remarks>
public static class FactRetention
{
    /// <summary>
    /// Prefixes whose facts are presence: high-volume, and not the moderation record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A prefix test rather than the member-by-member table this used to be. That table had to be
    /// updated by hand every time a fact type was added, and the one thing it could not do was
    /// classify a type nobody had added yet — which, now that an unrecognised upstream event is
    /// recorded rather than dropped, is a case that genuinely occurs.
    /// </para>
    /// <para>
    /// Operational noise takes the short class deliberately (spec 5.9.2): "a sync failed last
    /// March" is not history anyone needs, and letting it accumulate alongside the moderation
    /// record would bury the latter.
    /// </para>
    /// </remarks>
    private static readonly string[] PresencePrefixes =
    [
        "vrchat.instance.",
        "vrchat.avatar.",

        // Discord voice sessions are presence like instance sessions and arrive at the same kind
        // of rate. Discord membership and roles are membership history, so they are not here.
        "discord.voice.",

        "modbot.sync.",
        "modbot.ratelimit.",
        "modbot.waf.",
        "modbot.migration.",
        "modbot.retention.",
        "modbot.partition.",
    ];

    /// <summary>
    /// Which retention class a fact belongs to. Anything unrecognised is kept forever.
    /// </summary>
    /// <remarks>
    /// The default is the safe direction and it is load-bearing. A fact type added without a
    /// thought about retention, or one Modbot has never seen at all, must not be scheduled for
    /// deletion on a guess — history cannot be backfilled (§5.1), and the cost of keeping
    /// something unnecessarily is a few hundred bytes.
    /// </remarks>
    public static RetentionClass ClassOf(string type)
    {
        if (string.IsNullOrEmpty(type)) return RetentionClass.Moderation;

        foreach (var prefix in PresencePrefixes)
        {
            if (type.StartsWith(prefix, StringComparison.Ordinal))
                return RetentionClass.Presence;
        }

        return RetentionClass.Moderation;
    }

    /// <summary>Every declared fact type in one class.</summary>
    public static IReadOnlyList<string> TypesIn(RetentionClass retention)
        => FactType.All.Where(t => ClassOf(t) == retention).ToList();

    public static IReadOnlyList<RetentionClass> All { get; } = Enum.GetValues<RetentionClass>();
}
