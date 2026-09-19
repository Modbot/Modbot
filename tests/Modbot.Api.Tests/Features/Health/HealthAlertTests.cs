using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Health.Alerts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Email;
using Modbot.Core.Notifications;
using Modbot.Core.Time;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Health;

/// <summary>
/// Modbot telling somebody about its own health: said once, repeated after the quiet time, and
/// said to be over.
/// </summary>
/// <remarks>
/// <para>
/// Against the real database, because the whole point of the design is that the state is a row: a
/// restart in the middle of a problem must not start the emails over.
/// </para>
/// <para>
/// These run the checker and then the notification send pass, which is what the host does a moment
/// later. The assertions are still about email arriving, on purpose: the health checker moved onto
/// the notification pipeline (notifications design, 2026-09-18) and the thing that must not have
/// changed is what lands in somebody's inbox.
/// </para>
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class HealthAlertTests
{
    private readonly PostgresFixture _db;

    public HealthAlertTests(PostgresFixture db) => _db = db;

    private static readonly DateTimeOffset Start = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Keeps what would have been sent.</summary>
    private sealed class StubSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<SendOutcome> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.FromResult(SendOutcome.Ok);
        }

        public Task<bool> WouldQueueAsync(EmailKind kind, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class MovingClock(DateTimeOffset now) : IModbotClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    /// <summary>
    /// A deployment with the email check on, one recipient, and an email that has failed. Every
    /// other check is off, so the test is about the state machine and not about the sources.
    /// </summary>
    private async Task<(ModbotContext Db, MovingClock Clock, StubSender Sender)> ReadyAsync(CancellationToken ct)
    {
        var db = _db.NewContext();

        await db.HealthWatches.ExecuteDeleteAsync(ct);
        await db.HealthAlertRecipients.ExecuteDeleteAsync(ct);
        await db.HealthAlertSettings.ExecuteDeleteAsync(ct);
        await db.EmailQueue.ExecuteDeleteAsync(ct);
        await db.NotificationSends.ExecuteDeleteAsync(ct);
        await db.NotificationsForPeople.ExecuteDeleteAsync(ct);
        await db.Notifications.ExecuteDeleteAsync(ct);
        await db.NotificationChoices.ExecuteDeleteAsync(ct);
        // Email became unique per account, so the user this method creates below must not be
        // left behind for the next test in the file to collide with.
        await db.Users.Where(u => u.Email == "keeper@example.com").ExecuteDeleteAsync(ct);

        var user = new ModbotUser
        {
            Id = Guid.NewGuid(),
            Username = $"keeper_{Guid.NewGuid():N}",
            UsernameNormalized = $"KEEPER_{Guid.NewGuid():N}".ToUpperInvariant(),
            Email = "keeper@example.com",
            PasswordHash = "x",
        };

        db.Users.Add(user);
        db.HealthAlertRecipients.Add(new HealthAlertRecipient { UserId = user.Id });
        db.HealthWatches.Add(new HealthWatch { Check = HealthChecks.Email, On = true });
        db.HealthAlertSettings.Add(new HealthAlertSettings { Id = 1, QuietHours = 6 });

        await db.SaveChangesAsync(ct);

        return (db, new MovingClock(Start), new StubSender());
    }

    private static IEnumerable<INotificationChannel> Channels(StubSender sender) =>
        [new EmailNotificationChannel(sender)];

    private static HealthAlertChecker Checker(ModbotContext db, MovingClock clock, StubSender sender) =>
        new(db, clock, new Notifier(db, clock, Channels(sender)));

    /// <summary>One pass of the checker, then the send pass that carries what it raised.</summary>
    private static async Task<HealthAlertRun> RunAsync(
        ModbotContext db, MovingClock clock, StubSender sender, CancellationToken ct)
    {
        var run = await Checker(db, clock, sender).RunOnceAsync(ct);
        await new NotificationPass(db, clock, Channels(sender)).RunOnceAsync(ct);

        return run;
    }

    /// <summary>Puts a failed email in the queue, which is what the Email check looks at.</summary>
    private static void Fail(ModbotContext db, DateTimeOffset at) =>
        db.EmailQueue.Add(new EmailQueueEntry
        {
            Id = Guid.NewGuid(),
            ToAddress = "someone@example.com",
            Subject = "test",
            Kind = EmailKinds.Other,
            State = EmailStates.Failed,
            QueuedAt = at,
        });

    [Fact]
    public async Task AProblemIsSaidOnceAndRepeatedOnlyAfterTheQuietTime()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, clock, sender) = await ReadyAsync(ct);
        await using var _ = db;

        Fail(db, Start);
        await db.SaveChangesAsync(ct);

        var first = await RunAsync(db, clock, sender, ct);

        Assert.Equal([HealthChecks.Email], first.Problems);
        Assert.Single(sender.Sent);
        Assert.Equal("keeper@example.com", sender.Sent[0].To);

        // Inside the quiet time: nothing more.
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Empty((await RunAsync(db, clock, sender, ct)).Problems);
        Assert.Single(sender.Sent);

        // Past it: a problem nobody fixed is still a problem.
        clock.Advance(TimeSpan.FromHours(6));
        Assert.Equal([HealthChecks.Email], (await RunAsync(db, clock, sender, ct)).Problems);
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task ARecoveryIsSaidOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, clock, sender) = await ReadyAsync(ct);
        await using var _ = db;

        Fail(db, Start);
        await db.SaveChangesAsync(ct);

        Assert.Single((await RunAsync(db, clock, sender, ct)).Problems);

        await db.EmailQueue.ExecuteDeleteAsync(ct);
        clock.Advance(TimeSpan.FromMinutes(10));

        var back = await RunAsync(db, clock, sender, ct);

        Assert.Equal([HealthChecks.Email], back.Recoveries);
        Assert.Equal(2, sender.Sent.Count);
        Assert.Contains("working again", sender.Sent[1].Subject, StringComparison.Ordinal);

        // And nothing after that.
        clock.Advance(TimeSpan.FromHours(12));
        var quiet = await RunAsync(db, clock, sender, ct);

        Assert.Empty(quiet.Problems);
        Assert.Empty(quiet.Recoveries);
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task ACheckThatIsOffSaysNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, clock, sender) = await ReadyAsync(ct);
        await using var _ = db;

        await db.HealthWatches.ExecuteUpdateAsync(s => s.SetProperty(w => w.On, false), ct);

        Fail(db, Start);
        await db.SaveChangesAsync(ct);

        var run = await RunAsync(db, clock, sender, ct);

        Assert.Empty(run.Problems);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task TheStateSurvivesARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, clock, sender) = await ReadyAsync(ct);
        await using var _ = db;

        Fail(db, Start);
        await db.SaveChangesAsync(ct);

        Assert.Single((await RunAsync(db, clock, sender, ct)).Problems);

        // A different context and a different sender: what a restart looks like from here.
        await using var again = _db.NewContext();
        var afterRestart = new StubSender();

        clock.Advance(TimeSpan.FromMinutes(5));
        var run = await RunAsync(again, clock, afterRestart, ct);

        Assert.Empty(run.Problems);
        Assert.Empty(afterRestart.Sent);

        var watch = await again.HealthWatches.AsNoTracking().SingleAsync(w => w.Check == HealthChecks.Email, ct);
        Assert.True(watch.Problem);
        Assert.Equal(Start, watch.Since);
    }

    [Fact]
    public async Task NobodyChosenMeansNoEmailButTheStateIsStillKept()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, clock, sender) = await ReadyAsync(ct);
        await using var _ = db;

        await db.HealthAlertRecipients.ExecuteDeleteAsync(ct);

        Fail(db, Start);
        await db.SaveChangesAsync(ct);

        var run = await RunAsync(db, clock, sender, ct);

        // Still raised -- the record of what went wrong does not depend on anybody hearing it --
        // and sent to nobody, because nobody was chosen.
        Assert.Equal([HealthChecks.Email], run.Problems);
        Assert.Equal(1, run.Raised);
        Assert.Empty(sender.Sent);

        var watch = await db.HealthWatches.AsNoTracking().SingleAsync(w => w.Check == HealthChecks.Email, ct);
        Assert.True(watch.Problem);
    }
}
