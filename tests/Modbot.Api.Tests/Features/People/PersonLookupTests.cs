using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.People;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.People;

/// <summary>
/// One person from any one of their accounts (one view per person design §3).
/// </summary>
/// <remarks>
/// The rules under test are the ones a screen can get wrong quietly: that an account that ties to
/// nothing still answers, that nothing is tied that the data does not tie, that the old
/// <c>?subject=&lt;vrchat id&gt;</c> address still means what it always meant, and that a side is
/// withheld from a caller who may not read it rather than leaked through the resolution.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class PersonLookupTests
{
    private readonly PostgresFixture _db;

    public PersonLookupTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Day = new(2026, 4, 2, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Every permission the three sides need, so a test about resolution is about resolution.</summary>
    private const ModbotPermissions Reads =
        ModbotPermissions.ViewProfile
        | ModbotPermissions.ViewMembers
        | ModbotPermissions.ViewOperationalLog
        | ModbotPermissions.ViewAuditLog;

    private static string Ask(string? vrchat = null, string? discord = null, Guid? account = null)
    {
        if (vrchat is not null) return $"/api/people?vrchatUserId={Uri.EscapeDataString(vrchat)}";
        if (discord is not null) return $"/api/people?discordUserId={Uri.EscapeDataString(discord)}";
        return $"/api/people?accountId={account}";
    }

    private static async Task SeedAsync(ReadSurfaceTestHost host, Action<ModbotContext> seed)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        seed(db);
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// Puts a VRChat id and a Discord id on an account, the way the link step and the contact
    /// form do. Read back inside the scope rather than reattached, so the roles the account was
    /// created with are not tracked twice.
    /// </summary>
    private static async Task RecordOnAccountAsync(
        ReadSurfaceTestHost host, Guid accountId, string? vrchatUserId = null, string? discordUserId = null)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var account = await db.Users.FirstAsync(u => u.Id == accountId, Ct);

        if (vrchatUserId is not null)
        {
            account.VRChatUserId = vrchatUserId;
            account.VRChatLinkedAt = Day;
        }

        if (discordUserId is not null)
            account.DiscordUserId = discordUserId;

        await db.SaveChangesAsync(Ct);
    }

    private static DiscordAccountLink Link(string discordUserId, string vrchatUserId) => new()
    {
        DiscordUserId = discordUserId,
        DiscordUsername = "someone",
        VRChatUserId = vrchatUserId,
        StartedFrom = LinkStartedFrom.Discord,
        LinkedAt = Day,
    };

    private static DiscordMember Member(string userId, string displayName) => new()
    {
        GuildId = "g_1",
        UserId = userId,
        Username = displayName,
        DisplayName = displayName,
        FirstSeenAt = Day,
        UpdatedAt = Day,
    };

    // ── Each account resolves to the others ─────────────────────────────────────────────────

    [Fact]
    public async Task AVRChatAccountFindsTheLinkedDiscordAccountAndTheModbotAccount()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var account = await host.CreateUserAsync("mira", "hunter2", ModbotPermissions.Ban, Ct, linked: false);
        await SeedAsync(host, db =>
        {
            db.DiscordAccountLinks.Add(Link("d_1", "usr_mira"));
            db.DiscordMembers.Add(Member("d_1", "Mira"));
            db.VRChatUsers.Add(new VRChatUser { UserId = "usr_mira", DisplayName = "Mira" });
        });
        await RecordOnAccountAsync(host, account.Id, vrchatUserId: "usr_mira");

        var cookie = await host.SignedInAsync(Reads, Ct);
        var person = await host.GetJsonAsync<PersonView>(Ask(vrchat: "usr_mira"), cookie, Ct);

        Assert.Equal("usr_mira", person.VRChat?.Id);
        Assert.Equal(FoundBy.Asked, person.VRChat?.FoundBy);
        Assert.Equal("Mira", person.VRChat?.Name);

        Assert.Equal("d_1", person.Discord?.Id);
        Assert.Equal(FoundBy.Link, person.Discord?.FoundBy);

        Assert.Equal(account.Id, person.Account?.Id);
        Assert.Equal("mira", person.Account?.Username);
        Assert.True(person.CanSeeAccount);
    }

    [Fact]
    public async Task ADiscordAccountFindsTheVRChatAccountAndTheModbotAccount()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var account = await host.CreateUserAsync("ada", "hunter2", ModbotPermissions.Ban, Ct, linked: false);
        await SeedAsync(host, db =>
        {
            db.DiscordAccountLinks.Add(Link("d_2", "usr_ada"));
        });
        await RecordOnAccountAsync(host, account.Id, vrchatUserId: "usr_ada");

        var cookie = await host.SignedInAsync(Reads, Ct);
        var person = await host.GetJsonAsync<PersonView>(Ask(discord: "d_2"), cookie, Ct);

        Assert.Equal(FoundBy.Asked, person.Discord?.FoundBy);
        Assert.Equal("usr_ada", person.VRChat?.Id);
        Assert.Equal(FoundBy.Link, person.VRChat?.FoundBy);
        Assert.Equal(account.Id, person.Account?.Id);
    }

    [Fact]
    public async Task AModbotAccountFindsTheVRChatAndDiscordAccounts()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var account = await host.CreateUserAsync("rae", "hunter2", ModbotPermissions.Ban, Ct, linked: false);
        await SeedAsync(host, db =>
        {
            db.DiscordAccountLinks.Add(Link("d_3", "usr_rae"));
        });
        await RecordOnAccountAsync(host, account.Id, vrchatUserId: "usr_rae");

        var cookie = await host.SignedInAsync(Reads, Ct);
        var person = await host.GetJsonAsync<PersonView>(Ask(account: account.Id), cookie, Ct);

        Assert.Equal(FoundBy.Asked, person.Account?.FoundBy);
        Assert.Equal("usr_rae", person.VRChat?.Id);
        Assert.Equal(FoundBy.Account, person.VRChat?.FoundBy);
        Assert.Equal("d_3", person.Discord?.Id);
        Assert.Equal(FoundBy.Link, person.Discord?.FoundBy);
    }

    // ── Nothing is invented ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADiscordAccountNobodyLinkedResolvesToNothingElse()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        await SeedAsync(host, db => db.DiscordMembers.Add(Member("d_stranger", "Stranger")));

        var cookie = await host.SignedInAsync(Reads, Ct);
        var person = await host.GetJsonAsync<PersonView>(Ask(discord: "d_stranger"), cookie, Ct);

        Assert.Equal("d_stranger", person.Discord?.Id);
        Assert.Null(person.VRChat);
        Assert.Null(person.Account);

        // And it is an answer, not a refusal: the view opens on the one account that is known.
        Assert.True(person.CanSeeAccount);
    }

    [Fact]
    public async Task AModbotAccountWithNoLinksResolvesToItselfAlone()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var account = await host.CreateUserAsync("newcomer", "hunter2", ModbotPermissions.None, Ct, linked: false);

        var cookie = await host.SignedInAsync(Reads, Ct);
        var person = await host.GetJsonAsync<PersonView>(Ask(account: account.Id), cookie, Ct);

        Assert.Equal(account.Id, person.Account?.Id);
        Assert.Null(person.VRChat);
        Assert.Null(person.Discord);
    }

    [Fact]
    public async Task TwoPeopleWhoShareADisplayNameAreNotTiedTogether()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        // Same name on both sides, and nothing linking them. A name is never a join key.
        await SeedAsync(host, db =>
        {
            db.VRChatUsers.Add(new VRChatUser { UserId = "usr_twin", DisplayName = "Twin" });
            db.DiscordMembers.Add(Member("d_twin", "Twin"));
        });

        var cookie = await host.SignedInAsync(Reads, Ct);
        var person = await host.GetJsonAsync<PersonView>(Ask(vrchat: "usr_twin"), cookie, Ct);

        Assert.Null(person.Discord);
    }

    [Fact]
    public async Task AnEndedLinkTiesNothing()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        await SeedAsync(host, db =>
        {
            var link = Link("d_gone", "usr_gone");
            link.UnlinkedAt = Day.AddDays(1);
            link.UnlinkedBy = LinkEndedBy.Member;
            db.DiscordAccountLinks.Add(link);
        });

        var cookie = await host.SignedInAsync(Reads, Ct);
        var person = await host.GetJsonAsync<PersonView>(Ask(vrchat: "usr_gone"), cookie, Ct);

        Assert.Null(person.Discord);
    }

    [Fact]
    public async Task ADiscordIdTypedOntoAnAccountIsMarkedAsSuch_NotAsALink()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var account = await host.CreateUserAsync("typed", "hunter2", ModbotPermissions.None, Ct, linked: false);
        await RecordOnAccountAsync(host, account.Id, vrchatUserId: "usr_typed", discordUserId: "d_typed");

        var cookie = await host.SignedInAsync(Reads, Ct);
        var person = await host.GetJsonAsync<PersonView>(Ask(vrchat: "usr_typed"), cookie, Ct);

        Assert.Equal("d_typed", person.Discord?.Id);
        Assert.Equal(FoundBy.Account, person.Discord?.FoundBy);
    }

    // ── What a caller may be told ───────────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutTheOperationalLogTheModbotAccountIsWithheld_AndSaidToBeWithheld()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var account = await host.CreateUserAsync("hidden", "hunter2", ModbotPermissions.None, Ct, linked: false);
        await RecordOnAccountAsync(host, account.Id, vrchatUserId: "usr_hidden");

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);
        var person = await host.GetJsonAsync<PersonView>(Ask(vrchat: "usr_hidden"), cookie, Ct);

        Assert.Equal("usr_hidden", person.VRChat?.Id);
        Assert.Null(person.Account);

        // Not "there is no account": "you may not be told". A screen that said the first would
        // be lying, and a moderator would act on it.
        Assert.False(person.CanSeeAccount);
    }

    [Fact]
    public async Task AnAccountIdResolvesToNothingForSomebodyWhoMayNotKnowWhoHoldsAccounts()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var account = await host.CreateUserAsync("secret", "hunter2", ModbotPermissions.None, Ct, linked: false);
        await RecordOnAccountAsync(host, account.Id, vrchatUserId: "usr_secret");

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);
        var person = await host.GetJsonAsync<PersonView>(Ask(account: account.Id), cookie, Ct);

        // The mapping must not leak backwards: an account id must not become a VRChat id.
        Assert.Null(person.VRChat);
        Assert.Null(person.Discord);
        Assert.Null(person.Account);
        Assert.False(person.CanSeeAccount);
    }

    [Fact]
    public async Task WithoutViewProfileTheDiscordSideIsNotRevealed()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        await SeedAsync(host, db => db.DiscordAccountLinks.Add(Link("d_quiet", "usr_quiet")));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);
        var person = await host.GetJsonAsync<PersonView>(Ask(vrchat: "usr_quiet"), cookie, Ct);

        Assert.Equal("usr_quiet", person.VRChat?.Id);
        Assert.Null(person.Discord);
    }

    [Fact]
    public async Task GivingNeitherOrBothIsARefusal()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var cookie = await host.SignedInAsync(Reads, Ct);

        var none = await host.GetAsync("/api/people", cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);

        var both = await host.GetAsync("/api/people?vrchatUserId=usr_a&discordUserId=d_a", cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
    }

    [Fact]
    public async Task AnIdThatLooksLikeNothingIsStillAnswered()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        // A legacy VRChat id follows no structure (foundation §3.1.1), so nothing here may refuse
        // one for its shape.
        const string Legacy = "8JoV9XEdpo";

        var cookie = await host.SignedInAsync(Reads, Ct);
        var person = await host.GetJsonAsync<PersonView>(Ask(vrchat: Legacy), cookie, Ct);

        Assert.Equal(Legacy, person.VRChat?.Id);
        Assert.Equal(FoundBy.Asked, person.VRChat?.FoundBy);
    }
}
