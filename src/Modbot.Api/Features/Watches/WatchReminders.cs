using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Notifications;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Api.Features.Watches;

/// <summary>What one pass did.</summary>
/// <param name="Ended">Watches whose end day had passed, now closed in the log.</param>
/// <param name="Reminded">Follow-ups whose day had come, now raised as notifications.</param>
public sealed record WatchPassResult(int Ended, int Reminded);

/// <summary>
/// The timed half of watching a person: closes watches whose end day has passed, and raises a
/// reminder for each follow-up whose day has come (watching a person design §5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing waits on this.</strong> Every reader already treats a watch past its end day as
/// over, and Now lists a follow-up as due from its day, so a pass that runs a minute late changes
/// nothing a moderator sees. It only makes the log say the watch ended, and tells the person who
/// set a follow-up that it has come.
/// </para>
/// <para>
/// <strong>Each follow-up is said once.</strong> <see cref="PersonWatch.FollowUpRemindedAt"/> is
/// set as it is raised, and the notification's own key names the watch and the day, so a pass
/// that runs twice says it once.
/// </para>
/// </remarks>
public sealed class WatchPass
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly WatchService _watches;
    private readonly INotifier? _notifier;

    public WatchPass(ModbotContext db, IModbotClock clock, WatchService watches, INotifier? notifier = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(watches);

        _db = db;
        _clock = clock;
        _watches = watches;
        _notifier = notifier;
    }

    public async Task<WatchPassResult> RunOnceAsync(CancellationToken ct)
    {
        var ended = await _watches.EndExpiredAsync(ct);
        var reminded = await RemindAsync(ct);

        return new WatchPassResult(ended, reminded);
    }

    private async Task<int> RemindAsync(CancellationToken ct)
    {
        if (_notifier is null)
            return 0;

        var now = _clock.UtcNow;

        var due = await _db.PersonWatches
            .Where(w => w.EndedAt == null
                     && (w.EndsAt == null || w.EndsAt > now)
                     && w.FollowUpAt != null
                     && w.FollowUpAt <= now
                     && w.FollowUpRemindedAt == null)
            .OrderBy(w => w.FollowUpAt)
            .Take(WatchService.ListLimit)
            .ToListAsync(ct);

        if (due.Count == 0)
            return 0;

        foreach (var watch in due)
        {
            // To whoever set it: they asked to be reminded. Everybody else who may read watches
            // sees it on Now, under Follow-ups due, without a message each.
            //
            // Nothing about the person goes in: not their name, id or the reason. The row outlives
            // a purge of the person and goes out by email and Discord message; Now, which the link
            // opens, shows who under the reader's own permissions.
            await _notifier.RaiseAsync(
                new Notification(
                    NotificationKinds.WatchFollowUpDue,
                    NotificationSeverity.Warning,
                    "Modbot: time to check on a watched person",
                    "A follow-up on a watched person is due.",
                    NotificationAudience.These([watch.SetByUserId]))
                {
                    SameAs = $"{NotificationKinds.WatchFollowUpDue}:{watch.Id}:{watch.FollowUpAt!.Value:O}",
                    Link = "/",
                },
                ct);

            watch.FollowUpRemindedAt = now;
            await _db.SaveChangesAsync(ct);
        }

        return due.Count;
    }
}

/// <summary>Runs <see cref="WatchPass"/> once a minute.</summary>
public sealed class WatchReminderService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger _log = Log.Logger.ForContext(LogArea.Name, LogArea.Setup);

    public WatchReminderService(IServiceScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        _scopes = scopes;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var run = await scope.ServiceProvider.GetRequiredService<WatchPass>()
                    .RunOnceAsync(stoppingToken)
                    .ConfigureAwait(false);

                if (run.Ended > 0 || run.Reminded > 0)
                {
                    _log.Information(
                        "Watches: {Ended} ran out, {Reminded} follow-up reminder(s) raised",
                        run.Ended,
                        run.Reminded);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _log.Warning(e, "Checking watches for end days and follow-ups failed; it will try again");
            }
        }
    }
}
