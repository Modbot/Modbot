using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Core.Users;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// <c>/verify code:</c> (Discord account linking design §14): a code from the account page proves
/// the Discord account that runs it, once, for fifteen minutes, and never one another account
/// proved; wrong codes count, and each Discord account gets a few tries a minute.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class VerifyCommandTests
{
    private const string Caller = "5001";
    private const string Other = "5002";

    private readonly PostgresFixture _db;

    public VerifyCommandTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DiscordCommandCall Call(string discordUserId, string? code, string username = "kiri")
    {
        var options = new Dictionary<string, string>();
        if (code is not null)
            options[DiscordCommands.VerifyCodeOption] = code;

        return new DiscordCommandCall(discordUserId, username, DiscordCommands.Verify, options, (_, _) => Task.CompletedTask);
    }

    private static async Task<string?> RunAsync(TestServices services, string discordUserId, string? code, string username = "kiri")
    {
        using var scope = services.Scope();
        var reply = await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>()
            .HandleAsync(Call(discordUserId, code, username), Ct);
        return reply.Text;
    }

    /// <summary>A staff account with no Discord account, as someone who has just been invited.</summary>
    private static async Task<ModbotUser> AccountAsync(TestServices services)
    {
        await using var db = services.Database.NewContext();
        return await TestAccounts.CreateAsync(
            db, "staff_" + Guid.NewGuid().ToString("n")[..8], TestAccounts.Password, ModbotPermissions.ViewProfile, linked: true, Ct);
    }

    /// <summary>A code for the account, as the account page hands it out: <c>K7P-42Q</c>.</summary>
    private static async Task<string> CodeAsync(TestServices services, Guid userId)
    {
        await using var db = services.Database.NewContext();
        var row = await StaffDiscordCodes.IssueAsync(db, userId, services.Clock.UtcNow, Ct);
        return StaffDiscordCodes.Show(row.Code);
    }

    private static async Task<ModbotUser> ReloadAsync(TestServices services, Guid id)
    {
        await using var db = services.Database.NewContext();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == id, Ct);
    }

    private static async Task<StaffDiscordCode?> RowAsync(TestServices services, Guid userId)
    {
        await using var db = services.Database.NewContext();
        return await db.StaffDiscordCodes.AsNoTracking().SingleOrDefaultAsync(c => c.UserId == userId, Ct);
    }

    /// <summary>The same first half, a different second half: what a wrong guess at this code looks like.</summary>
    private static string WrongSecondHalf(string shown)
    {
        var code = StaffDiscordCodes.Normalize(shown)!;
        var second = code[StaffDiscordCodes.FindLength..];
        var alphabet = StaffDiscordCodes.Alphabet;
        var changed = alphabet[(alphabet.IndexOf(second[0], StringComparison.Ordinal) + 1) % alphabet.Length] + second[1..];
        return code[..StaffDiscordCodes.FindLength] + "-" + changed;
    }

    /// <summary>A code whose first half no live code has.</summary>
    private static string Unrelated(string shown)
    {
        var code = StaffDiscordCodes.Normalize(shown)!;
        var alphabet = StaffDiscordCodes.Alphabet;
        var first = alphabet[(alphabet.IndexOf(code[0], StringComparison.Ordinal) + 1) % alphabet.Length] + code[1..StaffDiscordCodes.FindLength];
        return first + "-" + code[StaffDiscordCodes.FindLength..];
    }

    [Fact]
    public async Task ACode_WorksOnce()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var me = await AccountAsync(services);
        var code = await CodeAsync(services, me.Id);

        Assert.Equal(VerifyCommand.ConnectedMessage, await RunAsync(services, Caller, code));

        var proven = await ReloadAsync(services, me.Id);
        Assert.Equal(Caller, proven.DiscordUserId);
        Assert.Equal("kiri", proven.DiscordUsername);
        Assert.Equal(services.Clock.UtcNow, proven.DiscordVerifiedAt);
        Assert.Null(await RowAsync(services, me.Id));

        // Used: from anybody, the same code proves nothing again.
        Assert.Equal(VerifyCommand.WrongMessage, await RunAsync(services, Other, code));
        Assert.Equal(Caller, (await ReloadAsync(services, me.Id)).DiscordUserId);
    }

    [Fact]
    public async Task ACode_CanBeTypedWithoutTheDashAndInLowerCase()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var me = await AccountAsync(services);
        var code = await CodeAsync(services, me.Id);

        var typed = " " + code.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant() + " ";
        Assert.Equal(VerifyCommand.ConnectedMessage, await RunAsync(services, Caller, typed));
    }

    [Fact]
    public async Task AnExpiredCode_IsRefused()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var me = await AccountAsync(services);
        var code = await CodeAsync(services, me.Id);

        services.Clock.UtcNow += StaffDiscordCodes.Lifetime;

        Assert.Equal(VerifyCommand.WrongMessage, await RunAsync(services, Caller, code));
        Assert.Null((await ReloadAsync(services, me.Id)).DiscordUserId);
    }

    [Fact]
    public async Task AWrongCode_IsRefused_AndCountedAgainstTheCodeItBeganLike()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var me = await AccountAsync(services);
        var code = await CodeAsync(services, me.Id);

        Assert.Equal(VerifyCommand.WrongMessage, await RunAsync(services, Other, WrongSecondHalf(code)));
        Assert.Equal(1, (await RowAsync(services, me.Id))!.FailedTries);

        // Rubbish that begins like no live code costs nobody's code anything.
        Assert.Equal(VerifyCommand.WrongMessage, await RunAsync(services, Other, Unrelated(code)));
        Assert.Equal(VerifyCommand.WrongMessage, await RunAsync(services, Other, "not a code"));
        Assert.Equal(1, (await RowAsync(services, me.Id))!.FailedTries);

        // Every refusal is on the record, and none of them carries the code.
        var facts = await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct);
        Assert.Equal(3, facts.Count);
        Assert.All(facts, f =>
        {
            Assert.Contains("\"wrong-code\"", f.Data, StringComparison.Ordinal);
            Assert.Null(f.ActorId);
            Assert.DoesNotContain(StaffDiscordCodes.Normalize(code)!, f.Data, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ACode_StopsWorking_AfterTooManyWrongTriesAtIt()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var me = await AccountAsync(services);
        var code = await CodeAsync(services, me.Id);

        // From many Discord accounts at once, so the per-person limit is not what stops them.
        for (var i = 0; i < StaffDiscordCodes.MaxFailedTries; i++)
            Assert.Equal(VerifyCommand.WrongMessage, await RunAsync(services, "60" + i, WrongSecondHalf(code)));

        Assert.Equal(VerifyCommand.WrongMessage, await RunAsync(services, Caller, code));
        Assert.Null((await ReloadAsync(services, me.Id)).DiscordUserId);
    }

    [Fact]
    public async Task EachDiscordAccount_GetsAFewTriesAMinute()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var me = await AccountAsync(services);
        var code = await CodeAsync(services, me.Id);

        for (var i = 0; i < MemberCommandLimits.PerMinute; i++)
            Assert.Equal(VerifyCommand.WrongMessage, await RunAsync(services, Caller, Unrelated(code)));

        // Over the limit even the right code is refused, and nothing is read or recorded.
        Assert.Equal(MeCommand.TooFastMessage, await RunAsync(services, Caller, code));
        Assert.Null((await ReloadAsync(services, me.Id)).DiscordUserId);
        Assert.Equal(MemberCommandLimits.PerMinute, (await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct)).Count);

        // Somebody else is not slowed down by it, and neither is /me.
        Assert.Equal(VerifyCommand.WrongMessage, await RunAsync(services, Other, Unrelated(code)));
        Assert.True(services.Provider.GetRequiredService<MemberCommandLimits>().TryUse(Caller, services.Clock.UtcNow));

        services.Clock.UtcNow += TimeSpan.FromMinutes(1);
        Assert.Equal(VerifyCommand.ConnectedMessage, await RunAsync(services, Caller, code));
    }

    [Fact]
    public async Task AnIdProvenOnAnotherAccount_IsRefused_AndNotMoved()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var owner = await services.LinkedAccountAsync(Caller, ModbotPermissions.ViewProfile, ct: Ct);
        var me = await AccountAsync(services);
        var code = await CodeAsync(services, me.Id);

        Assert.Equal(VerifyCommand.TakenMessage, await RunAsync(services, Caller, code));

        Assert.Null((await ReloadAsync(services, me.Id)).DiscordUserId);
        var stillOwner = await ReloadAsync(services, owner.Id);
        Assert.Equal(Caller, stillOwner.DiscordUserId);
        Assert.NotNull(stillOwner.DiscordVerifiedAt);
        Assert.Empty(await services.FactsOfTypeAsync(FactType.DiscordConnected, Ct));

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct));
        Assert.Contains("\"taken\"", fact.Data, StringComparison.Ordinal);

        // The code was right, so it stays: run from the right Discord account, it still works.
        Assert.Equal(VerifyCommand.ConnectedMessage, await RunAsync(services, Other, code));
        Assert.Equal(Other, (await ReloadAsync(services, me.Id)).DiscordUserId);
    }

    [Fact]
    public async Task ANewCode_ReplacesTheOldOne()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var me = await AccountAsync(services);
        var first = await CodeAsync(services, me.Id);
        var second = await CodeAsync(services, me.Id);

        Assert.NotEqual(first, second);

        await using (var db = services.Database.NewContext())
            Assert.Equal(1, await db.StaffDiscordCodes.CountAsync(c => c.UserId == me.Id, Ct));

        Assert.Equal(VerifyCommand.WrongMessage, await RunAsync(services, Caller, first));
        Assert.Equal(VerifyCommand.ConnectedMessage, await RunAsync(services, Caller, second));
    }

    [Fact]
    public async Task ADisabledAccountsCode_ProvesNothing()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var me = await AccountAsync(services);
        var code = await CodeAsync(services, me.Id);

        await using (var db = services.Database.NewContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == me.Id, Ct);
            user.IsDisabled = true;
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(VerifyCommand.WrongMessage, await RunAsync(services, Caller, code));
        Assert.Null((await ReloadAsync(services, me.Id)).DiscordUserId);
    }

    [Fact]
    public async Task TheFacts_AreTheOnesConnectDiscordWrites_SayingItWasACommand()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var typedIt = await services.LinkedAccountAsync(Caller, ModbotPermissions.ViewProfile, ct: Ct, proven: false);
        var me = await AccountAsync(services);
        var code = await CodeAsync(services, me.Id);

        Assert.Equal(VerifyCommand.ConnectedMessage, await RunAsync(services, Caller, code));

        var connected = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordConnected, Ct));
        Assert.Equal(FactPlatform.Modbot, connected.SubjectPlatform);
        Assert.Equal(me.Id.ToString(), connected.SubjectId);
        Assert.Equal(FactPlatform.Modbot, connected.ActorPlatform);
        Assert.Equal(me.Id.ToString(), connected.ActorId);

        using (var data = JsonDocument.Parse(connected.Data))
        {
            var root = data.RootElement;
            Assert.Equal(Caller, root.GetProperty("discordUserId").GetString());
            Assert.Equal("kiri", root.GetProperty("discordUsername").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("replaced").ValueKind);
            Assert.Equal(VerifyCommand.Via, root.GetProperty("via").GetString());
            Assert.Equal(me.Username, root.GetProperty("username").GetString());
            Assert.Equal(me.Username, root.GetProperty("actorDisplayName").GetString());
        }

        // The account that had only typed the id in loses it, with the fact sign-in writes.
        Assert.Null((await ReloadAsync(services, typedIt.Id)).DiscordUserId);
        var unlinked = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordDisconnected, Ct));
        Assert.Equal(typedIt.Id.ToString(), unlinked.SubjectId);
        Assert.Contains("\"proven-by-another-account\"", unlinked.Data, StringComparison.Ordinal);

        var command = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct));
        Assert.Equal(me.Id.ToString(), command.ActorId);
        Assert.Contains("\"connected\"", command.Data, StringComparison.Ordinal);
        Assert.DoesNotContain(StaffDiscordCodes.Normalize(code)!, command.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaffDiscord_CountsTheId_AsProven_AfterTypedIdsStop()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var me = await AccountAsync(services);
        var code = await CodeAsync(services, me.Id);

        Assert.Equal(VerifyCommand.ConnectedMessage, await RunAsync(services, Caller, code));

        var after = StaffDiscord.TypedIdsEnd.AddDays(1);
        await using var db = services.Database.NewContext();
        var proven = await db.Users.AsNoTracking().SingleAsync(u => u.Id == me.Id, Ct);

        Assert.True(proven.IsDiscordProven);
        Assert.Equal(Caller, StaffDiscord.IdOf(proven, after));
        Assert.Equal(Caller, await StaffDiscord.CountedIdAsync(db, proven, after, Ct));
        Assert.Equal(me.Id, (await StaffDiscord.AccountForAsync(db, Caller, after, Ct))?.Id);
    }

    [Fact]
    public void Verify_IsForEveryone_AndTakesARequiredCode()
    {
        var verify = DiscordCommands.All.Single(c => c.Name == DiscordCommands.Verify);

        Assert.False(verify.StaffOnly);
        Assert.True(DiscordCommands.IsForEveryone(DiscordCommands.Verify));
        Assert.Contains(DiscordCommands.For(meCommand: false), c => c.Name == DiscordCommands.Verify);

        var option = Assert.Single(verify.Options);
        Assert.Equal(DiscordCommands.VerifyCodeOption, option.Name);
        Assert.Equal(DiscordOptionKind.Text, option.Kind);
        Assert.True(option.Required);
    }
}
