using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modbot.Core.Discord;
using Modbot.Core.Moderation;
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
        services.TryAddSingleton<IDiscordGatewayFactory, DiscordNetGatewayFactory>();

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

        // The calendar's server events and channel posts (calendar design §9). Its own loop too.
        services.AddScoped<Calendar.CalendarDiscordPublisher>();
        services.AddHostedService<Calendar.CalendarDiscordService>();

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

        services.AddScoped<LookupQuery>();
        services.AddScoped<DiscordCommandHandler>();
        services.AddScoped<ModerationLogPoster>();
        services.AddScoped<InstanceAnnouncer>();
        services.AddScoped<DiscordServerIndex>();
        services.AddScoped<InsightPoster>();
        services.AddScoped<AlertPoster>();

        // What an AI moderation rule set to act does on Discord (M8 §2), through the live session.
        services.AddSingleton<IDiscordModerationActions, DiscordModerationActions>();

        // Messages, stored in full (M5 spec §5.1), and checked by AI moderation. The checker that
        // checks nothing stands in when AI moderation is not registered; when it is, it wins.
        services.AddScoped<DiscordMessageStore>();
        services.AddScoped<DiscordMessageHandler>();
        services.AddScoped<DiscordEventRecorder>();
        services.TryAddScoped<IModerationChecker, NoModerationChecker>();

        return services;
    }
}
