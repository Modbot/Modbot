using Modbot.TestSupport;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.RateLimiting;

/// <summary>
/// Tests of the fake itself.
/// </summary>
/// <remarks>
/// These look like tests of test code, and they are the most important tests in this file. Every
/// other assertion about the limiter is only worth what the fake's fidelity is worth: against a
/// fake where retrying is free, a design that retries passes. So the punishing property gets
/// pinned here, explicitly, and if somebody later softens the fake to make a test go green these
/// fail first and say why.
/// </remarks>
public class PunitiveVRChatTests
{
    private const string Members = VRChatEndpointClass.GroupsMembers;
    private const string Bans = VRChatEndpointClass.GroupsBans;

    [Fact]
    public void ExceedingTheAllowanceStartsAPenalty()
    {
        var clock = new FakeClock();
        var vrchat = new PunitiveVRChat(clock) { AllowancePerWindow = 3 };

        Assert.Equal(200, vrchat.Call(Members));
        Assert.Equal(200, vrchat.Call(Members));
        Assert.Equal(200, vrchat.Call(Members));

        Assert.Equal(429, vrchat.Call(Members));
        Assert.Equal(clock.UtcNow + vrchat.PenaltyDuration, vrchat.PenaltyUntil(Members));
    }

    [Fact]
    public void ARequestDuringThePenaltyExtendsIt()
    {
        var clock = new FakeClock();
        var vrchat = new PunitiveVRChat(clock) { AllowancePerWindow = 1 };

        vrchat.Call(Members);
        Assert.Equal(429, vrchat.Call(Members));

        var original = vrchat.PenaltyUntil(Members);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(429, vrchat.Call(Members));

        Assert.Equal(original + vrchat.PenaltyExtension, vrchat.PenaltyUntil(Members));
    }

    [Fact]
    public void ProbingTooEarlyPushesRecoveryPastWhenItWouldHaveArrived()
    {
        var clock = new FakeClock();
        var vrchat = new PunitiveVRChat(clock) { AllowancePerWindow = 1 };

        vrchat.Call(Members);
        vrchat.Call(Members);
        var wouldHaveEnded = vrchat.PenaltyUntil(Members)!.Value;

        // The behaviour of the old implementation: wait a few minutes, try again, repeat.
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(3));
            Assert.Equal(429, vrchat.Call(Members));
        }

        // At the moment the original penalty would have expired, it has not, because probing it
        // three times bought three more extensions. This is the whole argument for the cold stop.
        clock.Advance(wouldHaveEnded - clock.UtcNow);
        Assert.True(vrchat.PenaltyUntil(Members) > clock.UtcNow);
        Assert.Equal(429, vrchat.Call(Members));
    }

    [Fact]
    public void WaitingOutThePenaltyWithoutProbingRecovers()
    {
        var clock = new FakeClock();
        var vrchat = new PunitiveVRChat(clock) { AllowancePerWindow = 1 };

        vrchat.Call(Members);
        vrchat.Call(Members);

        clock.Advance(vrchat.PenaltyDuration + TimeSpan.FromSeconds(1));

        Assert.Equal(200, vrchat.Call(Members));
    }

    [Fact]
    public void PenaltiesAreScopedToAnEndpointClass()
    {
        var clock = new FakeClock();
        var vrchat = new PunitiveVRChat(clock) { AllowancePerWindow = 1 };

        vrchat.Call(Members);
        Assert.Equal(429, vrchat.Call(Members));

        Assert.Equal(200, vrchat.Call(Bans));
    }
}
