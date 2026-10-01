using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;
using VRChat.API.Model;

namespace Modbot.VRChat.Calendar;

/// <summary>
/// Posts once in the VRChat group's posts when the first person is in the instance Modbot opened for
/// an event (calendar auto-invite design §7).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A group post, not a group announcement.</strong> VRChat's own description of its
/// announcement call says making one removes the group's other announcements, and posts are the
/// newer way. The post call is one Modbot already makes from the VRChat page, on its
/// <c>groups.posts.write</c> budget (one every ten seconds); nothing new is asked of VRChat.
/// </para>
/// <para>
/// Visible to the group, with VRChat's member notification <strong>off</strong>: the post is there
/// for whoever looks, and nobody's phone buzzes because of it.
/// </para>
/// <para>
/// <strong>Once per occurrence.</strong> <c>calendar_opening.first_join_vrchat_posted_at</c> is
/// written before the call, so a restart never posts twice, and a refusal is kept on the row, never
/// tried again. A 429 cold-stops the class (foundation §4.3.1) and is a refusal like any other; only
/// a call the gate never sent is left for a later pass.
/// </para>
/// </remarks>
public sealed class CalendarFirstJoinVRChatPost
{
    /// <summary>The longest one occurrence may last; openings older than this cannot still be running.</summary>
    private static readonly TimeSpan LongestOccurrence = TimeSpan.FromDays(7);

    private readonly IVRChatGate _gate;
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    public CalendarFirstJoinVRChatPost(IVRChatGate gate, ModbotContext db, IModbotClock clock, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _gate = gate;
        _db = db;
        _clock = clock;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>What the post asks VRChat for: the title, the join link, the group only, no notification.</summary>
    internal static CreateGroupPostRequest Request(string title, string? joinLink) => new(
        imageId: null!,
        roleIds: null!,
        sendNotification: false,
        text: joinLink is null ? $"{title} has started." : $"{title} has started.\n{joinLink}",
        title: title,
        visibility: GroupPostVisibility.Group);

    /// <returns>How many posts VRChat accepted.</returns>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);
        if (settings.ManagedGroupId is not { Length: > 0 } groupId)
            return 0;

        var now = _clock.UtcNow;
        var since = now - LongestOccurrence;

        var openings = await _db.CalendarOpenings
            .Where(o => o.Location != null && o.InstanceId != null && o.FirstJoinVRChatPostedAt == null && o.OccurrenceStartsAt > since)
            .ToListAsync(ct).ConfigureAwait(false);

        var posted = 0;

        foreach (var opening in openings)
        {
            var calendarEvent = await _db.CalendarEvents.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == opening.EventId, ct).ConfigureAwait(false);

            if (calendarEvent is null
                || !calendarEvent.AnnounceFirstJoinInVRChat
                || calendarEvent.DeletedAt is not null
                || !CalendarEventStates.IsLive(calendarEvent.State))
            {
                continue;
            }

            if (now >= opening.OccurrenceStartsAt + (calendarEvent.EndsAt - calendarEvent.StartsAt))
                continue;

            var instance = await _db.VRChatInstances.AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == opening.InstanceId, ct).ConfigureAwait(false);

            if (instance is null || instance.ClosedAt is not null)
                continue;

            // The group instance poll's own count: nothing here asks VRChat who is there.
            if ((instance.LastUserCount ?? 0) <= 0 && (instance.PeakUserCount ?? 0) <= 0)
                continue;

            // Written first: once, however many restarts.
            opening.FirstJoinVRChatPostedAt = now;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            var request = Request(calendarEvent.Title, InstanceJoinLink.For(opening.Location));

            var result = await _gate.ExecuteAsync(
                new VRChatEndpoint(VRChatEndpointClass.GroupsPostsWrite, groupId, "AddGroupPost"),
                (client, token) => client.Groups.AddGroupPostWithHttpInfoAsync(groupId, request, cancellationToken: token),
                VRChatCallPriority.Background,
                ct).ConfigureAwait(false);

            if (result.Success)
            {
                posted++;
                continue;
            }

            // Nothing reached VRChat: the class is cold-stopped or there is no session yet. The next
            // pass may try, because there was no first try.
            if (result.WasNotSent && result.Kind is VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting or VRChatFailureKind.NotConfigured)
            {
                opening.FirstJoinVRChatPostedAt = null;
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                break;
            }

            var error = result.ErrorMessage ?? $"VRChat answered {result.StatusCode}.";
            opening.FirstJoinVRChatPostError = error.Length <= 1024 ? error : error[..1024];
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            _log.Warning(
                "Could not post in the group that the event {EventId} has started: {Error}", calendarEvent.Id, error);
        }

        return posted;
    }
}
