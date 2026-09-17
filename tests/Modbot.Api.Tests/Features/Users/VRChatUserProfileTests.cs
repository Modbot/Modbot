using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Health;
using Modbot.Api.Features.Users;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat.Sync;
using Modbot.Core.Users;

namespace Modbot.Api.Tests.Features.Users;

/// <summary>
/// The profile endpoints: what is shown, how old it is said to be, who may change the 18+ flag,
/// and what asking for a refresh does.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class VRChatUserProfileTests
{
    private readonly PostgresFixture _db;

    public VRChatUserProfileTests(PostgresFixture db) => _db = db;

    private static readonly DateTimeOffset Day = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static async Task SeedAsync(ReadSurfaceTestHost host, VRChatUser row, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        db.VRChatUsers.Add(row);
        await db.SaveChangesAsync(ct);
    }

    [Fact]
    public async Task TheStoredProfileIsShownWithItsAge()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        host.Clock.UtcNow = Day;
        await SeedAsync(host, new VRChatUser
        {
            UserId = "usr_a",
            DisplayName = "Trinity",
            Bio = "hello",
            Tags = """["system_trust_veteran"]""",
            TrustRank = TrustRank.TrustedUser,
            AgeVerificationStatus = "hidden",
            Is18PlusVerified = true,
            Is18PlusVerifiedAt = Day.AddDays(-2),
            Is18PlusVerifiedSource = AgeVerificationSource.VRChat,
            FirstSeenAt = Day.AddDays(-10),
            LastSeenAt = Day.AddHours(-1),
            LastRefreshedAt = Day.AddHours(-1),
        }, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, ct);
        var profile = await host.GetJsonAsync<VRChatUserProfile>("/api/vrchat-users/profile?id=usr_a", cookie, ct);

        Assert.True(profile.Known);
        Assert.Equal("Trinity", profile.DisplayName);
        Assert.Equal(["system_trust_veteran"], profile.Tags);
        Assert.Equal(TrustRank.TrustedUser, profile.TrustRank);
        Assert.Equal(Day.AddHours(-1), profile.LastRefreshedAt);
        Assert.False(profile.Stale);
        Assert.Equal(Day, profile.Now);

        // The sticky flag beside what VRChat says today: they disagree, and both are shown.
        Assert.True(profile.EighteenPlus.Verified);
        Assert.Equal("vrchat", profile.EighteenPlus.Source);
        Assert.Equal("hidden", profile.AgeVerificationStatusLastSeen);
    }

    [Fact]
    public async Task AProfileOlderThanTheStaleWindowIsLabelledStale()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        host.Clock.UtcNow = Day;
        await SeedAsync(host, new VRChatUser
        {
            UserId = "usr_old", FirstSeenAt = Day.AddDays(-30), LastSeenAt = Day.AddDays(-30), LastRefreshedAt = Day.AddDays(-30),
        }, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, ct);
        var profile = await host.GetJsonAsync<VRChatUserProfile>("/api/vrchat-users/profile?id=usr_old", cookie, ct);

        Assert.True(profile.Stale);
        Assert.Equal(TimeSpan.FromHours(6).TotalSeconds, profile.StaleAfterSeconds);
    }

    /// <summary>Not a 404: the screen still has an id to show, a refresh to offer, and a flag to set.</summary>
    [Fact]
    public async Task AnUnknownIdIsAnsweredAsUnknown_NotAsAnError()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, ct);
        var profile = await host.GetJsonAsync<VRChatUserProfile>("/api/vrchat-users/profile?id=usr_nobody", cookie, ct);

        Assert.False(profile.Known);
        Assert.True(profile.Stale);
        Assert.False(profile.EighteenPlus.Verified);
    }

    [Fact]
    public async Task ViewingAProfileNeedsViewProfile()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var response = await host.GetAsync("/api/vrchat-users/profile?id=usr_a", cookie, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AskingForARefreshQueuesItAtTheOpenedTier_AndTheProfileSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, ct);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/vrchat-users/refresh?id=usr_new");
        request.Headers.Add("Cookie", cookie);
        var response = await host.Client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<RefreshRequestResult>(ct);

        Assert.Equal("Queued", result!.Outcome);
        Assert.Equal(RefreshReason.OpenedInModbot, host.Queue.PendingFor("usr_new")!.Reason);

        // The row exists now, and the profile reports the pending refresh.
        var profile = await host.GetJsonAsync<VRChatUserProfile>("/api/vrchat-users/profile?id=usr_new", cookie, ct);

        Assert.True(profile.Known);
        Assert.True(profile.Refresh.Pending);
        Assert.Equal("OpenedInModbot", profile.Refresh.Reason);
    }

    /// <summary>A moderator clicking through a list must not spend the lane on the same person twice.</summary>
    [Fact]
    public async Task AFreshProfileIsAnsweredFreshEnough()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        host.Clock.UtcNow = Day;
        await SeedAsync(host, new VRChatUser
        {
            UserId = "usr_fresh", FirstSeenAt = Day, LastSeenAt = Day, LastRefreshedAt = Day.AddSeconds(-5),
        }, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, ct);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/vrchat-users/refresh?id=usr_fresh");
        request.Headers.Add("Cookie", cookie);
        var response = await host.Client.SendAsync(request, ct);
        var result = await response.Content.ReadFromJsonAsync<RefreshRequestResult>(ct);

        Assert.Equal("FreshEnough", result!.Outcome);
        Assert.Equal(Day.AddSeconds(-5), result.LastRefreshedAt);
        Assert.Null(host.Queue.PendingFor("usr_fresh"));
    }

    [Fact]
    public async Task AHostWithoutTheSync_SaysARefreshCannotBeArrangedHere()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db, withSync: false);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, ct);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/vrchat-users/refresh?id=usr_a");
        request.Headers.Add("Cookie", cookie);
        var response = await host.Client.SendAsync(request, ct);
        var result = await response.Content.ReadFromJsonAsync<RefreshRequestResult>(ct);

        Assert.Equal("NotAvailable", result!.Outcome);

        var profile = await host.GetJsonAsync<VRChatUserProfile>("/api/vrchat-users/profile?id=usr_a", cookie, ct);
        Assert.NotNull(profile.Refresh.Blocked);
    }

    [Fact]
    public async Task ClearingTheFlagNeedsEditAgeVerification()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, ct);

        var request = new HttpRequestMessage(HttpMethod.Put, "/api/vrchat-users/age-verified?id=usr_a")
        {
            Content = JsonContent.Create(new SetAgeVerifiedRequest(false, "no")),
        };
        request.Headers.Add("Cookie", cookie);

        var response = await host.Client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AModeratorClearsTheFlag_AndTheFactNamesThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        host.Clock.UtcNow = Day;
        await SeedAsync(host, new VRChatUser
        {
            UserId = "usr_a",
            Is18PlusVerified = true,
            Is18PlusVerifiedAt = Day.AddDays(-1),
            Is18PlusVerifiedSource = AgeVerificationSource.VRChat,
            FirstSeenAt = Day.AddDays(-1),
            LastSeenAt = Day.AddDays(-1),
        }, ct);

        var moderator = await host.CreateUserAsync("mod_" + Guid.NewGuid().ToString("N"), "hunter2",
            ModbotPermissions.ViewProfile | ModbotPermissions.EditAgeVerification, ct);

        var login = await host.Client.PostAsync(
            "/api/auth/login",
            JsonContent.Create(new { username = moderator.Username, password = "hunter2" }),
            ct);
        var cookie = login.Headers.GetValues("Set-Cookie").First().Split(';')[0];

        var request = new HttpRequestMessage(HttpMethod.Put, "/api/vrchat-users/age-verified?id=usr_a")
        {
            Content = JsonContent.Create(new SetAgeVerifiedRequest(false, "Verified badge belonged to a different account")),
        };
        request.Headers.Add("Cookie", cookie);

        var response = await host.Client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var profile = await response.Content.ReadFromJsonAsync<VRChatUserProfile>(ct);

        Assert.False(profile!.EighteenPlus.Verified);
        Assert.Equal("manual", profile.EighteenPlus.Source);
        Assert.Equal(moderator.Id, profile.EighteenPlus.SetByUserId);
        Assert.Equal(moderator.Username, profile.EighteenPlus.SetByUsername);
        Assert.Equal(Day, profile.EighteenPlus.Since);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var fact = await db.Events.AsNoTracking().SingleAsync(e => e.Type == FactType.UserAgeFlagCleared, ct);

        Assert.Equal("usr_a", fact.SubjectId);
        Assert.Equal(FactPlatform.Modbot, fact.ActorPlatform);
        Assert.Equal(moderator.Id.ToString(), fact.ActorId);
        Assert.Contains("different account", fact.Data);
    }

    [Fact]
    public async Task SyncHealth_CarriesTheProfileSyncsNumbers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        host.Diagnostics.RecordUserProfileCounts(new UserProfileCounts(1200, 300, 4, Day.AddHours(-9), host.Clock.UtcNow));
        host.Diagnostics.RecordUserProfileRun(
            new SyncRunReport(SyncOutcome.Produced, host.Clock.UtcNow, TimeSpan.FromSeconds(0.3), "changed: bio usr_a"),
            refreshed: true);
        host.Queue.Offer(
            new RefreshRequest("usr_x", RefreshReason.SeenInInstance, host.Clock.UtcNow, host.Clock.UtcNow),
            null, host.Clock.UtcNow, new UserProfileSyncOptions());

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, ct);
        var health = await host.GetJsonAsync<SyncHealth>("/api/health/sync", cookie, ct);

        Assert.NotNull(health.UserProfiles);
        Assert.Equal(1200, health.UserProfiles.KnownUsers);
        Assert.Equal(300, health.UserProfiles.NeverRefreshed);
        Assert.Equal(1, health.UserProfiles.Waiting);
        Assert.Equal(1, health.UserProfiles.WaitingByReason["SeenInInstance"]);
        Assert.Equal(1, health.UserProfiles.RefreshesInLastHour);
        Assert.Equal("Produced", health.LastUserProfileRun!.Outcome);
    }
}
