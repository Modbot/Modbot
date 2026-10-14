using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Discord;
using Modbot.Core.Supporters;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// <c>/supporter-code</c>: a member gets a short code in a private reply, the same one every time; it
/// is refused while Ko-fi supporters is off; five uses a minute; and it is registered only while
/// Ko-fi is on.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class SupporterCodeCommandTests
{
    private const string Ada = "7001";
    private const string Bo = "7002";

    private readonly PostgresFixture _db;

    public SupporterCodeCommandTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<TestServices> ServicesAsync(bool kofiOn = true)
    {
        var services = await TestServices.CreateAsync(_db, Ct);
        await services.ConfigureAsync(s => s.KofiOn = kofiOn, Ct);
        return services;
    }

    private static DiscordCommandCall Call(string who)
        => new(who, "kiri", DiscordCommands.SupporterCode, new Dictionary<string, string>(), (_, _) => Task.CompletedTask);

    private static async Task<DiscordReply> RunAsync(TestServices services, string who)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<SupporterCodeCommand>().RunAsync(Call(who), Ct);
    }

    private static string CodeIn(DiscordReply reply)
    {
        var text = reply.Text ?? string.Empty;
        var start = text.IndexOf("**", StringComparison.Ordinal) + 2;
        return text[start..text.IndexOf("**", start, StringComparison.Ordinal)];
    }

    [Fact]
    public async Task WhileKofiIsOn_TheReplyHasASixCharacterCode_StoredForTheCaller()
    {
        await using var services = await ServicesAsync();

        var reply = await RunAsync(services, Ada);

        var code = CodeIn(reply);
        Assert.Equal(SupporterCodes.Length, code.Length);
        Assert.Equal(SupporterCodeCommand.Answer(code), reply.Text);

        await using var db = services.Database.NewContext();
        var row = await db.SupporterCodes.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(Ada, row.DiscordUserId);
        Assert.Equal(code, row.Code);
    }

    [Fact]
    public async Task AskingAgain_GivesTheSameCode_AndAnotherPersonGetsAnother()
    {
        await using var services = await ServicesAsync();

        var first = CodeIn(await RunAsync(services, Ada));
        var again = CodeIn(await RunAsync(services, Ada));
        var other = CodeIn(await RunAsync(services, Bo));

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public async Task WhileKofiIsOff_ItSaysSo_AndMakesNoCode()
    {
        await using var services = await ServicesAsync(kofiOn: false);

        var reply = await RunAsync(services, Ada);

        Assert.Equal(SupporterCodeCommand.OffMessage, reply.Text);
        await using var db = services.Database.NewContext();
        Assert.Empty(await db.SupporterCodes.ToListAsync(Ct));
    }

    [Fact]
    public async Task ASixthUseInAMinute_IsRefused()
    {
        await using var services = await ServicesAsync();

        for (var i = 0; i < MemberCommandLimits.PerMinute; i++)
            Assert.NotEqual(SupporterCodeCommand.TooFastMessage, (await RunAsync(services, Ada)).Text);

        Assert.Equal(SupporterCodeCommand.TooFastMessage, (await RunAsync(services, Ada)).Text);
    }

    [Fact]
    public void ItIsRegisteredOnlyWhileKofiIsOn_NotOnTheCommandsCard_AndForEveryone()
    {
        Assert.DoesNotContain(DiscordCommands.For(DiscordCommandSwitches.Empty), c => c.Name == DiscordCommands.SupporterCode);
        Assert.Contains(DiscordCommands.For(DiscordCommandSwitches.Empty, kofiOn: true), c => c.Name == DiscordCommands.SupporterCode);
        Assert.False(DiscordCommands.NamesFor(null).SetEquals(DiscordCommands.NamesFor(null, kofiOn: true)));
        Assert.Null(DiscordCommandSwitches.Find(DiscordCommands.SupporterCode));
        Assert.True(DiscordCommands.IsForEveryone(DiscordCommands.SupporterCode));
    }
}
