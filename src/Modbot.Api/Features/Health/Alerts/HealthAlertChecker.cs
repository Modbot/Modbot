using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Storage;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Email;
using Modbot.Core.Logging.Store;
using Modbot.Core.Time;
using Modbot.VRChat;

namespace Modbot.Api.Features.Health.Alerts;

/// <summary>What one check found.</summary>
/// <param name="Check"><see cref="HealthChecks"/>.</param>
/// <param name="Problem">True when something is wrong.</param>
/// <param name="Detail">One sentence a person can act on. Empty when nothing is wrong.</param>
public sealed record CheckReading(string Check, bool Problem, string Detail);

/// <summary>What one pass did.</summary>
/// <param name="Problems">Checks that went wrong and were emailed about.</param>
/// <param name="Recoveries">Checks that came back and were emailed about.</param>
/// <param name="Sent">Emails handed to the queue.</param>
public sealed record HealthAlertRun(
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Recoveries,
    int Sent);

/// <summary>
/// Watches what the Health page already knows, and emails the staff accounts that asked.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Each check has a state, so a problem is said once.</strong> Going wrong sends one email;
/// staying wrong sends nothing until the quiet time is up, and then says it again, because a problem
/// nobody has fixed is still a problem; coming back sends one email saying it is over. Without the
/// state this is a mail every five minutes, which is how alerting gets turned off.
/// </para>
/// <para>
/// The quiet time works the same way the unusual-activity alerts' does (AI insights design §8), and
/// for the same reason, but there is no "much worse" exception here: a sync that is broken is not
/// twice as broken an hour later.
/// </para>
/// <para>
/// <strong>What this half cannot see.</strong> A Modbot whose process is not running, or whose
/// database is gone, cannot notice either and cannot send mail. Those are Cloud's to watch, from
/// outside, by noticing that the logs stopped arriving. Every check here is one Modbot can answer
/// while it is up.
/// </para>
/// <para>
/// Mail goes through the ordinary sender, so it obeys the daily email limit and the queue (accounts
/// and access design §4.4). A deployment with no SMTP set up records the state and sends nothing.
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
    private readonly IEmailSender _email;
    private readonly IVRChatGate? _gate;
    private readonly IDiscordBotStatus? _discord;
    private readonly Modbot.AI.Usage.AiSpendReport? _aiSpend;
    private readonly StorageEstimator? _storage;
    private readonly DatabaseLogSink? _logStore;
    private readonly ModbotCloudAddress? _cloud;

    public HealthAlertChecker(
        ModbotContext db,
        IModbotClock clock,
        IEmailSender email,
        IVRChatGate? gate = null,
        IDiscordBotStatus? discord = null,
        Modbot.AI.Usage.AiSpendReport? aiSpend = null,
        StorageEstimator? storage = null,
        DatabaseLogSink? logStore = null,
        ModbotCloudAddress? cloud = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(email);

        _db = db;
        _clock = clock;
        _email = email;
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

        var problems = new List<string>();
        var recoveries = new List<string>();
        var messages = new List<(string Subject, string Body)>();

        foreach (var watch in watches)
        {
            if (readings.FirstOrDefault(r => r.Check == watch.Check) is not { } reading)
                continue;

            var quiet = TimeSpan.FromHours(Math.Clamp(settings.QuietHours, 0, HealthAlertSettings.MaxQuietHours));

            if (reading.Problem)
            {
                if (!watch.Problem)
                {
                    watch.Problem = true;
                    watch.Since = now;
                }

                watch.Detail = Short(reading.Detail);

                var due = watch.LastSentAt is not { } last || now - last >= quiet;
                if (!due)
                    continue;

                watch.LastSentAt = now;
                problems.Add(watch.Check);
                messages.Add(ProblemMail(watch, now));
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

                recoveries.Add(watch.Check);
                messages.Add(RecoveryMail(watch.Check, wasFor));
            }
        }

        settings.LastCheckedAt = now;

        if (await _db.HealthAlertSettings.AnyAsync(s => s.Id == 1, ct))
            _db.HealthAlertSettings.Update(settings);
        else
            _db.HealthAlertSettings.Add(settings);

        await _db.SaveChangesAsync(ct);

        var sent = messages.Count == 0 ? 0 : await SendAsync(messages, ct);

        return new HealthAlertRun(problems, recoveries, sent);
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

    private (string Subject, string Body) ProblemMail(HealthWatch watch, DateTimeOffset now)
    {
        var label = HealthChecks.LabelOf(watch.Check);

        var body = $"{label} needs looking at on your Modbot.\n\n"
                   + $"{watch.Detail}\n\n"
                   + $"Since: {watch.Since ?? now:u}\n\n"
                   + "Open Sync health in Modbot for the whole picture.";

        return ($"Modbot: {label} needs looking at", body);
    }

    private static (string Subject, string Body) RecoveryMail(string check, TimeSpan wasFor)
    {
        var label = HealthChecks.LabelOf(check);

        var body = $"{label} is working again on your Modbot.\n\n"
                   + $"It was a problem for {Words(wasFor)}.";

        return ($"Modbot: {label} is working again", body);
    }

    /// <summary>One email per recipient per message. Disabled accounts and accounts with no address are skipped.</summary>
    private async Task<int> SendAsync(List<(string Subject, string Body)> messages, CancellationToken ct)
    {
        if (!await _email.IsConfiguredAsync(ct))
            return 0;

        var addresses = await _db.HealthAlertRecipients.AsNoTracking()
            .Where(r => !r.User.IsDisabled && r.User.Email != null && r.User.Email != "")
            .Select(r => r.User.Email!)
            .Distinct()
            .ToListAsync(ct);

        var sent = 0;

        foreach (var address in addresses)
        {
            foreach (var (subject, body) in messages)
            {
                var outcome = await _email.SendAsync(new EmailMessage(address, subject, body, EmailKind.Other), ct);

                if (outcome.Sent || outcome.Queued)
                    sent++;
            }
        }

        return sent;
    }

    private static string Short(string detail) => detail.Length <= 512 ? detail : detail[..512];

    private static string Gigabytes(long bytes) => (bytes / 1024d / 1024d / 1024d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    private static string Words(TimeSpan span) => span.TotalHours >= 1
        ? $"{Math.Round(span.TotalHours)} hour(s)"
        : $"{Math.Max(1, Math.Round(span.TotalMinutes))} minute(s)";
}
