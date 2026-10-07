using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Notifications;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Npgsql;
using Serilog;

namespace Modbot.Analytics.Reports;

/// <summary>The message a report was made from, as the right-click menu handed it over.</summary>
/// <param name="AttachmentNames">The names of the files attached. Their links expire, so only names are kept.</param>
/// <param name="Url">The message's own link, which opens it in Discord.</param>
public sealed record ReportedMessage(
    string Id,
    string ChannelId,
    string? ChannelName,
    DateTimeOffset SentAt,
    string Text,
    IReadOnlyList<string> AttachmentNames,
    string Url);

/// <summary>A member's report, before it is kept.</summary>
/// <param name="ReportedName">The name for the reported member, or null to look it up.</param>
/// <param name="Message">The message copy, for a report made from the message menu; null for <c>/report</c>.</param>
public sealed record NewMemberReport(
    string ReporterDiscordId,
    string ReporterName,
    string ReportedDiscordId,
    string? ReportedName,
    string Text,
    ReportedMessage? Message = null);

/// <summary>Why a report was or was not kept.</summary>
public enum MemberReportResult
{
    /// <summary>Kept, and the mods are told.</summary>
    Sent,

    /// <summary>The reporter and the reported are the same account.</summary>
    Yourself,

    /// <summary>This reporter already reported this message.</summary>
    AlreadyReportedMessage,

    /// <summary>This reporter already has an open report on this person.</summary>
    AlreadyOpen,

    /// <summary>This reporter has made 3 in the last 10 minutes or 10 in the last day.</summary>
    TooMany,

    /// <summary>Nothing written, or only spaces.</summary>
    NothingWritten,

    /// <summary>More than 1,000 characters.</summary>
    TooLong,
}

/// <param name="Id">The new report's id, when it was kept.</param>
public sealed record MemberReportOutcome(MemberReportResult Result, Guid? Id = null);

/// <summary>Why a close did or did not happen.</summary>
public enum MemberReportCloseResult
{
    Closed,
    NotFound,
    AlreadyClosed,
    NoNote,
    NoteTooLong,
}

/// <summary>
/// Member reports: made from Discord with <c>/report</c> or the Report to mods menu, read and closed
/// on the Reports page (Discord commands design §3.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The reporter is named in the report row and nowhere else.</strong> Not in the two facts,
/// not in the notification, not in anything the bot says. The facts carry the report's id; the
/// notification says "Somebody reported a member." and points at the Reports page.
/// </para>
/// <para>
/// <strong>Limits are counted in the database</strong>, from the reporter's own rows, so a restart
/// forgets nothing: 3 in any 10 minutes and 10 in any day, per reporter. Nothing is limited for the
/// server as a whole; the notifications collapse instead (one per 15 minutes).
/// </para>
/// <para>
/// <strong>Two promises are also unique indexes</strong> (<c>ux_member_report_message</c>,
/// <c>ux_member_report_open</c>), so two presses at the same moment cannot make two reports.
/// </para>
/// </remarks>
public sealed class MemberReports
{
    public const int PerTenMinutes = 3;

    public const int PerDay = 10;

    public static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan OneDay = TimeSpan.FromDays(1);

    /// <summary>How long a new-report notification stays quiet after it was said (decision: one per 15 minutes).</summary>
    public static readonly TimeSpan NotifyOncePer = TimeSpan.FromMinutes(15);

    /// <summary>Where the notification leads.</summary>
    public const string ReportsPath = "/reports";

    public const string NotificationTitle = "Modbot: new report";

    public const string NotificationBody = "Somebody reported a member.";

    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly IModbotClock _clock;
    private readonly INotifier? _notifier;

    public MemberReports(
        ModbotContext db,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        IModbotClock clock,
        INotifier? notifier = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _facts = facts;
        _partitions = partitions;
        _clock = clock;
        _notifier = notifier;
    }

    // ── Opening ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Keeps a report, if the rules allow it, and tells the mods. The order of the refusals is the
    /// order a member would want them in: the report itself, then a repeat, then the limits. A
    /// refusal keeps nothing and does not count against the limits.
    /// </summary>
    public async Task<MemberReportOutcome> OpenAsync(NewMemberReport input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ReporterDiscordId);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.ReportedDiscordId);

        var text = (input.Text ?? string.Empty).Trim();
        if (text.Length < MemberReport.MinTextLength)
            return new MemberReportOutcome(MemberReportResult.NothingWritten);
        if (text.Length > MemberReport.MaxTextLength)
            return new MemberReportOutcome(MemberReportResult.TooLong);

        var now = _clock.UtcNow;

        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

        MemberReport report;

        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            // One reporter at a time, so the refusals and the limits are checked and acted on as a
            // single step: two runs at once cannot both count two in ten minutes and both add a
            // third. The lock is on the reporter and is let go when the transaction ends, the same
            // way /remindme guards its ten. The unique indexes back up the two repeat rules.
            await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({"member_report:" + input.ReporterDiscordId}, 0))", ct)
                .ConfigureAwait(false);

            var refusal = await CheckAsync(input.ReporterDiscordId, input.ReportedDiscordId, input.Message?.Id, ct).ConfigureAwait(false);
            if (refusal is not null)
                return new MemberReportOutcome(refusal.Value);

            report = new MemberReport
            {
                ReporterDiscordId = input.ReporterDiscordId,
                ReporterName = input.ReporterName ?? string.Empty,
                ReportedDiscordId = input.ReportedDiscordId,
                ReportedName = input.ReportedName is { Length: > 0 } given ? given : await NameOfAsync(input.ReportedDiscordId, ct).ConfigureAwait(false),
                ReportedVRChatUserId = await LinkedVRChatAsync(input.ReportedDiscordId, ct).ConfigureAwait(false),
                Text = text,
                State = MemberReportStates.Open,
                CreatedAt = now,
            };

            if (input.Message is { } message)
            {
                report.MessageId = message.Id;
                report.MessageChannelId = message.ChannelId;
                report.MessageChannelName = message.ChannelName;
                report.MessageSentAt = message.SentAt;
                report.MessageText = Cut(message.Text, MemberReport.MaxMessageLength);
                report.MessageAttachments = [.. message.AttachmentNames];
                report.MessageUrl = message.Url;
            }

            _db.MemberReports.Add(report);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            await WriteOpenedAsync(report, now, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } violation)
        {
            // Two presses at once: the other one won. The promise in the index is the answer.
            foreach (var entry in _db.ChangeTracker.Entries<MemberReport>().ToList())
                entry.State = EntityState.Detached;

            return new MemberReportOutcome(
                violation.ConstraintName == "ux_member_report_message"
                    ? MemberReportResult.AlreadyReportedMessage
                    : MemberReportResult.AlreadyOpen);
        }

        await TellTheModsAsync(report, ct).ConfigureAwait(false);
        return new MemberReportOutcome(MemberReportResult.Sent, report.Id);
    }

    /// <summary>
    /// The refusal a report from this reporter about this person (and message, when there is one)
    /// would get, or null when it would be kept. Asked before a form is shown, so nobody writes a
    /// report that was always going to be refused.
    /// </summary>
    public async Task<MemberReportResult?> CheckAsync(
        string reporterDiscordId, string reportedDiscordId, string? messageId, CancellationToken ct)
    {
        if (string.Equals(reporterDiscordId, reportedDiscordId, StringComparison.Ordinal))
            return MemberReportResult.Yourself;

        if (messageId is not null
            && await _db.MemberReports.AsNoTracking()
                .AnyAsync(r => r.ReporterDiscordId == reporterDiscordId && r.MessageId == messageId, ct)
                .ConfigureAwait(false))
        {
            return MemberReportResult.AlreadyReportedMessage;
        }

        if (await _db.MemberReports.AsNoTracking()
                .AnyAsync(
                    r => r.ReporterDiscordId == reporterDiscordId
                         && r.ReportedDiscordId == reportedDiscordId
                         && r.State == MemberReportStates.Open,
                    ct)
                .ConfigureAwait(false))
        {
            return MemberReportResult.AlreadyOpen;
        }

        return await IsOverTheLimitAsync(reporterDiscordId, ct).ConfigureAwait(false)
            ? MemberReportResult.TooMany
            : null;
    }

    /// <summary>
    /// Whether this reporter has made <see cref="PerTenMinutes"/> reports in the last 10 minutes or
    /// <see cref="PerDay"/> in the last day. Counted from their rows, whatever state those are in.
    /// </summary>
    public async Task<bool> IsOverTheLimitAsync(string reporterDiscordId, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var dayAgo = now - OneDay;
        var tenAgo = now - TenMinutes;

        var times = await _db.MemberReports.AsNoTracking()
            .Where(r => r.ReporterDiscordId == reporterDiscordId && r.CreatedAt > dayAgo)
            .Select(r => r.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return times.Count >= PerDay || times.Count(t => t > tenAgo) >= PerTenMinutes;
    }

    private async Task<string?> NameOfAsync(string discordUserId, CancellationToken ct)
    {
        var shown = await _db.DiscordMembers.AsNoTracking()
            .Where(m => m.UserId == discordUserId)
            .Select(m => m.DisplayName)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(shown) ? null : shown.Trim();
    }

    private async Task<string?> LinkedVRChatAsync(string discordUserId, CancellationToken ct)
        => await _db.DiscordAccountLinks.AsNoTracking()
            .Where(l => l.DiscordUserId == discordUserId && l.UnlinkedAt == null)
            .Select(l => l.VRChatUserId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    private async Task WriteOpenedAsync(MemberReport report, DateTimeOffset now, CancellationToken ct)
    {
        // Nothing about the reporter and none of the words: the subject, the report's id, and the
        // channel when there was a message.
        var data = new JsonObject { ["reportId"] = report.Id.ToString() };
        if (report.MessageChannelId is not null)
            data["channelId"] = report.MessageChannelId;

        await _facts.WriteAsync(
            new FactRecord
            {
                Type = FactType.MemberReportOpened,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Discord,
                SubjectId = report.ReportedDiscordId,
                Source = FactSource.Modbot,
                Data = data,
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Tells everyone who holds See reports, or, for a report about a staff account, everyone who
    /// holds that and Review tickets. No names and no words; one per 15 minutes. A failure to tell
    /// is logged by the notifier and never undoes the report.
    /// </summary>
    private async Task TellTheModsAsync(MemberReport report, CancellationToken ct)
    {
        if (_notifier is null)
            return;

        try
        {
            var aboutStaff = (await MemberReportAccess.StaffDiscordIdsAsync(_db, _clock.UtcNow, ct).ConfigureAwait(false))
                .Contains(report.ReportedDiscordId);

            // A different key for the two audiences: a report only reviewers may see must not make
            // the ordinary one quiet, and the other way round.
            var key = aboutStaff ? NotificationKinds.MemberReportNew + ":staff" : NotificationKinds.MemberReportNew;

            await _notifier.RaiseAsync(
                new Notification(
                    NotificationKinds.MemberReportNew,
                    NotificationSeverity.Warning,
                    NotificationTitle,
                    NotificationBody,
                    NotificationAudience.Holding(
                        aboutStaff
                            ? ModbotPermissions.ViewReports | ModbotPermissions.ReviewTickets
                            : ModbotPermissions.ViewReports))
                {
                    SameAs = key,
                    Link = ReportsPath,
                    Quiet = NotifyOncePer,
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The report is kept; the mods will find it on the page.
            Log.Logger.ForContext(LogArea.Name, LogArea.Analytics).Warning(e, "Could not tell the mods about a new member report");
        }
    }

    // ── Closing ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Closes an open report with a required note. The row and the fact commit together: a report
    /// closed with no record of who closed it is the failure the Reviews page exists to prevent.
    /// The note is kept on the report and is not written into the fact.
    /// </summary>
    public async Task<MemberReportCloseResult> CloseAsync(
        Guid id, string? note, Guid closedByUserId, string closedByUsername, CancellationToken ct)
        => await CloseAsync(await _db.MemberReports.FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false),
            note, closedByUserId, closedByUsername, ct).ConfigureAwait(false);

    /// <summary>The same for a report already loaded, which the caller checked may be seen.</summary>
    public async Task<MemberReportCloseResult> CloseAsync(
        MemberReport? report, string? note, Guid closedByUserId, string closedByUsername, CancellationToken ct)
    {
        if (report is null)
            return MemberReportCloseResult.NotFound;

        var written = (note ?? string.Empty).Trim();
        if (written.Length == 0)
            return MemberReportCloseResult.NoNote;
        if (written.Length > MemberReport.MaxCloseNoteLength)
            return MemberReportCloseResult.NoteTooLong;

        if (report.State == MemberReportStates.Closed)
            return MemberReportCloseResult.AlreadyClosed;

        var now = _clock.UtcNow;
        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // One statement that only succeeds while the report is still open, and we go on only when it
        // changed exactly one row: two closes at once cannot both win, so only one fact is written
        // and the loser is told it is already closed.
        var id = report.Id;
        var changed = await _db.MemberReports
            .Where(r => r.Id == id && r.State == MemberReportStates.Open)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(r => r.State, MemberReportStates.Closed)
                    .SetProperty(r => r.ClosedAt, (DateTimeOffset?)now)
                    .SetProperty(r => r.ClosedByUserId, (Guid?)closedByUserId)
                    .SetProperty(r => r.ClosedByUsername, closedByUsername)
                    .SetProperty(r => r.CloseNote, written),
                ct)
            .ConfigureAwait(false);

        if (changed != 1)
            return MemberReportCloseResult.AlreadyClosed;

        // What the caller holds is what was just written.
        await _db.Entry(report).ReloadAsync(ct).ConfigureAwait(false);

        await _facts.WriteAsync(
            new FactRecord
            {
                Type = FactType.MemberReportClosed,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Discord,
                SubjectId = report.ReportedDiscordId,
                ActorPlatform = FactPlatform.Modbot,
                ActorId = closedByUserId.ToString(),
                Source = FactSource.Modbot,
                Data = new JsonObject
                {
                    ["reportId"] = report.Id.ToString(),
                    ["outcome"] = "closed",
                    ["actorDisplayName"] = closedByUsername,
                },
            },
            ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return MemberReportCloseResult.Closed;
    }

    // ── Retention ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes the text and the message copy from every closed report that was closed more than
    /// <see cref="Settings.MemberReportRetentionDays"/> days ago, and keeps the bare record
    /// (decision 10). With 0 days nothing is removed. An open report is never touched. Returns how
    /// many were reduced.
    /// </summary>
    public static async Task<int> RemoveOldTextAsync(ModbotContext db, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var days = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => (int?)s.MemberReportRetentionDays)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false) ?? MemberReport.DefaultRetentionDays;

        if (days <= 0)
            return 0;

        var cutoff = now.AddDays(-days);

        return await db.MemberReports
            .Where(r => r.State == MemberReportStates.Closed && r.TextRemovedAt == null && r.ClosedAt != null && r.ClosedAt < cutoff)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(r => r.Text, (string?)null)
                    .SetProperty(r => r.MessageId, (string?)null)
                    .SetProperty(r => r.MessageChannelId, (string?)null)
                    .SetProperty(r => r.MessageChannelName, (string?)null)
                    .SetProperty(r => r.MessageSentAt, (DateTimeOffset?)null)
                    .SetProperty(r => r.MessageText, (string?)null)
                    .SetProperty(r => r.MessageAttachments, (List<string>?)null)
                    .SetProperty(r => r.MessageUrl, (string?)null)
                    .SetProperty(r => r.TextRemovedAt, now),
                ct)
            .ConfigureAwait(false);
    }

    private static string Cut(string text, int max)
    {
        if (text.Length <= max)
            return text;

        // Never between the two halves of a character written as two: JSON and Postgres refuse half.
        var end = max;
        if (char.IsHighSurrogate(text[end - 1]))
            end--;

        return text[..end];
    }
}

/// <summary>Who may see which reports.</summary>
public static class MemberReportAccess
{
    /// <summary>
    /// Whether these permissions may see reports about staff accounts: Review tickets, the
    /// permission the people being reviewed are not meant to hold (M4 design §8.3).
    /// </summary>
    public static bool SeesReportsAboutStaff(ModbotPermissions held)
        => held.HasFlag(ModbotPermissions.Administrator) || held.HasFlag(ModbotPermissions.ReviewTickets);

    /// <summary>
    /// The Discord ids that count for a staff account right now (<see cref="StaffDiscord"/>): proven
    /// ones, and typed ones until they stop counting. A report about one of these is shown only to
    /// people who hold Review tickets. Worked out at the time of reading, so an account added or
    /// removed since the report was made is followed.
    /// </summary>
    public static async Task<IReadOnlySet<string>> StaffDiscordIdsAsync(ModbotContext db, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var accounts = await db.Users.AsNoTracking()
            .Where(u => u.DeletedAt == null && u.DiscordUserId != null)
            .Select(u => new { u.Id, u.DiscordUserId, u.DiscordVerifiedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var counted = await StaffDiscord.CountedIdsAsync(
                db, accounts.Select(a => (a.Id, a.DiscordUserId, a.DiscordVerifiedAt)), now, ct)
            .ConfigureAwait(false);

        return counted.Values.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The reports these permissions may see: all of them, or all but the ones about a staff
    /// account. The one rule for the list, the count and the close.
    /// </summary>
    public static async Task<IQueryable<MemberReport>> VisibleAsync(
        ModbotContext db, ModbotPermissions held, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var all = db.MemberReports.AsNoTracking();
        if (SeesReportsAboutStaff(held))
            return all;

        var staff = (await StaffDiscordIdsAsync(db, now, ct).ConfigureAwait(false)).ToList();
        return staff.Count == 0 ? all : all.Where(r => !staff.Contains(r.ReportedDiscordId));
    }
}
