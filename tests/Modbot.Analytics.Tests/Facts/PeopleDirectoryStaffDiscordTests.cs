using System.Security.Cryptography;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Facts;

/// <summary>
/// Who a staff account stands for on Discord when event routes and held roles ask (Discord event
/// routes design §3): the Discord account that counts for it (accounts and access design §4.6), by
/// the same rule the bot uses.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PeopleDirectoryStaffDiscordTests(PostgresFixture db)
{
    private static readonly DateTimeOffset BeforeTheEnd = StaffDiscord.TypedIdsEnd.AddDays(-1);
    private static readonly DateTimeOffset AfterTheEnd = StaffDiscord.TypedIdsEnd.AddDays(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewDiscordId()
        => RandomNumberGenerator.GetInt32(100_000_000, int.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture) + "4242";

    private static async Task<ModbotUser> AddAsync(ModbotContext context, string discordUserId, bool proven)
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
            DiscordVerifiedAt = proven ? BeforeTheEnd.AddDays(-5) : null,
        };

        context.Users.Add(user);
        await context.SaveChangesAsync(Ct);
        return user;
    }

    private static async Task<PeopleDirectory> LoadAsync(ModbotContext context, ModbotUser account, string discordUserId, DateTimeOffset now)
        => await PeopleDirectory.LoadAsync(
            context,
            [(FactPlatform.Modbot, account.Id.ToString()), (FactPlatform.Discord, discordUserId)],
            now,
            Ct);

    [Fact]
    public async Task AProvenId_StandsForTheAccount_BothWays()
    {
        await using var context = db.NewContext();
        var id = NewDiscordId();
        var account = await AddAsync(context, id, proven: true);

        var directory = await LoadAsync(context, account, id, AfterTheEnd);

        Assert.Contains(id, directory.Of(FactPlatform.Modbot, account.Id.ToString()).DiscordIds);
        Assert.Equal(account.Id, directory.Of(FactPlatform.Discord, id).Account?.Id);
    }

    [Fact]
    public async Task ATypedId_StandsForTheAccountUntilTheEnd_AndNotAfter()
    {
        await using var context = db.NewContext();
        var id = NewDiscordId();
        var account = await AddAsync(context, id, proven: false);

        var before = await LoadAsync(context, account, id, BeforeTheEnd);
        Assert.Contains(id, before.Of(FactPlatform.Modbot, account.Id.ToString()).DiscordIds);
        Assert.Equal(account.Id, before.Of(FactPlatform.Discord, id).Account?.Id);

        var after = await LoadAsync(context, account, id, AfterTheEnd);
        Assert.DoesNotContain(id, after.Of(FactPlatform.Modbot, account.Id.ToString()).DiscordIds);
        Assert.Null(after.Of(FactPlatform.Discord, id).Account);
    }

    [Fact]
    public async Task AnIdTypedOnTwoAccounts_StandsForNeither()
    {
        await using var context = db.NewContext();
        var id = NewDiscordId();
        var first = await AddAsync(context, id, proven: false);
        await AddAsync(context, id, proven: false);

        var directory = await LoadAsync(context, first, id, BeforeTheEnd);

        Assert.DoesNotContain(id, directory.Of(FactPlatform.Modbot, first.Id.ToString()).DiscordIds);
        Assert.Null(directory.Of(FactPlatform.Discord, id).Account);
    }

    [Fact]
    public async Task AnIdProvenByOneAccount_StandsForThatOne_NotOneThatTypedIt()
    {
        await using var context = db.NewContext();
        var id = NewDiscordId();
        var typed = await AddAsync(context, id, proven: false);
        var proven = await AddAsync(context, id, proven: true);

        var directory = await LoadAsync(context, typed, id, BeforeTheEnd);

        Assert.DoesNotContain(id, directory.Of(FactPlatform.Modbot, typed.Id.ToString()).DiscordIds);
        Assert.Equal(proven.Id, directory.Of(FactPlatform.Discord, id).Account?.Id);
    }
}
