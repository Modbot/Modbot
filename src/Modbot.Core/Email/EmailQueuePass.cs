using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Core.Email;

/// <summary>How the email queue paces itself.</summary>
public sealed class EmailQueueOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The most one pass sends, so a long queue does not hold one scope open for hours.</summary>
    public int SendsPerPass { get; init; } = 50;

    /// <summary>Refusals from the relay before a queued message is marked failed.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>A message left in "sending" this long belongs to a process that stopped.</summary>
    public TimeSpan StuckAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long failed and expired messages stay listed on the settings page.</summary>
    public TimeSpan KeepFinishedFor { get; init; } = TimeSpan.FromDays(3);

    /// <summary>5 minutes, then 15, 45, and so on, never more than 6 hours.</summary>
    public TimeSpan RetryDelay(int attempts)
    {
        var minutes = 5 * Math.Pow(3, Math.Max(0, attempts - 1));
        return TimeSpan.FromMinutes(Math.Min(minutes, 6 * 60));
    }
}

/// <summary>
/// One pass of the email queue: marks what went stale, then sends what the limit has room for,
/// account email first (accounts and access design §4.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Each message is handed to the relay once per try, never in a tight loop.</strong> It
/// is marked <see cref="EmailStates.Sending"/> under the lock before the relay is called, so a
/// second pass cannot pick it up; a refusal puts it back with a later
/// <see cref="EmailQueueEntry.NextAttemptAt"/>, and <see cref="EmailQueueOptions.MaxAttempts"/>
/// refusals mark it failed with the relay's words. A message a stopped process left in "sending"
/// is marked failed, not tried again: it may already have gone out.
/// </para>
/// <para>
/// <strong>A message whose link has expired is never sent.</strong> It is marked expired at the
/// start of every pass, before anything is picked, so a reset link that waited past its 24 hours
/// does not arrive dead.
/// </para>
/// </remarks>
public sealed class EmailQueuePass
{
    private readonly ModbotContext _db;
    private readonly IMailRelay _relay;
    private readonly ISecretProtector _protector;
    private readonly IModbotClock _clock;
    private readonly EmailQueueOptions _options;

    public EmailQueuePass(
        ModbotContext db, IMailRelay relay, ISecretProtector protector, IModbotClock clock, EmailQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);

        _db = db;
        _relay = relay;
        _protector = protector;
        _clock = clock;
        _options = options;
    }

    /// <returns>How many messages went out.</returns>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var sent = 0;

        // With no relay, nothing is tried: a try would only use up the message's attempts.
        var configured = await _relay.IsConfiguredAsync(ct);

        for (var i = 0; i < _options.SendsPerPass; i++)
        {
            var entry = await TakeNextAsync(configured, ct);
            if (entry is null)
                break;

            if (await SendAsync(entry, ct))
                sent++;
        }

        await TidyAsync(ct);
        return sent;
    }

    /// <summary>Under the lock: marks stale messages, then claims the next one if the limit has room.</summary>
    private async Task<EmailQueueEntry?> TakeNextAsync(bool configured, CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({EmailSender.LockKey})", ct);

        var now = _clock.UtcNow;

        await _db.EmailQueue
            .Where(e => e.State == EmailStates.Queued && e.ExpiresAt != null && e.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.State, EmailStates.Expired)
                .SetProperty(e => e.BodyEncrypted, (string?)null)
                .SetProperty(e => e.NextAttemptAt, (DateTimeOffset?)null)
                .SetProperty(e => e.FinishedAt, now), ct);

        var stuckBefore = now - _options.StuckAfter;
        await _db.EmailQueue
            .Where(e => e.State == EmailStates.Sending && e.SentAt < stuckBefore)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.State, EmailStates.Failed)
                .SetProperty(e => e.BodyEncrypted, (string?)null)
                .SetProperty(e => e.LastError, "Modbot stopped while this was being sent.")
                .SetProperty(e => e.FinishedAt, now), ct);

        EmailQueueEntry? entry = null;

        if (configured)
        {
            entry = await EmailSender.Waiting(_db, now).FirstOrDefaultAsync(ct);

            if (entry is not null)
            {
                var limit = await EmailSender.LimitAsync(_db, ct);
                var inWindow = (await EmailSender.SentInWindowAsync(_db, now, ct)).Count;

                // The head of the queue is the kind with the most room, when it is account email,
                // or the only kind waiting. No room for it is no room for anything behind it.
                if (inWindow >= EmailLimit.RoomFor(EmailLimit.KindOf(entry.Kind), limit))
                {
                    entry = null;
                }
                else
                {
                    entry.State = EmailStates.Sending;
                    entry.SentAt = now;
                    await _db.SaveChangesAsync(ct);
                }
            }
        }

        await transaction.CommitAsync(ct);
        return entry;
    }

    private async Task<bool> SendAsync(EmailQueueEntry entry, CancellationToken ct)
    {
        string? body;
        try
        {
            body = _protector.Unprotect(entry.BodyEncrypted);
        }
        catch (CryptographicException)
        {
            body = null;
        }

        SendOutcome outcome;
        if (body is null)
        {
            outcome = SendOutcome.Failed("The stored message could not be read.");
        }
        else
        {
            try
            {
                outcome = await _relay.SendAsync(
                    new EmailMessage(entry.ToAddress, entry.Subject, body, EmailLimit.KindOf(entry.Kind), entry.ExpiresAt),
                    ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                outcome = SendOutcome.Failed("Could not reach the mail server.");
            }
        }

        var now = _clock.UtcNow;

        if (outcome.Sent)
        {
            entry.State = EmailStates.Sent;
            entry.BodyEncrypted = null;
            entry.NextAttemptAt = null;
            entry.LastError = null;
            entry.FinishedAt = now;
        }
        else
        {
            entry.Attempts++;
            entry.LastError = Shorten(outcome.Error ?? "The mail server did not take it.");

            if (body is null || entry.Attempts >= _options.MaxAttempts)
            {
                entry.State = EmailStates.Failed;
                entry.BodyEncrypted = null;
                entry.NextAttemptAt = null;
                entry.FinishedAt = now;
            }
            else
            {
                // Back in line, but not before its next try. It no longer counts against the limit.
                entry.State = EmailStates.Queued;
                entry.SentAt = null;
                entry.NextAttemptAt = now + _options.RetryDelay(entry.Attempts);
            }

            // The id and kind only: the address is a person's, and the body carries the link.
            Log.Warning(
                "A queued {Kind} email {Id} was refused (try {Attempts} of {Max}): {Error}",
                entry.Kind, entry.Id, entry.Attempts, _options.MaxAttempts, entry.LastError);
        }

        await _db.SaveChangesAsync(CancellationToken.None);
        return outcome.Sent;
    }

    /// <summary>Drops sent rows once they no longer count, and failed or expired ones after a few days.</summary>
    private async Task TidyAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var windowStart = now - EmailLimit.Window;
        var keepFrom = now - _options.KeepFinishedFor;

        await _db.EmailQueue
            .Where(e => e.State == EmailStates.Sent && e.SentAt <= windowStart)
            .ExecuteDeleteAsync(ct);

        await _db.EmailQueue
            .Where(e => (e.State == EmailStates.Failed || e.State == EmailStates.Expired) && e.FinishedAt <= keepFrom)
            .ExecuteDeleteAsync(ct);
    }

    private static string Shorten(string text) => text.Length <= 512 ? text : text[..512];
}

/// <summary>Runs <see cref="EmailQueuePass"/> on a timer, one scope per pass.</summary>
public sealed class EmailQueueService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly EmailQueueOptions _options;

    public EmailQueueService(IServiceScopeFactory scopes, EmailQueueOptions options)
    {
        _scopes = scopes;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<EmailQueuePass>().RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                Log.Error(e, "An email queue pass failed; trying again shortly");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
