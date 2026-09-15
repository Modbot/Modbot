using System.Text.Json;
using Modbot.AI.Chat;
using Modbot.Api.Features.Analytics;
using Modbot.Api.Features.Analytics.Server;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Chat.Tools;

/// <summary>The My Server page's query, summed up.</summary>
/// <remarks>
/// The same daily totals the analytics pages draw, so a question about messages, voice or active
/// members is answered with the figure the page would show rather than a second count of the same
/// rows. Long windows come back as totals; a month or less also comes back per day.
/// </remarks>
internal sealed class ServerAnalyticsTool : ReadTool
{
    /// <summary>How many rows of a list of people or channels one answer carries.</summary>
    private const int Rows = 10;

    /// <summary>Past this many days the per-day series is left out and only the totals are sent.</summary>
    private const int DaysPerDay = 31;

    public override string Name => "server_analytics";

    public override string Label => "Server figures";

    public override string Description =>
        "The Discord server's figures over the last number of days: member count at the start and "
        + "end, joins and leaves, messages, minutes in voice, how many members were active, the "
        + "busiest channels, the people who wrote most, bans, kicks, timeouts and messages removed.";

    protected override string Schema => """
        {"type":"object","properties":{"days":{"type":"integer","minimum":1,"maximum":365,"description":"How many days back, today included. Default 30."}}}
        """;

    public override ModbotPermissions Needs => ModbotPermissions.ViewAnalytics;

    public override async Task<ChatToolResult> RunAsync(ChatToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var days = ChatArguments.Number(arguments, "days", 30, 1, 365);
        var now = Get<IModbotClock>(context).UtcNow;
        var to = AnalyticsSql.DayOf(now);
        var from = to.AddDays(-(days - 1));

        var a = await new ServerAnalyticsQuery(Get<ModbotContext>(context)).RunAsync(from, to, now, ct);

        var contributors = a.TopContributors.Take(Rows).ToList();

        return ChatToolResult.Json(
            new
            {
                a.From,
                a.To,
                memberCountAtStart = a.MemberCount.FirstOrDefault(),
                memberCountAtEnd = a.MemberCount.LastOrDefault(),
                joined = a.Joined.Sum(d => d.Value),
                left = a.Left.Sum(d => d.Value),
                messages = a.Messages.Sum(d => d.Value),
                voiceMinutes = a.VoiceMinutes.Sum(d => d.Value),
                bans = a.Bans.Sum(d => d.Value),
                kicks = a.Kicks.Sum(d => d.Value),
                timeouts = a.Timeouts.Sum(d => d.Value),
                messagesRemoved = a.MessagesRemoved.Sum(d => d.Value),
                messagesPerDay = days <= DaysPerDay ? a.Messages : null,
                voiceMinutesPerDay = days <= DaysPerDay ? a.VoiceMinutes : null,
                activePerDay = days <= DaysPerDay ? a.Active : null,
                activeOnTheLastDay = a.Active.LastOrDefault(),
                busiestChannels = a.BusiestChannels.Take(Rows).Select(c => new { channelId = c.Id, c.Name, c.Messages }),
                topContributors = contributors.Select(c => new
                {
                    discordUserId = c.Who.Id,
                    name = c.Who.Name,
                    c.Messages,
                    c.VoiceMinutes,
                }),
                newMembers = a.NewMembers,
                health = a.Health with { Quiet = a.Health.Quiet.Take(Rows).ToList() },
                a.Coverage,
            },
            contributors.Select(c => new ChatReference(ChatReference.DiscordPerson, c.Who.Id, c.Who.Name)));
    }
}
