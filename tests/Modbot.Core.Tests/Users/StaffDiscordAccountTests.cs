using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Tests.Data;
using Modbot.Core.Users;
using Modbot.TestSupport;

namespace Modbot.Core.Tests.Users;

/// <summary>
/// From a Discord account to the staff account it is (accounts and access design §4.6), against
/// the real database: the proven account wins, two typed ones are nobody, and no two accounts can
/// prove the same Discord account.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class StaffDiscordAccountTests
{
    private static readonly DateTimeOffset BeforeTheEnd = StaffDiscord.TypedIdsEnd.AddDays(-1);
    private static readonly DateTimeOffset AfterTheEnd = StaffDiscord.TypedIdsEnd.AddDays(1);

    private readonly PostgresFixture _db;

    public StaffDiscordAccountTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewDiscordId() => Random.Shared.NextInt64(100_000_000_000_000_000, 999_999_999_999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<ModbotUser> AddAsync(ModbotContext db, string discordUserId, bool proven, bool deleted = false)
    {
        var tag = Guid.NewGuid().ToString("N");
        var user = new ModbotUser
        {
            Id = Guid.NewGuid(),
            Username = $"staff_{tag}",
            UsernameNormalized = $"STAFF_{tag}".ToUpperInvariant(),
            Email = $"staff_{tag}@test.example",
            PasswordHash = "x",
            DiscordUserId = discordUserId,
            DiscordUsername = proven ? "someone" : null,
            DiscordVerifiedAt = proven ? BeforeTheEnd.AddDays(-5) : null,
            DeletedAt = deleted ? BeforeTheEnd.AddDays(-1) : null,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);
        return user;
    }

    [Fact]
    public async Task AProvenAccount_IsFound_AfterTypedIdsStopCounting()
    {
        await using var db = _db.NewContext();
        var id = NewDiscordId();
        var proven = await AddAsync(db, id, proven: true);

        var found = await StaffDiscord.AccountForAsync(db, id, AfterTheEnd, Ct);

        Assert.Equal(proven.Id, found?.Id);
    }

    [Fact]
    public async Task ATypedAccount_IsFoundUntilTheEnd_AndIgnoredAfter()
    {
        await using var db = _db.NewContext();
        var id = NewDiscordId();
        var typed = await AddAsync(db, id, proven: false);

        Assert.Equal(typed.Id, (await StaffDiscord.AccountForAsync(db, id, BeforeTheEnd, Ct))?.Id);
        Assert.Null(await StaffDiscord.AccountForAsync(db, id, AfterTheEnd, Ct));
    }

    [Fact]
    public async Task TwoAccountsThatTypedTheSameId_AreNobody()
    {
        await using var db = _db.NewContext();
        var id = NewDiscordId();
        await AddAsync(db, id, proven: false);
        await AddAsync(db, id, proven: false);

        Assert.Null(await StaffDiscord.AccountForAsync(db, id, BeforeTheEnd, Ct));
    }

    [Fact]
    public async Task TheProvenAccount_WinsOverOneThatTypedTheSameId()
    {
        await using var db = _db.NewContext();
        var id = NewDiscordId();
        await AddAsync(db, id, proven: false);
        var proven = await AddAsync(db, id, proven: true);
        await AddAsync(db, id, proven: false);

        Assert.Equal(proven.Id, (await StaffDiscord.AccountForAsync(db, id, BeforeTheEnd, Ct))?.Id);
    }

    [Fact]
    public async Task ADeletedAccount_IsNobody()
    {
        await using var db = _db.NewContext();
        var id = NewDiscordId();
        await AddAsync(db, id, proven: true, deleted: true);

        Assert.Null(await StaffDiscord.AccountForAsync(db, id, BeforeTheEnd, Ct));
    }

    [Fact]
    public async Task TheDatabase_RefusesASecondAccountProvingTheSameDiscordAccount()
    {
        await using var db = _db.NewContext();
        var id = NewDiscordId();
        await AddAsync(db, id, proven: true);

        await using var other = _db.NewContext();
        await Assert.ThrowsAsync<DbUpdateException>(() => AddAsync(other, id, proven: true));
    }

    [Fact]
    public async Task TheDatabase_AllowsTheSameTypedIdOnTwoAccounts_BecauseOldDataHasThem()
    {
        await using var db = _db.NewContext();
        var id = NewDiscordId();
        await AddAsync(db, id, proven: false);
        await AddAsync(db, id, proven: false);
        await AddAsync(db, id, proven: true);

        Assert.Equal(3, await db.Users.CountAsync(u => u.DiscordUserId == id, Ct));
    }
}
