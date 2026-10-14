using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.StaffReminders;

/// <param name="Council">Day-30 reminders for the Council's vote sent this pass.</param>
/// <param name="CheckIns">People listed in the 30-day inactive check-in sent this pass.</param>
/// <param name="Error">What went wrong with the first post that was not sent, if any.</param>
public sealed record StaffReminderPass(int Council, int CheckIns, string? Error);

/// <summary>
/// Posts the two staff reminders in the Discord channel chosen under Settings → People and roles → New staff:
/// the day-30 reminder for the Council's vote on a new staff member (one post each), and the check-in for staff
/// who have been inactive for 30 days (one post listing everyone due).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Each reminder is posted once.</strong> A trial records <see cref="StaffTrial.CouncilRemindedAt"/>
/// as it is posted. An account records <see cref="ModbotUser.CheckInRemindedAt"/>, and is due again only once it
/// has been active after that and gone quiet for 30 days again. With no channel chosen nothing is posted and
/// nothing is marked, so a reminder due while the setting was off goes out when a channel is chosen.
/// </para>
/// <para>
/// <strong>Active</strong> means any of: signed in to Modbot, did a moderation action (in Modbot, or as the
/// account's own proven VRChat or Discord account), or was host or staff on an event that has started.
/// An account that has done none of them is counted from the day it was made.
/// </para>
/// <para>
/// A post Discord refuses stops the pass and is tried again on the next one. A post whose outcome is unclear is
/// counted as sent, because a second card is worse than a missing one.
/// </para>
/// </remarks>
public sealed class StaffReminderPoster
{
    /// <summary>How long into a trial the Council's reminder is posted.</summary>
    public static readonly TimeSpan TrialLength = TimeSpan.FromDays(30);

    /// <summary>How long without activity makes a check-in due.</summary>
    public static readonly TimeSpan QuietFor = TimeSpan.FromDays(30);

    /// <summary>The most Council reminders in one pass, so a first run after the channel is set does not flood it.</summary>
    public const int PerPass = 5;

    /// <summary>The most people one check-in post lists; the rest follow in the next pass.</summary>
    public const int CheckInsPerPost = 40;

    /// <summary>The most of the buddy's note a card shows.</summary>
    private const int NoteLength = 900;

    private static readonly string[] ActionTypes =
    [
        FactType.MemberBanned,
        FactType.MemberUnbanned,
        FactType.MemberKicked,
        FactType.GroupInstanceKick,
        FactType.GroupInstanceWarn,
        FactType.JoinRequestRejected,
        FactType.JoinRequestBlocked,
        FactType.ActionKick,
        FactType.ActionBan,
        FactType.ActionUnban,
        FactType.ActionJoinRequestRejected,
        FactType.DiscordMemberBanned,
        FactType.DiscordMemberUnbanned,
        FactType.DiscordMemberKicked,
        FactType.DiscordMemberTimedOut,
    ];

    private static readonly string[] EventRoles = [CalendarInviteRoles.Host, CalendarInviteRoles.Staff];

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    public StaffReminderPoster(ModbotContext db, IModbotClock clock, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    public async Task<StaffReminderPass> RunOnceAsync(IDiscordGateway gateway, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.StaffRemindersChannelId, s.PublicAddress, s.ManagedGroupName })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (settings?.StaffRemindersChannelId is not { Length: > 0 } channel)
            return new StaffReminderPass(0, 0, null);

        var style = new CardStyle(settings.PublicAddress, settings.ManagedGroupName, BrandIcon.For(settings.PublicAddress));

        var (council, error) = await CouncilAsync(gateway, channel, style, ct).ConfigureAwait(false);

        // A channel Discord refuses is refused for both kinds.
        if (error is not null)
            return new StaffReminderPass(council, 0, error);

        var (checkIns, checkInError) = await CheckInsAsync(gateway, channel, style, ct).ConfigureAwait(false);

        return new StaffReminderPass(council, checkIns, checkInError);
    }

    private async Task<(int Posted, string? Error)> CouncilAsync(
        IDiscordGateway gateway, string channel, CardStyle style, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var startedBefore = now - TrialLength;

        var due = await _db.StaffTrials
            .Include(t => t.Items)
            .Where(t => t.ClosedAt == null && t.CouncilRemindedAt == null && t.StartedAt <= startedBefore)
            .OrderBy(t => t.StartedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var posted = 0;

        foreach (var trial in due)
        {
            if (posted >= PerPass)
                break;

            var account = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == trial.AccountId, ct).ConfigureAwait(false);
            if (account is null || account.IsDeleted || account.IsDisabled)
                continue;

            var buddy = await _db.Users.AsNoTracking()
                .Where(u => u.Id == trial.BuddyId)
                .Select(u => u.Username)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            var events = await _db.CalendarInvites.AsNoTracking()
                .Where(i => i.StaffUserId == account.Id
                         && EventRoles.Contains(i.Role)
                         && i.OccurrenceStartsAt >= trial.StartedAt
                         && i.OccurrenceStartsAt <= now)
                .Select(i => new { i.EventId, i.OccurrenceStartsAt })
                .Distinct()
                .CountAsync(ct)
                .ConfigureAwait(false);

            var outcome = await gateway.PostAsync(channel, [CouncilCard(trial, account, buddy, events, now, style)], ct).ConfigureAwait(false);

            if (!outcome.Sent && !outcome.Unclear)
                return (posted, Refused(outcome, "the Council reminder"));

            trial.CouncilRemindedAt = now;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            posted++;
        }

        return (posted, null);
    }

    private async Task<(int Posted, string? Error)> CheckInsAsync(
        IDiscordGateway gateway, string channel, CardStyle style, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var quietSince = now - QuietFor;

        // Only an account whose sign-in is already 30 days old can be due; the other two kinds of activity
        // are looked up for those alone.
        var quiet = await _db.Users
            .Where(u => u.DeletedAt == null && !u.IsDisabled && (u.LastLoginAt ?? u.CreatedAt) <= quietSince)
            .OrderBy(u => u.LastLoginAt ?? u.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var due = new List<(ModbotUser Account, DateTimeOffset LastActive)>();

        foreach (var account in quiet)
        {
            if (due.Count >= CheckInsPerPost)
                break;

            var lastActive = await LastActiveAsync(account, now, ct).ConfigureAwait(false);

            if (lastActive > quietSince || (account.CheckInRemindedAt is { } reminded && lastActive <= reminded))
                continue;

            due.Add((account, lastActive));
        }

        if (due.Count == 0)
            return (0, null);

        // One post for everyone due, so choosing the channel for the first time does not flood it with a card per
        // long-gone account.
        var outcome = await gateway.PostAsync(channel, [CheckInCard(due, now, style)], ct).ConfigureAwait(false);

        if (!outcome.Sent && !outcome.Unclear)
            return (0, Refused(outcome, "the check-ins"));

        foreach (var (account, _) in due)
            account.CheckInRemindedAt = now;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return (due.Count, null);
    }

    /// <summary>The latest of the account's sign-in, moderation actions and events it was host or staff on.</summary>
    public async Task<DateTimeOffset> LastActiveAsync(ModbotUser account, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(account);

        var modbotId = account.Id.ToString();
        var vrchatId = account.IsVRChatLinked ? account.VRChatUserId : null;
        var discordId = account.IsDiscordProven ? account.DiscordUserId : null;

        var action = await _db.Events.AsNoTracking()
            .Where(e => ActionTypes.Contains(e.Type)
                     && ((e.ActorPlatform == FactPlatform.Modbot && e.ActorId == modbotId)
                         || (vrchatId != null && e.ActorPlatform == FactPlatform.VRChat && e.ActorId == vrchatId)
                         || (discordId != null && e.ActorPlatform == FactPlatform.Discord && e.ActorId == discordId)))
            .MaxAsync(e => (DateTimeOffset?)e.OccurredAt, ct)
            .ConfigureAwait(false);

        var hosted = await _db.CalendarInvites.AsNoTracking()
            .Where(i => i.StaffUserId == account.Id && EventRoles.Contains(i.Role) && i.OccurrenceStartsAt <= now)
            .MaxAsync(i => (DateTimeOffset?)i.OccurrenceStartsAt, ct)
            .ConfigureAwait(false);

        return new[] { account.LastLoginAt ?? account.CreatedAt, action ?? DateTimeOffset.MinValue, hosted ?? DateTimeOffset.MinValue }.Max();
    }

    private string Refused(DiscordPostOutcome outcome, string what)
    {
        var error = outcome.Error ?? "Discord refused the message.";
        _log.Warning("Could not post {What} to Discord: {Reason}", what, error);
        return error;
    }

    /// <summary>The Council's card: the person, the checklist, events worked, the buddy and their note.</summary>
    public static DiscordEmbedContent CouncilCard(
        StaffTrial trial, ModbotUser account, string? buddy, int events, DateTimeOffset now, CardStyle style)
    {
        ArgumentNullException.ThrowIfNull(trial);
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(style);

        var tasks = trial.Items.Where(i => i.Kind == StaffTrialItemKinds.Task).ToList();
        var signOffs = trial.Items.Where(i => i.Kind == StaffTrialItemKinds.SignOff).ToList();

        var fields = new List<DiscordEmbedField>
        {
            new("Tasks answered", $"{tasks.Count(i => i.Answer is not null)} of {tasks.Count}", Inline: true),
            new("Lines signed off", $"{signOffs.Count(i => i.SignedOffAt is not null)} of {signOffs.Count}", Inline: true),
            new("Events worked", events.ToString(CultureInfo.InvariantCulture), Inline: true),
            new("Buddy", string.IsNullOrWhiteSpace(buddy) ? "None" : CardText.Fit(CardText.EscapeName(buddy), CardText.MaxNameLength), Inline: true),
            new("Started", Day(trial.StartedAt), Inline: true),
            new("Buddy note", trial.BuddyNote is { Length: > 0 } note ? CardText.Fit(CardText.EscapeText(note), NoteLength) : "No note."),
        };

        return new DiscordEmbedContent(
            Title: CardText.Plain($"Day 30: {account.Username}", 256),
            Description: null,
            Color: CardColour.Violet,
            Fields: fields,
            Timestamp: now,
            Url: CardLink.UrlFor(CardSubject.Account, account.Id.ToString(), style.PublicAddress),
            Footer: "Council vote",
            FooterIconUrl: style.FooterIconUrl);
    }

    /// <summary>The check-in card: one line for each person, with the day they were last active.</summary>
    public static DiscordEmbedContent CheckInCard(
        IReadOnlyList<(ModbotUser Account, DateTimeOffset LastActive)> people, DateTimeOffset now, CardStyle style)
    {
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(style);

        var lines = people.Select(p =>
        {
            var name = CardText.Fit(CardText.EscapeName(p.Account.Username), CardText.MaxNameLength);
            var url = CardLink.UrlFor(CardSubject.Account, p.Account.Id.ToString(), style.PublicAddress);
            var shown = url is null ? name : $"[{name}]({url})";
            return $"{shown}: last active {Day(p.LastActive)}";
        });

        return new DiscordEmbedContent(
            Title: people.Count == 1 ? "Check-in: 1 person" : $"Check-in: {people.Count.ToString(CultureInfo.InvariantCulture)} people",
            Description: string.Join('\n', lines),
            Color: CardColour.Amber,
            Fields: [],
            Timestamp: now,
            Url: null,
            Footer: "Inactive for 30 days",
            FooterIconUrl: style.FooterIconUrl);
    }

    private static string Day(DateTimeOffset at) => at.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}
