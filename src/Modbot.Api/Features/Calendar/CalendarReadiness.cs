using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;

namespace Modbot.Api.Features.Calendar;

/// <summary>
/// Whether each place an event can go is set up, so a ticked place that cannot work says
/// "Not set up" in the form, on the event and on Health, instead of doing nothing (calendar design
/// §14.3, added 2026-10-01).
/// </summary>
/// <remarks>
/// <para>
/// <strong>VRChat</strong> (the calendar and opening the instance) needs a managed group and a
/// VRChat account to sign in as. Both publishers stop without them: the VRChat one returns
/// "not configured", the opener has no group to open in.
/// </para>
/// <para>
/// <strong>Discord</strong> (the Discord event and the channel post) needs a server id and a bot
/// that is connected: the Discord loop only runs while the bot has a ready session, and a server
/// event needs the server id. A bot that is connecting or reconnecting counts as not set up while it
/// is, because nothing is sent until it is back.
/// </para>
/// <para>
/// Read from settings and the bot's own status, never by asking VRChat or Discord.
/// </para>
/// </remarks>
public static class CalendarReadiness
{
    public static CalendarReadyView Of(Modbot.Core.Data.Entities.Settings? settings, IDiscordBotStatus? bot) =>
        new(VRChat(settings), Discord(settings, bot));

    public static bool VRChat(Modbot.Core.Data.Entities.Settings? settings) =>
        settings is not null
        && !string.IsNullOrWhiteSpace(settings.ManagedGroupId)
        && !string.IsNullOrWhiteSpace(settings.VRChatUsername)
        && !string.IsNullOrWhiteSpace(settings.VRChatPasswordEncrypted);

    public static bool Discord(Modbot.Core.Data.Entities.Settings? settings, IDiscordBotStatus? bot) =>
        settings is not null
        && !string.IsNullOrWhiteSpace(settings.DiscordGuildId)
        && bot?.Snapshot().State == DiscordBotState.Connected;

    /// <summary>
    /// The places live events want that are not set up: <c>vrchat</c>, <c>instance</c>,
    /// <c>discordEvent</c> and <c>channelPost</c>, in that order.
    /// </summary>
    public static IReadOnlyList<string> NotSetUp(
        CalendarReadyView ready, bool wantsVRChat, bool wantsInstance, bool wantsDiscordEvent, bool wantsChannelPost)
    {
        ArgumentNullException.ThrowIfNull(ready);

        var missing = new List<string>();

        if (!ready.VRChat && wantsVRChat)
            missing.Add(CalendarPlaces.VRChat);

        if (!ready.VRChat && wantsInstance)
            missing.Add("instance");

        if (!ready.Discord && wantsDiscordEvent)
            missing.Add(CalendarPlaces.DiscordEvent);

        if (!ready.Discord && wantsChannelPost)
            missing.Add(CalendarPlaces.ChannelPost);

        return missing;
    }
}
