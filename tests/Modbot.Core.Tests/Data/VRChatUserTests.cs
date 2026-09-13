using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Data;

/// <summary>
/// The <c>vrchat_user</c> table as the migration actually creates it, against real PostgreSQL.
/// </summary>
/// <remarks>
/// What is worth proving here is the schema, not the sync: that the jsonb columns round-trip,
/// that an opaque id of any shape is accepted, and that the sticky flag's columns are named the
/// way the design says. The sync's own behaviour is tested in the VRChat suite.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class VRChatUserTests
{
    private readonly PostgresFixture _db;

    public VRChatUserTests(PostgresFixture db) => _db = db;

    private static readonly DateTimeOffset At = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ARowRoundTripsWithItsJsonColumns()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = $"usr_{Guid.NewGuid():N}";

        await using (var write = _db.NewContext())
        {
            write.VRChatUsers.Add(new VRChatUser
            {
                UserId = id,
                DisplayName = "Trinity",
                Bio = "hello",
                Pronouns = "she/her",
                DateJoined = new DateOnly(2019, 4, 2),
                Tags = """["system_trust_veteran","language_eng"]""",
                AgeVerificationStatus = "18+",
                AgeVerified = true,
                Is18PlusVerified = true,
                Is18PlusVerifiedAt = At,
                Is18PlusVerifiedSource = AgeVerificationSource.VRChat,
                FirstSeenAt = At,
                LastSeenAt = At,
                LastRefreshedAt = At,
                RawProfile = """{"id":"x","displayName":"Trinity","tags":["a"]}""",
            });

            await write.SaveChangesAsync(ct);
        }

        await using var read = _db.NewContext();
        var row = await read.VRChatUsers.AsNoTracking().SingleAsync(u => u.UserId == id, ct);

        Assert.Equal("Trinity", row.DisplayName);
        Assert.Equal(new DateOnly(2019, 4, 2), row.DateJoined);
        Assert.True(row.Is18PlusVerified);
        Assert.Equal(AgeVerificationSource.VRChat, row.Is18PlusVerifiedSource);

        // jsonb normalises whitespace, so the shape is asserted rather than the bytes.
        Assert.Contains("system_trust_veteran", row.Tags);
        Assert.Contains("\"displayName\"", row.RawProfile);
    }

    /// <summary>
    /// Spec 3.1.1: legacy ids follow no structure. The column must take whatever VRChat sends.
    /// </summary>
    [Fact]
    public async Task ALegacyIdOfAnyShapeIsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = $"Some Legacy Name {Guid.NewGuid():N} with spaces/and.punctuation";

        await using (var write = _db.NewContext())
        {
            write.VRChatUsers.Add(new VRChatUser { UserId = id, FirstSeenAt = At, LastSeenAt = At });
            await write.SaveChangesAsync(ct);
        }

        await using var read = _db.NewContext();
        Assert.NotNull(await read.VRChatUsers.FindAsync([id], ct));
    }

    /// <summary>The columns the design names exist under those names.</summary>
    [Fact]
    public async Task TheStickyFlagColumnsAreNamedPlainly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = _db.NewContext();

        var columns = await context.Database
            .SqlQueryRaw<string>(
                "SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'vrchat_user'")
            .ToListAsync(ct);

        Assert.Contains("is_18_plus_verified", columns);
        Assert.Contains("is_18_plus_verified_at", columns);
        Assert.Contains("is_18_plus_verified_source", columns);
        Assert.Contains("last_refreshed_at", columns);
        Assert.Contains("raw_profile", columns);
    }

    [Fact]
    public async Task TheSameIdCannotBeInsertedTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = $"usr_{Guid.NewGuid():N}";

        await using (var first = _db.NewContext())
        {
            first.VRChatUsers.Add(new VRChatUser { UserId = id, FirstSeenAt = At, LastSeenAt = At });
            await first.SaveChangesAsync(ct);
        }

        await using var second = _db.NewContext();
        second.VRChatUsers.Add(new VRChatUser { UserId = id, FirstSeenAt = At, LastSeenAt = At });

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync(ct));
    }
}
