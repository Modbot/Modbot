using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Discord;
using Modbot.Core.Moderation;
using Modbot.Core.Time;
using Modbot.Discord.Alerts;
using Modbot.Discord.Bot;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.Insights;
using Modbot.Discord.Instances;
using Modbot.Discord.Members;
using Modbot.Discord.Linking;
using Modbot.Discord.Messages;
using Modbot.Discord.ModerationLog;
using Modbot.Discord.ServerIndex;

namespace Modbot.Discord;

/// <summary>
/// Registers what Modbot does with Discord: a direct message to one person, and the bot of
/// foundation §9 -- a gateway session, three slash commands, and events sent to channels by route.
/// </summary>
/// <remarks>
/// Registered unconditionally, like the VRChat gate: the bot reads its token from the database
/// and does nothing until one is stored, so a deployment without Discord pays for one settings
/// read every ten seconds and nothing else.
/// </remarks>
public static class DiscordServiceCollectionExtensions
{
    public static IServiceCollection AddModbotDiscord(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(nameof(DiscordRestMessenger));
        services.AddScoped<IDiscordMessenger, DiscordRestMessenger>();

        services.TryAddSingleton<DiscordBotOptions>();
        services.TryAddSingleton<ModerationLogOptions>();
        // IPictures, when the VRChat side is there, so an event cover linked from VRChat can be
        // fetched with its session (calendar design §15.2).
        services.TryAddSingleton<IDiscordGatewayFactory>(p => new DiscordNetGatewayFactory(p.GetService<Core.Files.IPictures>()));

        // The pictures on cards (Discord embeds design §3). A singleton, so what one pass fetched
        // the next one does not fetch again. IPictures comes from the VRChat side and is absent in
        // a host without it; every card then goes without a picture rather than failing to post.
        services.TryAddSingleton(p => new Cards.CardPictures(p.GetService<Core.Files.IPictures>()));

        services.AddSingleton<DiscordBotStatus>();
        services.AddSingleton<IDiscordBotStatus>(p => p.GetRequiredService<DiscordBotStatus>());

        services.AddSingleton<DiscordBotService>();
        services.AddHostedService(p => p.GetRequiredService<DiscordBotService>());
        services.AddHostedService<ModerationLogService>();

        // Its own loop: a deleted announcements channel must not hold up the
        // moderation log, which is the record rather than a notice board.
        services.AddHostedService<InstanceAnnounceService>();

        // Old calendar posts Discord would not let the bot delete, kept between passes so each is
        // asked about once a day and not every twenty seconds.
        services.TryAddSingleton<Calendar.OldPostRefusals>();

        // The calendar's server events and channel posts (calendar design §9). Its own loop too.
        services.AddScoped<Calendar.CalendarDiscordPublisher>();
        services.AddScoped<Calendar.CalendarFirstJoinPost>();
        services.AddScoped<Calendar.CalendarInviteMessages>();
        services.AddHostedService<Calendar.CalendarDiscordService>();

        // Posts from the Marketing tab (posts design §3.5): its own loop, the calendar's shape, and
        // the Marketing tab's edit, delete and publish on a post already on Discord.
        services.AddScoped<Core.Posts.PostClaim>();
        services.AddScoped<Posts.PostDiscordSender>();
        services.AddHostedService<Posts.PostDiscordService>();
        services.AddSingleton<Core.Posts.IDiscordPostActions>(
            p => new Posts.DiscordPostActions(() => p.GetRequiredService<DiscordBotService>().ReadyGateway));

        // The event form's preview of the Discord event and the channel post, from the same
        // builders (calendar design §14).
        services.AddSingleton<Core.Calendar.ICalendarDiscordPreview, Calendar.CalendarDiscordPreviewer>();

        // Giveaway posts and winner announcements (giveaways design §7). Its own loop as well; the
        // closing and drawing themselves are not Discord's business and live in Modbot.Analytics.
        services.AddScoped<Giveaways.GiveawayReactions>();
        services.AddScoped<Giveaways.GiveawayDiscordPublisher>();
        services.AddHostedService<Giveaways.GiveawayDiscordService>();

        // Scheduled AI insights that name a channel (AI insights design §4). Its own loop too.
        services.AddHostedService<InsightPostService>();

        // Unusual-activity alerts (AI insights design §8.4). Its own loop as well, because an alert
        // is worth posting in the next minute and an insight is not.
        services.AddHostedService<AlertPostService>();

        // Linked members' roles (Discord account linking design §6). The signal is shared with the
        // API, which wakes the job when it saves or ends a link.
        services.TryAddSingleton<DiscordLinkSignal>();
        services.AddHostedService<LinkedRoleService>();
        services.AddScoped<LinkedRoles>();
        services.AddScoped<LinkPrompt>();

        // The join gate (join gate design). Its own loop as well; the state is shared by the loop,
        // the buttons and the API's actions, so one change to a person's row happens at a time.
        services.TryAddSingleton<Gate.JoinGateState>();
        services.AddScoped<Gate.JoinGate>();
        services.AddHostedService<Gate.JoinGateService>();
        services.AddSingleton<IJoinGateActions, Gate.JoinGateActions>();

        // Role and ban sync (M5 §3 and §4). Its own loop again, and everything it does is off
        // until somebody switches a direction on.
        services.AddScoped<Sync.CopyRecords>();
        services.AddScoped<Sync.RoleSync>();
        services.AddScoped<Sync.BanSync>();

        // Discord roles from saved lists, on the same loop, off until its own switch is on.
        services.AddScoped<Sync.ListRoleSync>();
        services.AddHostedService<Sync.DiscordSyncService>();
        services.AddSingleton<IDiscordSyncRunner, Sync.DiscordSyncRunner>();

        // Staff roles from Discord (design 2026-10-02). Runs in the same loop, after role and ban
        // sync, and does nothing until somebody maps a role and turns the switch on.
        services.AddScoped<Sync.StaffRoleSync>();
        services.AddSingleton<IStaffRoleRunner, Sync.StaffRoleRunner>();

        // A ban or unban made through Modbot reaches the person's linked Discord account too,
        // whatever the ban sync switches say. Registered after the API's and the AutoMod engine's
        // stand-ins, so it is the one they get where the bot exists.
        services.AddScoped<ILinkedDiscordBans>(provider => new Sync.LinkedDiscordBans(
            provider.GetRequiredService<ModbotContext>(),
            provider.GetRequiredService<IModbotClock>(),
            provider.GetRequiredService<IFactWriter>(),
            provider.GetRequiredService<EventPartitionMaintainer>(),
            provider.GetRequiredService<Sync.CopyRecords>(),
            () => provider.GetRequiredService<DiscordBotService>().ReadyGateway));

        services.AddScoped<LookupQuery>();
        services.TryAddSingleton<MemberCommandLimits>();
        services.AddScoped<MeCommand>();
        services.AddScoped<DiscordCommandHandler>();
        services.AddScoped<ModerationLogPoster>();
        services.AddScoped<InstanceAnnouncer>();
        services.AddScoped<DiscordServerIndex>();
        services.AddScoped<InsightPoster>();
        services.AddScoped<AlertPoster>();

        // What an AI moderation rule set to act does on Discord (M8 §2), through the live session.
        services.AddSingleton<IDiscordModerationActions, DiscordModerationActions>();

        // Ban, unban, remove and time out one member because somebody asked through the API (API
        // conventions design §8), through the same live session.
        services.AddSingleton<IDiscordMemberActions, DiscordMemberActions>();

        // Discord's online count for the server header, asked on page open and kept five minutes.
        services.AddSingleton<IDiscordOnlineCount, DiscordOnlineCount>();

        // The server's scheduled events, for the calendar's possible duplicates (calendar design
        // §16). Asked on page open and kept five minutes, the same way.
        services.AddSingleton<IDiscordServerEvents, DiscordServerEvents>();

        // Messages, stored in full (M5 spec §5.1), and checked by AI moderation. The checker that
        // checks nothing stands in when AI moderation is not registered; when it is, it wins.
        services.AddScoped<DiscordMessageStore>();
        services.AddScoped<DiscordMessageHandler>();
        services.AddScoped<DiscordEventRecorder>();
        services.AddScoped<DiscordBanList>();
        services.TryAddScoped<IModerationChecker, NoModerationChecker>();

        return services;
    }
}
