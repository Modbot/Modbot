using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Live;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Api.Tests.Features.Places;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Live;

/// <summary>
/// The Live page's read: every open group instance, its head count whether or not anybody is in it,
/// and who is there while a moderator is watching.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class LiveTests
{
    private readonly PostgresFixture _db;

    public LiveTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Group = "grp_1";

    private static async Task<ReadSurfaceTestHost> ReadyAsync(PostgresFixture db)
    {
        var host = await ReadSurfaceTestHost.StartAsync(db);
        await host.ResetAsync(Ct);

        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var settings = await context.GetSettingsAsync(Ct);
        settings.ManagedGroupId = Group;
        await context.SaveChangesAsync(Ct);

        return host;
    }

    /// <summary>A moderator with a paired client, the way the desktop app is paired.</summary>
    private static async Task<(Guid Device, string VRChatUserId)> ModeratorAsync(ReadSurfaceTestHost host)
    {
        var user = await host.CreateUserAsync($"mod_{Guid.NewGuid():N}", "hunter2", ModbotPermissions.None, Ct);

        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var device = Guid.NewGuid();

        context.CompanionDevices.Add(new CompanionDeviceRecord
        {
            Id = device,
            TokenHash = Guid.NewGuid().ToString("n"),
            CompanionVersion = "2026.9.0",
            Platform = "windows",
            IssuedToUserId = user.Id,
            IssuedAt = host.Clock.UtcNow,
        });

        await context.SaveChangesAsync(Ct);
        return (device, user.VRChatUserId!);
    }

    private static FactRecord Seen(string type, string subject, DateTimeOffset at, Guid device, string? name = null)
    {
        var data = new JsonObject { ["deviceId"] = device.ToString() };
        if (name is not null)
            data["displayName"] = name;

        return new FactRecord
        {
            Type = type,
            OccurredAt = at,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = subject,
            WorldId = "wrld_a",
            InstanceId = "39047",
            Source = FactSource.Client,
            Data = data,
        };
    }

    [Fact]
    public async Task WithoutViewLiveInstances_ThePageIsRefused()
    {
        await using var host = await ReadyAsync(_db);

        var cookie = await host.SignedInAsync(
            ModbotPermissions.ViewAnalytics | ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewMembers, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetAsync("/api/live", cookie, Ct)).StatusCode);
    }

    [Theory]
    [InlineData(ModbotPermissions.ViewLiveInstances)]
    [InlineData(ModbotPermissions.Administrator)]
    public async Task ViewLiveInstances_OrAdministrator_MayReadIt(ModbotPermissions held)
    {
        await using var host = await ReadyAsync(_db);

        var cookie = await host.SignedInAsync(held, Ct);

        Assert.Equal(HttpStatusCode.OK, (await host.GetAsync("/api/live", cookie, Ct)).StatusCode);
    }

    /// <summary>
    /// The case the page exists for as much as any other: a group event nobody from the team has
    /// joined. VRChat supplies the instance and its head count with no client involved, so it is on the
    /// page -- never left off, never shown as empty.
    /// </summary>
    [Fact]
    public async Task AnOpenInstanceNobodyIsWatching_IsListedWithItsHeadCount()
    {
        await using var host = await ReadyAsync(_db);
        var t = host.Clock.UtcNow.AddHours(-1);

        await PlacesFixtures.WorldAsync(host, "wrld_a", "The Black Cat", t, Ct);
        var instance = await PlacesFixtures.InstanceAsync(host, "wrld_a", "39047", t, t, null, Ct);

        await using (var context = _db.NewContext())
        {
            var row = await context.VRChatInstances.SingleAsync(i => i.Id == instance.Id, Ct);
            row.HeadCount = 14;
            row.HeadCountSource = HeadCounts.FromPage;
            await context.SaveChangesAsync(Ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);
        var live = await host.GetJsonAsync<LiveView>("/api/live", cookie, Ct);

        var listed = Assert.Single(live.Instances);
        Assert.Equal(instance.Id, listed.Id);
        Assert.Equal("The Black Cat", listed.WorldName);
        Assert.Equal("39047", listed.VRChatInstanceId);
        Assert.Equal(14, listed.HeadCount);
        Assert.Empty(listed.Watching);
        Assert.Empty(listed.People);
        Assert.Null(listed.LastWatchedAt);
        Assert.Empty(listed.LastSeen);
    }

    [Fact]
    public async Task BeforeTheInstancesPageIsRead_TheListsCountIsShown()
    {
        await using var host = await ReadyAsync(_db);
        var t = host.Clock.UtcNow.AddHours(-1);

        await PlacesFixtures.InstanceAsync(host, "wrld_a", "39047", t, t, null, Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);
        var live = await host.GetJsonAsync<LiveView>("/api/live", cookie, Ct);

        Assert.Equal(3, Assert.Single(live.Instances).HeadCount);
    }

    [Fact]
    public async Task ClosedInstances_AreNotListed()
    {
        await using var host = await ReadyAsync(_db);
        var t = host.Clock.UtcNow.AddHours(-3);

        await PlacesFixtures.InstanceAsync(host, "wrld_a", "39047", t, t.AddHours(1), t.AddHours(1), Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);

        Assert.Empty((await host.GetJsonAsync<LiveView>("/api/live", cookie, Ct)).Instances);
    }

    [Fact]
    public async Task AWatchedInstance_ListsWhoIsHere_ArrivedOrHereBefore_WithFlags()
    {
        await using var host = await ReadyAsync(_db);
        var t = host.Clock.UtcNow.AddHours(-1);

        await PlacesFixtures.InstanceAsync(host, "wrld_a", "39047", t, t, null, Ct);
        var (device, moderator) = await ModeratorAsync(host);

        await host.WriteFactAsync(Seen(FactType.InstancePresenceObserved, "usr_ada", t.AddMinutes(10).AddSeconds(-1), device, "Ada"), Ct);
        await host.WriteFactAsync(Seen(FactType.InstanceJoined, moderator, t.AddMinutes(10), device, "Mod"), Ct);
        await host.WriteFactAsync(Seen(FactType.InstanceJoined, "usr_bob", t.AddMinutes(20), device, "Bob"), Ct);
        await host.WriteFactAsync(new FactRecord
        {
            Type = FactType.MemberBanned,
            OccurredAt = t.AddDays(-3),
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = "usr_bob",
            Source = FactSource.AuditLog,
        }, Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);
        var instance = Assert.Single((await host.GetJsonAsync<LiveView>("/api/live", cookie, Ct)).Instances);

        var watcher = Assert.Single(instance.Watching);
        Assert.Equal(moderator, watcher.UserId);
        Assert.Equal(t.AddMinutes(10), watcher.Since);

        var ada = Assert.Single(instance.People, p => p.UserId == "usr_ada");
        Assert.Null(ada.ArrivedAt);
        Assert.Equal(t.AddMinutes(10).AddSeconds(-1), ada.HereBefore);
        Assert.Equal("Ada", ada.DisplayName);

        var bob = Assert.Single(instance.People, p => p.UserId == "usr_bob");
        Assert.Equal(t.AddMinutes(20), bob.ArrivedAt);
        Assert.Null(bob.HereBefore);
        Assert.Equal("Flagged", bob.Standing);
        Assert.Equal("1 prior action", Assert.Single(bob.Flags));

        Assert.Null(instance.LastWatchedAt);
    }

    [Fact]
    public async Task WhenTheLastModeratorLeaves_PeopleAreLastSeen_NotHereNow()
    {
        await using var host = await ReadyAsync(_db);
        var t = host.Clock.UtcNow.AddHours(-1);

        await PlacesFixtures.InstanceAsync(host, "wrld_a", "39047", t, t, null, Ct);
        var (device, moderator) = await ModeratorAsync(host);

        await host.WriteFactAsync(Seen(FactType.InstanceJoined, moderator, t.AddMinutes(10), device), Ct);
        await host.WriteFactAsync(Seen(FactType.InstancePresenceObserved, "usr_ada", t.AddMinutes(10), device, "Ada"), Ct);
        await host.WriteFactAsync(Seen(FactType.InstanceLeft, moderator, t.AddMinutes(30), device), Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);
        var instance = Assert.Single((await host.GetJsonAsync<LiveView>("/api/live", cookie, Ct)).Instances);

        Assert.Empty(instance.Watching);
        Assert.Empty(instance.People);
        Assert.Equal(t.AddMinutes(30), instance.LastWatchedAt);
        Assert.Equal("usr_ada", Assert.Single(instance.LastSeen).UserId);
        Assert.Equal(3, instance.HeadCount);
    }
}
