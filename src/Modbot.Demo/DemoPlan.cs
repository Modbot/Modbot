using System.Globalization;

namespace Modbot.Demo;

/// <summary>What sort of member somebody is. Decides how much of a history they get.</summary>
public enum DemoPersonKind
{
    /// <summary>In most weeks, for most of the year.</summary>
    Regular,

    /// <summary>Joined in the last few weeks.</summary>
    Newcomer,

    /// <summary>Has been acted on more than once.</summary>
    Offender,

    /// <summary>Joined, then left.</summary>
    Left,

    /// <summary>On the moderation team. Has a Modbot account as well as a VRChat one.</summary>
    Staff,
}

public sealed record DemoPerson
{
    public required string UserId { get; init; }
    public required string DisplayName { get; init; }
    public required DemoPersonKind Kind { get; init; }
    public required string Bio { get; init; }
    public required string Pronouns { get; init; }
    public required DateOnly DateJoined { get; init; }
    public required string Platform { get; init; }
    public required string Status { get; init; }
    public required string StatusDescription { get; init; }
    public required string Picture { get; init; }
    public required string Avatar { get; init; }

    /// <summary>VRChat's own trust tags, which is how trust level is stored.</summary>
    public required string[] Tags { get; init; }

    public required bool Is18Plus { get; init; }

    /// <summary>When they joined the group, and when they left it — or null for still here.</summary>
    public required DateTimeOffset JoinedGroupAt { get; init; }

    public required DateTimeOffset? LeftGroupAt { get; init; }

    /// <summary>The group roles they hold, from <see cref="DemoWords.GroupRoles"/>.</summary>
    public required string[] GroupRoles { get; init; }

    /// <summary>Their Discord account, when they have one Modbot knows about.</summary>
    public required string? DiscordUserId { get; init; }

    /// <summary>Whether the two accounts are linked in Modbot.</summary>
    public required bool DiscordLinked { get; init; }
}

public sealed record DemoWorld
{
    public required string WorldId { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required int Capacity { get; init; }
    public required string Image { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
}

/// <summary>One session of one world: when it opened, who was in it, and whether it is still open.</summary>
public sealed record DemoInstance
{
    public required Guid Id { get; init; }
    public required DemoWorld World { get; init; }
    public required string Number { get; init; }
    public required string Region { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }
    public required DateTimeOffset? ClosedAt { get; init; }
    public required IReadOnlyList<DemoVisit> Visits { get; init; }
    public required int Peak { get; init; }

    /// <summary>
    /// Which of the team opened it, as a place in <see cref="DemoPlan.Staff"/>.
    /// </summary>
    /// <remarks>
    /// Their paired client is what reports who is in the instance, so this decides whose device the
    /// instance's presence facts carry. Live shows names only while a moderator's client is in the
    /// instance (<c>InstanceWatching</c>), so an instance whose opener never walked into it is a head count and
    /// nothing else.
    /// </remarks>
    public required int OpenedBy { get; init; }

    public bool IsOpen => ClosedAt is null;

    public string Location(string groupId) =>
        $"{World.WorldId}:{Number}~group({groupId})~groupAccessType(members)~region({Region})";
}

/// <param name="Left">Null while the person is still in the instance.</param>
public sealed record DemoVisit(DemoPerson Person, DateTimeOffset Arrived, DateTimeOffset? Left);

/// <summary>One thing a moderator did to somebody.</summary>
public sealed record DemoAction(
    string Type,
    DemoPerson Subject,
    DemoPerson Moderator,
    DateTimeOffset At,
    string Reason,
    DemoInstance? Instance);

/// <summary>
/// The whole demo group, worked out in memory before a single row is written.
/// </summary>
/// <remarks>
/// <para>
/// Everything the demo shows comes from here, which is what makes it hang together: a ban points at
/// a person who exists, in an instance that existed, by a moderator who was on the team that week. The
/// analytics are then computed from the facts these become, so the charts add up by construction
/// rather than by being typed in (demo mode design §4).
/// </para>
/// <para>
/// The numbers are drawn from a fixed seed, so two demos started a minute apart hold the same group
/// — but every instant is measured back from <em>now</em>, so the charts always run up to today and
/// the Live page always has instances open (§4.1).
/// </para>
/// </remarks>
public sealed class DemoPlan
{
    /// <summary>The seed. Changing it changes every made-up name in the demo.</summary>
    private const int Seed = 20260916;

    public const int PeopleCount = 420;
    public const int StaffCount = 8;
    public const int DiscordMemberCount = 300;
    public const int DaysOfHistory = 365;

    /// <summary>How many instances the demo leaves open, so Live always has a handful in it.</summary>
    public const int InstancesOpenNow = 4;

    private DemoPlan(DateTimeOffset now)
    {
        Now = now;
    }

    public DateTimeOffset Now { get; }

    public string GroupId { get; private set; } = string.Empty;

    public string GroupName { get; private set; } = string.Empty;

    public string DiscordGuildId { get; private set; } = string.Empty;

    public IReadOnlyList<DemoPerson> People { get; private set; } = [];

    /// <summary>The people who are on the moderation team, first of them the demo administrator.</summary>
    public IReadOnlyList<DemoPerson> Staff { get; private set; } = [];

    public IReadOnlyList<DemoWorld> Worlds { get; private set; } = [];

    public IReadOnlyList<DemoInstance> Instances { get; private set; } = [];

    public IReadOnlyList<DemoAction> Actions { get; private set; } = [];

    public static DemoPlan Build(DateTimeOffset now)
    {
        var plan = new DemoPlan(now.ToUniversalTime());
        var random = new Random(Seed);

        plan.GroupId = "grp_" + Fixed(1);
        plan.GroupName = "The Long Porch";
        plan.DiscordGuildId = Snowflake(1);

        plan.People = BuildPeople(random, plan.Now);
        plan.Staff = plan.People.Where(p => p.Kind == DemoPersonKind.Staff).ToList();
        plan.Worlds = BuildWorlds(plan.Now);
        plan.Instances = BuildInstances(random, plan);
        plan.Actions = BuildActions(random, plan);

        return plan;
    }

    // --- people -----------------------------------------------------------------------------

    private static List<DemoPerson> BuildPeople(Random random, DateTimeOffset now)
    {
        var people = new List<DemoPerson>(PeopleCount);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < PeopleCount; index++)
        {
            var kind = KindOf(index);

            var name = kind == DemoPersonKind.Staff
                ? DemoWords.StaffNames[index] + "-" + DemoWords.NameSecond[random.Next(DemoWords.NameSecond.Length)]
                : UniqueName(random, used);

            // How long they have been in VRChat, which is the "age of account" the profile shows.
            var accountAgeDays = kind switch
            {
                DemoPersonKind.Staff => random.Next(700, 2600),
                DemoPersonKind.Regular => random.Next(200, 2200),
                DemoPersonKind.Newcomer => random.Next(3, 120),
                DemoPersonKind.Offender => random.Next(1, 300),
                _ => random.Next(60, 1400),
            };

            var joinedGroup = kind switch
            {
                DemoPersonKind.Staff => now.AddDays(-random.Next(240, DaysOfHistory)),
                DemoPersonKind.Regular => now.AddDays(-random.Next(60, DaysOfHistory)),
                DemoPersonKind.Newcomer => now.AddDays(-random.Next(1, 35)),
                DemoPersonKind.Offender => now.AddDays(-random.Next(20, 300)),
                _ => now.AddDays(-random.Next(120, DaysOfHistory)),
            };

            var left = kind == DemoPersonKind.Left
                ? joinedGroup.AddDays(random.Next(10, 200))
                : (DateTimeOffset?)null;

            if (left >= now)
                left = now.AddDays(-random.Next(1, 20));

            var is18Plus = kind == DemoPersonKind.Staff || random.Next(100) < 55;

            people.Add(new DemoPerson
            {
                UserId = "usr_" + Fixed(100 + index),
                DisplayName = name,
                Kind = kind,
                Bio = DemoWords.Bios[random.Next(DemoWords.Bios.Length)],
                Pronouns = DemoWords.Pronouns[random.Next(DemoWords.Pronouns.Length)],
                DateJoined = DateOnly.FromDateTime(now.AddDays(-accountAgeDays).UtcDateTime),
                Platform = DemoWords.Platforms[random.Next(100) < 75 ? 0 : random.Next(1, 3)],
                Status = DemoWords.Statuses[random.Next(DemoWords.Statuses.Length)],
                StatusDescription = DemoWords.StatusLines[random.Next(DemoWords.StatusLines.Length)],
                Picture = DemoPictures.Gradient(index * 7 + 3),
                Avatar = DemoPictures.Gradient(index * 11 + 29),
                Tags = TrustTags(accountAgeDays, kind, random),
                Is18Plus = is18Plus,
                JoinedGroupAt = joinedGroup,
                LeftGroupAt = left,
                GroupRoles = GroupRolesFor(kind, accountAgeDays, random),
                DiscordUserId = random.Next(100) < 72 ? Snowflake(1000 + index) : null,
                DiscordLinked = kind == DemoPersonKind.Staff || random.Next(100) < 38,
            });
        }

        return people;
    }

    /// <summary>
    /// The mix: a team, a core of regulars, a stream of newcomers, a handful of repeat offenders
    /// and the people who drifted away.
    /// </summary>
    private static DemoPersonKind KindOf(int index) => index switch
    {
        < StaffCount => DemoPersonKind.Staff,
        < 260 => DemoPersonKind.Regular,
        < 330 => DemoPersonKind.Newcomer,
        < 352 => DemoPersonKind.Offender,
        _ => DemoPersonKind.Left,
    };

    private static string UniqueName(Random random, HashSet<string> used)
    {
        for (var attempt = 0; ; attempt++)
        {
            var candidate = DemoWords.NameFirst[random.Next(DemoWords.NameFirst.Length)]
                + DemoWords.NameSecond[random.Next(DemoWords.NameSecond.Length)]
                + DemoWords.NameTail[random.Next(DemoWords.NameTail.Length)];

            if (attempt > 6)
                candidate += random.Next(10, 99).ToString(CultureInfo.InvariantCulture);

            if (used.Add(candidate))
                return candidate;
        }
    }

    /// <summary>VRChat stores trust as tags, so the demo does too (foundation §3.1.1: never parsed).</summary>
    private static string[] TrustTags(int accountAgeDays, DemoPersonKind kind, Random random)
    {
        var trust = (accountAgeDays, kind) switch
        {
            (_, DemoPersonKind.Staff) => "system_trust_veteran",
            (< 30, _) => "system_trust_basic",
            (< 120, _) => "system_trust_known",
            (< 500, _) => "system_trust_trusted",
            _ => random.Next(100) < 30 ? "system_trust_veteran" : "system_trust_trusted",
        };

        var tags = new List<string> { trust, "system_avatar_access", "system_feedback_access" };

        if (accountAgeDays > 45)
            tags.Add("system_world_access");

        return [.. tags];
    }

    private static string[] GroupRolesFor(DemoPersonKind kind, int accountAgeDays, Random random) => kind switch
    {
        DemoPersonKind.Staff => random.Next(100) < 25 ? ["Member", "Moderator", "Admin"] : ["Member", "Moderator"],
        DemoPersonKind.Regular when accountAgeDays > 400 && random.Next(100) < 30
            => ["Member", "Regular", "Event host"],
        DemoPersonKind.Regular when random.Next(100) < 45 => ["Member", "Regular"],
        _ => ["Member"],
    };

    // --- places -----------------------------------------------------------------------------

    private static List<DemoWorld> BuildWorlds(DateTimeOffset now)
    {
        var worlds = new List<DemoWorld>();

        for (var index = 0; index < DemoWords.Worlds.Length; index++)
        {
            var (name, description, capacity) = DemoWords.Worlds[index];

            worlds.Add(new DemoWorld
            {
                WorldId = "wrld_" + Fixed(500 + index),
                Name = name,
                Description = description,
                Capacity = capacity,
                Image = DemoPictures.Gradient(index * 23 + 101),
                PublishedAt = now.AddDays(-(400 + (index * 37))),
            });
        }

        return worlds;
    }

    /// <summary>
    /// A year of instances, ending with a few that are still open — so Live always has something in it.
    /// </summary>
    private static List<DemoInstance> BuildInstances(Random random, DemoPlan plan)
    {
        var instances = new List<DemoInstance>();
        var now = plan.Now;

        // Who was available to be in an instance on a given day: in the group, and not yet gone.
        var candidates = plan.People
            .Where(p => p.Kind != DemoPersonKind.Newcomer || true)
            .ToList();

        var number = 10000;

        for (var dayBack = DaysOfHistory; dayBack >= 0; dayBack--)
        {
            var day = now.AddDays(-dayBack);

            // Fridays and Saturdays are busier, and the group opens nothing at all some weekdays.
            var busy = day.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday;
            var instancesToday = busy ? random.Next(2, 5) : random.Next(0, 3);

            for (var i = 0; i < instancesToday; i++)
            {
                var world = plan.Worlds[random.Next(plan.Worlds.Count)];

                // Evenings, in the group's own time.
                var opened = day.Date.AddHours(18 + random.Next(0, 6)).AddMinutes(random.Next(0, 60));
                var openedAt = new DateTimeOffset(opened, TimeSpan.Zero);

                if (openedAt > now)
                    openedAt = now.AddMinutes(-random.Next(5, 90));

                var minutes = random.Next(45, 260);
                var closedAt = openedAt.AddMinutes(minutes);

                // Nothing the year's dice opened is left open. An instance still open by chance is one
                // nobody on the team happens to be standing in, so Live would show it as a head
                // count with no names -- and which instances those were would change with the hour the
                // demo was started. The instances that are open right now are the ones below.
                if (closedAt > now)
                    closedAt = now;

                var here = random.Next(busy ? 6 : 3, busy ? 22 : 14);
                var visits = new List<DemoVisit>();
                var pool = candidates
                    .Where(p => p.JoinedGroupAt <= openedAt && (p.LeftGroupAt is null || p.LeftGroupAt > openedAt))
                    .ToList();

                if (pool.Count == 0)
                    continue;

                var chosen = new HashSet<string>(StringComparer.Ordinal);

                for (var v = 0; v < here; v++)
                {
                    var person = pool[random.Next(pool.Count)];
                    if (!chosen.Add(person.UserId))
                        continue;

                    var arrived = openedAt.AddMinutes(random.Next(0, Math.Max(1, minutes - 10)));
                    var stayed = random.Next(12, Math.Max(20, minutes));
                    var left = arrived.AddMinutes(stayed);

                    visits.Add(new DemoVisit(person, arrived, left > closedAt ? closedAt : left));
                }

                if (visits.Count == 0)
                    continue;

                instances.Add(new DemoInstance
                {
                    Id = Guid.CreateVersion7(),
                    World = world,
                    Number = (number++).ToString(CultureInfo.InvariantCulture),
                    Region = DemoWords.Regions[random.Next(DemoWords.Regions.Length)],
                    OpenedAt = openedAt,
                    ClosedAt = closedAt,
                    Visits = visits,
                    Peak = Math.Max(1, visits.Count - random.Next(0, 3)),
                    OpenedBy = Spread((number - 1).ToString(CultureInfo.InvariantCulture), StaffCount),
                });
            }
        }

        // The instances that are open right now. A handful, each in its own world and region, each
        // with one of the team in it -- the doc promises "a few open right now", and Live is the
        // page a demo is judged on.
        var stillHere = plan.People.Where(p => p.LeftGroupAt is null && p.Kind != DemoPersonKind.Staff).ToList();

        for (var extra = 0; extra < InstancesOpenNow && stillHere.Count > 0; extra++)
        {
            var world = plan.Worlds[extra % plan.Worlds.Count];
            var openedAt = now.AddMinutes(-random.Next(15, 150));
            var inInstance = random.Next(4, 18);

            // A different moderator in each, so no two open instances claim the same person -- a
            // client reported in a second instance ends the watch on the first.
            var host = plan.Staff[extra % plan.Staff.Count];

            var pool = stillHere
                .Skip(Spread(world.WorldId, Math.Max(1, stillHere.Count - inInstance)))
                .Take(inInstance)
                .ToList();

            // The moderator is in first, because their client only reports what it saw after it
            // walked in: anybody already there before them is a head count and no name.
            var visits = new List<DemoVisit> { new(host, openedAt, null) };

            // Everybody in an open instance is still in it: Live counts a visit with no leave, and one
            // that had already ended would be a name on a page nobody is on.
            visits.AddRange(pool.Select((p, i) => new DemoVisit(p, openedAt.AddMinutes((i + 1) * 2), null)));

            instances.Add(new DemoInstance
            {
                Id = Guid.CreateVersion7(),
                World = world,
                Number = (number++).ToString(CultureInfo.InvariantCulture),
                Region = DemoWords.Regions[extra % DemoWords.Regions.Length],
                OpenedAt = openedAt,
                ClosedAt = null,
                Visits = visits,
                Peak = visits.Count,
                OpenedBy = extra % plan.Staff.Count,
            });
        }

        return instances;
    }

    // --- moderation -------------------------------------------------------------------------

    private static List<DemoAction> BuildActions(Random random, DemoPlan plan)
    {
        var actions = new List<DemoAction>();
        var staff = plan.Staff;
        var now = plan.Now;

        void Act(string type, DemoPerson subject, DateTimeOffset at, string reason, DemoInstance? instance)
        {
            if (at > now)
                at = now.AddHours(-random.Next(1, 48));

            actions.Add(new DemoAction(type, subject, staff[random.Next(staff.Count)], at, reason, instance));
        }

        // Repeat offenders: several actions each, by more than one moderator, which is exactly what
        // the repeat-offender list and the "same person" review are built to notice.
        foreach (var person in plan.People.Where(p => p.Kind == DemoPersonKind.Offender))
        {
            var count = random.Next(2, 6);
            var at = person.JoinedGroupAt.AddDays(random.Next(2, 30));

            for (var i = 0; i < count; i++)
            {
                var instance = InstanceAround(random, plan, at);

                var type = i switch
                {
                    0 => Core.Data.Entities.FactType.GroupInstanceWarn,
                    1 => random.Next(100) < 60
                        ? Core.Data.Entities.FactType.GroupInstanceKick
                        : Core.Data.Entities.FactType.GroupInstanceWarn,
                    _ => random.Next(100) < 45
                        ? Core.Data.Entities.FactType.MemberBanned
                        : Core.Data.Entities.FactType.GroupInstanceKick,
                };

                Act(type, person, at, ReasonFor(type, random), instance);

                if (type == Core.Data.Entities.FactType.MemberBanned)
                {
                    // Some bans get lifted. A group that never unbans anybody is not a real group.
                    if (random.Next(100) < 30)
                        Act(Core.Data.Entities.FactType.MemberUnbanned, person, at.AddDays(random.Next(5, 60)), "Appealed, and it held up.", null);

                    break;
                }

                at = at.AddDays(random.Next(3, 45));

                if (at > now)
                    break;
            }
        }

        // Everyone else: the ordinary trickle of one-off warnings and kicks.
        var others = plan.People.Where(p => p.Kind is DemoPersonKind.Regular or DemoPersonKind.Newcomer).ToList();

        for (var i = 0; i < 130; i++)
        {
            var person = others[random.Next(others.Count)];
            var at = person.JoinedGroupAt.AddDays(random.Next(1, 200));

            if (at > now)
                at = now.AddDays(-random.Next(1, 90));

            var type = random.Next(100) < 72
                ? Core.Data.Entities.FactType.GroupInstanceWarn
                : Core.Data.Entities.FactType.GroupInstanceKick;

            Act(type, person, at, ReasonFor(type, random), InstanceAround(random, plan, at));
        }

        return [.. actions.OrderBy(a => a.At)];
    }

    private static string ReasonFor(string type, Random random) => type switch
    {
        Core.Data.Entities.FactType.MemberBanned => DemoWords.BanReasons[random.Next(DemoWords.BanReasons.Length)].Label,
        Core.Data.Entities.FactType.GroupInstanceKick => DemoWords.KickReasons[random.Next(DemoWords.KickReasons.Length)],
        _ => DemoWords.WarnReasons[random.Next(DemoWords.WarnReasons.Length)],
    };

    /// <summary>An instance that was open at roughly that moment, so an action has somewhere to have happened.</summary>
    private static DemoInstance? InstanceAround(Random random, DemoPlan plan, DateTimeOffset at)
    {
        var near = plan.Instances
            .Where(r => r.OpenedAt <= at && (r.ClosedAt ?? plan.Now) >= at)
            .ToList();

        return near.Count > 0 ? near[random.Next(near.Count)] : null;
    }

    // --- identifiers ------------------------------------------------------------------------

    /// <summary>
    /// A stable id from a number, so the same demo rebuilds with the same ids after a reset.
    /// </summary>
    /// <remarks>
    /// VRChat's own ids follow no structure and Modbot never validates one (foundation §3.1.1), so
    /// these only have to be unique and obviously made up.
    /// </remarks>
    public static string Fixed(int n)
    {
        var bytes = new byte[16];
        bytes[0] = 0xDE;
        bytes[1] = 0x30;
        BitConverter.TryWriteBytes(bytes.AsSpan(8), (long)n);
        return new Guid(bytes).ToString();
    }

    /// <summary>A Discord-shaped id. Snowflakes are numbers; nothing in Modbot parses one.</summary>
    public static string Snowflake(int n)
        => (900_000_000_000_000_000L + (n * 7_919L)).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Spreads a name across a fixed number of buckets, the same way every time.
    /// </summary>
    /// <remarks>
    /// Not <c>string.GetHashCode</c>: .NET randomises that per process, so a demo rebuilt after a
    /// restart would hand the same instance to a different moderator and the seeded data would stop
    /// matching itself between the two halves of the seeding.
    /// </remarks>
    public static int Spread(string value, int buckets)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfLessThan(buckets, 1);

        var hash = 17;

        foreach (var c in value)
            hash = unchecked((hash * 31) + c);

        return (hash & 0x7FFFFFFF) % buckets;
    }
}
