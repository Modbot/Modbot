using System.Text;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using NodaTime;

namespace Modbot.Core.Profiles;

/// <summary>A member who can be featured: opted in, still in the server, not a bot.</summary>
/// <param name="UserId">Their Discord user id.</param>
/// <param name="Name">The name the server shows.</param>
/// <param name="AvatarUrl">The picture Modbot has for them, or null.</param>
public sealed record SpotlightMember(string UserId, string Name, string? AvatarUrl);

/// <summary>
/// The weekly member spotlight's rules (spotlight design): who may be featured, who comes next, and
/// the words of the card. Pure where it can be, so the rules are tested without a database.
/// </summary>
public static class MemberSpotlight
{
    /// <summary>How many weeks back a member counts as featured lately.</summary>
    public const int RepeatWeeks = 8;

    /// <summary>The title every spotlight draft starts with.</summary>
    public const string Title = "Member spotlight";

    /// <summary>The members who opted in and can be featured now, by name.</summary>
    public static async Task<List<SpotlightMember>> OptedInAsync(ModbotContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var rows = await (
                from p in db.MemberProfiles.AsNoTracking()
                where p.Spotlight
                join m in db.DiscordMembers.AsNoTracking() on p.DiscordUserId equals m.UserId
                where m.LeftAt == null && !m.IsBot
                select new { m.UserId, m.DisplayName, m.AvatarUrl })
            .ToListAsync(ct).ConfigureAwait(false);

        return
        [
            .. rows
                .GroupBy(r => r.UserId, StringComparer.Ordinal)
                .Select(g => g.First())
                .Select(r => new SpotlightMember(r.UserId, r.DisplayName, r.AvatarUrl))
                .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(m => m.UserId, StringComparer.Ordinal),
        ];
    }

    /// <summary>The Monday of the week <paramref name="moment"/> falls in, in <paramref name="zone"/>.</summary>
    public static DateOnly WeekOf(DateTimeOffset moment, DateTimeZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var date = Instant.FromDateTimeOffset(moment).InZone(zone).Date;
        var monday = date.PlusDays(-((int)date.DayOfWeek - 1));
        return new DateOnly(monday.Year, monday.Month, monday.Day);
    }

    /// <summary>
    /// How many weeks back count as lately for a pool this size: <see cref="RepeatWeeks"/>, or fewer
    /// when the pool is small, so someone is always left to pick.
    /// </summary>
    public static int WeeksBack(int poolSize) => Math.Clamp(poolSize - 1, 0, RepeatWeeks);

    /// <summary>
    /// Picks one member of <paramref name="pool"/> who was not featured in the weeks before
    /// <paramref name="thisWeek"/> that <see cref="WeeksBack"/> allows. Null for an empty pool.
    /// </summary>
    /// <param name="featured">The past spotlights, as (week, member) pairs; a week with no member is left out.</param>
    /// <param name="next">Gives a number from 0 up to, not including, the argument.</param>
    public static SpotlightMember? Pick(
        IReadOnlyList<SpotlightMember> pool,
        IEnumerable<(DateOnly Week, string? UserId)> featured,
        DateOnly thisWeek,
        Func<int, int> next)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(featured);
        ArgumentNullException.ThrowIfNull(next);

        if (pool.Count == 0)
            return null;

        var since = thisWeek.AddDays(-7 * WeeksBack(pool.Count));

        var lately = featured
            .Where(f => f.UserId is not null && f.Week >= since && f.Week < thisWeek)
            .Select(f => f.UserId!)
            .ToHashSet(StringComparer.Ordinal);

        var open = pool.Where(m => !lately.Contains(m.UserId)).ToList();
        var from = open.Count > 0 ? open : [.. pool];

        return from[next(from.Count)];
    }

    /// <summary>
    /// The draft's title and words for a member: their name and the answers they opened to Members,
    /// one line each. Nothing at Staff or Only me level is ever in it (spotlight design §5).
    /// </summary>
    public static async Task<(string Title, string Text)> CardAsync(
        ModbotContext db, string name, string discordUserId, CancellationToken ct)
    {
        var lines = await MemberProfiles.LinesAsync(db, discordUserId, ProfileViewer.Member, ct).ConfigureAwait(false);
        return Card(name, lines);
    }

    /// <summary>The title and words for <paramref name="name"/> from the lines a member may read.</summary>
    public static (string Title, string Text) Card(string name, IReadOnlyList<ProfileLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var title = $"{Title}: {name.Trim()}";
        if (title.Length > Post.MaxTitleLength)
            title = title[..Post.MaxTitleLength];

        var text = new StringBuilder();

        foreach (var line in lines.Where(l => l.Answer is not null))
            text.Append(line.Name).Append(": ").Append(line.Answer).Append('\n');

        // 25 fields of 300 characters can pass what a post keeps; staff see the cut in the draft.
        var words = text.ToString().TrimEnd();
        return (title, words.Length > Post.MaxTextLength ? words[..Post.MaxTextLength] : words);
    }
}
