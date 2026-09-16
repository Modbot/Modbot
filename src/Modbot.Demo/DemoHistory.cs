using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Messages;
using Modbot.Analytics.Reviews;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Demo;

/// <summary>
/// The year behind the demo: the fact log, the Discord messages, and the totals computed from both.
/// </summary>
/// <remarks>
/// <para>
/// This is the half that takes time — roughly eighteen thousand facts and five thousand messages —
/// so it runs after startup rather than during it, and reports where it has got to on the Health
/// page (demo mode design §5). The app is usable throughout: the group, the people and the rooms
/// are already there, and the charts fill in behind them.
/// </para>
/// <para>
/// <strong>Nothing is typed into the daily totals.</strong> Every figure the analytics pages show is
/// computed from these facts by the same job that computes them on a real deployment
/// (<see cref="DailyTotalsJob.RebuildAsync"/>), and the repeat-offender counts and moderator
/// baselines by the same review job. That is what makes the demo's numbers add up: they are not a
/// second story about the data, they are the data (§4).
/// </para>
/// </remarks>
public sealed class DemoHistory
{
    /// <summary>How many facts are written before the progress line moves.</summary>
    private const int ReportEvery = 250;

    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly MessagePartitionMaintainer _messagePartitions;
    private readonly DailyTotalsJob _totals;
    private readonly ReviewJob _reviews;
    private readonly IDailyTotalCounter _counter;
    private readonly IModbotClock _clock;

    public DemoHistory(
        ModbotContext db,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        MessagePartitionMaintainer messagePartitions,
        DailyTotalsJob totals,
        ReviewJob reviews,
        IDailyTotalCounter counter,
        IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(messagePartitions);
        ArgumentNullException.ThrowIfNull(totals);
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _facts = facts;
        _partitions = partitions;
        _messagePartitions = messagePartitions;
        _totals = totals;
        _reviews = reviews;
        _counter = counter;
        _clock = clock;
    }

    public async Task WriteAsync(DemoPlan plan, DemoState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);

        await PartitionsAsync(plan, ct);

        var facts = Facts(plan).ToList();

        state.Begin("Writing the group's history", facts.Count);

        var written = 0;

        foreach (var fact in facts)
        {
            await _facts.WriteAsync(fact, ct);

            if (++written % ReportEvery == 0)
                state.Advance(written);
        }

        state.Advance(facts.Count);

        await MessagesAsync(plan, state, ct);

        state.Begin("Working out the totals");
        await _totals.RebuildAsync(ct);

        state.Begin("Working out repeat offenders and reviews");
        await _reviews.RebuildAsync(ct);

        // Discord's member count is the one figure the daily totals job cannot derive from facts
        // -- it is a level, not a change -- so it goes in the counted way, exactly as the bot
        // writes it on a real deployment.
        state.Begin("Counting the Discord server");
        await DiscordMemberCountsAsync(plan, ct);
    }

    /// <summary>Monthly partitions for every month the demo writes into, past and present.</summary>
    private async Task PartitionsAsync(DemoPlan plan, CancellationToken ct)
    {
        var month = plan.Now.AddDays(-DemoPlan.DaysOfHistory - 2);
        var instants = new List<DateTimeOffset>();

        while (month <= plan.Now.AddMonths(1))
        {
            await _partitions.EnsureForAsync(month, ct);
            instants.Add(month);
            month = month.AddMonths(1);
        }

        await _messagePartitions.EnsureForAsync(instants, ct);
    }

    // --- the facts --------------------------------------------------------------------------

    private static IEnumerable<FactRecord> Facts(DemoPlan plan)
    {
        foreach (var fact in Membership(plan))
            yield return fact;

        foreach (var fact in Rooms(plan))
            yield return fact;

        foreach (var fact in Moderation(plan))
            yield return fact;

        foreach (var fact in Discord(plan))
            yield return fact;

        foreach (var fact in Modbot(plan))
            yield return fact;
    }

    private static IEnumerable<FactRecord> Membership(DemoPlan plan)
    {
        foreach (var person in plan.People)
        {
            yield return new FactRecord
            {
                Type = FactType.MemberJoined,
                OccurredAt = person.JoinedGroupAt,
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = person.UserId,
                Source = FactSource.AuditLog,
                Data = Held(person.GroupRoles, [], []),
            };

            // A leave is noticed by comparing two sweeps, so it carries the window it happened in
            // rather than an instant it did not (foundation §5.3).
            if (person.LeftGroupAt is { } left)
            {
                yield return new FactRecord
                {
                    Type = FactType.MemberLeft,
                    OccurredAt = left,
                    OccurredBefore = left.AddHours(4),
                    SubjectPlatform = FactPlatform.VRChat,
                    SubjectId = person.UserId,
                    Source = FactSource.SyncDiff,
                    Data = Held(person.GroupRoles, [], []),
                };
            }

            // The roles above Member were given by somebody, at some point.
            foreach (var role in person.GroupRoles.Where(r => r != "Member"))
            {
                var granted = person.JoinedGroupAt.AddDays(20);

                if (granted >= plan.Now)
                    continue;

                var by = plan.Staff[DemoPlan.Spread(person.UserId, plan.Staff.Count)];

                yield return new FactRecord
                {
                    Type = FactType.RoleGranted,
                    OccurredAt = granted,
                    SubjectPlatform = FactPlatform.VRChat,
                    SubjectId = person.UserId,
                    ActorPlatform = FactPlatform.VRChat,
                    ActorId = by.UserId,
                    Source = FactSource.AuditLog,
                    Data = Held(person.GroupRoles, by.GroupRoles, [], new JsonObject { ["role"] = role }),
                };
            }
        }
    }

    private static IEnumerable<FactRecord> Rooms(DemoPlan plan)
    {
        var staff = plan.Staff;

        foreach (var room in plan.Rooms)
        {
            var opener = staff[DemoPlan.Spread(room.Number, staff.Count)];

            yield return new FactRecord
            {
                Type = FactType.GroupInstanceCreated,
                OccurredAt = room.OpenedAt,
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = opener.UserId,
                ActorPlatform = FactPlatform.VRChat,
                ActorId = opener.UserId,
                WorldId = room.World.WorldId,
                InstanceId = room.Number,
                Source = FactSource.AuditLog,
                Data = Held(opener.GroupRoles, opener.GroupRoles, [], new JsonObject
                {
                    ["worldName"] = room.World.Name,
                    ["region"] = room.Region,
                }),
            };

            if (room.ClosedAt is { } closed)
            {
                yield return new FactRecord
                {
                    Type = FactType.GroupInstanceClosed,
                    OccurredAt = closed,
                    SubjectPlatform = FactPlatform.VRChat,
                    SubjectId = opener.UserId,
                    WorldId = room.World.WorldId,
                    InstanceId = room.Number,
                    Source = FactSource.AuditLog,
                    Data = Held([], [], [], new JsonObject { ["worldName"] = room.World.Name }),
                };
            }

            // Presence, as a moderator's desktop client reports it. The Live page reads these and
            // nothing else, and only counts a report from a paired client whose owner has linked a
            // VRChat account -- so every report names one of the demo's own devices (§4.4).
            var deviceIndex = DemoPlan.Spread(room.Number, DemoPlan.StaffCount);
            var device = DemoSeeder.DeviceIdOf(deviceIndex).ToString();

            foreach (var visit in room.Visits)
            {
                yield return new FactRecord
                {
                    Type = FactType.InstanceJoined,
                    OccurredAt = visit.Arrived,
                    SubjectPlatform = FactPlatform.VRChat,
                    SubjectId = visit.Person.UserId,
                    WorldId = room.World.WorldId,
                    InstanceId = room.Number,
                    Source = FactSource.Client,
                    Data = new JsonObject
                    {
                        ["deviceId"] = device,
                        ["displayName"] = visit.Person.DisplayName,
                    },
                };

                if (visit.Left is { } left)
                {
                    yield return new FactRecord
                    {
                        Type = FactType.InstanceLeft,
                        OccurredAt = left,
                        SubjectPlatform = FactPlatform.VRChat,
                        SubjectId = visit.Person.UserId,
                        WorldId = room.World.WorldId,
                        InstanceId = room.Number,
                        Source = FactSource.Client,
                        Data = new JsonObject
                        {
                            ["deviceId"] = device,
                            ["displayName"] = visit.Person.DisplayName,
                        },
                    };
                }
            }
        }
    }

    private static IEnumerable<FactRecord> Moderation(DemoPlan plan)
    {
        foreach (var action in plan.Actions)
        {
            yield return new FactRecord
            {
                Type = action.Type,
                OccurredAt = action.At,
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = action.Subject.UserId,
                ActorPlatform = FactPlatform.VRChat,
                ActorId = action.Moderator.UserId,
                WorldId = action.Room?.World.WorldId,
                InstanceId = action.Room?.Number,
                Source = FactSource.AuditLog,
                Data = Held(
                    action.Subject.GroupRoles,
                    action.Moderator.GroupRoles,
                    [],
                    new JsonObject { ["reason"] = action.Reason }),
            };
        }
    }

    private static IEnumerable<FactRecord> Discord(DemoPlan plan)
    {
        var voiceChannel = DemoPlan.Snowflake(3012);
        var people = plan.People.Where(p => p.DiscordUserId is not null).ToList();
        var random = new Random(77);

        foreach (var person in people)
        {
            yield return new FactRecord
            {
                Type = FactType.DiscordMemberJoined,
                OccurredAt = person.JoinedGroupAt.AddDays(-1),
                SubjectPlatform = FactPlatform.Discord,
                SubjectId = person.DiscordUserId!,
                Source = FactSource.Discord,
                Data = Held([], [], [], new JsonObject { ["username"] = person.DisplayName }),
            };

            if (person.LeftGroupAt is { } left)
            {
                yield return new FactRecord
                {
                    Type = FactType.DiscordMemberLeft,
                    OccurredAt = left,
                    SubjectPlatform = FactPlatform.Discord,
                    SubjectId = person.DiscordUserId!,
                    Source = FactSource.Discord,
                    Data = Held([], [], [], new JsonObject { ["username"] = person.DisplayName }),
                };
            }
        }

        // Voice: evenings in the lounge, for people who are still around.
        var present = people.Where(p => p.LeftGroupAt is null).ToList();

        for (var dayBack = DemoPlan.DaysOfHistory; dayBack >= 0; dayBack--)
        {
            var day = plan.Now.AddDays(-dayBack).Date;
            var sessions = random.Next(0, 7);

            for (var i = 0; i < sessions; i++)
            {
                var person = present[random.Next(present.Count)];

                if (person.JoinedGroupAt > plan.Now.AddDays(-dayBack))
                    continue;

                var joined = new DateTimeOffset(day.AddHours(19 + random.Next(0, 5)).AddMinutes(random.Next(0, 60)), TimeSpan.Zero);

                if (joined >= plan.Now)
                    continue;

                var leftAt = joined.AddMinutes(random.Next(8, 180));

                yield return new FactRecord
                {
                    Type = FactType.DiscordVoiceJoined,
                    OccurredAt = joined,
                    SubjectPlatform = FactPlatform.Discord,
                    SubjectId = person.DiscordUserId!,
                    Source = FactSource.Discord,
                    Data = Held([], [], [], new JsonObject { ["channelId"] = voiceChannel }),
                };

                if (leftAt < plan.Now)
                {
                    yield return new FactRecord
                    {
                        Type = FactType.DiscordVoiceLeft,
                        OccurredAt = leftAt,
                        SubjectPlatform = FactPlatform.Discord,
                        SubjectId = person.DiscordUserId!,
                        Source = FactSource.Discord,
                        Data = Held([], [], [], new JsonObject { ["channelId"] = voiceChannel }),
                    };
                }
            }
        }

        // A handful of Discord-side moderation, so that page is not all joins and leaves.
        var moderator = plan.Staff[0];
        var offenders = plan.People.Where(p => p.Kind == DemoPersonKind.Offender && p.DiscordUserId is not null).ToList();

        foreach (var person in offenders.Take(14))
        {
            var at = person.JoinedGroupAt.AddDays(random.Next(5, 120));

            if (at >= plan.Now)
                at = plan.Now.AddDays(-random.Next(2, 40));

            yield return new FactRecord
            {
                Type = random.Next(100) < 55 ? FactType.DiscordMemberTimedOut : FactType.DiscordMemberKicked,
                OccurredAt = at,
                SubjectPlatform = FactPlatform.Discord,
                SubjectId = person.DiscordUserId!,
                ActorPlatform = FactPlatform.VRChat,
                ActorId = moderator.UserId,
                Source = FactSource.Discord,
                Data = Held([], moderator.GroupRoles, [], new JsonObject
                {
                    ["reason"] = DemoWords.WarnReasons[random.Next(DemoWords.WarnReasons.Length)],
                }),
            };
        }
    }

    /// <summary>Modbot's own audit entries: the fact log is the audit log (foundation §5.9).</summary>
    private static IEnumerable<FactRecord> Modbot(DemoPlan plan)
    {
        var random = new Random(991);

        for (var dayBack = 90; dayBack >= 0; dayBack--)
        {
            var day = plan.Now.AddDays(-dayBack);
            var signIns = random.Next(0, 5);

            for (var i = 0; i < signIns; i++)
            {
                var person = plan.Staff[random.Next(plan.Staff.Count)];
                var at = day.Date.AddHours(random.Next(8, 23)).AddMinutes(random.Next(0, 60));
                var when = new DateTimeOffset(at, TimeSpan.Zero);

                if (when >= plan.Now)
                    continue;

                yield return new FactRecord
                {
                    Type = FactType.Login,
                    OccurredAt = when,
                    SubjectPlatform = FactPlatform.Modbot,
                    SubjectId = person.UserId,
                    Source = FactSource.Modbot,
                    Data = new JsonObject { ["username"] = person.DisplayName },
                };
            }
        }

        var administrator = plan.Staff[0];

        (string Type, int DaysBack, JsonObject Data)[] once =
        [
            (FactType.SettingsChanged, 300, new JsonObject { ["setting"] = "managedGroupId" }),
            (FactType.SettingsChanged, 180, new JsonObject { ["setting"] = "presenceFactRetentionDays" }),
            (FactType.ApiKeyCreated, 120, new JsonObject { ["name"] = "Events to the group's dashboard" }),
            (FactType.WebhookCreated, 90, new JsonObject { ["name"] = "Bans to the team's chat" }),
            (FactType.BanReasonsChanged, 60, new JsonObject { ["added"] = "Evading a ban" }),
            (FactType.RoleChanged, 45, new JsonObject { ["role"] = "Moderator" }),
        ];

        foreach (var (type, daysBack, data) in once)
        {
            yield return new FactRecord
            {
                Type = type,
                OccurredAt = plan.Now.AddDays(-daysBack),
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = administrator.UserId,
                ActorPlatform = FactPlatform.Modbot,
                ActorId = administrator.UserId,
                Source = FactSource.Modbot,
                Data = data,
            };
        }

        for (var index = 0; index < DemoWords.Events.Length; index++)
        {
            yield return new FactRecord
            {
                Type = FactType.PlannedEventCreated,
                OccurredAt = plan.Now.AddDays(-30 + index),
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = DemoPlan.Fixed(4000 + index),
                ActorPlatform = FactPlatform.Modbot,
                ActorId = administrator.UserId,
                Source = FactSource.Modbot,
                Data = new JsonObject { ["title"] = DemoWords.Events[index].Title },
            };
        }
    }

    /// <summary>
    /// The roles everybody held at the time, put in the payload so the writer does not have to look
    /// them up. It would otherwise make three queries per fact, which over eighteen thousand facts
    /// is the difference between a minute of seeding and most of an hour.
    /// </summary>
    private static JsonObject Held(
        IReadOnlyList<string> subjectRoles,
        IReadOnlyList<string> actorRoles,
        IReadOnlyList<Guid> actorModbotRoles,
        JsonObject? rest = null)
    {
        var data = rest ?? [];

        data[HeldRoles.Key] = new HeldRoles(
            subjectRoles,
            actorRoles,
            [.. actorModbotRoles.Select(id => id.ToString())]).ToJson();

        return data;
    }

    // --- Discord messages ---------------------------------------------------------------------

    private async Task MessagesAsync(DemoPlan plan, DemoState state, CancellationToken ct)
    {
        var random = new Random(4242);
        var authors = plan.People.Where(p => p.DiscordUserId is not null).ToList();

        var channels = DemoWords.Channels
            .Select((c, i) => (Id: DemoPlan.Snowflake(3000 + i), c.Name, c.Type, c.Lines))
            .Where(c => c.Type != "voice" && c.Lines.Length > 0)
            .ToList();

        state.Begin("Writing the Discord messages", DemoPlan.DaysOfHistory);

        var id = 700_000_000_000_000_000L;
        var batch = 0;

        for (var dayBack = DemoPlan.DaysOfHistory; dayBack >= 0; dayBack--)
        {
            var day = plan.Now.AddDays(-dayBack).Date;

            foreach (var channel in channels)
            {
                // The busy channels carry most of it, the quiet ones a message or two a week.
                var count = channel.Name switch
                {
                    "general" => random.Next(2, 20),
                    "off-topic" or "help" or "photos" => random.Next(0, 6),
                    "mod-chat" => random.Next(0, 4),
                    "announcements" or "rules" => random.Next(0, 2) == 0 ? 0 : 1,
                    _ => random.Next(0, 3),
                };

                for (var i = 0; i < count; i++)
                {
                    var author = authors[random.Next(authors.Count)];

                    if (author.JoinedGroupAt > plan.Now.AddDays(-dayBack))
                        continue;

                    var sent = new DateTimeOffset(
                        day.AddHours(random.Next(7, 24)).AddMinutes(random.Next(0, 60)).AddSeconds(random.Next(0, 60)),
                        TimeSpan.Zero);

                    if (sent >= plan.Now)
                        continue;

                    var text = channel.Lines[random.Next(channel.Lines.Length)];

                    _db.DiscordMessages.Add(new DiscordMessage
                    {
                        MessageId = (id++).ToString(CultureInfo.InvariantCulture),
                        SentAt = sent,
                        GuildId = plan.DiscordGuildId,
                        ChannelId = channel.Id,
                        AuthorId = author.DiscordUserId!,
                        AuthorName = author.DisplayName,
                        Text = text,
                        Attachments = "[]",
                        MentionCount = text.Contains('@', StringComparison.Ordinal) ? 1 : 0,

                        // A few are taken down, so the "deleted messages" part of a profile is not
                        // permanently empty.
                        DeletedAt = random.Next(100) < 2 ? sent.AddMinutes(random.Next(1, 90)) : null,
                        StoredAt = sent.AddSeconds(2),
                    });

                    batch++;
                }
            }

            if (batch >= 1000)
            {
                await _db.SaveChangesAsync(ct);
                _db.ChangeTracker.Clear();
                batch = 0;
            }

            state.Advance(DemoPlan.DaysOfHistory - dayBack);
        }

        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    private async Task DiscordMemberCountsAsync(DemoPlan plan, CancellationToken ct)
    {
        var joins = await _db.DiscordMembers.AsNoTracking()
            .Select(m => new { m.JoinedAt, m.LeftAt })
            .ToListAsync(ct);

        for (var dayBack = DemoPlan.DaysOfHistory; dayBack >= 0; dayBack--)
        {
            var at = plan.Now.AddDays(-dayBack);
            var day = DateOnly.FromDateTime(at.UtcDateTime);

            var count = joins.Count(j => j.JoinedAt <= at && (j.LeftAt is null || j.LeftAt > at));

            await _counter.SetAsync(DailyTotalMetrics.DiscordMembersCount, count, day: day, ct: ct);
        }

        // Nothing here reads the clock directly; the day above comes from the plan, which was
        // built from IModbotClock (foundation §4.4).
        _ = _clock;
    }
}
