using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Npgsql;
using Serilog;

namespace Modbot.Discord.Calendar;

/// <summary>
/// Acts on the member milestones: a post in the chosen channel when people reach their first
/// moment, become Regulars or come back after a long time, and the Regular role for a Regular with
/// a linked account.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What is counted</strong> comes from <see cref="MemberMilestoneCounter"/>, which runs
/// whatever is switched on here. This pass only acts on moments reached after the posts or the role
/// were last turned on (<see cref="Settings.MilestonesFrom"/>): a person who reached the number
/// before it is left alone, so turning them on never posts for people who got there long ago.
/// </para>
/// <para>
/// <strong>Once per person.</strong> Each moment is claimed with a conditional update before it is
/// posted, so a restart or a second copy of Modbot cannot post it twice; a moment is claimed even
/// when its post is off, so switching that post on later does not post for people it passed over. A
/// refusal that may pass gives the claim back so the next pass tries again; a final one, or one that
/// may have gone through, does not. What went wrong is kept on the settings, where the card shows it.
/// </para>
/// <para>
/// <strong>One post for each moment and pass,</strong> naming everyone who reached it, so a night
/// with forty first-timers is one post. Names are shown as VRChat shows them, in an embed, which
/// pings nobody, even a person with a linked account. The role is given only to a Regular with a
/// linked account who is still in the server, never removed, and refused if it holds a permission
/// the permission watch tells staff about.
/// </para>
/// </remarks>
public sealed class MemberMilestonePost
{
    /// <summary>How many people one moment handles in one pass; the rest wait for the next.</summary>
    public const int PerPass = 100;

    private const int ProblemAtMost = 300;

    private enum Moment
    {
        First,
        Regular,
        Role,
    }

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    public MemberMilestonePost(ModbotContext db, IModbotClock clock, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    /// <returns>How many posts went out.</returns>
    public async Task<int> RunOnceAsync(IDiscordGateway gateway, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (settings.MilestonesFrom is not { } from)
            return 0;

        var channelId = settings.MilestoneChannelId is { Length: > 0 } channel ? channel.Trim() : null;
        var roleId = settings.MilestoneRegularRoleId is { Length: > 0 } role ? role.Trim() : null;

        if (roleId is null && (channelId is null || !(settings.MilestonePostFirst || settings.MilestonePostRegular || settings.MilestonePostBack)))
            return 0;

        var now = _clock.UtcNow;
        var problems = new List<string>();
        var worked = false;
        var posted = 0;

        var first = await ClaimAsync(Moment.First, settings.MilestoneFirstAt, from, now, ct).ConfigureAwait(false);

        if (first.Count > 0 && settings.MilestonePostFirst && channelId is not null)
        {
            var sent = await PostAsync(
                gateway, channelId, $"Came to their {Ordinal(settings.MilestoneFirstAt)} event", first, problems,
                () => GiveBackAsync(Moment.First, first, now, ct), ct).ConfigureAwait(false);

            posted += sent ? 1 : 0;
            worked |= sent;
        }

        var regular = await ClaimAsync(Moment.Regular, settings.MilestoneRegularAt, from, now, ct).ConfigureAwait(false);

        if (regular.Count > 0 && settings.MilestonePostRegular && channelId is not null)
        {
            var sent = await PostAsync(
                gateway, channelId, "Now a Regular", regular, problems,
                () => GiveBackAsync(Moment.Regular, regular, now, ct), ct).ConfigureAwait(false);

            posted += sent ? 1 : 0;
            worked |= sent;
        }

        var back = await ClaimBackAsync(from, ct).ConfigureAwait(false);

        if (back.Count > 0 && settings.MilestonePostBack && channelId is not null)
        {
            var sent = await PostAsync(
                gateway, channelId, "Welcome back", [.. back.Select(b => b.UserId)], problems,
                () => GiveBackBackAsync(back, ct), ct).ConfigureAwait(false);

            posted += sent ? 1 : 0;
            worked |= sent;
        }

        if (roleId is not null)
        {
            var before = problems.Count;

            if (await GiveRoleAsync(gateway, settings, roleId, from, now, problems, ct).ConfigureAwait(false) && problems.Count == before)
                worked = true;
        }

        if (problems.Count > 0)
        {
            settings.MilestoneProblem = CardText.Fit(string.Join(" ", problems), ProblemAtMost);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        else if (worked && settings.MilestoneProblem is not null)
        {
            settings.MilestoneProblem = null;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return posted;
    }

    /// <summary>
    /// The people who reached <paramref name="number"/> events after <paramref name="from"/> and whose
    /// moment nobody has dealt with, claimed: marked here before anything is posted, so only the copy
    /// that marks a person gets to name them.
    /// </summary>
    private async Task<List<string>> ClaimAsync(
        Moment moment, int number, DateTimeOffset from, DateTimeOffset now, CancellationToken ct)
    {
        var ids = await ReachedAsync(moment, number, from, ct).ConfigureAwait(false);

        if (ids.Count == 0)
            return [];

        await EnsureRowsAsync(ids, ct).ConfigureAwait(false);

        var won = new List<string>(ids.Count);

        foreach (var id in ids)
        {
            var marked = moment == Moment.First
                ? await _db.MemberMilestones
                    .Where(m => m.UserId == id && m.FirstHandledAt == null)
                    .ExecuteUpdateAsync(set => set.SetProperty(m => m.FirstHandledAt, now), ct).ConfigureAwait(false)
                : await _db.MemberMilestones
                    .Where(m => m.UserId == id && m.RegularHandledAt == null)
                    .ExecuteUpdateAsync(set => set.SetProperty(m => m.RegularHandledAt, now), ct).ConfigureAwait(false);

            if (marked > 0)
                won.Add(id);
        }

        return won;
    }

    private async Task GiveBackAsync(Moment moment, List<string> ids, DateTimeOffset marked, CancellationToken ct)
    {
        if (moment == Moment.First)
        {
            await _db.MemberMilestones
                .Where(m => ids.Contains(m.UserId) && m.FirstHandledAt == marked)
                .ExecuteUpdateAsync(set => set.SetProperty(m => m.FirstHandledAt, (DateTimeOffset?)null), ct).ConfigureAwait(false);
        }
        else
        {
            await _db.MemberMilestones
                .Where(m => ids.Contains(m.UserId) && m.RegularHandledAt == marked)
                .ExecuteUpdateAsync(set => set.SetProperty(m => m.RegularHandledAt, (DateTimeOffset?)null), ct).ConfigureAwait(false);
        }
    }

    /// <summary>The people with a return after <paramref name="from"/> that nobody has dealt with, claimed.</summary>
    private async Task<List<(string UserId, DateTimeOffset BackAt)>> ClaimBackAsync(DateTimeOffset from, CancellationToken ct)
    {
        var waiting = await _db.MemberMilestones.AsNoTracking()
            .Where(m => m.BackAt != null && m.BackAt >= from && (m.BackHandledFor == null || m.BackHandledFor < m.BackAt))
            .OrderBy(m => m.BackAt)
            .Take(PerPass)
            .Select(m => new { m.UserId, BackAt = m.BackAt!.Value })
            .ToListAsync(ct).ConfigureAwait(false);

        var won = new List<(string, DateTimeOffset)>(waiting.Count);

        foreach (var person in waiting)
        {
            var marked = await _db.MemberMilestones
                .Where(m => m.UserId == person.UserId
                    && m.BackAt == person.BackAt
                    && (m.BackHandledFor == null || m.BackHandledFor < m.BackAt))
                .ExecuteUpdateAsync(set => set.SetProperty(m => m.BackHandledFor, m => m.BackAt), ct).ConfigureAwait(false);

            if (marked > 0)
                won.Add((person.UserId, person.BackAt));
        }

        return won;
    }

    private async Task GiveBackBackAsync(List<(string UserId, DateTimeOffset BackAt)> people, CancellationToken ct)
    {
        foreach (var (userId, backAt) in people)
        {
            await _db.MemberMilestones
                .Where(m => m.UserId == userId && m.BackHandledFor == backAt)
                .ExecuteUpdateAsync(set => set.SetProperty(m => m.BackHandledFor, (DateTimeOffset?)null), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// People whose count of events reached <paramref name="number"/> after <paramref name="from"/>: they
    /// have that many, and had fewer when the milestones were turned on. Without those whose moment
    /// is already dealt with.
    /// </summary>
    private async Task<List<string>> ReachedAsync(Moment moment, int number, DateTimeOffset from, CancellationToken ct)
    {
        var reached = _db.EventAttendees.AsNoTracking()
            .GroupBy(a => a.UserId)
            .Where(g => g.Count() >= number && g.Count(a => a.CountedAt < from) < number);

        reached = moment switch
        {
            Moment.First => reached.Where(g => !_db.MemberMilestones.Any(m => m.UserId == g.Key && m.FirstHandledAt != null)),
            Moment.Regular => reached.Where(g => !_db.MemberMilestones.Any(m => m.UserId == g.Key && m.RegularHandledAt != null)),
            _ => reached.Where(g => !_db.MemberMilestones.Any(m => m.UserId == g.Key && m.RegularRoleGivenAt != null)),
        };

        return await reached.OrderBy(g => g.Key).Select(g => g.Key).Take(PerPass).ToListAsync(ct).ConfigureAwait(false);
    }

    private Task<int> EnsureRowsAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
        => _db.Database.ExecuteSqlRawAsync(
            "INSERT INTO member_milestone (user_id) SELECT u FROM unnest(@ids) AS u ON CONFLICT DO NOTHING",
            [new NpgsqlParameter("ids", ids.ToArray())],
            ct);

    /// <summary>
    /// Posts one embed naming these people. True when it went up. When Discord refused in a way that
    /// may pass, <paramref name="giveBack"/> clears the claim so the next pass tries again.
    /// </summary>
    private async Task<bool> PostAsync(
        IDiscordGateway gateway,
        string channelId,
        string title,
        List<string> ids,
        List<string> problems,
        Func<Task> giveBack,
        CancellationToken ct)
    {
        var names = await NamesAsync(ids, ct).ConfigureAwait(false);

        var embed = new DiscordEmbedContent(
            Title: title,
            Description: EventRecapPost.NameList(names, ids.Count),
            Color: CardColour.Dark,
            Fields: [],
            Timestamp: _clock.UtcNow,
            Url: null,
            Footer: null);

        var outcome = await gateway.PostAsync(channelId, text: null, [embed], links: null, ct).ConfigureAwait(false);

        if (outcome.Sent)
            return true;

        problems.Add(outcome.Error ?? "Discord refused the milestone post.");
        _log.Warning("Could not post the milestone {Title}: {Error}", title, outcome.Error);

        // A refusal that may pass is tried again; a final one, or one that may have gone through,
        // is not, so a post never doubles.
        if (!outcome.Permanent && !outcome.Unclear)
            await giveBack().ConfigureAwait(false);

        return false;
    }

    /// <summary>Gives the Regular role to the Regulars with a linked account. True when it tried anyone.</summary>
    private async Task<bool> GiveRoleAsync(
        IDiscordGateway gateway,
        Settings settings,
        string roleId,
        DateTimeOffset from,
        DateTimeOffset now,
        List<string> problems,
        CancellationToken ct)
    {
        if (settings.DiscordGuildId is not { Length: > 0 } guildId)
            return false;

        var ids = await ReachedAsync(Moment.Role, settings.MilestoneRegularAt, from, ct).ConfigureAwait(false);

        if (ids.Count == 0)
            return false;

        var skipUntil = now - TimeSpan.FromDays(1);

        var links = await _db.DiscordAccountLinks.AsNoTracking()
            .Where(l => ids.Contains(l.VRChatUserId)
                && l.UnlinkedAt == null
                && (l.NotInServerAt == null || l.NotInServerAt < skipUntil))
            .Select(l => new { l.VRChatUserId, l.DiscordUserId })
            .ToListAsync(ct).ConfigureAwait(false);

        if (links.Count == 0)
            return false;

        var discordIds = links.Select(l => l.DiscordUserId).ToList();

        var gone = await _db.DiscordMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId && discordIds.Contains(m.UserId) && m.LeftAt != null)
            .Select(m => m.UserId)
            .ToListAsync(ct).ConfigureAwait(false);

        var due = links.Where(l => !gone.Contains(l.DiscordUserId)).ToList();

        if (due.Count == 0)
            return false;

        if (await MilestoneRoleCheck.RefusalAsync(_db, roleId, ct).ConfigureAwait(false) is { } refusal)
        {
            problems.Add("The Regular role was not given: " + refusal);
            return true;
        }

        foreach (var link in due)
        {
            // The gateway notes a role it gives as Modbot's own, so the permission watch stays quiet.
            var outcome = await gateway
                .ChangeRoleAsync(guildId, link.DiscordUserId, roleId, add: true, "Modbot: Regular", ct)
                .ConfigureAwait(false);

            if (outcome.Done)
            {
                await _db.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO member_milestone (user_id, regular_role_given_at) VALUES (@user, @now)
                    ON CONFLICT (user_id) DO UPDATE
                        SET regular_role_given_at = COALESCE(member_milestone.regular_role_given_at, @now)
                    """,
                    [new NpgsqlParameter("user", link.VRChatUserId), new NpgsqlParameter("now", now.ToUniversalTime())],
                    ct).ConfigureAwait(false);

                continue;
            }

            if (outcome.NotInServer)
                continue;

            // The rest would be refused the same way, so the card gets the one problem.
            problems.Add(outcome.RoleGone ? "The Regular role no longer exists." : "The Regular role was not given: " + outcome.Error);
            return true;
        }

        return true;
    }

    private async Task<IReadOnlyList<string>> NamesAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var names = await _db.VRChatUsers.AsNoTracking()
            .Where(u => ids.Contains(u.UserId) && u.DisplayName != null && u.DisplayName != "")
            .Select(u => u.DisplayName!)
            .ToListAsync(ct).ConfigureAwait(false);

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>1st, 2nd, 3rd, 4th, 11th, 21st.</summary>
    public static string Ordinal(int number)
    {
        var tail = number % 100 is >= 11 and <= 13
            ? "th"
            : (number % 10) switch
            {
                1 => "st",
                2 => "nd",
                3 => "rd",
                _ => "th",
            };

        return number.ToString(CultureInfo.InvariantCulture) + tail;
    }
}
