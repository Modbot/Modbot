namespace Modbot.Core.AutomaticRoles;

/// <summary>What to do about one role for one person.</summary>
public enum RoleAction
{
    Nothing = 0,
    Give = 1,
    Take = 2,
}

/// <summary>The answer: what to do and, in plain words, why. For a take the words go to the audit log as they are.</summary>
public readonly record struct RoleDecision(RoleAction Action, string Reason)
{
    public static RoleDecision Give(string reason) => new(RoleAction.Give, reason);

    public static RoleDecision Take(string reason) => new(RoleAction.Take, reason);

    public static RoleDecision Nothing(string why) => new(RoleAction.Nothing, why);
}

/// <summary>
/// Everything the decider needs about one person and one role (a kind and an id). The caller
/// gathers it; nothing here reads a database or a clock.
/// </summary>
/// <param name="Checks">
/// The <see cref="AutomaticRoleRule.Check"/> of every rule that gives this role and is for this
/// person (<see cref="AutomaticRoleRule.AppliesTo"/>). Empty when no such rule is left.
/// </param>
/// <param name="HoldsRole">The person holds the role right now.</param>
/// <param name="ModbotGaveIt">An open grant says Modbot gave them the role.</param>
/// <param name="OnTakeBackList">They are on the take-back list of one of the rules in <paramref name="Checks"/>.</param>
/// <param name="LastTakenAt">The last time Modbot took this role from them, if it ever did.</param>
/// <param name="Now">The moment of the decision.</param>
/// <param name="ModbotStartedAt">When Modbot started.</param>
public sealed record RoleDecisionInput(
    IReadOnlyList<RoleCheck> Checks,
    bool HoldsRole,
    bool ModbotGaveIt,
    bool OnTakeBackList,
    DateTimeOffset? LastTakenAt,
    DateTimeOffset Now,
    DateTimeOffset ModbotStartedAt);

/// <summary>
/// Decides whether Modbot gives a role, takes it or leaves it alone. Pure: the caller does the
/// giving and taking.
/// </summary>
/// <remarks>
/// <para>
/// If two rules give the same role, the person keeps it while any one of them holds.
/// </para>
/// <para>
/// A role is taken straight away, but only one Modbot gave or one on a take-back list. A role a
/// person already had that Modbot did not give is left alone. Nothing is taken in the first
/// <see cref="StartupWait"/> after Modbot starts, so companions have time to reconnect and a
/// restart does not look like everyone switching their companion off.
/// </para>
/// <para>
/// A role is given back no sooner than <see cref="GiveBackWait"/> after it was last taken, so
/// someone switching Clips on and off does not make Modbot call VRChat every few seconds.
/// </para>
/// </remarks>
public static class AutomaticRoleDecider
{
    /// <summary>How long after Modbot starts before it takes any role.</summary>
    public static readonly TimeSpan StartupWait = TimeSpan.FromMinutes(2);

    /// <summary>The least time between Modbot taking a role and giving it back.</summary>
    public static readonly TimeSpan GiveBackWait = TimeSpan.FromMinutes(1);

    public const string AlreadyHolds = "Already holds the role";
    public const string WaitingToGiveBack = "Waiting a minute after the role was taken";
    public const string NotHeld = "Does not hold the role";
    public const string GivenByHand = "Role was not given by Modbot";
    public const string JustStarted = "Modbot has only just started";

    public static RoleDecision Decide(RoleDecisionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Checks.Any(c => c.Holds))
        {
            if (input.HoldsRole)
                return RoleDecision.Nothing(AlreadyHolds);

            if (input.LastTakenAt is { } taken && input.Now - taken < GiveBackWait)
                return RoleDecision.Nothing(WaitingToGiveBack);

            return RoleDecision.Give("Every condition is met");
        }

        // No rule holds: either the person is no longer one a rule is for, or the first rule
        // that fails gives the reason.
        var reason = input.Checks.Count == 0
            ? AutomaticRoleReasons.NoLongerOnTheList
            : input.Checks[0].Reason ?? AutomaticRoleReasons.NumberMissing;

        if (!input.HoldsRole)
            return RoleDecision.Nothing(NotHeld);

        // With no rule left, only a role Modbot gave is Modbot's to take: the take-back list
        // belongs to the rules.
        var mayTake = input.ModbotGaveIt || (input.Checks.Count > 0 && input.OnTakeBackList);
        if (!mayTake)
            return RoleDecision.Nothing(GivenByHand);

        if (input.Now - input.ModbotStartedAt < StartupWait)
            return RoleDecision.Nothing(JustStarted);

        return RoleDecision.Take(reason);
    }
}
