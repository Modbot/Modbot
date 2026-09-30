using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Places;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Places;

[Collection(nameof(PostgresCollection))]
public class InstanceTests
{
    private readonly PostgresFixture _db;

    public InstanceTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task AnUnknownInstance_Is404()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(
            ModbotPermissions.ViewAnalytics | ModbotPermissions.ViewAuditLog, ct);

        var response = await host.GetAsync($"/api/instances/{Guid.NewGuid()}", cookie, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnInstance_CarriesItsWorld_AndWhoWasSeenInIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-4);

        await PlacesFixtures.WorldAsync(host, "wrld_a", "The Black Cat", t, ct);
        var instance = await PlacesFixtures.InstanceAsync(host, "wrld_a", "39047", t, t.AddHours(2), t.AddHours(2), ct);
        await PlacesFixtures.PersonAsync(host, "usr_a", "Ada", t, ct);

        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_a", t.AddMinutes(5), "wrld_a", "39047"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_a", t.AddMinutes(35), "wrld_a", "39047"), ct);

        var cookie = await host.SignedInAsync(
            ModbotPermissions.ViewAnalytics | ModbotPermissions.ViewAuditLog, ct);

        var view = await host.GetJsonAsync<InstanceView>($"/api/instances/{instance.Id}", cookie, ct);

        Assert.Equal("The Black Cat", view.Instance.WorldName);
        Assert.Equal("39047", view.Instance.VRChatInstanceId);
        Assert.Equal(7, view.Instance.PeakPeople);
        Assert.True(view.CanSeeWhoWasThere);

        var seen = Assert.Single(view.People);
        Assert.Equal("usr_a", seen.UserId);
        Assert.Equal("Ada", seen.DisplayName);
        Assert.Equal(30m, seen.MinutesSeen);

        Assert.Equal(2, view.Log.Count);
    }

    /// <summary>
    /// The popup's Joins row: every join, the different people who made them, and how many of those
    /// people are group members now. A member who joined twice is one returning member; a person who
    /// left the group, or belongs to another group, is not one.
    /// </summary>
    [Fact]
    public async Task ReturningMembers_AreThePeopleSeen_WhoAreMembersNow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-4);

        await PlacesFixtures.WorldAsync(host, "wrld_a", "The Black Cat", t, ct);
        var instance = await PlacesFixtures.InstanceAsync(host, "wrld_a", "39047", t, t.AddHours(2), t.AddHours(2), ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var settings = await db.GetSettingsAsync(ct);
            settings.ManagedGroupId = "grp_1";

            GroupMember Member(string user, string group = "grp_1", DateTimeOffset? left = null) => new()
            {
                GroupId = group,
                UserId = user,
                FirstSeenAt = t,
                LastSeenAt = t,
                LeftAt = left,
            };

            db.GroupMembers.AddRange(
                Member("usr_member"),
                Member("usr_gone", left: t.AddHours(3)),
                Member("usr_elsewhere", group: "grp_2"),
                Member("usr_absent"));
            await db.SaveChangesAsync(ct);
        }

        // The member joins twice: four joins in all, three different people, one returning member.
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_member", t.AddMinutes(5), "wrld_a", "39047"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_member", t.AddMinutes(15), "wrld_a", "39047"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_member", t.AddMinutes(25), "wrld_a", "39047"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_gone", t.AddMinutes(30), "wrld_a", "39047"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_elsewhere", t.AddMinutes(35), "wrld_a", "39047"), ct);

        var cookie = await host.SignedInAsync(
            ModbotPermissions.ViewAnalytics | ModbotPermissions.ViewAuditLog, ct);

        var view = await host.GetJsonAsync<InstanceView>($"/api/instances/{instance.Id}", cookie, ct);

        Assert.Equal(4, view.Counts.Arrivals);
        Assert.Equal(3, view.Counts.Visitors);
        Assert.Equal(1, view.ReturningMembers);
    }

    /// <summary>
    /// VRChat hands the same instance number out again once an instance closes. An instance must not show the
    /// people or the facts of the evening before it.
    /// </summary>
    [Fact]
    public async Task AnInstanceReusingANumber_DoesNotShowTheEarlierEvenings()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var monday = host.Clock.UtcNow.AddDays(-7);
        var tonight = host.Clock.UtcNow.AddHours(-2);

        await PlacesFixtures.WorldAsync(host, "wrld_a", "The Black Cat", monday, ct);

        var earlier = await PlacesFixtures.InstanceAsync(
            host, "wrld_a", "39047", monday, monday.AddHours(1), monday.AddHours(1), ct);
        var later = await PlacesFixtures.InstanceAsync(
            host, "wrld_a", "39047", tonight, tonight.AddMinutes(30), null, ct);

        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_old", monday.AddMinutes(5), "wrld_a", "39047"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_new", tonight.AddMinutes(5), "wrld_a", "39047"), ct);

        var cookie = await host.SignedInAsync(
            ModbotPermissions.ViewAnalytics | ModbotPermissions.ViewAuditLog, ct);

        var first = await host.GetJsonAsync<InstanceView>($"/api/instances/{earlier.Id}", cookie, ct);
        var second = await host.GetJsonAsync<InstanceView>($"/api/instances/{later.Id}", cookie, ct);

        Assert.Equal("usr_old", Assert.Single(first.People).UserId);
        Assert.Equal("usr_new", Assert.Single(second.People).UserId);
    }

    /// <summary>
    /// The instance's own shape is not moderation history; who was in it and what was done to them is
    /// (spec 5.9.4). A caller with ViewAnalytics alone gets the instance and neither list.
    /// </summary>
    [Fact]
    public async Task WithoutViewAuditLog_TheInstanceAnswers_AndWhoWasThereDoesNot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-4);
        await PlacesFixtures.WorldAsync(host, "wrld_a", "The Black Cat", t, ct);
        var instance = await PlacesFixtures.InstanceAsync(host, "wrld_a", "39047", t, t.AddHours(2), null, ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_a", t.AddMinutes(5), "wrld_a", "39047"), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var view = await host.GetJsonAsync<InstanceView>($"/api/instances/{instance.Id}", cookie, ct);

        Assert.Equal("The Black Cat", view.Instance.WorldName);
        Assert.False(view.CanSeeWhoWasThere);
        Assert.Empty(view.People);
        Assert.Equal(0, view.ReturningMembers);
        Assert.Empty(view.Log);
    }

    /// <summary>
    /// The popup draws how many were in the instance over time from its head count changes, oldest
    /// first, and only this instance's. How many is not who, so it shows without ViewAuditLog too.
    /// </summary>
    [Fact]
    public async Task AnInstance_CarriesItsHeadCountsOverTime_OldestFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-4);
        await PlacesFixtures.WorldAsync(host, "wrld_a", "The Black Cat", t, ct);
        var instance = await PlacesFixtures.InstanceAsync(host, "wrld_a", "39047", t, t.AddHours(2), null, ct);
        var other = await PlacesFixtures.InstanceAsync(host, "wrld_a", "50000", t, t.AddHours(2), null, ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.InstanceHeadCounts.AddRange(
                new InstanceHeadCount { InstanceId = instance.Id, CountedAt = t.AddMinutes(30), HeadCount = 4, Source = "page" },
                new InstanceHeadCount { InstanceId = instance.Id, CountedAt = t.AddMinutes(1), HeadCount = 1, Source = "list" },
                new InstanceHeadCount { InstanceId = other.Id, CountedAt = t.AddMinutes(10), HeadCount = 9, Source = "page" });
            await db.SaveChangesAsync(ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var view = await host.GetJsonAsync<InstanceView>($"/api/instances/{instance.Id}", cookie, ct);

        Assert.Equal([1, 4], view.HeadCounts.Select(h => h.People));
        Assert.Equal([t.AddMinutes(1), t.AddMinutes(30)], view.HeadCounts.Select(h => h.At));
    }

    /// <summary>
    /// A page reading with no userCount took n_users as its count, and is sent as unsure so the popup
    /// can show "80?". A list reading is never unsure. The instance's own current count and peak say
    /// the same about themselves.
    /// </summary>
    [Fact]
    public async Task AReadingWithNoUserCount_IsSentAsUnsure_AndSoAreTheInstancesOwnCounts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-4);
        await PlacesFixtures.WorldAsync(host, "wrld_a", "The Black Cat", t, ct);
        var instance = await PlacesFixtures.InstanceAsync(host, "wrld_a", "39047", t, t.AddHours(2), null, ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            db.InstanceHeadCounts.AddRange(
                new InstanceHeadCount { InstanceId = instance.Id, CountedAt = t.AddMinutes(1), HeadCount = 2, MemberCount = 2, Source = "list" },
                new InstanceHeadCount { InstanceId = instance.Id, CountedAt = t.AddMinutes(10), HeadCount = 51, UserCount = 51, NUsers = 80, Source = "page" },
                new InstanceHeadCount { InstanceId = instance.Id, CountedAt = t.AddMinutes(20), HeadCount = 70, NUsers = 70, Source = "page" });

            var row = await db.VRChatInstances.SingleAsync(i => i.Id == instance.Id, ct);
            row.HeadCount = 70;
            row.HeadCountUnsure = true;
            row.HeadCountSource = "page";
            row.PeakUserCount = 70;
            row.PeakUnsure = true;

            await db.SaveChangesAsync(ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var view = await host.GetJsonAsync<InstanceView>($"/api/instances/{instance.Id}", cookie, ct);

        Assert.Equal([2, 51, 70], view.HeadCounts.Select(h => h.People));
        Assert.Equal([false, false, true], view.HeadCounts.Select(h => h.Unsure));
        Assert.Equal([null, 80, 70], view.HeadCounts.Select(h => h.NUsers));

        Assert.Equal(70, view.Instance.PeopleNow);
        Assert.True(view.Instance.PeopleNowUnsure);
        Assert.Equal(70, view.Instance.PeakPeople);
        Assert.True(view.Instance.PeakPeopleUnsure);
    }

    /// <summary>
    /// The popup colours each step of the head count by what it was. A drop with a kick recorded about
    /// then is a kick; a drop without one is somebody leaving. The member and rank lines come from what
    /// a companion saw, against today's membership and rank; without a companion there they are empty.
    /// </summary>
    [Fact]
    public async Task HeadCountSteps_SayWhyTheyMoved_AndACompanionAddsMembersAndRanks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-4);
        await PlacesFixtures.WorldAsync(host, "wrld_a", "The Black Cat", t, ct);
        var instance = await PlacesFixtures.InstanceAsync(host, "wrld_a", "39047", t, t.AddHours(2), null, ct);
        await PlacesFixtures.PersonAsync(host, "usr_a", "Ada", t, ct);
        await PlacesFixtures.PersonAsync(host, "usr_b", "Bob", t, ct);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var settings = await db.GetSettingsAsync(ct);
            settings.ManagedGroupId = "grp_1";

            db.GroupMembers.Add(new GroupMember { GroupId = "grp_1", UserId = "usr_a", FirstSeenAt = t, LastSeenAt = t });

            var ada = await db.VRChatUsers.SingleAsync(u => u.UserId == "usr_a", ct);
            ada.TrustRank = Modbot.Core.Users.TrustRank.TrustedUser;

            db.InstanceHeadCounts.AddRange(
                new InstanceHeadCount { InstanceId = instance.Id, CountedAt = t.AddMinutes(1), HeadCount = 4, Source = "page" },
                new InstanceHeadCount { InstanceId = instance.Id, CountedAt = t.AddMinutes(10), HeadCount = 6, Source = "page" },
                new InstanceHeadCount { InstanceId = instance.Id, CountedAt = t.AddMinutes(20), HeadCount = 5, Source = "page" },
                new InstanceHeadCount { InstanceId = instance.Id, CountedAt = t.AddMinutes(30), HeadCount = 4, Source = "page" });

            await db.SaveChangesAsync(ct);
        }

        // A kick fifteen seconds before the reading that showed the drop; nothing near the second drop.
        await host.WriteFactAsync(
            AuditFact(FactType.GroupInstanceKick, "usr_x", t.AddMinutes(20).AddSeconds(-15), "usr_mod", "Mod", "wrld_a", "39047"), ct);

        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_a", t.AddMinutes(5), "wrld_a", "39047"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_b", t.AddMinutes(15), "wrld_a", "39047"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_a", t.AddMinutes(25), "wrld_a", "39047"), ct);

        var cookie = await host.SignedInAsync(
            ModbotPermissions.ViewAnalytics | ModbotPermissions.ViewAuditLog, ct);
        var view = await host.GetJsonAsync<InstanceView>($"/api/instances/{instance.Id}", cookie, ct);

        Assert.Equal([null, "up", "kick", "left"], view.HeadCounts.Select(h => h.Change));

        // Ada (member, Trusted) 5-25, Bob (not a member, rank not read) 15 until the last report at 25.
        Assert.Equal([t.AddMinutes(5), t.AddMinutes(15), t.AddMinutes(25)], view.PeoplePresent.Select(p => p.At));
        Assert.Equal([1, 1, 0], view.PeoplePresent.Select(p => p.Members));
        Assert.Equal([1, 1, 0], view.PeoplePresent.Select(p => p.TrustedUser));
        Assert.Equal([0, 1, 0], view.PeoplePresent.Select(p => p.RankUnknown));

        // How many is not who: the colours stay without ViewAuditLog, the lines go.
        var analyst = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, ct);
        var narrow = await host.GetJsonAsync<InstanceView>($"/api/instances/{instance.Id}", analyst, ct);

        Assert.Equal([null, "up", "kick", "left"], narrow.HeadCounts.Select(h => h.Change));
        Assert.Empty(narrow.PeoplePresent);
    }

    [Fact]
    public async Task WithoutViewAnalytics_Is403()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var response = await host.GetAsync($"/api/instances/{Guid.NewGuid()}", cookie, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
