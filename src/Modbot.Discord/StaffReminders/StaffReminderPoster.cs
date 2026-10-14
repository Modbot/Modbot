using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Cards;
using Modbot.Discord.Commands;
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

    /// <summary>The most people one check-in post tries to list; the card keeps what fits and the rest follow in the next pass.</summary>
    public const int CheckInsPerPost = 40;

    /// <summary>The most a card's description holds.</summary>
    private const int DescriptionLength = 4096;

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

        var (council, councilError, stop) = await CouncilAsync(gateway, channel, style, ct).ConfigureAwait(false);

        // A channel Discord refuses outright, or asks to slow down, is the same for both kinds.
        if (stop)
            return new StaffReminderPass(council, 0, councilError);

        var (checkIns, checkInError) = await CheckInsAsync(gateway, channel, style, ct).ConfigureAwait(false);

        return new StaffReminderPass(council, checkIns, checkInError ?? councilError);
    }

    /// <summary>
    /// One post for each trial that has reached day 30. A trial Discord refuses is left for the next pass and the
    /// others still go; two refusals in a row, or one that is not final, stop the pass (<c>Stop</c>).
    /// </summary>
    private async Task<(int Posted, string? Error, bool Stop)> CouncilAsync(
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
        var refusedInARow = 0;
        string? error = null;

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
            {
                error ??= Refused(outcome, "the Council reminder");
                refusedInARow++;

                // Only a final refusal of this one card leaves the others a chance; anything else is the channel's.
                if (!outcome.Permanent || outcome.NotFound || outcome.RateLimited || refusedInARow >= 2)
                    return (posted, error, true);

                continue;
            }

            refusedInARow = 0;
            trial.CouncilRemindedAt = now;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            posted++;
        }

        return (posted, error, false);
    }

    /// <summary>One post listing everyone due. Only the people the post shows are marked, so the rest go next pass.</summary>
    private async Task<(int Posted, string? Error)> CheckInsAsync(
        IDiscordGateway gateway, string channel, CardStyle style, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var quietSince = now - QuietFor;

        // Only an account whose sign-in is already 30 days old can be due; the other two kinds of activity
        // are looked up for those alone, in one query each.
        var quiet = await _db.Users
            .Where(u => u.DeletedAt == null && !u.IsDisabled && (u.LastLoginAt ?? u.CreatedAt) <= quietSince)
            .OrderBy(u => u.LastLoginAt ?? u.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (quiet.Count == 0)
            return (0, null);

        var lastActive = await LastActiveAsync(quiet, now, ct).ConfigureAwait(false);

        var due = quiet
            .Select(account => (Account: account, LastActive: lastActive[account.Id]))
            .Where(p => p.LastActive <= quietSince && !(p.Account.CheckInRemindedAt is { } reminded && p.LastActive <= reminded))
            .Take(CheckInsPerPost)
            .ToList();

        if (due.Count == 0)
            return (0, null);

        // One post for everyone due, so choosing the channel for the first time does not flood it with a card per
        // long-gone account.
        var (card, shown) = CheckInCard(due, now, style);
        var outcome = await gateway.PostAsync(channel, [card], ct).ConfigureAwait(false);

        if (!outcome.Sent && !outcome.Unclear)
            return (0, Refused(outcome, "the check-ins"));

        foreach (var (account, _) in due.Take(shown))
            account.CheckInRemindedAt = now;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return (shown, null);
    }

    /// <summary>
    /// For each account, the latest of its sign-in, moderation actions and events it was host or staff on.
    /// Two queries for all of them, however many accounts there are.
    /// </summary>
    public async Task<Dictionary<Guid, DateTimeOffset>> LastActiveAsync(
        IReadOnlyList<ModbotUser> accounts, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        var ids = accounts.Select(a => a.Id).ToList();
        var modbotIds = accounts.Select(a => a.Id.ToString()).ToList();
        var vrchatIds = accounts.Where(a => a.IsVRChatLinked).Select(a => a.VRChatUserId!).ToList();
        var discordIds = accounts.Where(a => a.IsDiscordProven).Select(a => a.DiscordUserId!).ToList();

        var actions = await _db.Events.AsNoTracking()
            .Where(e => ActionTypes.Contains(e.Type) && e.ActorId != null
                     && ((e.ActorPlatform == FactPlatform.Modbot && modbotIds.Contains(e.ActorId))
                         || (e.ActorPlatform == FactPlatform.VRChat && vrchatIds.Contains(e.ActorId))
                         || (e.ActorPlatform == FactPlatform.Discord && discordIds.Contains(e.ActorId))))
            .GroupBy(e => new { e.ActorPlatform, e.ActorId })
            .Select(g => new { g.Key.ActorPlatform, g.Key.ActorId, At = g.Max(e => e.OccurredAt) })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var hosted = await _db.CalendarInvites.AsNoTracking()
            .Where(i => i.StaffUserId != null && ids.Contains(i.StaffUserId.Value)
                     && EventRoles.Contains(i.Role) && i.OccurrenceStartsAt <= now)
            .GroupBy(i => i.StaffUserId)
            .Select(g => new { Id = g.Key, At = g.Max(i => i.OccurrenceStartsAt) })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        DateTimeOffset ActionAt(FactPlatform platform, string? id) =>
            id is null ? DateTimeOffset.MinValue
                : actions.Where(a => a.ActorPlatform == platform && a.ActorId == id).Select(a => a.At).DefaultIfEmpty(DateTimeOffset.MinValue).Max();

        return accounts.ToDictionary(
            a => a.Id,
            a => new[]
            {
                a.LastLoginAt ?? a.CreatedAt,
                ActionAt(FactPlatform.Modbot, a.Id.ToString()),
                ActionAt(FactPlatform.VRChat, a.IsVRChatLinked ? a.VRChatUserId : null),
                ActionAt(FactPlatform.Discord, a.IsDiscordProven ? a.DiscordUserId : null),
                hosted.Where(h => h.Id == a.Id).Select(h => h.At).DefaultIfEmpty(DateTimeOffset.MinValue).Max(),
            }.Max());
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

    /// <summary>
    /// The check-in card: one line for each person, with the day they were last active. Only as many whole lines
    /// as fit in a card are shown; <c>Shown</c> says how many, counting from the first.
    /// </summary>
    public static (DiscordEmbedContent Card, int Shown) CheckInCard(
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

        var description = DiscordCommandHandler.WholeLines(lines, DescriptionLength);

        // A list that was cut ends with an ellipsis line, which is not a person.
        var count = description.Split('\n').Length - (description.EndsWith("\n…", StringComparison.Ordinal) ? 1 : 0);

        var card = new DiscordEmbedContent(
            Title: count == 1 ? "Check-in: 1 person" : $"Check-in: {count.ToString(CultureInfo.InvariantCulture)} people",
            Description: description,
            Color: CardColour.Amber,
            Fields: [],
            Timestamp: now,
            Url: null,
            Footer: "Inactive for 30 days",
            FooterIconUrl: style.FooterIconUrl);

        return (card, count);
    }

    private static string Day(DateTimeOffset at) => at.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}
