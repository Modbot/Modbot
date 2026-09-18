using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Core.Notifications;

/// <summary>
/// The notification pipeline (foundation §4.5).
/// </summary>
/// <remarks>
/// <para>
/// Raising writes rows and returns. Nothing is sent here: the channels are tried by
/// <see cref="NotificationPass"/> a moment later, so a ban does not wait on SMTP and does not fail
/// because SMTP is down (§4.5.3).
/// </para>
/// <para>
/// <strong>Everybody addressed gets a row whether or not anything can reach them.</strong> That is
/// what makes the last sentence of §4.5.3 possible: a critical notification that went out on no
/// channel is not lost, it is sitting on the person's record waiting for them to sign in.
/// </para>
/// </remarks>
public sealed class Notifier : INotifier
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IReadOnlyList<INotificationChannel> _channels;
    private readonly ILogger _log = Log.Logger.ForContext(LogArea.Name, LogArea.Setup);

    public Notifier(ModbotContext db, IModbotClock clock, IEnumerable<INotificationChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(channels);

        _db = db;
        _clock = clock;
        _channels = channels.Where(c => NotificationChannels.IsKnown(c.Name)).ToList();
    }

    public async Task<NotificationOutcome> RaiseAsync(Notification notification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var now = _clock.UtcNow;
        var settings = await SettingsAsync(ct).ConfigureAwait(false);
        var quiet = notification.Quiet ?? NotificationRouting.QuietTime(settings);
        var key = notification.SameAsKey;

        var last = await _db.Notifications
            .Where(n => n.SameAs == key)
            .OrderByDescending(n => n.LastAt)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (last is not null
            && NotificationRouting.IsRepeat(
                NotificationRouting.SeverityOf(last.Severity), last.LastAt, notification.Severity, now, quiet))
        {
            last.LastAt = now;
            last.Repeats++;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            return new NotificationOutcome(last.Id, Repeat: true, People: 0, Sending: 0, ForSummary: 0, false);
        }

        var record = new NotificationRecord
        {
            Kind = notification.Kind,
            Severity = NotificationRouting.NameOf(notification.Severity),
            SameAs = key,
            Title = Short(notification.Title, 256),
            Body = Short(notification.Body, 2000),
            Link = notification.Link,
            FirstAt = now,
            LastAt = now,
        };

        _db.Notifications.Add(record);

        var people = await AudienceAsync(notification.Audience, ct).ConfigureAwait(false);
        var choices = await ChoicesAsync(people.Select(p => p.Id).ToList(), ct).ConfigureAwait(false);

        var sending = 0;
        var forSummary = 0;

        foreach (var person in people)
        {
            var forPerson = new NotificationForPerson
            {
                NotificationId = record.Id,
                UserId = person.Id,
            };

            _db.NotificationsForPeople.Add(forPerson);

            var reached = 0;

            if (settings.On)
            {
                foreach (var channel in _channels)
                {
                    var choice = choices.GetValueOrDefault((person.Id, channel.Name));
                    var (level, dailySummary) = choice is null
                        ? NotificationChannels.Default(channel.Name)
                        : (choice.Level, choice.DailySummary);

                    var nowish = NotificationRouting.GoesNow(notification.Severity, level);
                    var later = NotificationRouting.GoesInSummary(notification.Severity, level, dailySummary);

                    if (!nowish && !later)
                        continue;

                    if (!await CanReachAsync(channel, person, ct).ConfigureAwait(false))
                        continue;

                    _db.NotificationSends.Add(new NotificationSend
                    {
                        NotificationId = record.Id,
                        UserId = person.Id,
                        Channel = channel.Name,
                        State = nowish ? NotificationSendStates.Waiting : NotificationSendStates.ForSummary,
                        QueuedAt = now,
                    });

                    if (nowish)
                    {
                        sending++;
                        reached++;
                    }
                    else
                    {
                        forSummary++;
                    }
                }
            }

            // The rule that makes §4.5 worth having: a critical alert nobody can receive is the
            // same as no alerting at all, so it waits on the account rather than being dropped.
            // Something held only for a daily summary does not count as reaching them -- a critical
            // that arrives tomorrow morning is not a critical.
            if (notification.Severity == NotificationSeverity.Critical && reached == 0)
                forPerson.Waiting = true;
        }

        record.NobodyCouldReceive = notification.Severity == NotificationSeverity.Critical && sending == 0;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (record.NobodyCouldReceive)
        {
            _log.Warning(
                "A critical notification ({Kind}) could not be sent on any channel; it is waiting for {People} account(s) to sign in",
                record.Kind,
                people.Count);
        }

        return new NotificationOutcome(
            record.Id, Repeat: false, people.Count, sending, forSummary, record.NobodyCouldReceive);
    }

    /// <summary>The settings row, or the defaults when nobody has saved one.</summary>
    private async Task<NotificationSettings> SettingsAsync(CancellationToken ct)
        => await _db.NotificationSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct)
               .ConfigureAwait(false)
           ?? new NotificationSettings();

    /// <summary>
    /// Who this is for. Disabled accounts are left out: an account nobody can sign in to is not
    /// somebody who can be told anything.
    /// </summary>
    private async Task<List<ModbotUser>> AudienceAsync(NotificationAudience audience, CancellationToken ct)
    {
        if (audience.UserIds is { } ids)
        {
            if (ids.Count == 0)
                return [];

            return await _db.Users
                .Where(u => ids.Contains(u.Id) && !u.IsDisabled)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        var wanted = audience.Permission ?? ModbotPermissions.Administrator;

        var candidates = await _db.Users
            .Include(u => u.Roles).ThenInclude(r => r.Role)
            .Where(u => !u.IsDisabled)
            .ToListAsync(ct).ConfigureAwait(false);

        // Administrator is checked rather than expanded, the same way RequiresFlag does it, so a
        // permission introduced after an account was made is covered with no data migration.
        return candidates
            .Where(u =>
            {
                var held = u.EffectivePermissions;
                return held.HasFlag(ModbotPermissions.Administrator) || (held & wanted) == wanted;
            })
            .ToList();
    }

    private async Task<Dictionary<(Guid, string), NotificationChoice>> ChoicesAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0)
            return [];

        var rows = await _db.NotificationChoices.AsNoTracking()
            .Where(c => userIds.Contains(c.UserId))
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.ToDictionary(c => (c.UserId, c.Channel));
    }

    /// <summary>
    /// Whether a channel is available, never letting a channel's own trouble stop the raise.
    /// </summary>
    private async Task<bool> CanReachAsync(INotificationChannel channel, ModbotUser user, CancellationToken ct)
    {
        try
        {
            return await channel.CanReachAsync(user, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warning(e, "Could not tell whether {Channel} can reach an account; treating it as unavailable", channel.Name);
            return false;
        }
    }

    private static string Short(string text, int limit)
        => text.Length <= limit ? text : text[..limit];
}
