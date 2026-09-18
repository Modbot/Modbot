using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
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

    [Fact]
    public async Task TheHistoryReplaysEachChangeBackwardsFromTheRow()
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
            Pronouns = "she/her",
            Tags = """["system_trust_veteran"]""",
            FirstSeenAt = Day.AddDays(-10),
            LastSeenAt = Day,
            LastRefreshedAt = Day,
        }, ct);

        // First sighting ten days ago as Neo, with no pronouns; renamed and given a bio since.
        await host.WriteFactAsync(new FactRecord
        {
            Type = FactType.UserProfileFirstSeen,
            OccurredAt = Day.AddDays(-10),
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = "usr_a",
            Source = FactSource.SyncDiff,
            Data = new JsonObject
            {
                ["baseline"] = new JsonObject
                {
                    ["displayName"] = "Neo",
                    ["tags"] = new JsonArray("system_trust_veteran"),
                },
            },
        }, ct);

        await host.WriteFactAsync(new FactRecord
        {
            Type = FactType.UserProfileChanged,
            OccurredAt = Day.AddDays(-3),
            OccurredBefore = Day.AddDays(-2),
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = "usr_a",
            Source = FactSource.SyncDiff,
            Data = new JsonObject
            {
                ["changed"] = new JsonObject
                {
                    ["displayName"] = new JsonObject { ["old"] = "Neo", ["new"] = "Trinity" },
                    ["bio"] = new JsonObject { ["old"] = null, ["new"] = "hello" },
                },
            },
        }, ct);

        await host.WriteFactAsync(new FactRecord
        {
            Type = FactType.UserProfileChanged,
            OccurredAt = Day.AddDays(-1),
            OccurredBefore = Day,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = "usr_a",
            Source = FactSource.SyncDiff,
            Data = new JsonObject
            {
                ["changed"] = new JsonObject
                {
                    ["pronouns"] = new JsonObject { ["old"] = null, ["new"] = "she/her" },
                },
            },
        }, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, ct);
        var history = await host.GetJsonAsync<ProfileHistory>("/api/vrchat-users/history?id=usr_a", cookie, ct);

        Assert.True(history.Known);
        Assert.Equal(3, history.Versions.Count);

        var newest = history.Versions[0];
        Assert.True(newest.Current);
        Assert.Equal(["pronouns"], newest.Changed);
        Assert.Equal("Trinity", newest.Profile.DisplayName);
        Assert.Equal("she/her", newest.Profile.Pronouns);
        Assert.Equal("hello", newest.Profile.Bio);

        // jsonb keeps keys in its own order, so the changed list is compared as a set.
        var renamed = history.Versions[1];
        Assert.Equal(["bio", "displayName"], renamed.Changed.Order());
        Assert.Equal("Trinity", renamed.Profile.DisplayName);
        Assert.Null(renamed.Profile.Pronouns);
        Assert.Equal(Day.AddDays(-2), renamed.Before);

        var first = history.Versions[2];
        Assert.True(first.Baseline);
        Assert.Equal("Neo", first.Profile.DisplayName);
        Assert.Null(first.Profile.Bio);
        Assert.Equal(["system_trust_veteran"], first.Profile.Tags);
    }

    [Fact]
    public async Task TheRawBodiesAreHandedBackAsStored()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        host.Clock.UtcNow = Day;
        await SeedAsync(host, new VRChatUser
        {
            UserId = "usr_a",
            RawPublicProfile = """{"displayName":"Trinity","bio":"hello"}""",
            FirstSeenAt = Day,
            LastSeenAt = Day,
            LastRefreshedAt = Day,
        }, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, ct);
        var raw = await host.GetJsonAsync<RawProfile>("/api/vrchat-users/raw?id=usr_a", cookie, ct);

        Assert.Equal("Trinity", raw.PublicProfile?["displayName"]?.GetValue<string>());
        Assert.Equal(Day, raw.PublicProfileReadAt);
        Assert.Null(raw.User);
        Assert.Null(raw.UserReadAt);

        var stranger = await host.GetJsonAsync<RawProfile>("/api/vrchat-users/raw?id=usr_nobody", cookie, ct);
        Assert.Null(stranger.PublicProfile);
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

    /// <summary>
    /// The profile carries the icon, the banner and the represented group of their own, and the
    /// one picture a list would show is chosen by the shared rule -- here the icon, because
    /// profilePicOverride left every VRChat call in API specification v1.21.0.
    /// </summary>
    [Fact]
    public async Task TheProfileCarriesTheIconTheBannerAndTheRepresentedGroup()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        host.Clock.UtcNow = Day;
        await SeedAsync(host, new VRChatUser
        {
            UserId = "usr_a",
            DisplayName = "Trinity",
            IconUrl = "https://api.vrchat.cloud/api/1/file/file_icon/1/file",
            BannerUrl = "https://api.vrchat.cloud/api/1/file/file_banner/1/file",
            CurrentAvatarThumbnailImageUrl = "https://api.vrchat.cloud/api/1/file/file_thumb/1/file",
            RepresentedGroupId = "grp_a",
            RepresentedGroupName = "The Black Cat",
            RepresentedGroupIconUrl = "https://api.vrchat.cloud/api/1/file/file_group/1/file",
            FirstSeenAt = Day.AddDays(-1),
            LastSeenAt = Day,
            LastRefreshedAt = Day,
        }, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, ct);
        var profile = await host.GetJsonAsync<VRChatUserProfile>("/api/vrchat-users/profile?id=usr_a", cookie, ct);

        Assert.Equal("https://api.vrchat.cloud/api/1/file/file_icon/1/file", profile.IconUrl);
        Assert.Equal("https://api.vrchat.cloud/api/1/file/file_banner/1/file", profile.BannerUrl);

        // No override, so the icon is the best picture -- not the older avatar thumbnail.
        Assert.Equal("https://api.vrchat.cloud/api/1/file/file_icon/1/file", profile.ProfilePictureUrl);

        Assert.NotNull(profile.RepresentedGroup);
        Assert.Equal("grp_a", profile.RepresentedGroup.GroupId);
        Assert.Equal("The Black Cat", profile.RepresentedGroup.Name);

        // VRChat's own address, unchanged: a program holding an API key wants the real one, and
        // turning it into a Modbot address is the web app's job.
        Assert.StartsWith("https://api.vrchat.cloud/", profile.IconUrl, StringComparison.Ordinal);
    }

    /// <summary>
    /// The history replays the three new fields under VRChat's own field names, so a version
    /// shows the face, the banner and the group the person had at that moment.
    /// </summary>
    [Fact]
    public async Task TheHistoryReplaysTheIconTheBannerAndTheRepresentedGroup()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        host.Clock.UtcNow = Day;
        await SeedAsync(host, new VRChatUser
        {
            UserId = "usr_a",
            DisplayName = "Trinity",
            IconUrl = "https://api.vrchat.cloud/api/1/file/file_new/1/file",
            BannerUrl = "https://api.vrchat.cloud/api/1/file/file_banner2/1/file",
            RepresentedGroupId = "grp_b",
            RepresentedGroupName = "The White Rabbit",
            FirstSeenAt = Day.AddDays(-5),
            LastSeenAt = Day,
            LastRefreshedAt = Day,
        }, ct);

        await host.WriteFactAsync(new FactRecord
        {
            Type = FactType.UserProfileFirstSeen,
            OccurredAt = Day.AddDays(-5),
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = "usr_a",
            Source = FactSource.SyncDiff,
            Data = new JsonObject { ["baseline"] = new JsonObject { ["displayName"] = "Trinity" } },
        }, ct);

        await host.WriteFactAsync(new FactRecord
        {
            Type = FactType.UserProfileChanged,
            OccurredAt = Day.AddDays(-1),
            OccurredBefore = Day,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = "usr_a",
            Source = FactSource.SyncDiff,
            Data = new JsonObject
            {
                ["changed"] = new JsonObject
                {
                    ["iconUrl"] = new JsonObject
                    {
                        ["old"] = "https://api.vrchat.cloud/api/1/file/file_old/1/file",
                        ["new"] = "https://api.vrchat.cloud/api/1/file/file_new/1/file",
                    },
                    ["bannerUrl"] = new JsonObject
                    {
                        ["old"] = null,
                        ["new"] = "https://api.vrchat.cloud/api/1/file/file_banner2/1/file",
                    },

                    // The whole group under one key, so a version can name both groups rather
                    // than leave a moderator reading two ids.
                    ["representedGroup"] = new JsonObject
                    {
                        ["old"] = new JsonObject
                        {
                            ["groupId"] = "grp_a",
                            ["name"] = "The Black Cat",
                            ["iconUrl"] = null,
                        },
                        ["new"] = new JsonObject
                        {
                            ["groupId"] = "grp_b",
                            ["name"] = "The White Rabbit",
                            ["iconUrl"] = null,
                        },
                    },
                },
            },
        }, ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, ct);
        var history = await host.GetJsonAsync<ProfileHistory>("/api/vrchat-users/history?id=usr_a", cookie, ct);

        var now = history.Versions[0];
        Assert.Equal(["bannerUrl", "iconUrl", "representedGroup"], now.Changed.Order());
        Assert.Equal("https://api.vrchat.cloud/api/1/file/file_new/1/file", now.Profile.IconUrl);
        Assert.Equal("The White Rabbit", now.Profile.RepresentedGroup!.Name);

        // And as it stood before that change: the old icon, no banner, the old group.
        var before = history.Versions[1];
        Assert.Equal("https://api.vrchat.cloud/api/1/file/file_old/1/file", before.Profile.IconUrl);
        Assert.Null(before.Profile.BannerUrl);
        Assert.Equal("grp_a", before.Profile.RepresentedGroup!.GroupId);

        // With no override stored, the best picture at each moment is that moment's icon.
        Assert.Equal(now.Profile.IconUrl, now.Profile.ProfilePictureUrl);
        Assert.Equal(before.Profile.IconUrl, before.Profile.ProfilePictureUrl);
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
