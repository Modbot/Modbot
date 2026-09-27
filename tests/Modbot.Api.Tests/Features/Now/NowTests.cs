using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Now;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Now;

/// <summary>
/// The Now page's "since you last looked": when the counting starts, and what it counts.
/// </summary>
public class NowLookTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AFirstLook_StartsNothing()
    {
        var user = new ModbotUser();

        NowEndpoints.Look(user, T0);

        Assert.Equal(T0, user.NowLookedAt);
        Assert.Null(user.NowSince);
    }

    /// <summary>A reload, or the page's own call every few minutes, must not reset what it counts from.</summary>
    [Fact]
    public void LookingAgainSoon_KeepsWhereTheCountingStarts()
    {
        var user = new ModbotUser { NowLookedAt = T0, NowSince = T0.AddHours(-5) };

        NowEndpoints.Look(user, T0.AddMinutes(5));

        Assert.Equal(T0.AddMinutes(5), user.NowLookedAt);
        Assert.Equal(T0.AddHours(-5), user.NowSince);
    }

    [Fact]
    public void ComingBackAfterBeingAway_CountsFromTheLastLook()
    {
        var user = new ModbotUser { NowLookedAt = T0, NowSince = T0.AddHours(-5) };

        NowEndpoints.Look(user, T0 + NowEndpoints.AwayAfter);

        Assert.Equal(T0 + NowEndpoints.AwayAfter, user.NowLookedAt);
        Assert.Equal(T0, user.NowSince);
    }

    /// <summary>An hour on the page, told every five minutes, is one look: nothing moves the start on.</summary>
    [Fact]
    public void AnHourOfLooking_IsOneLook()
    {
        var user = new ModbotUser { NowLookedAt = T0.AddDays(-1) };
        NowEndpoints.Look(user, T0);

        for (var minutes = 5; minutes <= 60; minutes += 5)
            NowEndpoints.Look(user, T0.AddMinutes(minutes));

        Assert.Equal(T0.AddDays(-1), user.NowSince);
        Assert.Equal(T0.AddHours(1), user.NowLookedAt);
    }

    /// <summary>Two tabs whose calls arrive out of order must not move the last look backwards.</summary>
    [Fact]
    public void ALookFromEarlier_DoesNotMoveTheLastLookBack()
    {
        var user = new ModbotUser { NowLookedAt = T0 };

        NowEndpoints.Look(user, T0.AddMinutes(-2));

        Assert.Equal(T0, user.NowLookedAt);
    }
}

/// <summary>The endpoint, against real PostgreSQL, because the counts are one grouped query.</summary>
[Collection(nameof(PostgresCollection))]
public class NowEndpointTests
{
    private readonly PostgresFixture _db;

    public NowEndpointTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<NowLook> LookAsync(ReadSurfaceTestHost host, string cookie)
    {
        var response = await host.PostJsonAsync("/api/now/look", null, cookie, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<NowLook>(JsonSerializerOptions.Web, Ct))!;
    }

    private static FactRecord Fact(string type, string subject, DateTimeOffset at) => new()
    {
        Type = type,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = subject,
        Source = FactSource.AuditLog,
    };

    [Fact]
    public async Task AFirstLook_CountsTheLastDay_InAFixedOrder()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        var now = host.Clock.UtcNow;

        await host.WriteFactAsync(Fact(FactType.MemberJoined, "usr_a", now.AddHours(-2)), Ct);
        await host.WriteFactAsync(Fact(FactType.MemberJoined, "usr_b", now.AddHours(-3)), Ct);
        await host.WriteFactAsync(Fact(FactType.MemberBanned, "usr_c", now.AddHours(-1)), Ct);
        await host.WriteFactAsync(Fact(FactType.GroupInstanceKick, "usr_d", now.AddHours(-1)), Ct);
        await host.WriteFactAsync(Fact(FactType.MemberKicked, "usr_e", now.AddHours(-1)), Ct);

        // Older than the day a first look counts.
        await host.WriteFactAsync(Fact(FactType.MemberBanned, "usr_f", now.AddDays(-2)), Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);
        var look = await LookAsync(host, cookie);

        Assert.False(look.LookedBefore);
        Assert.Equal(now - NowEndpoints.FirstLookWindow, look.Since);
        Assert.NotNull(look.Changes);
        Assert.Equal(["bans", "kicks", "joins"], look.Changes.Select(c => c.Kind));
        Assert.Equal([1, 2, 2], look.Changes.Select(c => c.Count));
    }

    [Fact]
    public async Task ComingBack_CountsOnlyWhatHappenedSinceTheLastLook()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);
        await host.WriteFactAsync(Fact(FactType.MemberBanned, "usr_old", host.Clock.UtcNow.AddMinutes(-30)), Ct);

        await LookAsync(host, cookie);
        var looked = host.Clock.UtcNow;

        host.Clock.Advance(TimeSpan.FromHours(2));
        await host.WriteFactAsync(Fact(FactType.NoteAdded, "usr_new", host.Clock.UtcNow.AddMinutes(-10)), Ct);

        var look = await LookAsync(host, cookie);

        Assert.True(look.LookedBefore);
        Assert.Equal(looked, look.Since);
        var change = Assert.Single(look.Changes!);
        Assert.Equal("notes", change.Kind);
        Assert.Equal(1, change.Count);
    }

    /// <summary>Every count is a count of audit log entries, so somebody who may not read it gets none.</summary>
    [Fact]
    public async Task WithoutViewAuditLog_ThereAreNoCounts()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        await host.WriteFactAsync(Fact(FactType.MemberBanned, "usr_c", host.Clock.UtcNow.AddHours(-1)), Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewLiveInstances, Ct);
        var look = await LookAsync(host, cookie);

        Assert.Null(look.Changes);
    }

    [Fact]
    public async Task SignedOut_IsRefused()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var response = await host.Client.PostAsync("/api/now/look", null, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
