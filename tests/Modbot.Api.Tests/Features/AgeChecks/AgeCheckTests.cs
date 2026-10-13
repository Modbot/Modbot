using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Tests.Fakes;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Users;
using Modbot.TestSupport;
using Modbot.VRChat;
using Modbot.VRChat.Sync;
using VRChat.API.Model;
using SettingsRow = Modbot.Core.Data.Entities.Settings;

namespace Modbot.Api.Tests.Features.AgeChecks;

/// <summary>
/// The 18+ check that <c>/ageverified</c> and the profile's button share: the flag, the Discord
/// roles, the VRChat group role and the log, for a linked member, an unlinked one and a mismatch,
/// with a dangerous role and with roles that fail.
/// </summary>
/// <remarks>The gate and Discord are fakes, so nothing here reaches VRChat or Discord.</remarks>
[Collection(nameof(PostgresCollection))]
public class AgeCheckTests
{
    private const string Guild = "700";
    private const string Party = "811";
    private const string Waiting = "812";
    private const string Admin = "813";
    private const string GroupId = "grp_1";
    private const string Member = "555";
    private const string Linked = "usr_c9094d86-1846-43eb-b79d-7e3dc318f42a";
    private const string Other = "usr_0a17232e-6ad4-4889-8e1e-6e0c5fa815fd";

    private const long Community = (1L << 10) | (1L << 11);
    private const long Administrator = 1L << 3;

    private readonly PostgresFixture _db;

    public AgeCheckTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DiscordRole Role(string id, string name, long permissions, DateTimeOffset at) => new()
    {
        RoleId = id,
        GuildId = Guild,
        Name = name,
        Permissions = permissions,
        BotCanAssign = true,
        FirstSeenAt = at,
        UpdatedAt = at,
    };

    private async Task<(ReadSurfaceTestHost Host, FakeDiscordAgeActions Discord, FakeVRChatGate Gate, Guid Checker)> StartAsync(
        Action<SettingsRow>? more = null, bool linkMember = true)
    {
        var discord = new FakeDiscordAgeActions();
        var gate = new FakeVRChatGate().Returns("AddGroupMemberRole", VRChatResult<List<string>>.Ok(["grol_adult"], 200, """["grol_adult"]"""));
        var host = await ReadSurfaceTestHost.StartAsync(_db, gate, configure: s => s.AddSingleton<IDiscordAgeActions>(discord));
        await host.ResetAsync(Ct);

        var checker = await host.CreateUserAsync("checker", "hunter2", ModbotPermissions.EditAgeVerification, Ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var settings = await db.GetSettingsAsync(Ct);
            var at = host.Clock.UtcNow;

            settings.DiscordGuildId = Guild;
            settings.ManagedGroupId = GroupId;
            settings.GroupInfoSnapshot = GroupInfoSnapshot.From(new Group
            {
                Id = GroupId,
                Name = "Test Group",
                Roles = [new GroupRole { Id = "grol_adult", Name = "Adult", Order = 1, Permissions = [] }],
            }).ToJson();
            settings.AgeCheckGiveRoleIds = [Party];
            settings.AgeCheckRemoveRoleIds = [Waiting];
            settings.AgeCheckGroupRoleId = "grol_adult";
            more?.Invoke(settings);

            db.DiscordRoles.AddRange(
                Role(Party, "Adult", Community, at),
                Role(Waiting, "Waiting", Community, at),
                Role(Admin, "Boss", Community | Administrator, at));

            if (linkMember)
            {
                db.DiscordAccountLinks.Add(new DiscordAccountLink
                {
                    DiscordUserId = Member,
                    DiscordUsername = "someone",
                    VRChatUserId = Linked,
                    LinkedAt = at,
                });
            }

            await db.SaveChangesAsync(Ct);
        }

        return (host, discord, gate, checker.Id);
    }

    private static async Task<AgeCheckAnswer> RunAsync(ReadSurfaceTestHost host, AgeCheckRequest request)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAgeCheck>().RunAsync(request, Ct);
    }

    private static async Task<List<ModbotEvent>> FactsAsync(ReadSurfaceTestHost host, string type)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        return await db.Events.AsNoTracking().Where(e => e.Type == type).OrderBy(e => e.Id).ToListAsync(Ct);
    }

    private static async Task<VRChatUser?> PersonAsync(ReadSurfaceTestHost host, string id)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        return await db.VRChatUsers.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == id, Ct);
    }

    [Fact]
    public async Task ALinkedMember_IsMarked_GetsTheRolesAndTheGroupRole_AndTheCheckIsLogged()
    {
        var (host, discord, gate, checker) = await StartAsync();
        await using var _ = host;

        var answer = await RunAsync(host, new AgeCheckRequest(checker, AgeCheckMethods.Photo, Member, null));

        Assert.True(answer.Done);
        Assert.Equal(
            [(Guild, Member, Party, true), (Guild, Member, Waiting, false)],
            discord.RoleChanges);
        Assert.Single(gate.Calls);

        Assert.True((await PersonAsync(host, Linked))!.Is18PlusVerified);

        var fact = Assert.Single(await FactsAsync(host, FactType.UserAgeFlagSet));
        Assert.Equal(Linked, fact.SubjectId);
        Assert.Equal(checker.ToString(), fact.ActorId);
        Assert.Equal(AgeCheckMethods.Photo, JsonDocument.Parse(fact.Data).RootElement.GetProperty("method").GetString());

        Assert.Single(await FactsAsync(host, FactType.GroupRoleGiven));
    }

    [Fact]
    public async Task AnUnlinkedMember_NeedsTheirVRChatProfile_ThenGetsBothTheDiscordAndTheVRChatRoles()
    {
        var (host, discord, gate, checker) = await StartAsync(linkMember: false);
        await using var _ = host;

        var without = await RunAsync(host, new AgeCheckRequest(checker, AgeCheckMethods.Badge, Member, null));

        Assert.False(without.Done);
        Assert.Empty(discord.RoleChanges);
        Assert.Empty(gate.Calls);
        Assert.Null(await PersonAsync(host, Linked));

        var with = await RunAsync(
            host, new AgeCheckRequest(checker, AgeCheckMethods.Badge, Member, $"https://vrchat.com/home/user/{Linked}"));

        Assert.True(with.Done);
        Assert.Equal(2, discord.RoleChanges.Count);
        Assert.Single(gate.Calls);
        Assert.True((await PersonAsync(host, Linked))!.Is18PlusVerified);
    }

    [Fact]
    public async Task ADifferentProfileThanTheLinkedOne_IsRefused_AndNothingChanges()
    {
        var (host, discord, gate, checker) = await StartAsync();
        await using var _ = host;

        var answer = await RunAsync(host, new AgeCheckRequest(checker, AgeCheckMethods.Badge, Member, Other));

        Assert.False(answer.Done);
        Assert.Empty(discord.RoleChanges);
        Assert.Empty(gate.Calls);
        Assert.Null(await PersonAsync(host, Linked));
        Assert.Null(await PersonAsync(host, Other));
        Assert.Empty(await FactsAsync(host, FactType.UserAgeFlagSet));
    }

    [Fact]
    public async Task TheProfilesButton_FindsTheLinkedMemberItself()
    {
        var (host, discord, _, checker) = await StartAsync();
        await using var _ = host;

        var answer = await RunAsync(host, new AgeCheckRequest(checker, AgeCheckMethods.Badge, null, Linked));

        Assert.True(answer.Done);
        Assert.Equal(2, discord.RoleChanges.Count);
        Assert.All(discord.RoleChanges, c => Assert.Equal(Member, c.UserId));
    }

    [Fact]
    public async Task AUnknownMethod_IsRefused_BeforeAnythingElse()
    {
        var (host, discord, _, checker) = await StartAsync();
        await using var _ = host;

        var answer = await RunAsync(host, new AgeCheckRequest(checker, "passport", Member, null));

        Assert.False(answer.Done);
        Assert.Empty(discord.RoleChanges);
        Assert.Empty(await FactsAsync(host, FactType.UserAgeFlagSet));
    }

    [Fact]
    public async Task ARoleThatGainedADangerousPermission_IsNotGiven_ButTheCheckStands()
    {
        var (host, discord, _, checker) = await StartAsync(s => s.AgeCheckGiveRoleIds = [Admin, Party]);
        await using var _ = host;

        var answer = await RunAsync(host, new AgeCheckRequest(checker, AgeCheckMethods.Badge, Member, null));

        Assert.True(answer.Done);
        Assert.DoesNotContain(discord.RoleChanges, c => c.RoleId == Admin);
        Assert.Contains(discord.RoleChanges, c => c.RoleId == Party && c.Add);
        Assert.Contains(answer.Lines, l => l.Contains("Did not give Boss", StringComparison.Ordinal));
        Assert.True((await PersonAsync(host, Linked))!.Is18PlusVerified);
    }

    [Fact]
    public async Task ARoleThatFails_StopsTheRest_ButNeverUndoesTheFlag()
    {
        var (host, discord, _, checker) = await StartAsync();
        await using var _ = host;

        discord.Refusals[Party] = DiscordAgeOutcome.Failed("Missing Permissions.");

        var answer = await RunAsync(host, new AgeCheckRequest(checker, AgeCheckMethods.Badge, Member, null));

        Assert.True(answer.Done);
        Assert.Equal([(Guild, Member, Party, true)], discord.RoleChanges);
        Assert.Contains(answer.Lines, l => l.Contains("Missing Permissions.", StringComparison.Ordinal));
        Assert.True((await PersonAsync(host, Linked))!.Is18PlusVerified);
        Assert.Single(await FactsAsync(host, FactType.UserAgeFlagSet));
    }

    [Fact]
    public async Task AMemberWhoLeftTheServer_IsTold_AndTheFlagStays()
    {
        var (host, discord, _, checker) = await StartAsync();
        await using var _ = host;

        discord.Refusals[Party] = DiscordAgeOutcome.Left;

        var answer = await RunAsync(host, new AgeCheckRequest(checker, AgeCheckMethods.Badge, Member, null));

        Assert.True(answer.Done);
        Assert.Contains(answer.Lines, l => l.Contains("not in the Discord server", StringComparison.Ordinal));
        Assert.True((await PersonAsync(host, Linked))!.Is18PlusVerified);
    }
}
