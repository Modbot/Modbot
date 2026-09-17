using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Time;

namespace Modbot.Demo;

/// <summary>
/// Fills an empty database with a made-up group, and empties it again.
/// </summary>
/// <remarks>
/// <para>
/// Only ever runs while <see cref="DemoMode.IsOn"/>, which is only ever true on a deployment that
/// was never set up for real (demo mode design §2). Nothing here is reachable otherwise.
/// </para>
/// <para>
/// The quick half. Everything a person sees the moment they open the site — the group, the people,
/// the rooms, the team, the case files, the calendar — is written here, synchronously during
/// startup. The year of history behind it is written afterwards by <see cref="DemoHistory"/>,
/// because it is fifty times the rows and would hold the container's start open for the better part
/// of a minute (§5).
/// </para>
/// </remarks>
public sealed class DemoSeeder
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly ISecretProtector _protector;

    public DemoSeeder(ModbotContext db, IModbotClock clock, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(protector);

        _db = db;
        _clock = clock;
        _protector = protector;
    }

    /// <summary>Whether the demo has already been filled in.</summary>
    public async Task<bool> IsSeededAsync(CancellationToken ct = default)
    {
        var settings = await _db.GetSettingsAsync(ct);
        return settings.DemoData && settings.OnboardingComplete;
    }

    /// <summary>
    /// Writes the group and everything visible on the first screen. Returns the plan the rest of
    /// the seeding is written from, so both halves describe the same group.
    /// </summary>
    public async Task<DemoPlan> SeedCoreAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var plan = DemoPlan.Build(now);

        await SettingsAsync(plan, ct);
        await StaffAsync(plan, ct);
        await PeopleAsync(plan, ct);
        await MembershipAsync(plan, ct);
        await PlacesAsync(plan, ct);
        await ModerationAsync(plan, ct);
        await DiscordAsync(plan, ct);
        await CalendarAsync(plan, ct);
        await KeysAsync(plan, ct);

        return plan;
    }

    /// <summary>
    /// Removes everything the demo made up, so it can be made up again.
    /// </summary>
    /// <remarks>
    /// Deletes rather than drops: the schema is the migrations' business, and a reset that recreated
    /// tables would be a second, untested path to a schema the migrations already own. The fact log
    /// is the one exception — it is emptied by truncating the parent, which takes the monthly
    /// partitions with it and is far quicker than deleting eighteen thousand rows.
    /// </remarks>
    public async Task WipeAsync(CancellationToken ct = default)
    {
        // Order matters only where a foreign key does. Everything here is demo data; there is
        // nothing else in the database to damage.
        await _db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE modbot_event", ct);
        await _db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE discord_message, discord_message_edit", ct);

        await _db.DailyTotals.ExecuteDeleteAsync(ct);
        await _db.DailyTotalsState.ExecuteDeleteAsync(ct);
        await _db.ReviewRunState.ExecuteDeleteAsync(ct);
        await _db.StorageDays.ExecuteDeleteAsync(ct);

        await _db.InstanceHeadCounts.ExecuteDeleteAsync(ct);
        await _db.VRChatInstances.ExecuteDeleteAsync(ct);
        await _db.VRChatWorlds.ExecuteDeleteAsync(ct);

        await _db.CaseFiles.ExecuteDeleteAsync(ct);
        await _db.BanReasons.ExecuteDeleteAsync(ct);
        await _db.Reviews.ExecuteDeleteAsync(ct);
        await _db.RepeatOffenders.ExecuteDeleteAsync(ct);
        await _db.ModeratorBaselines.ExecuteDeleteAsync(ct);

        await _db.GroupMembers.ExecuteDeleteAsync(ct);
        await _db.GroupBans.ExecuteDeleteAsync(ct);

        await _db.DiscordAccountLinks.ExecuteDeleteAsync(ct);
        await _db.DiscordLinkCodes.ExecuteDeleteAsync(ct);
        await _db.DiscordMembers.ExecuteDeleteAsync(ct);
        await _db.DiscordReadBacks.ExecuteDeleteAsync(ct);
        await _db.DiscordChannels.ExecuteDeleteAsync(ct);
        await _db.DiscordRoles.ExecuteDeleteAsync(ct);
        await _db.DiscordServers.ExecuteDeleteAsync(ct);
        await _db.DiscordEventRoutes.ExecuteDeleteAsync(ct);
        await _db.DiscordEventChannels.ExecuteDeleteAsync(ct);

        await _db.CalendarOpenings.ExecuteDeleteAsync(ct);
        await _db.CalendarEventPlaces.ExecuteDeleteAsync(ct);
        await _db.CalendarEvents.ExecuteDeleteAsync(ct);
        await _db.CalendarFeeds.ExecuteDeleteAsync(ct);

        await _db.WebhookDeliveries.ExecuteDeleteAsync(ct);
        await _db.Webhooks.ExecuteDeleteAsync(ct);
        await _db.ApiKeys.ExecuteDeleteAsync(ct);

        await _db.Alerts.ExecuteDeleteAsync(ct);
        await _db.Insights.ExecuteDeleteAsync(ct);
        await _db.ModerationFlags.ExecuteDeleteAsync(ct);
        await _db.ModerationTestRuns.ExecuteDeleteAsync(ct);
        await _db.ModerationTestSamples.ExecuteDeleteAsync(ct);
        await _db.ModerationRuleVersions.ExecuteDeleteAsync(ct);
        await _db.ModerationTopics.ExecuteDeleteAsync(ct);
        await _db.ModerationTermLists.ExecuteDeleteAsync(ct);

        await _db.AiChatMessages.ExecuteDeleteAsync(ct);
        await _db.AiChatConversations.ExecuteDeleteAsync(ct);
        await _db.AiCalls.ExecuteDeleteAsync(ct);
        await _db.AiUsage.ExecuteDeleteAsync(ct);

        await _db.EvidenceBlobs.ExecuteDeleteAsync(ct);
        await _db.ClientDevices.ExecuteDeleteAsync(ct);
        await _db.ClientPairingCodes.ExecuteDeleteAsync(ct);
        await _db.OneTimeLinks.ExecuteDeleteAsync(ct);
        await _db.EmailQueue.ExecuteDeleteAsync(ct);

        await _db.VRChatUsers.ExecuteDeleteAsync(ct);

        // The accounts go last: roles hang off them, and AI spend limits hang off both.
        await _db.AiSpendLimits.ExecuteDeleteAsync(ct);
        await _db.UserRoles.ExecuteDeleteAsync(ct);
        await _db.Users.ExecuteDeleteAsync(ct);
        await _db.Roles.Where(r => !r.IsBuiltIn).ExecuteDeleteAsync(ct);

        // Everything above was deleted with a statement rather than through the change tracker, so
        // anything this context loaded earlier is still being tracked and still believes it exists.
        // A reset wipes and re-seeds through one context, and without this the re-seed fails on the
        // first row whose key the tracker has already seen.
        _db.ChangeTracker.Clear();

        // The settings row is rewritten rather than deleted, so nothing the operator set outside
        // the demo -- an AI provider and key, which is the whole point of demo mode -- is lost.
        var settings = await _db.GetSettingsAsync(ct);
        settings.OnboardingComplete = false;
        settings.DemoData = false;
        settings.ManagedGroupId = null;
        settings.ManagedGroupName = null;
        settings.DiscordGuildId = null;

        // The sweep markers and the group snapshot go with the rows they described. Left behind,
        // they would have an empty member list claiming four hundred members at the last sweep.
        settings.GroupInfoSnapshot = null;
        settings.GroupInfoPolledAt = null;
        settings.MemberSweepCompletedAt = null;
        settings.MemberSweepPreviousStartedAt = null;
        settings.MemberSweepCount = 0;
        settings.MemberSweepPolledAt = null;
        settings.BanSweepCompletedAt = null;
        settings.BanSweepPreviousStartedAt = null;
        settings.BanSweepCount = 0;
        settings.BanSweepPolledAt = null;

        await _db.SaveChangesAsync(ct);
    }

    // --- settings ---------------------------------------------------------------------------

    private async Task SettingsAsync(DemoPlan plan, CancellationToken ct)
    {
        var settings = await _db.GetSettingsAsync(ct);

        settings.DemoData = true;
        settings.OnboardingComplete = true;
        settings.ManagedGroupId = plan.GroupId;
        settings.ManagedGroupName = plan.GroupName;
        settings.DiscordGuildId = plan.DiscordGuildId;

        // The demo never signs in to VRChat, but the wizard's own "is this configured" question is
        // answered from these, and an unfinished wizard would put the whole app behind /setup.
        settings.VRChatUsername = "modbot-demo";
        settings.VRChatDisplayName = "Modbot (demo)";
        settings.VRChatVerifiedAt = plan.Now.AddDays(-DemoPlan.DaysOfHistory);
        settings.ConnectionCheckedAt = plan.Now.AddDays(-DemoPlan.DaysOfHistory);

        // The group as the group-info sync would have left it. Without this the member list shows
        // roles by id and My Group has no "as of" time.
        settings.GroupInfoSnapshot = DemoGroupInfo.At(plan, plan.Now).ToJson();
        settings.GroupInfoPolledAt = plan.Now.AddMinutes(-2);

        // The sweeps, as a finished pass would have left them. A demo's member and ban lists are
        // filled in, so "the member list has not been read yet" is simply untrue on one -- and it
        // is the first thing a visitor sees on two of the pages.
        settings.MemberSweepCompletedAt = plan.Now.AddMinutes(-4);
        settings.MemberSweepPreviousStartedAt = plan.Now.AddMinutes(-9);
        settings.MemberSweepCount = plan.People.Count(p => p.LeftGroupAt is null);
        settings.MemberSweepPolledAt = plan.Now.AddMinutes(-4);

        settings.BanSweepCompletedAt = plan.Now.AddMinutes(-3);
        settings.BanSweepPreviousStartedAt = plan.Now.AddMinutes(-8);
        settings.BanSweepCount = plan.Actions
            .Where(a => a.Type is FactType.MemberBanned or FactType.MemberUnbanned)
            .GroupBy(a => a.Subject.UserId, StringComparer.Ordinal)
            .Count(g => g.OrderBy(a => a.At).Last().Type == FactType.MemberBanned);
        settings.BanSweepPolledAt = plan.Now.AddMinutes(-3);

        // Keep everything the demo made up, whatever the default window is: a year of charts that
        // emptied out after ninety days would make the demo look broken rather than tidy.
        settings.ModerationFactRetentionDays = 0;
        settings.PresenceFactRetentionDays = 0;
        settings.DiscordMessageRetentionDays = 0;

        // Evidence lives in the database, so a demo needs no bucket and no disk (§4.6).
        settings.EvidenceBackend = 3;
        settings.EvidenceStoreId ??= Guid.CreateVersion7();

        await _db.SaveChangesAsync(ct);
    }

    // --- the team ---------------------------------------------------------------------------

    private async Task StaffAsync(DemoPlan plan, CancellationToken ct)
    {
        var hasher = new PasswordHasher<ModbotUser>();

        // Nobody signs in to a demo, so no password is ever typed. They are still hashed from
        // random bytes rather than left blank: an account with a guessable password is a habit
        // worth not having, even where there is no sign-in page to use it on.
        string Unusable(ModbotUser user)
            => hasher.HashPassword(user, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        for (var index = 0; index < plan.Staff.Count; index++)
        {
            var person = plan.Staff[index];
            var name = DemoWords.StaffNames[index];

            var user = new ModbotUser
            {
                // The first one is the account every visitor is served as (§3).
                Id = index == 0 ? DemoMode.AdministratorId : Guid.CreateVersion7(),
                Username = name,
                UsernameNormalized = name.ToUpperInvariant(),
                Email = $"{name}@demo.invalid",
                VRChatUserId = person.UserId,
                VRChatDisplayName = person.DisplayName,
                VRChatLinkedAt = person.JoinedGroupAt,
                CreatedAt = person.JoinedGroupAt,
                LastLoginAt = plan.Now.AddHours(-index),
            };

            user.PasswordHash = Unusable(user);
            _db.Users.Add(user);

            var role = index switch
            {
                0 or 1 => BuiltInRoles.AdministratorId,
                > 5 => BuiltInRoles.ViewerId,
                _ => BuiltInRoles.ModeratorId,
            };

            _db.UserRoles.Add(new ModbotUserRole { UserId = user.Id, RoleId = role });

            // A paired companion each, because the Live page only shows who is in a room when
            // a paired client reported them (Live reads facts, not VRChat).
            _db.ClientDevices.Add(new ClientDeviceRecord
            {
                Id = DeviceIdOf(index),
                TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"demo-device-{index}"))),
                ClientVersion = "2026.9.0",
                Platform = "windows",
                IssuedToUserId = user.Id,
                IssuedAt = person.JoinedGroupAt,
                LastSeenAt = plan.Now.AddMinutes(-index),
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>The paired client a moderator's presence reports come from. Fixed, like their ids.</summary>
    internal static Guid DeviceIdOf(int index) => new(DemoPlan.Fixed(9000 + index));

    /// <summary>A time inside a stored snapshot, written the way the real capture writes it.</summary>
    private static string Stamp(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    // --- people -----------------------------------------------------------------------------

    private async Task PeopleAsync(DemoPlan plan, CancellationToken ct)
    {
        foreach (var person in plan.People)
        {
            _db.VRChatUsers.Add(new VRChatUser
            {
                UserId = person.UserId,
                DisplayName = person.DisplayName,
                Bio = person.Bio,
                Pronouns = person.Pronouns,
                Status = person.Status,
                StatusDescription = person.StatusDescription,
                ProfilePictureUrl = person.Picture,
                CurrentAvatarImageUrl = person.Avatar,
                CurrentAvatarThumbnailImageUrl = person.Avatar,
                DateJoined = person.DateJoined,
                Tags = JsonSerializer.Serialize(person.Tags),
                LastPlatform = person.Platform,
                AgeVerificationStatus = person.Is18Plus ? "18+" : "unverified",
                AgeVerified = person.Is18Plus,
                Is18PlusVerified = person.Is18Plus,
                Is18PlusVerifiedAt = person.Is18Plus ? person.JoinedGroupAt : null,
                Is18PlusVerifiedSource = person.Is18Plus ? AgeVerificationSource.VRChat : null,
                FirstSeenAt = person.JoinedGroupAt,
                LastSeenAt = person.LeftGroupAt ?? plan.Now.AddHours(-DemoPlan.Spread(person.UserId, 200)),
                LastRefreshedAt = plan.Now.AddHours(-1),
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task MembershipAsync(DemoPlan plan, CancellationToken ct)
    {
        foreach (var person in plan.People)
        {
            _db.GroupMembers.Add(new GroupMember
            {
                GroupId = plan.GroupId,
                UserId = person.UserId,
                MembershipId = "gmem_" + DemoPlan.Fixed(20000 + DemoPlan.Spread(person.UserId, 100000)),
                Roles = JsonSerializer.Serialize(person.GroupRoles),
                JoinedAt = person.JoinedGroupAt,
                MembershipStatus = "member",
                Visibility = "visible",
                IsRepresenting = person.Kind is DemoPersonKind.Staff or DemoPersonKind.Regular,
                FirstSeenAt = person.JoinedGroupAt,
                LastSeenAt = person.LeftGroupAt ?? plan.Now,
                LeftAt = person.LeftGroupAt,
            });
        }

        // The ban list: whoever the plan's last action on them was a ban that was never lifted.
        var bans = plan.Actions
            .Where(a => a.Type is FactType.MemberBanned or FactType.MemberUnbanned)
            .GroupBy(a => a.Subject.UserId, StringComparer.Ordinal)
            .Select(g => g.OrderBy(a => a.At).Last())
            .Where(a => a.Type == FactType.MemberBanned)
            .ToList();

        foreach (var ban in bans)
        {
            _db.GroupBans.Add(new GroupBan
            {
                GroupId = plan.GroupId,
                UserId = ban.Subject.UserId,
                BannedAt = ban.At,
                FirstSeenAt = ban.At,
                LastSeenAt = plan.Now,
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    // --- worlds and rooms -------------------------------------------------------------------

    private async Task PlacesAsync(DemoPlan plan, CancellationToken ct)
    {
        foreach (var world in plan.Worlds)
        {
            _db.VRChatWorlds.Add(new VRChatWorld
            {
                WorldId = world.WorldId,
                Name = world.Name,
                Description = world.Description,
                AuthorId = plan.People[0].UserId,
                AuthorName = plan.People[0].DisplayName,
                ImageUrl = world.Image,
                ThumbnailImageUrl = world.Image,
                Capacity = world.Capacity,
                RecommendedCapacity = Math.Max(8, world.Capacity / 2),
                Tags = JsonSerializer.Serialize(new[] { "author_tag_hangout", "system_approved" }),
                ReleaseStatus = "public",
                PublishedAt = world.PublishedAt,
                UpdatedAt = world.PublishedAt.AddDays(30),
                FirstSeenAt = world.PublishedAt,
                LastSeenAt = plan.Now,
                LastRefreshedAt = plan.Now.AddHours(-2),
            });
        }

        await _db.SaveChangesAsync(ct);

        // Rooms, in batches: a year of them is a few hundred rows plus their head-count history.
        var written = 0;

        foreach (var room in plan.Rooms)
        {
            var here = room.Visits.Count(v => v.Left is null);

            var entity = new VRChatInstance
            {
                Id = room.Id,
                Location = room.Location(plan.GroupId),
                WorldId = room.World.WorldId,
                VRChatInstanceId = room.Number,
                GroupId = plan.GroupId,
                Type = "group",
                GroupAccessType = "members",
                Region = room.Region,
                OpenedAt = room.OpenedAt,
                LastSeenAt = room.ClosedAt ?? plan.Now,
                ClosedAt = room.ClosedAt,
                ClosedBy = room.ClosedAt is null ? null : "list",
                LastUserCount = room.IsOpen ? here : 0,
                PeakUserCount = room.Peak,
                HeadCount = room.IsOpen ? here : 0,
                HeadCountSource = HeadCounts.FromRoom,
                PageUserCount = room.IsOpen ? here : 0,
                PageReadAt = room.ClosedAt ?? plan.Now.AddSeconds(-20),
                PageCheckedAt = room.ClosedAt ?? plan.Now.AddSeconds(-20),

                // Live only shows rooms the group's own list has been seen to hold (§4.4).
                SeenInGroupList = true,
                AnnouncementFinished = !room.IsOpen,
            };

            _db.VRChatInstances.Add(entity);

            // The head-count history the room chart is drawn from: one reading every ten minutes
            // while the room was open, counted from who was actually in it at that moment.
            var until = room.ClosedAt ?? plan.Now;

            for (var at = room.OpenedAt; at < until; at = at.AddMinutes(10))
            {
                var count = room.Visits.Count(v => v.Arrived <= at && (v.Left is null || v.Left > at));

                _db.InstanceHeadCounts.Add(new InstanceHeadCount
                {
                    InstanceId = room.Id,
                    CountedAt = at,
                    HeadCount = count,
                    UserCount = count,
                    MemberCount = count,
                    Source = HeadCounts.FromRoom,
                });
            }

            if (++written % 100 == 0)
                await _db.SaveChangesAsync(ct);
        }

        await _db.SaveChangesAsync(ct);
    }

    // --- moderation ---------------------------------------------------------------------------

    private async Task ModerationAsync(DemoPlan plan, CancellationToken ct)
    {
        var reasons = new List<BanReason>();

        for (var index = 0; index < DemoWords.BanReasons.Length; index++)
        {
            var (label, description, needsWritten) = DemoWords.BanReasons[index];

            var reason = new BanReason
            {
                Id = new Guid(DemoPlan.Fixed(3000 + index)),
                Label = label,
                Description = description,
                SortOrder = index,
                IsActive = true,
                NeedsWrittenReason = needsWritten,
                CreatedAt = plan.Now.AddDays(-DemoPlan.DaysOfHistory),
                UpdatedAt = plan.Now.AddDays(-DemoPlan.DaysOfHistory),
            };

            reasons.Add(reason);
            _db.BanReasons.Add(reason);
        }

        await _db.SaveChangesAsync(ct);

        var staffAccounts = await _db.Users.AsNoTracking()
            .Where(u => u.VRChatUserId != null)
            .ToDictionaryAsync(u => u.VRChatUserId!, u => u, StringComparer.Ordinal, ct);

        var bans = plan.Actions.Where(a => a.Type == FactType.MemberBanned).ToList();
        var writeUp = 0;

        foreach (var ban in bans)
        {
            if (!staffAccounts.TryGetValue(ban.Moderator.UserId, out var author))
                continue;

            var reason = reasons.Find(r => r.Label == ban.Reason) ?? reasons[0];

            _db.CaseFiles.Add(new CaseFile
            {
                Id = Guid.CreateVersion7(),
                UserId = ban.Subject.UserId,
                GroupId = plan.GroupId,
                BannedAt = ban.At,
                AuthorUserId = author.Id,
                AuthorUsername = author.Username,
                ReasonIds = JsonSerializer.Serialize(new[] { reason.Id }),
                WrittenReason = DemoWords.BanWriteUps[writeUp++ % DemoWords.BanWriteUps.Length],
                CreatedAt = ban.At.AddMinutes(6),
                UpdatedAt = ban.At.AddMinutes(6),
                SnapshotTakenAt = ban.At,
                ProfileRefreshedAt = ban.At.AddMinutes(-20),

                // The field names are the ones the real capture writes (Cases/ProfileSnapshot.cs),
                // because the case file page reads this stored JSON straight through. A snapshot
                // written to a shape of its own is a page that cannot show it.
                ProfileAtBan = JsonSerializer.Serialize(new
                {
                    userId = ban.Subject.UserId,
                    displayName = ban.Subject.DisplayName,
                    bio = ban.Subject.Bio,
                    status = ban.Subject.Status,
                    statusDescription = ban.Subject.StatusDescription,
                    pronouns = ban.Subject.Pronouns,
                    avatarImageUrl = ban.Subject.Avatar,
                    avatarThumbnailUrl = ban.Subject.Avatar,
                    profilePictureUrl = ban.Subject.Picture,
                    dateJoined = ban.Subject.DateJoined.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    tags = ban.Subject.Tags,
                    lastPlatform = ban.Subject.Platform,
                    ageVerificationStatus = ban.Subject.Is18Plus ? "18+" : "unverified",
                    ageVerified = ban.Subject.Is18Plus,
                    eighteenPlus = new
                    {
                        verified = ban.Subject.Is18Plus,
                        since = ban.Subject.Is18Plus ? Stamp(ban.Subject.JoinedGroupAt) : null,
                        source = ban.Subject.Is18Plus ? AgeVerificationSource.VRChat : null,
                    },
                    firstSeenAt = Stamp(ban.Subject.JoinedGroupAt),
                    lastSeenAt = Stamp(ban.At),
                    lastRefreshedAt = Stamp(ban.At.AddMinutes(-20)),
                    lastUserReadAt = Stamp(ban.At.AddMinutes(-20)),
                    notFoundAt = (string?)null,
                }),
                MembershipAtBan = JsonSerializer.Serialize(new
                {
                    isMember = false,
                    membershipId = "gmem_" + DemoPlan.Fixed(20000 + DemoPlan.Spread(ban.Subject.UserId, 100000)),
                    roleIds = ban.Subject.GroupRoles,
                    joinedAt = Stamp(ban.Subject.JoinedGroupAt),
                    membershipStatus = "member",
                    visibility = "visible",
                    isRepresenting = false,
                    managerNotes = (string?)null,
                    firstSeenAt = Stamp(ban.Subject.JoinedGroupAt),
                    lastSeenAt = Stamp(ban.At),
                    leftAt = Stamp(ban.At),
                }),
                BanListEntryAtBan = JsonSerializer.Serialize(new
                {
                    bannedAt = Stamp(ban.At),
                    firstSeenAt = Stamp(ban.At),
                    lastSeenAt = Stamp(plan.Now),
                    liftedAt = (string?)null,
                }),
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    // --- Discord ----------------------------------------------------------------------------

    private async Task DiscordAsync(DemoPlan plan, CancellationToken ct)
    {
        _db.DiscordServers.Add(new DiscordServer
        {
            GuildId = plan.DiscordGuildId,
            Name = plan.GroupName,
            BotCanViewAuditLog = true,
            BotCanManageRoles = true,
            BotCanManageEvents = true,
            RefreshedAt = plan.Now.AddMinutes(-4),
            UpdatedAt = plan.Now.AddMinutes(-4),
            SeenThrough = plan.Now,
            MembersListedAt = plan.Now.AddMinutes(-4),
        });

        for (var index = 0; index < DemoWords.DiscordRoles.Length; index++)
        {
            var (name, colour, assign) = DemoWords.DiscordRoles[index];

            _db.DiscordRoles.Add(new DiscordRole
            {
                RoleId = DemoPlan.Snowflake(2000 + index),
                GuildId = plan.DiscordGuildId,
                Name = name,
                Color = colour,
                Position = DemoWords.DiscordRoles.Length - index,
                Managed = name == "Modbot",
                Everyone = name == "@everyone",
                BotCanAssign = assign,
                FirstSeenAt = plan.Now.AddDays(-DemoPlan.DaysOfHistory),
                UpdatedAt = plan.Now.AddMinutes(-4),
            });
        }

        for (var index = 0; index < DemoWords.Channels.Length; index++)
        {
            var (name, type, _) = DemoWords.Channels[index];

            _db.DiscordChannels.Add(new DiscordChannel
            {
                ChannelId = DemoPlan.Snowflake(3000 + index),
                GuildId = plan.DiscordGuildId,
                Name = name,
                Type = type,
                Position = index,
                BotCanView = true,
                BotCanReadHistory = true,
                BotCanSend = type != "voice",
                BotCanEmbedLinks = true,
                BotCanAttachFiles = true,
                BotCanManageMessages = true,
                FirstSeenAt = plan.Now.AddDays(-DemoPlan.DaysOfHistory),
                UpdatedAt = plan.Now.AddMinutes(-4),
            });
        }

        var withDiscord = plan.People.Where(p => p.DiscordUserId is not null).Take(DemoPlan.DiscordMemberCount).ToList();

        foreach (var person in withDiscord)
        {
            var roles = new List<string> { DemoPlan.Snowflake(2001) };

            if (person.Kind == DemoPersonKind.Staff)
                roles.Add(DemoPlan.Snowflake(2002));

            if (person.Is18Plus)
                roles.Add(DemoPlan.Snowflake(2004));

            _db.DiscordMembers.Add(new DiscordMember
            {
                GuildId = plan.DiscordGuildId,
                UserId = person.DiscordUserId!,
                Username = person.DisplayName.ToLowerInvariant(),
                DisplayName = person.DisplayName,
                GlobalName = person.DisplayName,
                AvatarUrl = person.Picture,
                JoinedAt = person.JoinedGroupAt.AddDays(-1),
                FirstSeenAt = person.JoinedGroupAt.AddDays(-1),
                LeftAt = person.LeftGroupAt,
                Roles = JsonSerializer.Serialize(roles),
                UpdatedAt = plan.Now.AddMinutes(-4),
            });

            if (person.DiscordLinked)
            {
                _db.DiscordAccountLinks.Add(new DiscordAccountLink
                {
                    Id = Guid.CreateVersion7(),
                    DiscordUserId = person.DiscordUserId!,
                    DiscordUsername = person.DisplayName.ToLowerInvariant(),
                    VRChatUserId = person.UserId,
                    VRChatDisplayName = person.DisplayName,
                    StartedFrom = LinkStartedFrom.Discord,
                    LinkedAt = person.JoinedGroupAt.AddDays(2),
                    LinkedRoleId = DemoPlan.Snowflake(2001),
                    EighteenPlusRoleId = person.Is18Plus ? DemoPlan.Snowflake(2004) : null,
                });
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    // --- calendar ---------------------------------------------------------------------------

    private async Task CalendarAsync(DemoPlan plan, CancellationToken ct)
    {
        var administrator = DemoMode.AdministratorId;

        for (var index = 0; index < DemoWords.Events.Length; index++)
        {
            var (title, description, hours) = DemoWords.Events[index];

            // Four behind us, two ahead, so the calendar is never a blank page in either direction.
            var daysOut = index switch
            {
                0 => -21,
                1 => -14,
                2 => -7,
                3 => -2,
                4 => 3,
                _ => 9,
            };

            var starts = plan.Now.Date.AddDays(daysOut).AddHours(20);
            var startsAt = new DateTimeOffset(starts, TimeSpan.Zero);
            var endsAt = startsAt.AddHours(hours);
            var world = plan.Worlds[index % plan.Worlds.Count];

            var state = endsAt < plan.Now
                ? CalendarEventStates.Finished
                : startsAt <= plan.Now ? CalendarEventStates.Open : CalendarEventStates.Scheduled;

            var id = new Guid(DemoPlan.Fixed(4000 + index));

            _db.CalendarEvents.Add(new CalendarEvent
            {
                Id = id,
                Title = title,
                Description = description,
                StartsAt = startsAt,
                EndsAt = endsAt,
                TimeZone = "UTC",
                Repeat = index == 0 ? CalendarRepeats.Weekly : CalendarRepeats.None,
                RepeatDays = index == 0 ? ["FR"] : [],
                WorldId = world.WorldId,
                AccessType = "members",
                Region = "us",
                ImageUrl = world.Image,
                Category = "hangout",
                Languages = ["eng"],
                Platforms = ["standalonewindows", "android"],
                Tags = [],
                Visibility = "group",
                PublishToVRChat = true,
                PublishToDiscord = true,
                PostToChannel = true,
                ChannelId = DemoPlan.Snowflake(3004),
                AutoOpen = index is 0 or 5,
                OpenMinutesBefore = 10,
                State = state,
                OccurrenceStartsAt = startsAt,
                CreatedAt = startsAt.AddDays(-10),
                UpdatedAt = startsAt.AddDays(-10),
                CreatedByUserId = administrator,
            });

            foreach (var place in new[] { CalendarPlaces.VRChat, CalendarPlaces.DiscordEvent, CalendarPlaces.ChannelPost })
            {
                _db.CalendarEventPlaces.Add(new CalendarEventPlace
                {
                    EventId = id,
                    Place = place,
                    State = CalendarPlaceStates.Published,
                    ExternalId = DemoPlan.Snowflake(5000 + (index * 4) + place.Length),
                    ChannelId = place == CalendarPlaces.ChannelPost ? DemoPlan.Snowflake(3004) : null,
                    OccurrenceStartsAt = startsAt,
                    UpdatedAt = startsAt.AddDays(-10),
                });
            }
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

        _db.CalendarFeeds.Add(new CalendarFeed
        {
            Id = 1,
            TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant(),
            TokenEncrypted = _protector.Protect(token),
            CreatedAt = plan.Now.AddDays(-DemoPlan.DaysOfHistory),
        });

        await _db.SaveChangesAsync(ct);
    }

    // --- keys and webhooks --------------------------------------------------------------------

    private async Task KeysAsync(DemoPlan plan, CancellationToken ct)
    {
        // A key nobody holds: the demo shows the page with something on it, and the secret itself
        // is shown once at creation and never stored, so there is nothing here to hand out.
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

        _db.ApiKeys.Add(new ApiKey
        {
            Id = new Guid(DemoPlan.Fixed(6001)),
            Name = "Events to the group's dashboard",
            Start = "mb_" + secret[..6],
            KeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant(),
            Permissions = ModbotPermissions.ViewMembers | ModbotPermissions.ViewAnalytics | ModbotPermissions.ViewAuditLog,
            CreatedByUserId = DemoMode.AdministratorId,
            CreatedAt = plan.Now.AddDays(-120),
            LastUsedAt = plan.Now.AddHours(-3),
        });

        _db.Webhooks.Add(new Webhook
        {
            Id = new Guid(DemoPlan.Fixed(6002)),
            Name = "Bans to the team's chat",
            Url = "https://example.invalid/hooks/modbot",
            EventTypes = [FactType.MemberBanned, FactType.MemberUnbanned, FactType.GroupInstanceKick],
            SubjectIds = [],
            Enabled = true,
            SecretEncrypted = _protector.Protect(Convert.ToHexString(RandomNumberGenerator.GetBytes(32))),
            CreatedByUserId = DemoMode.AdministratorId,
            CreatedAt = plan.Now.AddDays(-90),
            UpdatedAt = plan.Now.AddDays(-90),
            LastSuccessAt = plan.Now.AddHours(-9),
        });

        await _db.SaveChangesAsync(ct);
    }
}
