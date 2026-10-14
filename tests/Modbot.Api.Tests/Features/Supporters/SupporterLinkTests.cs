using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Supporters;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Supporters;

/// <summary>
/// Staff matching a supporter to another Discord account: the role Modbot gave the old account stays on
/// record against that account, so the role pass can take it back from there.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class SupporterLinkTests(PostgresFixture db)
{
    private const string Guild = "9100";
    private const string OldAccount = "9101";
    private const string NewAccount = "9102";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(ApiTestHost Host, Guid Id)> StartAsync(string? givenRole)
    {
        await using (var context = db.NewContext())
        {
            await context.Supporters.ExecuteDeleteAsync(Ct);
            await context.DiscordMembers.Where(m => m.GuildId == Guild).ExecuteDeleteAsync(Ct);
        }

        var host = await ApiTestHost.StartAsync(db);
        var now = host.Clock.UtcNow;
        var id = Guid.CreateVersion7();

        using var scope = host.Services.CreateScope();
        var context2 = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        (await context2.GetSettingsAsync(Ct)).DiscordGuildId = Guild;
        context2.DiscordMembers.Add(new DiscordMember { GuildId = Guild, UserId = NewAccount, Username = "new", DisplayName = "New", FirstSeenAt = now, UpdatedAt = now });
        context2.Supporters.Add(new Supporter
        {
            Id = id,
            PayerId = "payer",
            Name = "Sam",
            DiscordUserId = OldAccount,
            State = SupporterStates.Active,
            FirstPaidAt = now,
            LastPaidAt = now,
            PaidThrough = now.AddDays(30),
            GivenDiscordRoleId = givenRole,
            UpdatedAt = now,
        });
        await context2.SaveChangesAsync(Ct);
        return (host, id);
    }

    [Fact]
    public async Task MovingASupporterToAnotherAccount_KeepsWhichAccountTheRoleWentTo()
    {
        var (host, id) = await StartAsync("role-1");
        await using var _ = host;
        var (_, staff) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/supporters/{id}/link", new { discordUserId = NewAccount }, staff, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var read = db.NewContext();
        var supporter = await read.Supporters.AsNoTracking().SingleAsync(s => s.Id == id, Ct);
        Assert.Equal(NewAccount, supporter.DiscordUserId);
        Assert.Equal("role-1", supporter.GivenDiscordRoleId);
        Assert.Equal(OldAccount, supporter.GivenDiscordUserId);
    }

    [Fact]
    public async Task MovingASupporterWhoWasGivenNoRole_RecordsNoAccount()
    {
        var (host, id) = await StartAsync(null);
        await using var _ = host;
        var (_, staff) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.SendJsonAsync(HttpMethod.Post, $"/api/supporters/{id}/link", new { discordUserId = NewAccount }, staff, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var read = db.NewContext();
        Assert.Null((await read.Supporters.AsNoTracking().SingleAsync(s => s.Id == id, Ct)).GivenDiscordUserId);
    }
}
