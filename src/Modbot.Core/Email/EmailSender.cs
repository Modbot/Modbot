using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Time;

namespace Modbot.Core.Email;

/// <summary>
/// <see cref="IEmailSender"/>: every email Modbot sends is counted here against the daily limit,
/// and what does not fit waits in the email queue (accounts and access design §4.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Decided under a lock, sent outside it.</strong> Whether a message goes now or waits is
/// decided inside a transaction holding a PostgreSQL advisory lock, and a message that goes now
/// is written as <see cref="EmailStates.Sending"/> before the lock is let go -- so two requests
/// at once cannot both take the last place under the limit. The relay is then called with no
/// lock held, because a slow relay would otherwise hold up every other email for up to its
/// timeout.
/// </para>
/// <para>
/// <strong>A message sent straight away that the relay refuses is not queued.</strong> The caller
/// gets the refusal, as it did before the limit existed: the test message has to show the
/// relay's own words, and forgot-password records them. Only messages the limit queued are
/// tried again later (<see cref="EmailQueuePass"/>).
/// </para>
/// </remarks>
public sealed class EmailSender : IEmailSender
{
    /// <summary>"MOD" "MAIL". Held while deciding what goes next.</summary>
    internal const long LockKey = 0x4D4F44_4D41494C;

    private readonly ModbotContext _db;
    private readonly IMailRelay _relay;
    private readonly ISecretProtector _protector;
    private readonly IModbotClock _clock;

    public EmailSender(ModbotContext db, IMailRelay relay, ISecretProtector protector, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _relay = relay;
        _protector = protector;
        _clock = clock;
    }

    public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => _relay.IsConfiguredAsync(ct);

    public async Task<SendOutcome> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Nothing is queued for a relay that is not there: it would sit in the queue forever and
        // the caller would be told it is on its way.
        if (!await _relay.IsConfiguredAsync(ct))
            return SendOutcome.NotConfigured("Email");

        EmailQueueEntry entry;

        var own = await LockAsync(_db, ct);
        try
        {
            var now = _clock.UtcNow;
            var (queue, at) = await PlanAsync(_db, message.Kind, now, ct);

            entry = new EmailQueueEntry
            {
                Kind = EmailLimit.NameOf(message.Kind),
                ToAddress = message.To,
                Subject = message.Subject,
                QueuedAt = now,
                ExpiresAt = message.ExpiresAt,
            };

            if (queue)
            {
                entry.State = EmailStates.Queued;
                entry.BodyEncrypted = _protector.Protect(message.Body);
            }
            else
            {
                entry.State = EmailStates.Sending;
                entry.SentAt = now;
            }

            _db.EmailQueue.Add(entry);
            await _db.SaveChangesAsync(ct);

            if (own is not null)
                await own.CommitAsync(ct);

            if (queue)
                return SendOutcome.Held(at);
        }
        finally
        {
            if (own is not null)
                await own.DisposeAsync();
        }

        SendOutcome outcome;
        try
        {
            outcome = await _relay.SendAsync(message, ct);
        }
        catch
        {
            // Whatever the relay threw, this message did not take a place under the limit.
            _db.EmailQueue.Remove(entry);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        if (outcome.Sent)
        {
            entry.State = EmailStates.Sent;
            entry.FinishedAt = _clock.UtcNow;
        }
        else
        {
            _db.EmailQueue.Remove(entry);
        }

        await _db.SaveChangesAsync(CancellationToken.None);
        return outcome;
    }

    public async Task<bool> WouldQueueAsync(EmailKind kind, CancellationToken ct = default)
    {
        if (!await _relay.IsConfiguredAsync(ct))
            return false;

        var (queue, _) = await PlanAsync(_db, kind, _clock.UtcNow, ct);
        return queue;
    }

    /// <summary>
    /// Starts a transaction, unless the caller already has one, and takes the email lock in it.
    /// </summary>
    /// <returns>The transaction this started, for the caller to commit; null when it joined one.</returns>
    internal static async Task<IDbContextTransaction?> LockAsync(ModbotContext db, CancellationToken ct)
    {
        var own = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({LockKey})", ct);
        return own;
    }

    /// <summary>The saved limit, or the default before the settings row exists.</summary>
    internal static async Task<int> LimitAsync(ModbotContext db, CancellationToken ct)
        => await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => (int?)s.EmailLimitPer24Hours)
            .FirstOrDefaultAsync(ct) ?? EmailLimit.Default;

    /// <summary>When each email in the last 24 hours went out, sends still in progress included.</summary>
    internal static async Task<List<DateTimeOffset>> SentInWindowAsync(ModbotContext db, DateTimeOffset now, CancellationToken ct)
    {
        var start = now - EmailLimit.Window;

        var times = await db.EmailQueue.AsNoTracking()
            .Where(e => (e.State == EmailStates.Sent || e.State == EmailStates.Sending) && e.SentAt > start)
            .Select(e => e.SentAt)
            .ToListAsync(ct);

        return [.. times.Select(t => t!.Value)];
    }

    /// <summary>The queued messages that can go when there is room, account email first, then oldest first.</summary>
    internal static IQueryable<EmailQueueEntry> Waiting(ModbotContext db, DateTimeOffset now)
        => db.EmailQueue
            .Where(e => e.State == EmailStates.Queued
                        && (e.NextAttemptAt == null || e.NextAttemptAt <= now)
                        && (e.ExpiresAt == null || e.ExpiresAt > now))
            .OrderBy(e => e.Kind == EmailKinds.Account ? 0 : 1)
            .ThenBy(e => e.QueuedAt)
            .ThenBy(e => e.Id);

    private static async Task<(bool Queue, DateTimeOffset? At)> PlanAsync(
        ModbotContext db, EmailKind kind, DateTimeOffset now, CancellationToken ct)
    {
        var limit = await LimitAsync(db, ct);
        var sent = await SentInWindowAsync(db, now, ct);

        // Account email waits only behind account email; other email waits behind everything.
        var waiting = Waiting(db, now).AsNoTracking();
        if (kind == EmailKind.Account)
            waiting = waiting.Where(e => e.Kind == EmailKinds.Account);

        var ahead = await waiting.Select(e => e.Kind).ToListAsync(ct);

        return EmailLimit.WhenCanSend(sent, [.. ahead.Select(EmailLimit.KindOf)], kind, limit, now);
    }
}
