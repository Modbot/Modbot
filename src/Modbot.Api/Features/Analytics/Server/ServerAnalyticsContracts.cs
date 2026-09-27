namespace Modbot.Api.Features.Analytics.Server;

/// <summary>People active on one day, and in the week and the thirty days ending on it.</summary>
/// <remarks>Active means sent a message or spent time in voice. Distinct people, so the three cannot be added up from each other.</remarks>
public sealed record ActiveMembersDay(DateOnly Day, int Daily, int Weekly, int Monthly);

/// <param name="Name">The channel's name as last stored, or null when the channel is not known.</param>
/// <param name="Type">
/// The channel's kind as stored (<c>text</c>, <c>voice</c>, <c>forum</c> and so on), or null when the
/// channel is not known. A voice channel's own text chat counts under the voice channel.
/// </param>
/// <param name="Category">The name of the category it sits in, or null for none.</param>
/// <param name="Removed">Whether the channel has since been deleted in Discord.</param>
public sealed record ChannelMessages(string Id, string? Name, string? Type, string? Category, bool Removed, decimal Messages);

/// <summary>One number for the last seven days and the seven before them.</summary>
public sealed record WeekPair(decimal ThisWeek, decimal LastWeek);

/// <summary>
/// The last seven days against the seven before, whatever the window: the numbers Discord's Server
/// Insights opens on, so a moderator can see at once whether things are going up or down.
/// </summary>
/// <param name="From">The first day of this week: six days before <paramref name="To"/>.</param>
/// <param name="To">Today by the server's clock, so not over yet.</param>
/// <param name="NewMembers">
/// People who joined in the week and are still in the server, bots left out. Joins that left again,
/// as a captcha bot's kicks do, are not counted, so the number reads like Discord's own.
/// </param>
/// <param name="Talked">Distinct people who sent a message or were in voice.</param>
/// <param name="Messages">Messages sent.</param>
/// <param name="VoiceMinutes">Minutes spent in voice.</param>
public sealed record ServerWeek(
    DateOnly From,
    DateOnly To,
    WeekPair NewMembers,
    WeekPair Talked,
    WeekPair Messages,
    WeekPair VoiceMinutes);

/// <summary>
/// How much of the server the bot can read, from the permissions stored on each channel's row.
/// Messages in a channel it cannot read are counted nowhere, and kicks and removed messages are
/// only ever read from the audit log, so the page says when either is missing.
/// </summary>
/// <param name="ChannelsRead">Channels the bot may both view and read the history of.</param>
/// <param name="Channels">Every channel that is not a category and has not been deleted.</param>
/// <param name="AuditLog">Whether the bot may read the audit log.</param>
public sealed record ServerReach(int ChannelsRead, int Channels, bool AuditLog);

/// <summary>How long the members in the server now have been in it, bots left out.</summary>
public sealed record MemberTenure(int UnderAMonth, int OneToSixMonths, int SixToTwelveMonths, int YearOrMore);

/// <summary>Who the members are, as things are now and whatever the window. Bots are left out throughout.</summary>
/// <param name="Members">People in the server now.</param>
/// <param name="Linked">Of them, how many have an active link to a VRChat account.</param>
/// <param name="Joined">People who joined in the last thirty days, whether or not they are still in.</param>
/// <param name="NewAccounts">
/// Of those, how many joined from a Discord account less than thirty days old. An account's age is
/// read from its id, as the server's own "Est." is.
/// </param>
/// <param name="NewAccountsStillHere">Of the new accounts, how many are still in the server.</param>
public sealed record MembersNow(
    int Members,
    int Linked,
    int Joined,
    int NewAccounts,
    int NewAccountsStillHere,
    MemberTenure Tenure);

/// <param name="Messages">Messages sent in the window.</param>
/// <param name="VoiceMinutes">Minutes in voice in the window.</param>
public sealed record Contributor(Person Who, decimal Messages, decimal VoiceMinutes);

/// <summary>New members who stayed, for one span after joining.</summary>
/// <param name="Days">How long after joining: 7 or 30.</param>
/// <param name="Joined">People who joined in the window at least <paramref name="Days"/> days ago.</param>
/// <param name="StillHere">Of those, how many had not left, been kicked or been banned by then.</param>
/// <param name="StillActive">Of those, how many sent a message or were in voice in the week from then.</param>
public sealed record NewMembersStayed(int Days, int Joined, int StillHere, int StillActive);

/// <summary>How the membership is doing now, whatever the window.</summary>
/// <param name="Members">People in the server now, bots left out.</param>
/// <param name="ActiveLast30Days">Of them, how many were active in the last thirty days.</param>
/// <param name="WentQuiet">Members active in the thirty days before that and not since.</param>
/// <param name="Quiet">The ones who went quiet, the busiest before first. At most twenty.</param>
public sealed record MemberHealth(int Members, int ActiveLast30Days, int WentQuiet, IReadOnlyList<Contributor> Quiet);

/// <summary>Messages by hour of the week, UTC: index 0 is Monday 00:00, 167 is Sunday 23:00.</summary>
public sealed record MessageHours(IReadOnlyList<decimal> Messages);

/// <summary>
/// The server as Discord's own server profile shows it, for the top of the page. Whatever the window.
/// </summary>
/// <param name="GuildId">The server in settings, or null when none is set.</param>
/// <param name="Name">The server's name as the bot last read it, or null before it has.</param>
/// <param name="IconUrl">The server's icon, or null for none.</param>
/// <param name="BannerUrl">The server's banner, or null for none.</param>
/// <param name="CreatedAt">
/// When the server was made, read from its id: a Discord id carries the moment it was made. Null
/// when the id is not a number.
/// </param>
/// <param name="Members">Discord's own member count, bots included, from the newest reading.</param>
/// <param name="Online">
/// How many members Discord counts as online, asked of Discord and kept five minutes. Null when
/// the bot is not connected or Discord did not answer.
/// </param>
/// <param name="BoostCount">How many boosts the server has, or null when not known.</param>
/// <param name="BoostLevel">The boost level Discord gives the server, 0 to 3, or null when not known.</param>
public sealed record ServerProfile(
    string? GuildId,
    string? Name,
    string? IconUrl,
    string? BannerUrl,
    DateTimeOffset? CreatedAt,
    int? Members,
    int? Online,
    int? BoostCount,
    int? BoostLevel);

/// <summary>
/// My Server: is the Discord server healthy, and who keeps it going? (M5 spec §6)
/// </summary>
/// <param name="Server">The server itself, as it is now. Read from what the bot stored, all but the online count.</param>
/// <param name="Week">The last seven days against the seven before, whatever the window.</param>
/// <param name="Reach">How much of the server the bot can read, or null when no server is set.</param>
/// <param name="MembersNow">Who the members are now, whatever the window.</param>
/// <param name="MemberCount">Discord's own member count, the last reading of each day it was read.</param>
/// <param name="MessagesRemoved">Times a moderator removed messages, one or many at once.</param>
/// <param name="Today">The window's last day when it is today by the server's clock, so not over yet.</param>
/// <param name="DaysWithoutBot">
/// Days before the bot first read the server. Member counts, joins, leaves, voice and moderation
/// have no record for them. Stretches the bot was disconnected are not recorded, so not in here.
/// </param>
/// <param name="DaysWithoutMessages">
/// Days before both the first stored message and the bot's first day. The bot reads history back
/// when it signs in, so messages can start earlier than everything else.
/// </param>
public sealed record ServerAnalytics(
    DateOnly From,
    DateOnly To,
    DateOnly? Today,
    ServerProfile Server,
    ServerWeek Week,
    ServerReach? Reach,
    MembersNow MembersNow,
    IReadOnlyList<DayValue> MemberCount,
    IReadOnlyList<DayValue> Joined,
    IReadOnlyList<DayValue> Left,
    IReadOnlyList<DayValue> Messages,
    IReadOnlyList<DayValue> VoiceMinutes,
    IReadOnlyList<ActiveMembersDay> Active,
    IReadOnlyList<ChannelMessages> BusiestChannels,
    MessageHours HourOfWeek,
    IReadOnlyList<NewMembersStayed> NewMembers,
    IReadOnlyList<DayValue> Bans,
    IReadOnlyList<DayValue> Kicks,
    IReadOnlyList<DayValue> Timeouts,
    IReadOnlyList<DayValue> MessagesRemoved,
    IReadOnlyList<Contributor> TopContributors,
    MemberHealth Health,
    IReadOnlyList<DateOnly> DaysWithoutBot,
    IReadOnlyList<DateOnly> DaysWithoutMessages,
    AnalyticsCoverage Coverage,
    DateTimeOffset GeneratedAt);
