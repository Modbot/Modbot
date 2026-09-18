using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.RateLimiting;

/// <summary>
/// Join requests design §3: reading the queue and answering one of it are two unmeasured
/// endpoints, budgeted apart from each other and apart from kick, ban and unban.
/// </summary>
/// <remarks>
/// Both numbers are guesses set deliberately low, not findings — nobody has asked VRChat what
/// either endpoint allows (spec 4.3.4). They are pinned here so that raising one is a decision
/// somebody makes on purpose.
/// </remarks>
public class JoinRequestBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VRChatEndpoint Read = new(VRChatEndpointClass.GroupsRequests, "grp_test");
    private static readonly VRChatEndpoint Answer = new(VRChatEndpointClass.GroupsRequestsAnswer, "grp_test");
    private static readonly VRChatEndpoint Moderate = new(VRChatEndpointClass.GroupsModerate, "grp_test");
    private static readonly VRChatEndpoint Members = new(VRChatEndpointClass.GroupsMembers, "grp_test");

    [Fact]
    public void ReadingTheQueueIsOnePerFiveSeconds_OnItsOwnLane_UnderTheInteractiveBackstop()
    {
        var read = VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsRequests];

        Assert.Equal(0.2, read.HardMaxPerSecond, 9);
        Assert.Equal(
            0.2,
            Math.Min(read.HardMaxPerSecond, read.DefaultCeilingPerSecond * RateLimitOptions.DefaultFraction),
            9);

        // No faster than the most conservative group-shaped class it could resemble, which is the
        // rule spec 4.3.4.1 set for an unmeasured endpoint returning group data.
        Assert.True(read.HardMaxPerSecond <= VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsRead].HardMaxPerSecond);

        Assert.Equal(VRChatRateLimits.GroupsRequestsLane, read.Lane);
        Assert.NotEqual(VRChatRateLimits.GroupLane, read.Lane);

        // A moderator opened the page, so it draws from the room spec 4.2 reserves for that
        // (spec 4.3.5) rather than from the bucket the sweeps keep empty.
        Assert.Equal(VRChatEndpointClass.Interactive, read.Backstop);
        Assert.False(read.CountsAgainstGlobal);
        Assert.True(read.ResourceScoped);
        Assert.Equal(1, read.BurstTokens);

        // Nothing polls it, so it is not part of what the settings screen sums as scheduled sync.
        Assert.DoesNotContain(VRChatEndpointClass.GroupsRequests, VRChatRateLimits.Scheduled);
    }

    [Fact]
    public void AnsweringOneIsOnePerTwoSeconds_OnItsOwnLane_UnderTheInteractiveBackstop()
    {
        var answer = VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsRequestsAnswer];

        Assert.Equal(0.5, answer.HardMaxPerSecond, 9);
        Assert.Equal(
            0.5,
            Math.Min(answer.HardMaxPerSecond, answer.DefaultCeilingPerSecond * RateLimitOptions.DefaultFraction),
            9);

        Assert.Equal(VRChatRateLimits.GroupsRequestsAnswerLane, answer.Lane);
        Assert.Equal(VRChatEndpointClass.Interactive, answer.Backstop);
        Assert.True(answer.ResourceScoped);
        Assert.Equal(1, answer.BurstTokens);
        Assert.DoesNotContain(VRChatEndpointClass.GroupsRequestsAnswer, VRChatRateLimits.Scheduled);
    }

    /// <summary>
    /// The reason the two are separate classes at all: reading the list and answering it must not
    /// share a queue, or the answer a moderator just pressed waits behind a refresh.
    /// </summary>
    [Fact]
    public void TheListReadAndTheAnswerAreNeverInTheSameLaneOrTheSameBucket()
    {
        var read = VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsRequests];
        var answer = VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsRequestsAnswer];

        Assert.NotEqual(read.Lane, answer.Lane);
        Assert.NotEqual(read.Name, answer.Name);
    }

    /// <summary>
    /// And the reason answering is not on <c>groups.moderate</c>: clearing a backlog of join
    /// requests is many writes in a row, and the button it must never cold stop is Ban.
    /// </summary>
    [Fact]
    public async Task A429OnAnsweringJoinRequestsDoesNotStopABan()
    {
        var harness = new LimiterHarness();

        await harness.CallAsync(Answer, status: 429, ct: Ct);

        var refused = await harness.CallAsync(Answer, ct: Ct);
        Assert.False(refused.IsAcquired);
        Assert.Equal(RateLimitDenialReason.ColdStop, refused.Denial?.Reason);

        Assert.True((await harness.CallAsync(Moderate, ct: Ct)).IsAcquired);
        Assert.True((await harness.CallAsync(Members, ct: Ct)).IsAcquired);
    }

    /// <summary>A queue Modbot cannot read is still a queue a moderator can answer from memory.</summary>
    [Fact]
    public async Task A429OnReadingTheQueueDoesNotStopAnsweringOne()
    {
        var harness = new LimiterHarness();

        await harness.CallAsync(Read, status: 429, ct: Ct);

        Assert.False((await harness.CallAsync(Read, ct: Ct)).IsAcquired);
        Assert.True((await harness.CallAsync(Answer, ct: Ct)).IsAcquired);
    }

    [Fact]
    public async Task AnsweringGoesOutAtMostOncePerTwoSeconds()
    {
        var harness = new LimiterHarness();
        var start = harness.Clock.UtcNow;

        for (var i = 0; i < 4; i++)
            await harness.CallAsync(Answer, ct: Ct);

        // The first spends the burst token; the other three each wait out two seconds.
        Assert.True(
            harness.Clock.UtcNow - start >= TimeSpan.FromSeconds(6) - TimeSpan.FromMilliseconds(100),
            $"four answers took {harness.Clock.UtcNow - start}, faster than one per two seconds");
    }
}
