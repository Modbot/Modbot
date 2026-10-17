using System.Globalization;
using System.Text.Json.Nodes;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Calendar;

/// <summary>
/// Everything a moderator can edit on an event, as the payload of its facts, so a change fact can
/// say what changed (calendar design §8).
/// </summary>
/// <remarks>
/// The description, pictures, category, languages, platforms, tags, visibility and notifying were
/// left out until 2026-09-25, and an edit to only those was recorded as a change with no difference
/// in it. Shared by the page's own saves and by changes read from VRChat's calendar, so both are
/// told the same way.
/// </remarks>
public static class CalendarEventFields
{
    public static JsonObject Of(CalendarEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);

        return new JsonObject
        {
            ["title"] = e.Title,
            ["description"] = e.Description,
            ["vrchatTitle"] = e.VRChatTitle,
            ["vrchatDescription"] = e.VRChatDescription,
            ["discordTitle"] = e.DiscordTitle,
            ["discordDescription"] = e.DiscordDescription,
            ["googleTitle"] = e.GoogleTitle,
            ["googleDescription"] = e.GoogleDescription,
            ["blueskyTitle"] = e.BlueskyTitle,
            ["blueskyDescription"] = e.BlueskyDescription,
            ["imageUrl"] = e.ImageUrl,
            ["vrchatImageId"] = e.VRChatImageId,
            ["coverPictureId"] = e.CoverPictureId?.ToString(),
            ["blueskyPictures"] = e.BlueskyPictures is { } pictures
                ? string.Join(", ", pictures.Select(p => $"{p.PictureId}: {p.Alt}"))
                : null,
            ["blueskyWhoCanReply"] = e.BlueskyWhoCanReply is { } whoCanReply ? string.Join(", ", whoCanReply) : null,
            ["blueskyQuotesOff"] = e.BlueskyQuotesOff,
            ["blueskyLanguages"] = e.BlueskyLanguages is { } blueskyLanguages ? string.Join(", ", blueskyLanguages) : null,
            ["blueskyVideo"] = e.BlueskyVideo is { } video
                ? $"{video.VideoId?.ToString() ?? video.Url}: {video.Alt}"
                : null,
            ["blueskyGif"] = e.BlueskyGif is { } gif ? $"{gif.Url}: {gif.Alt}" : null,
            ["category"] = e.Category,
            ["modbotCategories"] = string.Join(", ", e.Categories.Select(c => c.Name).Order(StringComparer.Ordinal)),
            ["languages"] = string.Join(", ", e.Languages),
            ["platforms"] = string.Join(", ", e.Platforms),
            ["tags"] = string.Join(", ", e.Tags),
            ["visibility"] = e.Visibility,
            ["notifyMembers"] = e.NotifyMembers,
            ["featured"] = e.Featured,
            ["startsAt"] = e.StartsAt.ToString("O", CultureInfo.InvariantCulture),
            ["endsAt"] = e.EndsAt.ToString("O", CultureInfo.InvariantCulture),
            ["timeZone"] = e.TimeZone,
            ["repeat"] = e.Repeat,
            ["repeatDays"] = string.Join(",", e.RepeatDays),
            ["repeatEvery"] = e.RepeatEvery,
            ["repeatUntil"] = e.RepeatUntil?.ToString("O", CultureInfo.InvariantCulture),
            ["repeatTimes"] = e.RepeatTimes,
            ["worldId"] = e.WorldId,
            ["worldListId"] = e.WorldListId?.ToString(),
            ["accessType"] = e.AccessType,
            ["region"] = e.Region,
            ["state"] = e.State,
            ["publishToVRChat"] = e.PublishToVRChat,
            ["publishToDiscord"] = e.PublishToDiscord,
            ["publishToGoogle"] = e.PublishToGoogle,
            ["publishToBluesky"] = e.PublishToBluesky,
            ["blueskyPostBeforeMinutes"] = e.BlueskyPostBeforeMinutes,
            ["postToChannel"] = e.PostToChannel,
            ["channelId"] = e.ChannelId,
            ["mentionRoleId"] = e.MentionRoleId,
            ["postColour"] = e.PostColour,
            ["autoOpen"] = e.AutoOpen,
            ["openMinutesBefore"] = e.OpenMinutesBefore,

            // Staff account and list ids, never a member's: who an event invites is the organiser's
            // choice of accounts and lists, and the people on a list are worked out later.
            ["inviteHost"] = e.InviteHostUserId?.ToString(),
            ["inviteStaff"] = string.Join(",", e.InviteStaffUserIds),
            ["inviteList"] = e.InviteListId?.ToString(),
            ["announceFirstJoinInDiscord"] = e.AnnounceFirstJoinInDiscord,
            ["announceFirstJoinInVRChat"] = e.AnnounceFirstJoinInVRChat,
            ["announceFirstJoinOnBluesky"] = e.AnnounceFirstJoinOnBluesky,
        };
    }
}
