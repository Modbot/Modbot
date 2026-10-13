using Microsoft.Extensions.DependencyInjection;
using Modbot.Discord.Gateway;
using Modbot.Discord.Permissions;
using Modbot.Discord.ServerIndex;
using Modbot.TestSupport;
using static Modbot.Discord.Tests.Messages.MessageTestData;

namespace Modbot.Discord.Tests.ServerIndex;

/// <summary>
/// Reading the server finds the roles that gained a permission the permission watch tells staff
/// about -- and says nothing about the first read, or about a role whose permissions were never kept.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordServerIndexPermissionGainTests
{
    private const long Administrator = 1L << 3;
    private const long ManageMessages = 1L << 13;
    private const long SendMessages = 1L << 11;

    private readonly PostgresFixture _db;

    public DiscordServerIndexPermissionGainTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DiscordServerSnapshot Server(params DiscordRoleSnapshot[] roles)
        => new(Guild, "The Black Cat", BotCanViewAuditLog: true, BotCanManageRoles: true, [], roles);

    private static DiscordRoleSnapshot Role(string id, long? permissions, bool managed = false)
        => new(id, "Role " + id, 0, 1, managed, Everyone: false, BotCanAssign: !managed, permissions);

    private static async Task<IReadOnlyList<DiscordRoleGain>> RefreshAsync(TestServices services, DiscordServerSnapshot server)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordServerIndex>().RefreshAsync(server, Ct);
    }

    [Fact]
    public async Task TheFirstRead_ReportsNothing_EvenForRolesWithAdministrator()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);

        Assert.Empty(await RefreshAsync(services, Server(Role("1", Administrator))));
    }

    [Fact]
    public async Task ARoleWhosePermissionsGrew_IsReportedWithOnlyWhatItGained()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await RefreshAsync(services, Server(Role("1", SendMessages | ManageMessages)));

        var gain = Assert.Single(await RefreshAsync(services, Server(Role("1", SendMessages | ManageMessages | Administrator))));

        Assert.Equal("1", gain.RoleId);
        Assert.Equal(Administrator, gain.Gained);
        Assert.False(gain.Created);
    }

    [Fact]
    public async Task APermissionThatIsNotWatched_AndAPermissionTakenAway_AreNotReported()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await RefreshAsync(services, Server(Role("1", ManageMessages), Role("2", Administrator)));

        var gains = await RefreshAsync(services, Server(Role("1", ManageMessages | SendMessages), Role("2", SendMessages)));

        Assert.Empty(gains);
    }

    [Fact]
    public async Task ARoleMadeAfterTheFirstRead_IsReportedAsNew()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await RefreshAsync(services, Server(Role("1", SendMessages)));

        var gain = Assert.Single(await RefreshAsync(services, Server(Role("1", SendMessages), Role("2", Administrator))));

        Assert.Equal("2", gain.RoleId);
        Assert.True(gain.Created);
    }

    [Fact]
    public async Task ARoleWhoseStoredPermissionsWereNeverKept_IsNotReported()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await RefreshAsync(services, Server(Role("1", null)));

        Assert.Empty(await RefreshAsync(services, Server(Role("1", Administrator))));
    }

    [Fact]
    public async Task AGainIsReportedOnce_NotOnEveryLaterRead()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await RefreshAsync(services, Server(Role("1", SendMessages)));
        await RefreshAsync(services, Server(Role("1", Administrator)));

        Assert.Empty(await RefreshAsync(services, Server(Role("1", Administrator))));
    }
}
