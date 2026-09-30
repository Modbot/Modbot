using System.Text.Json.Nodes;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Tests.Sync;
using Modbot.VRChat.Users;

namespace Modbot.VRChat.Tests.Users;

/// <summary>
/// <c>last_seen_at</c> moves when a fact is written, so the People page and the audit log agree
/// without waiting for the profile sync's next pass -- and that pass, when it comes, changes nothing.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class FactSightingsTests(PostgresFixture fixture) : SyncTestBase(fixture)
{
    [Fact]
    public async Task AFactAboutAPersonMovesLastSeenTheMomentItIsWritten()
    {
        var stoppedAt = Now.AddHours(-1);

        await WriteAsync(FactType.InstanceLogStopped, "usr_mod", stoppedAt, FactSource.Companion);

        // No profile sync pass has run.
        var row = await UserRowAsync("usr_mod");

        Assert.NotNull(row);
        Assert.Equal(stoppedAt, row.LastSeenAt);
        Assert.Equal(stoppedAt, row.FirstSeenAt);
    }

    /// <summary>The pass stays as the catch-up: it still reads the fact, and its upsert is a no-op here.</summary>
    [Fact]
    public async Task TheProfileSyncsOwnPassLeavesAWriteTimeSightingWhereItIs()
    {
        var stoppedAt = Now.AddHours(-1);

        await WriteAsync(FactType.InstanceLogStopped, "usr_mod", stoppedAt, FactSource.Companion);

        var run = await RunUserProfileAsync();

        Assert.Equal(1, run.Discovered);

        var row = await UserRowAsync("usr_mod");

        Assert.Equal(stoppedAt, row!.LastSeenAt);
        Assert.Equal(stoppedAt, row.FirstSeenAt);
    }

    [Fact]
    public async Task AnOlderFactArrivingLaterNeverMovesLastSeenBack()
    {
        await WriteAsync(FactType.InstanceJoined, "usr_a", Now.AddHours(-1), FactSource.Companion);
        await WriteAsync(FactType.MemberJoined, "usr_a", Now.AddHours(-3), FactSource.AuditLog);

        var row = await UserRowAsync("usr_a");

        Assert.Equal(Now.AddHours(-1), row!.LastSeenAt);
        Assert.Equal(Now.AddHours(-3), row.FirstSeenAt);
    }

    [Fact]
    public async Task ABanNamesBothPeopleAndTouchesNobodyElse()
    {
        await WriteAsync(FactType.MemberJoined, "usr_bystander", Now.AddHours(-5), FactSource.AuditLog);

        await WriteAsync(FactType.MemberBanned, "usr_banned", Now.AddMinutes(-1), FactSource.AuditLog, actor: "usr_mod");

        Assert.Equal(Now.AddMinutes(-1), (await UserRowAsync("usr_banned"))!.LastSeenAt);
        Assert.Equal(Now.AddMinutes(-1), (await UserRowAsync("usr_mod"))!.LastSeenAt);
        Assert.Equal(Now.AddHours(-5), (await UserRowAsync("usr_bystander"))!.LastSeenAt);
    }

    /// <summary>The profile sync's own facts must not count as seeing the person, at write time either.</summary>
    [Theory]
    [InlineData(FactType.UserProfileChanged)]
    [InlineData(FactType.UserProfileFirstSeen)]
    [InlineData(FactType.UserAgeVerified)]
    public async Task TheProfileSyncsOwnFactsAreNotSightings(string type)
    {
        await WriteAsync(type, "usr_a", Now.AddMinutes(-1), FactSource.SyncDiff);

        Assert.Null(await UserRowAsync("usr_a"));
    }

    /// <summary>A second report of the same arrival is a duplicate, not a second sighting.</summary>
    [Fact]
    public async Task ADeduplicatedReportDoesNotMoveLastSeen()
    {
        var joinedAt = Now.AddMinutes(-10);

        await WriteAsync(FactType.InstanceJoined, "usr_a", joinedAt, FactSource.Companion);
        await WriteAsync(FactType.InstanceJoined, "usr_a", joinedAt.AddSeconds(2), FactSource.Companion);

        Assert.Equal(joinedAt, (await UserRowAsync("usr_a"))!.LastSeenAt);
    }

    /// <summary>A writer without a recorder -- a host with no profile sync -- writes the fact and nothing else.</summary>
    [Fact]
    public async Task AWriterWithNoRecorderStillWritesTheFact()
    {
        await using var context = Database.NewContext();

        await new FactWriter(context, Clock).WriteAsync(Fact(FactType.InstanceLogStopped, "usr_mod", Now, FactSource.Companion), Ct);

        Assert.Single(await FactsOfTypeAsync(FactType.InstanceLogStopped));
        Assert.Null(await UserRowAsync("usr_mod"));
    }

    /// <summary>The writer wired the way the host wires it: told about each fact it inserts.</summary>
    private async Task WriteAsync(string type, string subject, DateTimeOffset at, FactSource source, string? actor = null)
    {
        await using var context = Database.NewContext();

        await new EventPartitionMaintainer(context, Clock).EnsureForAsync(at, Ct);
        await new FactWriter(context, Clock, sightings: new FactSightings(context))
            .WriteAsync(Fact(type, subject, at, source, actor), Ct);
    }

    private static FactRecord Fact(string type, string subject, DateTimeOffset at, FactSource source, string? actor = null)
        => new()
        {
            Type = type,
            OccurredAt = at,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = subject,
            ActorPlatform = actor is null ? null : FactPlatform.VRChat,
            ActorId = actor,
            WorldId = "wrld_test",
            InstanceId = "instance-1",
            Source = source,
            Data = new JsonObject(),
        };
}
