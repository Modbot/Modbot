using Microsoft.EntityFrameworkCore;
using Modbot.AI.Insights;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using NodaTime;
using Serilog;

namespace Modbot.Api.Features.Birthdays;

/// <summary>
/// Posts the day's birthdays in Discord, gives the birthday role for the day and takes it back on a
/// later one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One post a day.</strong> The day is the one the settings' hour, in their time zone, last
/// reached. The pass claims that moment on the settings row before it posts, so a restart, a second
/// pass or a second copy of Modbot cannot post the day twice. Saving the settings marks the moment
/// already past as dealt with, so turning birthdays on never posts for a time that went by before.
/// </para>
/// <para>
/// <strong>A day that could not be tried is tried again.</strong> With the bot offline, or Discord
/// rate limiting it, nothing was posted, so the claim is given back and the next pass tries. A post
/// Discord refused, or one that may or may not have gone through, is not sent again: the claim stays
/// and the problem is kept on the settings for the card to show.
/// </para>
/// <para>
/// <strong>Whose day.</strong> A member who set 29 February is celebrated on 28 February in a year
/// with no 29th. Members who left the server are skipped. The role goes only to members the post
/// names, is refused if it holds a permission the permission watch tells staff about, and comes off
/// at the next day's moment. Times come from <see cref="IModbotClock"/>.
/// </para>
/// </remarks>
public sealed class BirthdayPass
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IDiscordBirthdayActions _discord;
    private readonly ILogger _log;

    public BirthdayPass(ModbotContext db, IModbotClock clock, IDiscordBirthdayActions discord, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(discord);

        _db = db;
        _clock = clock;
        _discord = discord;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>The latest moment the settings name, at or before <paramref name="now"/>.</summary>
    public static DateTimeOffset LatestMoment(Core.Data.Entities.Settings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return InsightTimes.LatestMoment(
            now,
            InsightKinds.EveryDay,
            settings.BirthdayHour,
            weekday: 0,
            InsightTimes.ZoneOrUtc(settings.BirthdayTimeZone));
    }

    /// <summary>The day, in the settings' time zone, that a moment falls on.</summary>
    public static DateOnly DayOf(Core.Data.Entities.Settings settings, DateTimeOffset moment)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var date = Instant.FromDateTimeOffset(moment).InZone(InsightTimes.ZoneOrUtc(settings.BirthdayTimeZone)).Date;
        return new DateOnly(date.Year, date.Month, date.Day);
    }

    /// <summary>Does what is due. Returns whether it posted the day's birthdays.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (settings.DiscordGuildId is not { } guildId)
            return false;

        var now = _clock.UtcNow;
        var moment = LatestMoment(settings, now);
        var today = DayOf(settings, moment);

        // The role comes back even when birthdays have been turned off since it was given.
        var problems = new List<string>();

        if (await TakeRolesBackAsync(guildId, today, ct).ConfigureAwait(false) is { } stuck)
            problems.Add(stuck);

        if (!settings.BirthdaysOn
            || settings.BirthdayChannelId is not { } channelId
            || (settings.BirthdayHandledThrough is { } handled && handled >= moment))
        {
            if (problems.Count > 0)
                await KeepProblemAsync(settings.Id, string.Join(" ", problems), ct).ConfigureAwait(false);

            return false;
        }

        var previous = settings.BirthdayHandledThrough;

        var claimed = await _db.Settings
            .Where(s => s.Id == settings.Id && (s.BirthdayHandledThrough == null || s.BirthdayHandledThrough < moment))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.BirthdayHandledThrough, moment), ct)
            .ConfigureAwait(false);

        if (claimed == 0)
            return false;

        var members = await ListTodayAsync(guildId, today, ct).ConfigureAwait(false);

        if (members.Count == 0)
        {
            await KeepProblemAsync(settings.Id, null, ct).ConfigureAwait(false);
            return false;
        }

        var posted = false;

        foreach (var chunk in members.Chunk(BirthdayRules.MostPerPost))
        {
            var text = BirthdayRules.Fill(settings.BirthdayMessage, [.. chunk.Select(b => $"<@{b.DiscordUserId}>")]);
            var outcome = await _discord.PostAsync(channelId, text, ct).ConfigureAwait(false);

            if (outcome.Done)
            {
                posted = true;
                continue;
            }

            // Nothing was made, so the day is still to do. Once something has gone out it is not
            // sent again, since that would name the same members twice.
            if (!posted && (outcome.BotOffline || outcome.TryAgain))
            {
                await _db.Settings
                    .Where(s => s.Id == settings.Id && s.BirthdayHandledThrough == moment)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.BirthdayHandledThrough, previous), ct)
                    .ConfigureAwait(false);

                return false;
            }

            problems.Add($"The birthday post failed: {outcome.Error}");
            break;
        }

        if (settings.BirthdayRoleId is { } roleId)
            await GiveRoleAsync(guildId, roleId, members, today, problems, ct).ConfigureAwait(false);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await KeepProblemAsync(settings.Id, problems.Count == 0 ? null : string.Join(" ", problems), ct).ConfigureAwait(false);

        if (posted)
            _log.Information("Posted {Count} birthdays for {Day}", members.Count, today);

        return posted;
    }

    /// <summary>The members whose day it is and who are still in the server, by id.</summary>
    private async Task<List<MemberBirthday>> ListTodayAsync(string guildId, DateOnly today, CancellationToken ct)
    {
        // 29 February is month 2 as well, so one month's rows hold it and 28 February's.
        var inMonth = await _db.MemberBirthdays
            .Where(b => b.Month == today.Month)
            .OrderBy(b => b.DiscordUserId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var due = inMonth.Where(b => BirthdayRules.FallsOn(b.Month, b.Day, today)).ToList();

        if (due.Count == 0)
            return due;

        var ids = due.Select(b => b.DiscordUserId).ToList();
        var gone = await _db.DiscordMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId && ids.Contains(m.UserId) && m.LeftAt != null)
            .Select(m => m.UserId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. due.Where(b => !gone.Contains(b.DiscordUserId))];
    }

    /// <summary>
    /// Gives the role to each member. A role that holds a dangerous permission is given to nobody. The
    /// first refusal from Discord stops the rest, so a missing permission is one call, not forty.
    /// </summary>
    private async Task GiveRoleAsync(
        string guildId, string roleId, List<MemberBirthday> members, DateOnly today, List<string> problems, CancellationToken ct)
    {
        if (await BirthdayRoleCheck.RefusalAsync(_db, roleId, ct).ConfigureAwait(false) is { } refusal)
        {
            problems.Add($"The birthday role was not given: {refusal}");
            return;
        }

        foreach (var member in members)
        {
            var outcome = await _discord.ChangeRoleAsync(guildId, member.DiscordUserId, roleId, add: true, ct).ConfigureAwait(false);

            if (outcome.Done)
            {
                member.RoleGivenId = roleId;
                member.RoleGivenOn = today;
                continue;
            }

            if (outcome.NotInServer)
                continue;

            problems.Add($"The birthday role was not given: {outcome.Error}");
            return;
        }
    }

    /// <summary>
    /// Takes the role from every member whose day is over, or who took their birthday off while they
    /// held it. Returns what went wrong when Discord refused, after which the rest wait for the next
    /// pass; null when all is done or only the bot being offline stopped it.
    /// </summary>
    private async Task<string?> TakeRolesBackAsync(string guildId, DateOnly today, CancellationToken ct)
    {
        var holders = await _db.MemberBirthdays
            .Where(b => b.RoleGivenId != null)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var holder in holders.Where(b => b.IsForgotten || b.RoleGivenOn is null || b.RoleGivenOn < today))
        {
            var outcome = await _discord
                .ChangeRoleAsync(guildId, holder.DiscordUserId, holder.RoleGivenId!, add: false, ct)
                .ConfigureAwait(false);

            if (outcome.BotOffline)
                break;

            if (!outcome.Done && !outcome.NotInServer)
                return $"The birthday role was not taken back: {outcome.Error}";

            holder.RoleGivenId = null;
            holder.RoleGivenOn = null;

            if (holder.IsForgotten)
                _db.MemberBirthdays.Remove(holder);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return null;
    }

    private async Task KeepProblemAsync(int settingsId, string? problem, CancellationToken ct)
    {
        var kept = problem is { Length: > 300 } ? problem[..300] : problem;

        await _db.Settings
            .Where(s => s.Id == settingsId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.BirthdayProblem, kept), ct)
            .ConfigureAwait(false);
    }
}
