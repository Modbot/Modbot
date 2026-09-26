namespace Modbot.Api.Features.Analytics.Server;

/// <summary>People active on one day, and in the week and the thirty days ending on it.</summary>
/// <remarks>Active means sent a message or spent time in voice. Distinct people, so the three cannot be added up from each other.</remarks>
public sealed record ActiveMembersDay(DateOnly Day, int Daily, int Weekly, int Monthly);

/// <param name="Name">The channel's name as last stored, or null when the channel is not known.</param>
public sealed record ChannelMessages(string Id, string? Name, decimal Messages);

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
/// My Server: is the Discord server healthy, and who keeps it going? (M5 spec §6)
/// </summary>
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
