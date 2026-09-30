using Modbot.Api.Features.Evidence;

namespace Modbot.Api.Tests.Features.Evidence;

/// <summary>
/// The limit on how often one person's look at one file is written to the audit log.
/// </summary>
public class EvidenceViewThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Sam = Guid.NewGuid();
    private static readonly Guid Alex = Guid.NewGuid();

    [Fact]
    public void ALookIsWrittenOncePerPersonPerFileEveryTenMinutes()
    {
        var throttle = new EvidenceViewThrottle();

        Assert.True(throttle.TryClaim(Sam, "a", Now));
        Assert.False(throttle.TryClaim(Sam, "a", Now.AddMinutes(9)));
        Assert.True(throttle.TryClaim(Sam, "a", Now.AddMinutes(10)));
    }

    [Fact]
    public void AnotherPersonOrAnotherFileHasItsOwnSlot()
    {
        var throttle = new EvidenceViewThrottle();

        Assert.True(throttle.TryClaim(Sam, "a", Now));
        Assert.True(throttle.TryClaim(Alex, "a", Now));
        Assert.True(throttle.TryClaim(Sam, "b", Now));
    }

    /// <summary>
    /// A look whose line could not be written gives its slot back, so the next look is written and
    /// not silenced for ten minutes by one that left no trace.
    /// </summary>
    [Fact]
    public void AReleasedSlotCanBeClaimedAgain()
    {
        var throttle = new EvidenceViewThrottle();

        Assert.True(throttle.TryClaim(Sam, "a", Now));
        throttle.Release(Sam, "a", Now);

        Assert.True(throttle.TryClaim(Sam, "a", Now.AddMinutes(1)));
    }

    /// <summary>A release for a claim that has since been replaced leaves the newer one alone.</summary>
    [Fact]
    public void ReleasingAnOldClaimDoesNotFreeANewerOne()
    {
        var throttle = new EvidenceViewThrottle();

        Assert.True(throttle.TryClaim(Sam, "a", Now));
        Assert.True(throttle.TryClaim(Sam, "a", Now.AddMinutes(11)));

        throttle.Release(Sam, "a", Now);

        Assert.False(throttle.TryClaim(Sam, "a", Now.AddMinutes(12)));
    }
}
