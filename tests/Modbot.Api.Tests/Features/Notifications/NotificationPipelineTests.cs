using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Email;
using Modbot.Core.Notifications;
using Modbot.Core.Time;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Notifications;

/// <summary>
/// The notification pipeline end to end (foundation §4.5): routing, one person's own settings,
/// saying a thing once, the daily summary, and the critical nobody could receive.
/// </summary>
/// <remarks>
/// Against the real database, because every one of those rules is a row that has to survive a
/// restart. A pipeline that forgets what it already said when the process restarts says everything
/// again, which is the failure §4.5.1 is about.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class NotificationPipelineTests
{
    private readonly PostgresFixture _db;

    public NotificationPipelineTests(PostgresFixture db) => _db = db;

    private static readonly DateTimeOffset Start = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private sealed class MovingClock(DateTimeOffset now) : IModbotClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    /// <summary>Keeps what would have been emailed, and can be told it is not set up.</summary>
    private sealed class StubSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public bool Configured { get; set; } = true;

        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(Configured);

        public Task<SendOutcome> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.FromResult(SendOutcome.Ok);
        }

        public Task<bool> WouldQueueAsync(EmailKind kind, CancellationToken ct = default) => Task.FromResult(false);
    }

    /// <summary>Keeps what would have been sent as a direct message.</summary>
    private sealed class StubMessenger : IDiscordMessenger
    {
        public List<(string To, string Text)> Sent { get; } = [];

        public bool Configured { get; set; } = true;

        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(Configured);

        public Task<SendOutcome> SendDirectMessageAsync(
            string discordUserId, string text, CancellationToken ct = default)
        {
            Sent.Add((discordUserId, text));
            return Task.FromResult(SendOutcome.Ok);
        }
    }

    private sealed record Rig(
        ModbotContext Db, MovingClock Clock, StubSender Email, StubMessenger Discord, ModbotUser Person)
    {
        public IEnumerable<INotificationChannel> Channels =>
            [new EmailNotificationChannel(Email), new DiscordNotificationChannel(Discord)];

        public INotifier Notifier => new Notifier(Db, Clock, Channels);

        public NotificationPass Pass => new(Db, Clock, Channels);

        public async Task<NotificationOutcome> RaiseAsync(Notification note, CancellationToken ct)
        {
            var outcome = await Notifier.RaiseAsync(note, ct);
            await Pass.RunOnceAsync(ct);

            return outcome;
        }
    }

    /// <summary>One administrator with an address and a linked Discord account, and clean tables.</summary>
    private async Task<Rig> ReadyAsync(CancellationToken ct, bool withEmail = true, bool withDiscord = true)
    {
        var db = _db.NewContext();

        await db.NotificationSends.ExecuteDeleteAsync(ct);
        await db.NotificationsForPeople.ExecuteDeleteAsync(ct);
        await db.Notifications.ExecuteDeleteAsync(ct);
        await db.NotificationChoices.ExecuteDeleteAsync(ct);
        await db.NotificationSettings.ExecuteDeleteAsync(ct);

        var tag = Guid.NewGuid().ToString("N");

        var person = new ModbotUser
        {
            Id = Guid.NewGuid(),
            Username = $"keeper_{tag}",
            UsernameNormalized = $"KEEPER_{tag}".ToUpperInvariant(),
            Email = withEmail ? $"keeper_{tag}@example.com" : null,
            DiscordUserId = withDiscord ? $"discord_{tag}" : null,
            PasswordHash = "x",
        };

        db.Users.Add(person);
        db.UserRoles.Add(new ModbotUserRole { UserId = person.Id, RoleId = BuiltInRoles.AdministratorId });

        await db.SaveChangesAsync(ct);

        return new Rig(db, new MovingClock(Start), new StubSender(), new StubMessenger(), person);
    }

    private static Notification Note(
        NotificationSeverity severity, NotificationAudience audience, string sameAs = "test.thing") =>
        new("test.thing", severity, "Something happened", "A sentence about it.", audience)
        {
            SameAs = sameAs,
        };

    // ── Routing by severity ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Critical reaches every channel. Email's default is critical only and Discord's is critical
    /// and warnings, so a critical is the one severity both take with nothing configured.
    /// </summary>
    [Fact]
    public async Task ACriticalReachesEveryChannel()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ReadyAsync(ct);
        await using var _ = rig.Db;

        await rig.RaiseAsync(Note(NotificationSeverity.Critical, NotificationAudience.Administrators), ct);

        Assert.Single(rig.Email.Sent);
        Assert.Single(rig.Discord.Sent);
        Assert.Equal(rig.Person.Email, rig.Email.Sent[0].To);
        Assert.Equal(rig.Person.DiscordUserId, rig.Discord.Sent[0].To);
    }

    /// <summary>A warning interrupts on Discord and waits for email's daily summary.</summary>
    [Fact]
    public async Task AWarningGoesToDiscordAndNotToEmail()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ReadyAsync(ct);
        await using var _ = rig.Db;

        await rig.RaiseAsync(Note(NotificationSeverity.Warning, NotificationAudience.Administrators), ct);

        Assert.Single(rig.Discord.Sent);
        Assert.Empty(rig.Email.Sent);
    }

    /// <summary>Information never interrupts anybody, on any channel.</summary>
    [Fact]
    public async Task InformationInterruptsNobody()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ReadyAsync(ct);
        await using var _ = rig.Db;

        await rig.RaiseAsync(Note(NotificationSeverity.Information, NotificationAudience.Administrators), ct);

        Assert.Empty(rig.Email.Sent);
        Assert.Empty(rig.Discord.Sent);
    }

    /// <summary>Somebody who wants everything by email gets it.</summary>
    [Fact]
    public async Task APersonCanAskForEverythingOnAChannel()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ReadyAsync(ct);
        await using var _ = rig.Db;

        rig.Db.NotificationChoices.Add(new NotificationChoice
        {
            UserId = rig.Person.Id,
            Channel = NotificationChannels.Email,
            Level = NotificationLevels.Everything,
        });

        await rig.Db.SaveChangesAsync(ct);

        await rig.RaiseAsync(Note(NotificationSeverity.Information, NotificationAudience.Administrators), ct);

        Assert.Single(rig.Email.Sent);
    }

    // ── A setting that silences a channel ────────────────────────────────────────────────────

    [Fact]
    public async Task ASettingOfOffSilencesThatChannelAndLeavesTheOther()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ReadyAsync(ct);
        await using var _ = rig.Db;

        rig.Db.NotificationChoices.Add(new NotificationChoice
        {
            UserId = rig.Person.Id,
            Channel = NotificationChannels.Discord,
            Level = NotificationLevels.Off,
        });

        await rig.Db.SaveChangesAsync(ct);

        await rig.RaiseAsync(Note(NotificationSeverity.Critical, NotificationAudience.Administrators), ct);

        Assert.Empty(rig.Discord.Sent);
        Assert.Single(rig.Email.Sent);
    }

    // ── Saying a thing once ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The rule the whole pipeline exists for: a thing going wrong every minute is one message and
    /// then a number.
    /// </summary>
    [Fact]
    public async Task TheSameThingInsideTheQuietTimeIsCountedNotSent()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ReadyAsync(ct);
        await using var _ = rig.Db;

        var note = Note(NotificationSeverity.Critical, NotificationAudience.Administrators);

        await rig.RaiseAsync(note, ct);

        for (var i = 0; i < 20; i++)
        {
            rig.Clock.Advance(TimeSpan.FromMinutes(1));
            var again = await rig.RaiseAsync(note, ct);

            Assert.True(again.Repeat);
        }

        Assert.Single(rig.Email.Sent);

        var record = await rig.Db.Notifications.AsNoTracking().SingleAsync(n => n.SameAs == "test.thing", ct);
        Assert.Equal(20, record.Repeats);
    }

    [Fact]
    public async Task TheSameThingPastTheQuietTimeIsSentAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ReadyAsync(ct);
        await using var _ = rig.Db;

        var note = Note(NotificationSeverity.Critical, NotificationAudience.Administrators);

        await rig.RaiseAsync(note, ct);

        rig.Clock.Advance(TimeSpan.FromHours(NotificationSettings.DefaultQuietHours));
        var again = await rig.RaiseAsync(note, ct);

        Assert.False(again.Repeat);
        Assert.Equal(2, rig.Email.Sent.Count);
    }

    /// <summary>Two different things are never each other's repeat, however close together.</summary>
    [Fact]
    public async Task ADifferentThingIsNotARepeat()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ReadyAsync(ct);
        await using var _ = rig.Db;

        await rig.RaiseAsync(Note(NotificationSeverity.Critical, NotificationAudience.Administrators, "thing.one"), ct);
        await rig.RaiseAsync(Note(NotificationSeverity.Critical, NotificationAudience.Administrators, "thing.two"), ct);

        Assert.Equal(2, rig.Email.Sent.Count);
    }

    /// <summary>A warning that has become critical is news, quiet time or not.</summary>
    [Fact]
    public async Task SomethingThatGotWorseInsideTheQuietTimeStillGoesOut()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ReadyAsync(ct);
        await using var _ = rig.Db;

        await rig.RaiseAsync(Note(NotificationSeverity.Warning, NotificationAudience.Administrators), ct);
        Assert.Single(rig.Discord.Sent);

        rig.Clock.Advance(TimeSpan.FromMinutes(1));

        var worse = await rig.RaiseAsync(Note(NotificationSeverity.Critical, NotificationAudience.Administrators), ct);

        Assert.False(worse.Repeat);
        Assert.Single(rig.Email.Sent);
    }

    // ── The critical nobody could receive ────────────────────────────────────────────────────

    /// <summary>
    /// Foundation §4.5.3's last sentence. Nothing can reach this person, so the critical is on
    /// their record waiting for them rather than gone.
    /// </summary>
    [Fact]
    public async Task ACriticalNobodyCanReceiveIsWaitingAtNextSignIn()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ReadyAsync(ct, withEmail: false, withDiscord: false);
        await using var _ = rig.Db;

        var outcome = await rig.RaiseAsync(Note(NotificationSeverity.Critical, NotificationAudience.Administrators), ct);

        Assert.True(outcome.NobodyCouldReceive);
        Assert.Equal(0, outcome.Sending);

        var forPerson = await rig.Db.NotificationsForPeople.AsNoTracking()
            .SingleAsync(p => p.UserId == rig.Person.Id, ct);

        Assert.True(forPerson.Waiting);
        Assert.Null(forPerson.SeenAt);
    }

    /// <summary>A daily summary is not receiving a critical. Tomorrow morning is not now.</summary>
    [Fact]
    public async Task ACriticalHeldOnlyForASummaryStillCountsAsUnreceived()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ReadyAsync(ct, withDiscord: false);
        await using var _ = rig.Db;

        rig.Db.NotificationChoices.Add(new NotificationChoice
        {
            UserId = rig.Person.Id,
            Channel = NotificationChannels.Email,
            Level = NotificationLevels.Off,
            DailySummary = true,
        });

        await rig.Db.SaveChangesAsync(ct);

        await rig.RaiseAsync(Note(NotificationSeverity.Critical, NotificationAudience.Administrators), ct);

        var forPerson = await rig.Db.NotificationsForPeople.AsNoTracking()
            .SingleAsync(p => p.UserId == rig.Person.Id, ct);

        Assert.True(forPerson.Waiting);
    }

    /// <summary>A critical that did reach somebody is not waiting for anybody.</summary>
    [Fact]
    public async Task ACriticalThatArrivedIsNotWaiting()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ReadyAsync(ct);
        await using var _ = rig.Db;

        await rig.RaiseAsync(Note(NotificationSeverity.Critical, NotificationAudience.Administrators), ct);

        var forPerson = await rig.Db.NotificationsForPeople.AsNoTracking()
            .SingleAsync(p => p.UserId == rig.Person.Id, ct);

        Assert.False(forPerson.Waiting);
    }

    /// <summary>A warning nobody can receive is not held for a sign-in. Only criticals are.</summary>
    [Fact]
    public async Task AWarningNobodyCanReceiveIsNotHeldForASignIn()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ReadyAsync(ct, withEmail: false, withDiscord: false);
        await using var _ = rig.Db;

        await rig.RaiseAsync(Note(NotificationSeverity.Warning, NotificationAudience.Administrators), ct);

        var forPerson = await rig.Db.NotificationsForPeople.AsNoTracking()
            .SingleAsync(p => p.UserId == rig.Person.Id, ct);

        Assert.False(forPerson.Waiting);
    }

    // ── The daily summary ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Information collects and goes out as one message a day, not as one message each.
    /// </summary>
    [Fact]
    public async Task TheDailySummaryIsOneMessageForEverythingHeldBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ReadyAsync(ct, withDiscord: false);
        await using var _ = rig.Db;

        for (var i = 0; i < 3; i++)
        {
            await rig.RaiseAsync(
                Note(NotificationSeverity.Information, NotificationAudience.Administrators, $"thing.{i}"), ct);
        }

        // Nothing yet: a day has not passed.
        Assert.Empty(rig.Email.Sent);

        rig.Clock.Advance(TimeSpan.FromHours(24));
        await rig.Pass.RunOnceAsync(ct);

        var summary = Assert.Single(rig.Email.Sent);
        Assert.Contains("3 things", summary.Subject, StringComparison.Ordinal);

        // And nothing is sent twice.
        rig.Clock.Advance(TimeSpan.FromHours(24));
        await rig.Pass.RunOnceAsync(ct);

        Assert.Single(rig.Email.Sent);
    }

    /// <summary>A second summary is a day after the first, not a day after the first item.</summary>
    [Fact]
    public async Task ASecondSummaryWaitsADayAfterTheFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ReadyAsync(ct, withDiscord: false);
        await using var _ = rig.Db;

        await rig.RaiseAsync(Note(NotificationSeverity.Information, NotificationAudience.Administrators, "one"), ct);

        rig.Clock.Advance(TimeSpan.FromHours(24));
        await rig.Pass.RunOnceAsync(ct);
        Assert.Single(rig.Email.Sent);

        await rig.RaiseAsync(Note(NotificationSeverity.Information, NotificationAudience.Administrators, "two"), ct);

        rig.Clock.Advance(TimeSpan.FromHours(23));
        await rig.Pass.RunOnceAsync(ct);
        Assert.Single(rig.Email.Sent);

        rig.Clock.Advance(TimeSpan.FromHours(1));
        await rig.Pass.RunOnceAsync(ct);
        Assert.Equal(2, rig.Email.Sent.Count);
    }

    // ── Availability ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A channel the deployment has not set up is skipped silently and never fails anything.
    /// </summary>
    [Fact]
    public async Task AChannelThatIsNotSetUpIsSkippedWithoutFailing()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ReadyAsync(ct);
        await using var _ = rig.Db;

        rig.Email.Configured = false;

        var outcome = await rig.RaiseAsync(Note(NotificationSeverity.Critical, NotificationAudience.Administrators), ct);

        Assert.Empty(rig.Email.Sent);
        Assert.Single(rig.Discord.Sent);
        Assert.Equal(1, outcome.Sending);
        Assert.False(outcome.NobodyCouldReceive);
    }

    /// <summary>Notifications by email are "other" email, so the room kept for account email holds.</summary>
    [Fact]
    public async Task NotificationEmailNeverEatsTheRoomKeptForAccountEmail()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ReadyAsync(ct);
        await using var _ = rig.Db;

        await rig.RaiseAsync(Note(NotificationSeverity.Critical, NotificationAudience.Administrators), ct);

        Assert.Equal(EmailKind.Other, Assert.Single(rig.Email.Sent).Kind);
    }

    // ── Who hears about it ───────────────────────────────────────────────────────────────────

    /// <summary>A named list reaches exactly those accounts.</summary>
    [Fact]
    public async Task ANamedListReachesOnlyThoseAccounts()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ReadyAsync(ct);
        await using var _ = rig.Db;

        await rig.RaiseAsync(Note(NotificationSeverity.Critical, NotificationAudience.These([rig.Person.Id])), ct);

        var people = await rig.Db.NotificationsForPeople.AsNoTracking().ToListAsync(ct);

        Assert.Equal(rig.Person.Id, Assert.Single(people).UserId);
    }

    /// <summary>An account nobody can sign in to is not somebody who can be told anything.</summary>
    [Fact]
    public async Task ADisabledAccountIsNotInTheAudience()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await ReadyAsync(ct);
        await using var _ = rig.Db;

        rig.Person.IsDisabled = true;
        await rig.Db.SaveChangesAsync(ct);

        var outcome = await rig.RaiseAsync(
            Note(NotificationSeverity.Critical, NotificationAudience.Administrators), ct);

        Assert.Equal(0, outcome.People);
        Assert.Empty(rig.Email.Sent);
    }
}
