using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Companion.Context;
using Modbot.Api.Features.Watches;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Notifications;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Watches;

/// <summary>
/// Watching a person (watching a person design): starting, stopping and following up through the
/// endpoints, what each leaves in the audit log, the pass that closes watches and raises reminders,
/// and the notification a watched person's arrival raises.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class WatchTests
{
    private const string Person = "usr_watched";

    private static readonly DateTimeOffset Day = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const ModbotPermissions Moderator = ModbotPermissions.WriteNotes | ModbotPermissions.ViewAuditLog;

    private readonly PostgresFixture _db;

    public WatchTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Helpers ────────────────────────────────────────────────────────────────────────────

    /// <summary>Keeps what was raised instead of sending it anywhere.</summary>
    private sealed class RecordingNotifier : INotifier
    {
        private readonly List<Notification> _raised = [];

        public IReadOnlyList<Notification> Raised
        {
            get
            {
                lock (_raised)
                    return [.. _raised];
            }
        }

        public Task<NotificationOutcome> RaiseAsync(Notification notification, CancellationToken ct = default)
        {
            lock (_raised)
                _raised.Add(notification);

            return Task.FromResult(NotificationOutcome.Nothing);
        }
    }

    private async Task<(ReadSurfaceTestHost Host, RecordingNotifier Notifier)> ReadyAsync()
    {
        var notifier = new RecordingNotifier();
        var host = await ReadSurfaceTestHost.StartAsync(_db, configure: s => s.AddSingleton<INotifier>(notifier));
        await host.ResetAsync(Ct);
        host.Clock.UtcNow = Day;

        return (host, notifier);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
        => JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(Ct), Web)!;

    private static Task<HttpResponseMessage> StartAsync(
        ReadSurfaceTestHost host,
        string cookie,
        string reason = "Came back on an alt",
        DateTimeOffset? endsAt = null,
        DateTimeOffset? followUpAt = null,
        string userId = Person,
        string? platform = null)
        => host.PostJsonAsync("/api/watches", new { userId, platform, reason, endsAt, followUpAt }, cookie, Ct);

    private static async Task<List<ModbotEvent>> FactsAsync(ReadSurfaceTestHost host, string type)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        return await db.Events.AsNoTracking()
            .Where(e => e.Type == type)
            .OrderBy(e => e.Id)
            .ToListAsync(Ct);
    }

    private static async Task<bool> FlaggedAsync(ReadSurfaceTestHost host, string person = Person)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var matches = await FlagRules.ReadAsync(
            db, [person], new Dictionary<string, Modbot.Core.Users.TrustRank?>(StringComparer.Ordinal), host.Clock.UtcNow, Ct);

        return matches[person].IsFlagged;
    }

    private static async Task<WatchPassResult> RunPassAsync(ReadSurfaceTestHost host)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WatchPass>().RunOnceAsync(Ct);
    }

    // ── Starting ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Starting_KeepsTheWatch_WritesOneFact_AndFlagsThePerson()
    {
        var (host, _) = await ReadyAsync();
        await using var hosting = host;
        var cookie = await host.SignedInAsync(Moderator, Ct);

        Assert.False(await FlaggedAsync(host));

        var response = await StartAsync(host, cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var watch = await ReadAsync<WatchView>(response);
        Assert.True(watch.Standing);
        Assert.Equal("Came back on an alt", watch.Reason);
        Assert.Equal("VRChat", watch.SubjectPlatform);
        Assert.True(watch.CanChange);

        var fact = Assert.Single(await FactsAsync(host, FactType.WatchStarted));
        Assert.Equal(Person, fact.SubjectId);
        Assert.Equal(FactSource.Manual, fact.Source);
        Assert.Contains("Came back on an alt", fact.Data, StringComparison.Ordinal);

        Assert.True(await FlaggedAsync(host));
    }

    [Fact]
    public async Task Starting_NeedsWriteNotes_AndReadingTheLogIsNotEnough()
    {
        var (host, _) = await ReadyAsync();
        await using var hosting = host;
        var reader = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, (await StartAsync(host, reader)).StatusCode);
        Assert.Empty(await FactsAsync(host, FactType.WatchStarted));
    }

    [Fact]
    public async Task Starting_WithoutAReason_InThePast_OrTwice_IsRefused()
    {
        var (host, _) = await ReadyAsync();
        await using var hosting = host;
        var cookie = await host.SignedInAsync(Moderator, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(host, cookie, reason: "  ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(host, cookie, reason: new string('a', 201))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(host, cookie, endsAt: Day.AddHours(-1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(host, cookie, followUpAt: Day.AddDays(-3))).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await StartAsync(host, cookie, endsAt: Day.AddDays(2), followUpAt: Day.AddDays(5))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await StartAsync(host, cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await StartAsync(host, cookie)).StatusCode);

        // The same id on Discord is a different account, and may be watched on its own.
        Assert.Equal(HttpStatusCode.OK, (await StartAsync(host, cookie, platform: "Discord")).StatusCode);
    }

    [Fact]
    public async Task Starting_AfterOneRanOut_ClosesTheOldOneFirst()
    {
        var (host, _) = await ReadyAsync();
        await using var hosting = host;
        var cookie = await host.SignedInAsync(Moderator, Ct);

        Assert.Equal(HttpStatusCode.OK, (await StartAsync(host, cookie, endsAt: Day.AddDays(1))).StatusCode);

        host.Clock.Advance(TimeSpan.FromDays(2));

        Assert.Equal(HttpStatusCode.OK, (await StartAsync(host, cookie, reason: "Again")).StatusCode);

        var ended = Assert.Single(await FactsAsync(host, FactType.WatchEnded));
        Assert.Contains("expired", ended.Data, StringComparison.Ordinal);
        Assert.Null(ended.ActorId);
    }

    // ── Stopping ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stopping_EndsIt_WritesASecondFact_AndKeepsItInTheHistory()
    {
        var (host, _) = await ReadyAsync();
        await using var hosting = host;
        var cookie = await host.SignedInAsync(Moderator, Ct);

        var watch = await ReadAsync<WatchView>(await StartAsync(host, cookie));

        var stopped = await host.PostJsonAsync($"/api/watches/{watch.Id}/stop", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        Assert.False((await ReadAsync<WatchView>(stopped)).Standing);

        Assert.Single(await FactsAsync(host, FactType.WatchEnded));
        Assert.False(await FlaggedAsync(host));

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await host.PostJsonAsync($"/api/watches/{watch.Id}/stop", null, cookie, Ct)).StatusCode);

        var list = await host.GetJsonAsync<PersonWatchList>($"/api/watches/person?vrchat={Person}", cookie, Ct);
        var only = Assert.Single(list.Watches);
        Assert.False(only.Standing);
        Assert.NotNull(only.EndedByName);
    }

    [Fact]
    public async Task Stopping_SomebodyElsesWatch_WithoutWriteNotes_IsRefused()
    {
        var (host, _) = await ReadyAsync();
        await using var hosting = host;
        var moderator = await host.SignedInAsync(Moderator, Ct);
        var reader = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);

        var watch = await ReadAsync<WatchView>(await StartAsync(host, moderator));

        var list = await host.GetJsonAsync<PersonWatchList>($"/api/watches/person?vrchat={Person}", reader, Ct);
        Assert.False(list.CanWrite);
        Assert.False(Assert.Single(list.Watches).CanChange);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.PostJsonAsync($"/api/watches/{watch.Id}/stop", null, reader, Ct)).StatusCode);
    }

    // ── Following up ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFollowUp_IsDueOnItsDay_IsSaidOnce_AndFollowingUpClearsIt()
    {
        var (host, notifier) = await ReadyAsync();
        await using var hosting = host;
        var cookie = await host.SignedInAsync(Moderator, Ct);

        var watch = await ReadAsync<WatchView>(await StartAsync(host, cookie, followUpAt: Day.AddDays(2)));

        Assert.Empty((await host.GetJsonAsync<WatchList>("/api/watches?due=true", cookie, Ct)).Watches);
        Assert.Equal(0, (await RunPassAsync(host)).Reminded);

        host.Clock.Advance(TimeSpan.FromDays(2));

        var due = Assert.Single((await host.GetJsonAsync<WatchList>("/api/watches?due=true", cookie, Ct)).Watches);
        Assert.True(due.FollowUpDue);

        Assert.Equal(1, (await RunPassAsync(host)).Reminded);
        Assert.Equal(0, (await RunPassAsync(host)).Reminded);

        var reminder = Assert.Single(notifier.Raised);
        Assert.Equal(NotificationKinds.WatchFollowUpDue, reminder.Kind);
        Assert.Contains(watch.Id.ToString(), reminder.SameAsKey, StringComparison.Ordinal);

        // To whoever set it, and nobody else.
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            var setBy = await db.PersonWatches.Where(w => w.Id == watch.Id).Select(w => w.SetByUserId).SingleAsync(Ct);
            Assert.Equal(new[] { setBy }, reminder.Audience.UserIds!);
        }

        var done = await host.PostJsonAsync($"/api/watches/{watch.Id}/followed-up", null, cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);

        var after = await ReadAsync<WatchView>(done);
        Assert.Null(after.FollowUpAt);
        Assert.True(after.Standing);

        Assert.Single(await FactsAsync(host, FactType.WatchFollowedUp));
        Assert.Empty((await host.GetJsonAsync<WatchList>("/api/watches?due=true", cookie, Ct)).Watches);

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await host.PostJsonAsync($"/api/watches/{watch.Id}/followed-up", null, cookie, Ct)).StatusCode);
    }

    // ── Running out ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePass_ClosesAWatchPastItsEndDay_OnItsEndDay_WithNoActor()
    {
        var (host, _) = await ReadyAsync();
        await using var hosting = host;
        var cookie = await host.SignedInAsync(Moderator, Ct);

        var watch = await ReadAsync<WatchView>(await StartAsync(host, cookie, endsAt: Day.AddDays(1)));

        Assert.Equal(0, (await RunPassAsync(host)).Ended);

        host.Clock.Advance(TimeSpan.FromDays(3));

        // Already over for every reader before the pass writes it down.
        Assert.False(await FlaggedAsync(host));

        Assert.Equal(1, (await RunPassAsync(host)).Ended);
        Assert.Equal(0, (await RunPassAsync(host)).Ended);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var row = await db.PersonWatches.AsNoTracking().SingleAsync(w => w.Id == watch.Id, Ct);
        Assert.Equal(watch.EndsAt, row.EndedAt);
        Assert.Null(row.EndedByUserId);

        var ended = Assert.Single(await FactsAsync(host, FactType.WatchEnded));
        Assert.Null(ended.ActorId);
        Assert.Equal(FactSource.Modbot, ended.Source);
    }

    // ── Arriving ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AWatchedPersonArriving_RaisesOneNotification_AndSomebodyElseFlaggedDoesNot()
    {
        var (host, notifier) = await ReadyAsync();
        await using var hosting = host;
        var cookie = await host.SignedInAsync(Moderator, Ct);

        var watch = await ReadAsync<WatchView>(await StartAsync(host, cookie));
        await host.WriteFactAsync(AuditFact(FactType.MemberBanned, "usr_banned", Day.AddDays(-3)), Ct);

        FactRecord Join(string who) => new()
        {
            Type = FactType.InstanceJoined,
            OccurredAt = Day,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = who,
            InstanceId = "12345~group(grp_x)",
            WorldId = "wrld_x",
            Source = FactSource.Companion,
        };

        FactRecord[] arrivals = [Join(Person), Join("usr_banned")];

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var flagged = await FlagRules.ReadAsync(
            db, [Person, "usr_banned"], new Dictionary<string, Modbot.Core.Users.TrustRank?>(StringComparer.Ordinal), Day, Ct);

        Assert.True(flagged["usr_banned"].IsFlagged);
        Assert.Null(flagged["usr_banned"].Watch);

        var raised = await WatchAlerts.RaiseAsync(
            notifier,
            db,
            arrivals,
            flagged,
            new Dictionary<string, string?>(StringComparer.Ordinal) { [Person] = "Ada" },
            Ct);

        Assert.Equal(1, raised);

        var notification = Assert.Single(notifier.Raised);
        Assert.Equal(NotificationKinds.WatchedPersonJoined, notification.Kind);
        Assert.Equal(NotificationSeverity.Warning, notification.Severity);
        Assert.Contains("Ada", notification.Body, StringComparison.Ordinal);
        Assert.Contains("Came back on an alt", notification.Body, StringComparison.Ordinal);
        Assert.Equal($"{NotificationKinds.WatchedPersonJoined}:{watch.Id}:12345~group(grp_x)", notification.SameAsKey);
        Assert.Equal<ModbotPermissions?>(WatchAlerts.Audience, notification.Audience.Permission);
    }
}
