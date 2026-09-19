using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Core.Notifications;

/// <summary>How the sending pass behaves.</summary>
public sealed class NotificationPassOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    public int SendsPerPass { get; init; } = 50;

    /// <summary>
    /// How many times one message is tried before it is given up on.
    /// </summary>
    /// <remarks>
    /// Low on purpose. The two channels that exist retry underneath already — email through its own
    /// queue, a direct message not at all, because a closed inbox does not open on the third try.
    /// </remarks>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>How many people's daily summaries are built in one pass.</summary>
    public int SummariesPerPass { get; init; } = 20;
}

/// <summary>What one pass did.</summary>
/// <param name="Sent">Messages handed to a channel.</param>
/// <param name="Failed">Messages given up on.</param>
/// <param name="Summaries">Daily summaries sent.</param>
/// <param name="NowWaiting">People a critical notification could not reach, left for next sign-in.</param>
public sealed record NotificationPassResult(int Sent, int Failed, int Summaries, int NowWaiting);

/// <summary>
/// Sends what the notifier queued, and builds the daily summaries (foundation §4.5).
/// </summary>
/// <remarks>
/// <para>
/// Out of band on purpose (§4.5.3). Whatever raised the notification has long since returned, so a
/// slow channel costs nothing that a moderator is waiting on, and a broken one is recorded as
/// channel health rather than thrown at whoever raised it.
/// </para>
/// <para>
/// <strong>This is also where a critical notification becomes "waiting".</strong> The notifier
/// catches the case where nothing could even be tried; this catches the case where everything that
/// was tried failed. Both end the same way: the person sees it at next sign-in.
/// </para>
/// </remarks>
public sealed class NotificationPass
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IReadOnlyList<INotificationChannel> _channels;
    private readonly NotificationPassOptions _options;
    private readonly ILogger _log = Log.Logger.ForContext(LogArea.Name, LogArea.Setup);

    public NotificationPass(
        ModbotContext db,
        IModbotClock clock,
        IEnumerable<INotificationChannel> channels,
        NotificationPassOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(channels);

        _db = db;
        _clock = clock;
        _channels = channels.ToList();
        _options = options ?? new NotificationPassOptions();
    }

    public async Task<NotificationPassResult> RunOnceAsync(CancellationToken ct = default)
    {
        var sent = 0;
        var failed = 0;

        var due = await _db.NotificationSends
            .Include(s => s.Notification)
            .Where(s => s.State == NotificationSendStates.Waiting)
            .OrderBy(s => s.QueuedAt)
            .Take(_options.SendsPerPass)
            .ToListAsync(ct).ConfigureAwait(false);

        if (due.Count > 0)
        {
            var users = await UsersAsync(due.Select(s => s.UserId).Distinct().ToList(), ct).ConfigureAwait(false);

            foreach (var send in due)
            {
                if (!users.TryGetValue(send.UserId, out var user))
                {
                    send.State = NotificationSendStates.Failed;
                    send.LastError = "The account is gone.";
                    failed++;
                    continue;
                }

                var channel = _channels.FirstOrDefault(c => c.Name == send.Channel);

                if (channel is null)
                {
                    send.State = NotificationSendStates.Failed;
                    send.LastError = $"{NotificationChannels.Label(send.Channel)} is not part of this build.";
                    failed++;
                    continue;
                }

                var outcome = await TrySendAsync(
                    channel, user, send.Notification.Title, Body(send.Notification), ct).ConfigureAwait(false);

                send.Attempts++;

                if (outcome.Sent)
                {
                    send.State = NotificationSendStates.Sent;
                    send.SentAt = _clock.UtcNow;
                    send.LastError = null;
                    sent++;
                }
                else
                {
                    send.LastError = Short(outcome.Error ?? "Sending failed.", 512);

                    if (send.Attempts >= _options.MaxAttempts)
                    {
                        send.State = NotificationSendStates.Failed;
                        failed++;
                    }
                }
            }
        }

        var summaries = await SendSummariesAsync(ct).ConfigureAwait(false);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var nowWaiting = await SettleUnreachedCriticalsAsync(ct).ConfigureAwait(false);

        if (sent > 0 || failed > 0 || summaries > 0 || nowWaiting > 0)
        {
            _log.Information(
                "Notifications: {Sent} sent, {Failed} failed, {Summaries} daily summaries, {Waiting} critical waiting for a sign-in",
                sent, failed, summaries, nowWaiting);
        }

        return new NotificationPassResult(sent, failed, summaries, nowWaiting);
    }

    /// <summary>
    /// One message per person per channel for everything held back, at most once a day.
    /// </summary>
    private async Task<int> SendSummariesAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;

        var held = await _db.NotificationSends
            .Include(s => s.Notification)
            .Where(s => s.State == NotificationSendStates.ForSummary)
            .ToListAsync(ct).ConfigureAwait(false);

        if (held.Count == 0)
            return 0;

        var groups = held
            .GroupBy(s => (s.UserId, s.Channel))
            .Take(_options.SummariesPerPass)
            .ToList();

        var userIds = groups.Select(g => g.Key.UserId).Distinct().ToList();
        var users = await UsersAsync(userIds, ct).ConfigureAwait(false);

        var choices = await _db.NotificationChoices
            .Where(c => userIds.Contains(c.UserId))
            .ToListAsync(ct).ConfigureAwait(false);

        var sent = 0;

        foreach (var group in groups)
        {
            var (userId, channelName) = group.Key;

            var choice = choices.FirstOrDefault(c => c.UserId == userId && c.Channel == channelName);
            var oldest = group.Min(s => s.QueuedAt);

            if (!NotificationRouting.SummaryDue(oldest, choice?.SummarySentAt, now))
                continue;

            var channel = _channels.FirstOrDefault(c => c.Name == channelName);
            var items = group.OrderBy(s => s.QueuedAt).ToList();

            if (channel is null || !users.TryGetValue(userId, out var user))
            {
                foreach (var item in items)
                {
                    item.State = NotificationSendStates.Failed;
                    item.LastError = "There is nowhere to send this summary.";
                }

                continue;
            }

            var outcome = await TrySendAsync(channel, user, SummaryTitle(items.Count), SummaryBody(items), ct)
                .ConfigureAwait(false);

            if (!outcome.Sent)
            {
                // Left where they are. A summary that could not go out today goes out tomorrow with
                // the rest, which is what a daily summary should do anyway.
                foreach (var item in items)
                    item.LastError = Short(outcome.Error ?? "Sending failed.", 512);

                continue;
            }

            foreach (var item in items)
            {
                item.State = NotificationSendStates.Sent;
                item.SentAt = now;
                item.Attempts++;
                item.LastError = null;
            }

            if (choice is null)
            {
                var (level, _) = NotificationChannels.Default(channelName);

                _db.NotificationChoices.Add(new NotificationChoice
                {
                    UserId = userId,
                    Channel = channelName,
                    Level = level,
                    DailySummary = true,
                    SummarySentAt = now,
                });
            }
            else
            {
                choice.SummarySentAt = now;
            }

            sent++;
        }

        return sent;
    }

    /// <summary>
    /// Marks a person waiting when every channel a critical notification was tried on has failed.
    /// </summary>
    private async Task<int> SettleUnreachedCriticalsAsync(CancellationToken ct)
    {
        var unsettled = await _db.NotificationsForPeople
            .Include(p => p.Notification)
            .Where(p => !p.Waiting
                        && p.Notification.Severity == NotificationSeverities.Critical
                        && !_db.NotificationSends.Any(s =>
                            s.NotificationId == p.NotificationId
                            && s.UserId == p.UserId
                            && (s.State == NotificationSendStates.Sent
                                || s.State == NotificationSendStates.Waiting)))
            .ToListAsync(ct).ConfigureAwait(false);

        if (unsettled.Count == 0)
            return 0;

        foreach (var person in unsettled)
            person.Waiting = true;

        var notificationIds = unsettled.Select(p => p.NotificationId).Distinct().ToList();

        var records = await _db.Notifications
            .Where(n => notificationIds.Contains(n.Id))
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var record in records)
        {
            record.NobodyCouldReceive = !await _db.NotificationSends
                .AnyAsync(s => s.NotificationId == record.Id && s.State == NotificationSendStates.Sent, ct)
                .ConfigureAwait(false);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return unsettled.Count;
    }

    private async Task<Dictionary<Guid, ModbotUser>> UsersAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
        => await _db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct).ConfigureAwait(false);

    private async Task<Modbot.Core.Email.SendOutcome> TrySendAsync(
        INotificationChannel channel, ModbotUser user, string title, string body, CancellationToken ct)
    {
        try
        {
            return await channel.SendAsync(user, title, body, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warning(e, "Sending a notification on {Channel} failed", channel.Name);
            return Modbot.Core.Email.SendOutcome.Failed(e.Message);
        }
    }

    private static string Body(NotificationRecord record)
        => record.Repeats == 0
            ? record.Body
            : $"{record.Body}\n\nThis has happened {record.Repeats + 1} times since {record.FirstAt:u}.";

    private static string SummaryTitle(int count)
        => count == 1 ? "Modbot: 1 thing from the last day" : $"Modbot: {count} things from the last day";

    private static string SummaryBody(IReadOnlyList<NotificationSend> items)
        => string.Join(
            "\n\n",
            items.Select(i => $"{NotificationSeverities.Label(i.Notification.Severity)}: {i.Notification.Title}\n{i.Notification.Body}"));

    private static string Short(string text, int limit) => text.Length <= limit ? text : text[..limit];
}

/// <summary>Runs the sending pass.</summary>
public sealed class NotificationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly NotificationPassOptions _options;
    private readonly ILogger _log = Log.Logger.ForContext(LogArea.Name, LogArea.Setup);

    public NotificationService(IServiceScopeFactory scopes, NotificationPassOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        _scopes = scopes;
        _options = options ?? new NotificationPassOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var pass = scope.ServiceProvider.GetRequiredService<NotificationPass>();

                await pass.RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _log.Warning(e, "Sending notifications failed");
            }
        }
    }
}
