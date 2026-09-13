using System.Reflection;
using Modbot.VRChat;
using Modbot.VRChat.RateLimiting;

namespace Modbot.VRChat.Tests.RateLimiting;

/// <summary>
/// Spec 4.3.4: adding an endpoint class is a decision, and a class with no budget is the shape
/// that decision takes when somebody forgets half of it.
/// </summary>
public class BudgetCoverageTests
{
    private static IEnumerable<string> DeclaredClasses =>
        typeof(VRChatEndpointClass)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    /// <summary>
    /// Every class named in <see cref="VRChatEndpointClass"/> has a budget.
    /// </summary>
    /// <remarks>
    /// Without this, adding a class and forgetting its budget is silent right up until the first
    /// call to that endpoint reaches a limiter with nothing to look up -- in production, against
    /// VRChat, where the penalty for guessing wrong is a punitive 429 (spec 4.3.1). The two
    /// halves live in different files and there is nothing in the type system tying them
    /// together, so the test is the tie.
    /// </remarks>
    [Fact]
    public void EveryEndpointClassHasABudget()
    {
        var missing = DeclaredClasses
            .Where(c => !VRChatRateLimits.Defaults.ContainsKey(c))
            .ToArray();

        Assert.Empty(missing);
    }

    /// <summary>The reverse: a budget for a class nobody declares is dead configuration.</summary>
    [Fact]
    public void EveryBudgetNamesADeclaredClass()
    {
        var declared = DeclaredClasses.ToHashSet(StringComparer.Ordinal);
        var orphans = VRChatRateLimits.Defaults.Keys.Where(k => !declared.Contains(k)).ToArray();

        Assert.Empty(orphans);
    }

    /// <summary>
    /// Spec 4.3.4's whole point: an unmeasured endpoint must not be able to cold-stop a measured
    /// one.
    /// </summary>
    /// <remarks>
    /// <c>users.groups</c> is the case this was written for -- two calls in one interactive
    /// onboarding step, against a limit nobody has measured. Sharing a class with
    /// <c>users.read</c> would mean a 429 there stopped every profile fetch in the deployment,
    /// which is how one unknown endpoint takes down a subsystem that was working fine.
    /// </remarks>
    [Fact]
    public void TheUnmeasuredGroupLookupIsIsolatedFromEverythingElse()
    {
        var it = VRChatRateLimits.Defaults[VRChatEndpointClass.UsersGroups];
        var profiles = VRChatRateLimits.Defaults[VRChatEndpointClass.UsersRead];

        Assert.NotEqual(profiles.Lane, it.Lane);

        // Conservative, not inferred: no faster than the slowest plausible neighbour.
        Assert.True(
            it.HardMaxPerSecond <= VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsRead].HardMaxPerSecond,
            "An endpoint with no measured limit is budgeted no faster than the most conservative "
            + "class it could plausibly resemble.");

        // It still passes the global backstop. users.read's exemption rests on evidence
        // (spec 4.2.5); this class has none, so it gets no exemption.
        Assert.True(it.CountsAgainstGlobal);

        // Exactly the size of the sequence it exists to admit -- the pair of calls group
        // selection makes, and no more.
        Assert.Equal(2, it.BurstTokens);
    }
}
