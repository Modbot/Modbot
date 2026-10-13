using Microsoft.EntityFrameworkCore;
using Modbot.AI.Insights;
using Modbot.Api.Features.Posts;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Net;
using Modbot.Core.Profiles;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Api.Features.Spotlight;

/// <summary>
/// Makes the week's spotlight draft when its moment comes (spotlight design §4 and §6).
/// </summary>
/// <remarks>
/// <para>
/// <strong>One draft a week.</strong> The moment is the latest time at or before now that the
/// settings name (day and hour in their zone). The pass claims it on the settings row, and the week
/// is also one <c>spotlight_week</c> row under a unique index, so a restart, a second pass or a second
/// copy of Modbot cannot make two. Saving the settings marks the moments already past as dealt with,
/// so turning the spotlight on never makes a draft straight away for a time that went by before.
/// </para>
/// <para>
/// <strong>Nothing goes out.</strong> The draft is an ordinary Marketing draft: staff write the note
/// and send it. In random mode the member is picked here; in staff mode the draft waits for staff
/// to pick one on the Marketing page.
/// </para>
/// <para>
/// Everything a pass writes, its audit entry included, is one transaction, so a pass that fails
/// halfway leaves nothing behind and runs again whole a minute later. Times come from
/// <see cref="IModbotClock"/>.
/// </para>
/// </remarks>
public sealed class SpotlightPass
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly AccountFacts _facts;
    private readonly PictureLinks _pictures;
    private readonly Func<int, int> _random;
    private readonly ILogger _log;

    public SpotlightPass(
        ModbotContext db,
        IModbotClock clock,
        AccountFacts facts,
        PictureLinks? pictures = null,
        Func<int, int>? random = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);

        _db = db;
        _clock = clock;
        _facts = facts;
        _pictures = pictures ?? PictureLinks.Shared;
        _random = random ?? Random.Shared.Next;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>The latest moment the settings name, at or before <paramref name="now"/>.</summary>
    public static DateTimeOffset LatestMoment(Core.Data.Entities.Settings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return InsightTimes.LatestMoment(
            now,
            InsightKinds.EveryWeek,
            settings.SpotlightHour,
            settings.SpotlightDay,
            InsightTimes.ZoneOrUtc(settings.SpotlightTimeZone));
    }

    /// <summary>Makes the draft if one is due. Returns whether it made one.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (!settings.SpotlightOn)
            return false;

        var now = _clock.UtcNow;
        var moment = LatestMoment(settings, now);

        if (settings.SpotlightHandledThrough is { } handled && handled >= moment)
            return false;

        await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Claimed first, in the same transaction as the draft: a second pass finds the moment taken,
        // and a pass that fails gives the claim back with the rest.
        var claimed = await _db.Settings
            .Where(s => s.Id == settings.Id && (s.SpotlightHandledThrough == null || s.SpotlightHandledThrough < moment))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.SpotlightHandledThrough, moment), ct)
            .ConfigureAwait(false);

        if (claimed == 0)
            return false;

        var zone = InsightTimes.ZoneOrUtc(settings.SpotlightTimeZone);
        var week = MemberSpotlight.WeekOf(moment, zone);

        // A week with a row already (the day was changed within the week) gets no second draft.
        if (await _db.SpotlightWeeks.AnyAsync(w => w.Week == week, ct).ConfigureAwait(false))
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return false;
        }

        SpotlightMember? member = null;

        if (settings.SpotlightMode == SpotlightModes.Random)
        {
            var pool = await MemberSpotlight.OptedInAsync(_db, ct).ConfigureAwait(false);
            var featured = (await _db.SpotlightWeeks.AsNoTracking()
                    .Select(w => new { w.Week, w.DiscordUserId })
                    .ToListAsync(ct).ConfigureAwait(false))
                .Select(w => (w.Week, w.DiscordUserId));

            member = MemberSpotlight.Pick(pool, featured, week, _random);

            // Nobody opted in: there is nothing to feature this week.
            if (member is null)
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                _log.Information("No spotlight this week: nobody has opted in");
                return false;
            }
        }

        var post = await SpotlightPosts.BuildAsync(_db, settings, member, _pictures, now, ct).ConfigureAwait(false);

        _db.SpotlightWeeks.Add(new SpotlightWeek
        {
            Id = Guid.CreateVersion7(),
            Week = week,
            DiscordUserId = member?.UserId,
            PostId = post.Id,
            CreatedAt = now,
            PickedAt = member is null ? null : now,
        });

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await _facts.RecordAsync(FactType.PostCreated, post.Id.ToString(), actor: null, PostRequests.Describe(post), ct)
            .ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        _log.Information("Made the spotlight draft for the week of {Week}", week);
        return true;
    }
}
