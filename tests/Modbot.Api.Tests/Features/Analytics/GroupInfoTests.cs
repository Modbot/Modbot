using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Analytics.Group;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Tests.Features.Analytics;

/// <summary>
/// The top of the VRChat analytics page: the group as its own VRChat page shows it, read from what
/// the group-info sync left behind.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class GroupInfoTests
{
    private readonly PostgresFixture _db;

    public GroupInfoTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task TheGroup_ComesFromTheSnapshot_ThePicturesAndTheNewestReading()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var now = host.Clock.UtcNow;

        await SettingsAsync(host, ct, s =>
        {
            s.ManagedGroupId = "grp_1";
            s.ManagedGroupName = "Old Name";
            s.ManagedGroupIconUrl = "https://files.example/icon.png";
            s.ManagedGroupBannerUrl = "https://files.example/banner.png";
            s.ManagedGroupLanguages = ["eng", "jpn"];
            s.ManagedGroupLinks = ["https://example.org/"];
            s.GroupInfoPolledAt = now.AddMinutes(-2);
            s.GroupInfoSnapshot = Snapshot("Test Group", members: 900, online: 9).ToJson();
        });

        await ReadingsAsync(host, ct, (now.AddMinutes(-10), 1000, 40), (now.AddMinutes(-5), 1001, 42));

        var info = await InfoAsync(host, ct);

        Assert.Equal("grp_1", info.Id);
        Assert.Equal("Test Group", info.Name);
        Assert.Equal("TESTIN", info.ShortCode);
        Assert.Equal("4698", info.Discriminator);
        Assert.Equal("a group", info.Description);
        Assert.Equal("be nice", info.Rules);
        Assert.Equal("https://files.example/icon.png", info.IconUrl);
        Assert.Equal("https://files.example/banner.png", info.BannerUrl);
        Assert.Equal(["eng", "jpn"], info.Languages);
        Assert.Equal(["https://example.org/"], info.Links);

        // The newest reading, not the snapshot's counts.
        Assert.Equal(1001, info.Members);
        Assert.Equal(42, info.Online);
        Assert.Equal(now.AddMinutes(-5), info.CountedAt!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(now.AddMinutes(-2), info.ReadAt!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WithNoReadings_TheCountsAreTheSnapshots()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        await SettingsAsync(host, ct, s =>
        {
            s.ManagedGroupId = "grp_1";
            s.GroupInfoSnapshot = Snapshot("Test Group", members: 900, online: 9).ToJson();
        });

        var info = await InfoAsync(host, ct);

        Assert.Equal(900, info.Members);
        Assert.Equal(9, info.Online);
        Assert.Null(info.CountedAt);
    }

    /// <summary>Before the sync has read anything: every field empty, and the page still answers.</summary>
    [Fact]
    public async Task BeforeTheFirstRead_EverythingIsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var info = await InfoAsync(host, ct);

        Assert.Null(info.Name);
        Assert.Null(info.ShortCode);
        Assert.Null(info.Members);
        Assert.Empty(info.Languages);
        Assert.Empty(info.Links);
        Assert.Null(info.ReadAt);
    }

    private static GroupInfoSnapshot Snapshot(string name, int members, int online) => new(
        name,
        "TESTIN",
        "4698",
        "a group",
        "be nice",
        OwnerId: "usr_owner",
        JoinState: "open",
        Privacy: "default",
        IsVerified: false,
        members,
        online,
        Roles: []);

    private static async Task<GroupInfo> InfoAsync(ReadSurfaceTestHost host, CancellationToken ct)
    {
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        return await host.GetJsonAsync<GroupInfo>("/api/analytics/group/info", cookie, ct);
    }

    private static async Task SettingsAsync(ReadSurfaceTestHost host, CancellationToken ct, Action<Core.Data.Entities.Settings> change)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var settings = await db.GetSettingsAsync(ct);
        change(settings);
        await db.SaveChangesAsync(ct);
    }

    private static async Task ReadingsAsync(
        ReadSurfaceTestHost host,
        CancellationToken ct,
        params (DateTimeOffset At, int Members, int Online)[] readings)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        db.GroupMemberCounts.AddRange(readings.Select(r => new GroupMemberCount
        {
            GroupId = "grp_1",
            CountedAt = r.At,
            MemberCount = r.Members,
            OnlineMemberCount = r.Online,
        }));

        await db.SaveChangesAsync(ct);
    }
}
