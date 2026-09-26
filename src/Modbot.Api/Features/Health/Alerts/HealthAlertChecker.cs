using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Storage;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Email;
using Modbot.Core.Logging.Store;
using Modbot.Core.Notifications;
using Modbot.Core.Time;
using Modbot.VRChat;

namespace Modbot.Api.Features.Health.Alerts;

/// <summary>What one check found.</summary>
/// <param name="Check"><see cref="HealthChecks"/>.</param>
/// <param name="Problem">True when something is wrong.</param>
/// <param name="Detail">One sentence a person can act on. Empty when nothing is wrong.</param>
public sealed record CheckReading(string Check, bool Problem, string Detail);

/// <summary>What one pass did.</summary>
/// <param name="Problems">Checks that went wrong and were said out loud.</param>
/// <param name="Recoveries">Checks that came back and were said out loud.</param>
/// <param name="Raised">Notifications raised. Repeats inside the quiet time are not counted.</param>
public sealed record HealthAlertRun(
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Recoveries,
    int Raised);

/// <summary>
/// Watches what the Health page already knows, and tells the staff accounts that asked.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Each check has a state, so a problem is said once.</strong> Going wrong raises one
/// notification; staying wrong raises the same one every pass and the pipeline counts it rather than
/// sending it, until the quiet time is up and it goes out again, because a problem nobody has fixed
/// is still a problem; coming back says once that it is over.
/// </para>
/// <para>
/// <strong>This used to send its own email</strong> and no longer does (notifications design,
/// 2026-09-18). It raises through <see cref="INotifier"/>, which decides channels from severity and
/// each person's own settings, and email is one of those channels — still through the same sender,
/// so the daily email limit is exactly as it was. Nothing here sends anything itself: two paths to
/// the same inbox is how a deployment gets told twice.
/// </para>
/// <para>
/// The quiet time is still the one on the health alerts card, handed to the pipeline with each
/// notification, so a number an operator chose keeps meaning what it meant.
/// </para>
/// <para>
/// <strong>What this half cannot see.</strong> A Modbot whose process is not running, or whose
/// database is gone, cannot notice either and cannot send mail. Those are Cloud's to watch, from
/// outside, by noticing that the logs stopped arriving. Every check here is one Modbot can answer
/// while it is up.
/// </para>
/// <para>
/// A deployment that can reach nobody at all records the state and sends nothing — and because a
/// stopped sync is <see cref="NotificationSeverity.Critical"/>, it is waiting on the recipients'
/// accounts the next time they sign in (foundation §4.5.3).
/// </para>
/// </remarks>
public sealed class HealthAlertChecker
{
    /// <summary>How often the checks run.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long Modbot may be waiting on VRChat on purpose before it is worth an email. A cold stop
    /// is spec 4.3.1 working and recovers by itself; an hour of it is not recovering.
    /// </summary>
    public static readonly TimeSpan WaitingTooLong = TimeSpan.FromHours(1);

    /// <summary>How long a sync job may go without finishing a pass before it counts as stopped.</summary>
    public static readonly TimeSpan SyncStopped = TimeSpan.FromHours(3);

    /// <summary>How long email may sit in the queue before it counts as stuck.</summary>
    public static readonly TimeSpan EmailStuck = TimeSpan.FromHours(3);

    /// <summary>How long sending logs to Cloud may keep failing before it is worth an email.</summary>
    public static readonly TimeSpan ShippingFailingFor = TimeSpan.FromHours(3);

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly INotifier _notifier;
    private readonly IVRChatGate? _gate;
    private readonly IDiscordBotStatus? _discord;
    private readonly Modbot.AI.Usage.AiSpendReport? _aiSpend;
    private readonly StorageEstimator? _storage;
    private readonly DatabaseLogSink? _logStore;
    private readonly ModbotCloudAddress? _cloud;

    public HealthAlertChecker(
        ModbotContext db,
        IModbotClock clock,
        INotifier notifier,
        IVRChatGate? gate = null,
        IDiscordBotStatus? discord = null,
        Modbot.AI.Usage.AiSpendReport? aiSpend = null,
        StorageEstimator? storage = null,
        DatabaseLogSink? logStore = null,
        ModbotCloudAddress? cloud = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(notifier);

        _db = db;
        _clock = clock;
        _notifier = notifier;
        _gate = gate;
        _discord = discord;
        _aiSpend = aiSpend;
        _storage = storage;
        _logStore = logStore;
        _cloud = cloud;
    }

    public async Task<HealthAlertRun> RunOnceAsync(CancellationToken ct = default)
    {
        var watches = await _db.HealthWatches.Where(w => w.On).ToListAsync(ct);

        if (watches.Count == 0)
            return new HealthAlertRun([], [], 0);

        var settings = await _db.HealthAlertSettings.FirstOrDefaultAsync(s => s.Id == 1, ct)
                       ?? new HealthAlertSettings();

        var now = _clock.UtcNow;
        var readings = await ReadAsync(watches.Select(w => w.Check).ToHashSet(StringComparer.Ordinal), settings, ct);

        var quiet = TimeSpan.FromHours(Math.Clamp(settings.QuietHours, 0, HealthAlertSettings.MaxQuietHours));
        var audience = await AudienceAsync(ct);
        var raising = new List<(HealthWatch Watch, Notification Note, bool IsProblem)>();

        foreach (var watch in watches)
        {
            if (readings.FirstOrDefault(r => r.Check == watch.Check) is not { } reading)
                continue;

            if (reading.Problem)
            {
                if (!watch.Problem)
                {
                    watch.Problem = true;
                    watch.Since = now;
                }

                watch.Detail = Short(reading.Detail);

                // Raised every pass while the problem is there. Saying it once and then counting
                // is the pipeline's job, not this checker's: the quiet time this deployment chose
                // is handed over with the notification and the pipeline holds it.
                raising.Add((watch, ProblemNotification(watch, now, quiet, audience), IsProblem: true));
            }
            else if (watch.Problem)
            {
                var wasFor = watch.Since is { } since ? now - since : TimeSpan.Zero;

                watch.Problem = false;
                watch.Since = null;
                watch.Detail = null;

                // A recovery is told about whether or not the quiet time is up: "it is over" is
                // never noise, and a recovery nobody heard about leaves somebody still worrying.
                var said = watch.LastSentAt is not null;
                watch.LastSentAt = null;

                if (!said)
                    continue;

                raising.Add((watch, RecoveryNotification(watch.Check, wasFor, quiet, audience), IsProblem: false));
            }
        }

        settings.LastCheckedAt = now;

        if (await _db.HealthAlertSettings.AnyAsync(s => s.Id == 1, ct))
            _db.HealthAlertSettings.Update(settings);
        else
            _db.HealthAlertSettings.Add(settings);

        await _db.SaveChangesAsync(ct);

        var problems = new List<string>();
        var recoveries = new List<string>();
        var raised = 0;

        foreach (var (watch, note, isProblem) in raising)
        {
            var outcome = await _notifier.RaiseAsync(note, ct);

            // A repeat inside the quiet time is not news and is not counted. The watch's own
            // "something was said" mark only moves when something actually was.
            if (outcome.Repeat)
                continue;

            raised++;

            if (isProblem)
            {
                watch.LastSentAt = now;
                problems.Add(watch.Check);
            }
            else
            {
                recoveries.Add(watch.Check);
            }
        }

        await _db.SaveChangesAsync(ct);

        return new HealthAlertRun(problems, recoveries, raised);
    }

    /// <summary>Who hears about Modbot's own health: the accounts somebody chose, and nobody else.</summary>
    /// <remarks>
    /// An explicit list rather than a permission, because the person who keeps the server running is
    /// often not the person who moderates, and mail nobody wanted is mail everybody filters.
    /// </remarks>
    private async Task<NotificationAudience> AudienceAsync(CancellationToken ct)
    {
        var ids = await _db.HealthAlertRecipients.AsNoTracking()
            .Select(r => r.UserId)
            .ToListAsync(ct);

        return NotificationAudience.These(ids);
    }

    /// <summary>Everything the watched checks need, read once.</summary>
    private async Task<List<CheckReading>> ReadAsync(
        HashSet<string> wanted, HealthAlertSettings settings, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var readings = new List<CheckReading>();

        var modbot = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        if (wanted.Contains(HealthChecks.VRChat) && _gate is not null)
        {
            var (gate, _) = await GateHealthReader.ReadAsync(_gate, ct);

            var stuck = gate.Status == GateStatus.NeedsOperator
                        || (gate.Status == GateStatus.WaitingOnPurpose
                            && gate.ColdStopEndsAt is { } until
                            && until - now > WaitingTooLong);

            readings.Add(new CheckReading(HealthChecks.VRChat, stuck, gate.Headline));
        }

        if (wanted.Contains(HealthChecks.DiscordBot) && _discord?.Snapshot() is { } bot)
        {
            var down = bot.State is DiscordBotState.Disconnected or DiscordBotState.Failed;

            readings.Add(new CheckReading(
                HealthChecks.DiscordBot,
                down,
                down
                    ? $"The Discord bot is {bot.State.ToString().ToLowerInvariant()}." + (bot.LastError is null ? "" : " " + bot.LastError)
                    : ""));
        }

        if (wanted.Contains(HealthChecks.Sync) && modbot is not null && !string.IsNullOrWhiteSpace(modbot.ManagedGroupId))
        {
            // The audit log is the one that must never stop: it is where every moderation fact
            // comes from. A member sweep that is slow is not the same kind of problem.
            var last = modbot.AuditLogPolledAt;
            var stopped = last is null || now - last.Value > SyncStopped;

            readings.Add(new CheckReading(
                HealthChecks.Sync,
                stopped,
                last is null
                    ? "Modbot has never finished reading the group's audit log."
                    : $"Modbot last read the group's audit log at {last.Value:u}."));
        }

        if (wanted.Contains(HealthChecks.Storage) && settings.StorageWarnBytes > 0 && _storage is not null)
        {
            var measurement = await _storage.MeasureAsync(ct);
            var over = measurement.TotalBytes >= settings.StorageWarnBytes;

            readings.Add(new CheckReading(
                HealthChecks.Storage,
                over,
                $"The database is {Gigabytes(measurement.TotalBytes)} GB, past the "
                + $"{Gigabytes(settings.StorageWarnBytes)} GB you asked to be told about."));
        }

        if (wanted.Contains(HealthChecks.AiSpend) && _aiSpend is not null)
        {
            var warnings = await _aiSpend.WarningsAsync(ct);
            var reached = warnings.Where(w => w.Reached).ToList();

            readings.Add(new CheckReading(
                HealthChecks.AiSpend,
                reached.Count > 0,
                reached.Count == 0
                    ? ""
                    : $"{reached.Count} AI spending limit{(reached.Count == 1 ? " has" : "s have")} been reached."));
        }

        if (wanted.Contains(HealthChecks.Email))
        {
            var queue = await EmailQueueStatus.ReadAsync(_db, now, ct);

            var oldest = await _db.EmailQueue.AsNoTracking()
                .Where(e => e.State == EmailStates.Queued)
                .MinAsync(e => (DateTimeOffset?)e.QueuedAt, ct);

            var stuck = queue.Failed > 0 || (oldest is { } waiting && now - waiting > EmailStuck);

            readings.Add(new CheckReading(
                HealthChecks.Email,
                stuck,
                queue.Failed > 0
                    ? $"{queue.Failed} email(s) could not be sent."
                    : $"{queue.Queued} email(s) have been waiting since {oldest:u}."));
        }

        if (wanted.Contains(HealthChecks.LogShipping) && _logStore is not null)
        {
            var shipping = await CloudLogStatus.ReadAsync(_db, _cloud ?? ModbotCloudAddress.Default, ct);

            var failing = shipping.On
                          && shipping.LastError is not null
                          && shipping.LastErrorAt is { } at
                          && now - at < CheckEvery * 3
                          && (shipping.LastSentAt is null || now - shipping.LastSentAt.Value > ShippingFailingFor);

            readings.Add(new CheckReading(
                HealthChecks.LogShipping,
                failing,
                $"Sending logs to Modbot Cloud has been failing: {shipping.LastError}"));
        }

        return readings;
    }

    /// <summary>
    /// How serious each check is (foundation §4.5.1).
    /// </summary>
    /// <remarks>
    /// Most of these are §4.5.1's own critical class said another way: Modbot cannot reach VRChat,
    /// cannot read the audit log, cannot send mail, cannot ship its logs. The two that are not are
    /// the ones that are a line somebody drew rather than something that has stopped working: the
    /// database passing a size, and a spending limit reached.
    /// </remarks>
    private static NotificationSeverity SeverityOf(string check) => check switch
    {
        HealthChecks.Storage or HealthChecks.AiSpend => NotificationSeverity.Warning,
        _ => NotificationSeverity.Critical,
    };

    private static Notification ProblemNotification(
        HealthWatch watch, DateTimeOffset now, TimeSpan quiet, NotificationAudience audience)
    {
        var label = HealthChecks.LabelOf(watch.Check);

        var body = $"{label} needs looking at on your Modbot.\n\n"
                   + $"{watch.Detail}\n\n"
                   + $"Since: {watch.Since ?? now:u}\n\n"
                   + "Open Health in Modbot for the whole picture.";

        return new Notification(
            NotificationKinds.HealthProblem,
            SeverityOf(watch.Check),
            $"Modbot: {label} needs looking at",
            body,
            audience)
        {
            // One key per check, not per pass: the check being broken is one thing however many
            // times it is noticed, and a key that moved would send every five minutes.
            SameAs = $"{NotificationKinds.HealthProblem}:{watch.Check}",
            Link = "/health",
            Quiet = quiet,
        };
    }

    private static Notification RecoveryNotification(
        string check, TimeSpan wasFor, TimeSpan quiet, NotificationAudience audience)
    {
        var label = HealthChecks.LabelOf(check);

        var body = $"{label} is working again on your Modbot.\n\n"
                   + $"It was a problem for {Words(wasFor)}.";

        return new Notification(
            NotificationKinds.HealthRecovered,

            // The same severity as the problem it closes. "It is over" belongs to the incident that
            // interrupted somebody: telling them about the break at once and about the fix in
            // tomorrow's summary would leave them investigating something already fixed.
            SeverityOf(check),
            $"Modbot: {label} is working again",
            body,
            audience)
        {
            SameAs = $"{NotificationKinds.HealthRecovered}:{check}",
            Link = "/health",
            Quiet = quiet,
        };
    }

    private static string Short(string detail) => detail.Length <= 512 ? detail : detail[..512];

    private static string Gigabytes(long bytes) => (bytes / 1024d / 1024d / 1024d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    private static string Words(TimeSpan span) => span.TotalHours >= 1
        ? $"{Math.Round(span.TotalHours)} hour(s)"
        : $"{Math.Max(1, Math.Round(span.TotalMinutes))} minute(s)";
}
