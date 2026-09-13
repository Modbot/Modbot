using System.Net;
using Modbot.TestSupport;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Sync;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// Spec 4.3.1: a 429 halts the most specific bucket that matched, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// This is the property the whole per-endpoint model exists for, and it is the one that is easy
/// to lose by accident: two producers sharing a loop, a shared "is VRChat healthy" flag, or a
/// catch that stops the host would all pass their own unit tests and break this.
/// </para>
/// <para>
/// Spec 4.2.3 names the direction that matters. The audit log is cheap and authoritative, so a
/// limit hit on anything else must not stop it -- a group that hits its group-info limit must
/// still be recording its bans.
/// </para>
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class ColdStopIsolationTests(PostgresFixture fixture) : SyncTestBase(fixture)
{
    [Fact]
    public async Task ALimitOnGroupInfoDoesNotStopTheAuditLog()
    {
        VRChat.Groups.Group = GroupInfoSnapshotTests.Group();
        VRChat.Groups.Add(Entry("gaud_1", Now.AddMinutes(-1)));

        VRChat.Groups.GroupStatus = HttpStatusCode.TooManyRequests;
        var info = await RunGroupInfoAsync();

        Assert.Equal(SyncOutcome.RateLimited, info.Outcome);
        Assert.True((await Health(VRChatEndpointClass.GroupsRead)).IsColdStopped);

        // The ancestors take the multiplicative decrease and keep running -- spec 4.3.1 stops the
        // most specific bucket only, precisely so that one endpoint's limit is not an outage.
        Assert.False((await Health(VRChatEndpointClass.Global)).IsColdStopped);

        // The producer that matters most is untouched, and it writes.
        var audit = await RunAuditLogAsync();

        Assert.Equal(SyncOutcome.Produced, audit.Outcome);
        Assert.False((await Health(VRChatEndpointClass.GroupsAuditLog)).IsColdStopped);
        Assert.Single(await FactsAsync());
    }

    [Fact]
    public async Task ALimitOnTheAuditLogDoesNotStopGroupInfo()
    {
        VRChat.Groups.Group = GroupInfoSnapshotTests.Group();
        VRChat.Groups.AuditLogStatus = HttpStatusCode.TooManyRequests;

        var audit = await RunAuditLogAsync();

        Assert.Equal(SyncOutcome.RateLimited, audit.Outcome);
        Assert.True((await Health(VRChatEndpointClass.GroupsAuditLog)).IsColdStopped);

        var info = await RunGroupInfoAsync();

        Assert.Equal(SyncOutcome.Produced, info.Outcome);
        Assert.True(info.Baseline);
    }

    /// <summary>
    /// Spec 4.3.1: never retry a 429, ever. The second pass must not send another request -- the
    /// gate refuses before the wire, and the producer must not work around it.
    /// </summary>
    [Fact]
    public async Task AColdStoppedProducerIssuesNothingAtAllUntilTheStopLifts()
    {
        VRChat.Groups.Add(Entry("gaud_1", Now.AddMinutes(-1)));
        VRChat.Groups.AuditLogStatus = HttpStatusCode.TooManyRequests;

        await RunAuditLogAsync();
        var requestsAfterTheLimit = VRChat.Groups.AuditLogRequests;

        // Whatever the fake would answer now is irrelevant: nothing should reach it.
        VRChat.Groups.AuditLogStatus = HttpStatusCode.OK;

        var second = await RunAuditLogAsync();
        var third = await RunAuditLogAsync();

        Assert.Equal(SyncOutcome.RateLimited, second.Outcome);
        Assert.Equal(SyncOutcome.RateLimited, third.Outcome);
        Assert.Equal(requestsAfterTheLimit, VRChat.Groups.AuditLogRequests);
        Assert.Empty(await FactsAsync());
    }

    /// <summary>
    /// And it resumes on its own once the cold stop expires, rather than needing a restart. The
    /// cursor never moved, so nothing was lost while it waited.
    /// </summary>
    [Fact]
    public async Task ItResumesOnceTheColdStopExpiresAndLosesNothing()
    {
        VRChat.Groups.Add(Entry("gaud_1", Now.AddMinutes(-1)));
        VRChat.Groups.AuditLogStatus = HttpStatusCode.TooManyRequests;

        await RunAuditLogAsync();

        VRChat.Groups.AuditLogStatus = HttpStatusCode.OK;
        Clock.Advance(Limiter.Options.ColdStopBase + TimeSpan.FromMinutes(1));

        var resumed = await RunAuditLogAsync();

        Assert.Equal(SyncOutcome.Produced, resumed.Outcome);
        Assert.Single(await FactsAsync());
    }

    /// <summary>
    /// The bucket a cold stop actually lands on.
    /// </summary>
    /// <remarks>
    /// The most specific one, which for the group endpoints is resource-scoped and therefore named
    /// <c>&lt;class&gt;:&lt;group id&gt;</c>. Asking the class bucket instead would quietly assert
    /// nothing: spec 4.3.1 stops the specific bucket and only applies the multiplicative decrease
    /// to its ancestors, so the class bucket is not stopped and never was meant to be.
    /// </remarks>
    private async Task<RateLimitBucketHealth> Health(string endpointClass)
    {
        var buckets = await Limiter.Limiter.DescribeAsync(Ct);
        var scoped = $"{endpointClass}:{GroupId}";

        return buckets.SingleOrDefault(b => b.Name == scoped)
            ?? buckets.Single(b => b.Name == endpointClass);
    }
}
