using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Data;

public class ModbotContext : DbContext, IDataProtectionKeyContext
{
    public ModbotContext(DbContextOptions<ModbotContext> options) : base(options) { }

    public DbSet<Settings> Settings => Set<Settings>();

    public DbSet<ProtectorKey> ProtectorKeys => Set<ProtectorKey>();

    public DbSet<ModbotUser> Users => Set<ModbotUser>();

    /// <summary>Named permission sets (accounts and access design §3).</summary>
    public DbSet<ModbotRole> Roles => Set<ModbotRole>();

    public DbSet<ModbotUserRole> UserRoles => Set<ModbotUserRole>();

    /// <summary>Invite and password reset links, stored as hashes (design §4.1).</summary>
    public DbSet<OneTimeLink> OneTimeLinks => Set<OneTimeLink>();

    /// <summary>Keys for programs, stored as hashes (API keys design §3).</summary>
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    /// <summary>Addresses events are sent to (API keys design §6).</summary>
    public DbSet<Webhook> Webhooks => Set<Webhook>();

    /// <summary>The last attempts per webhook.</summary>
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    /// <summary>The fact log (spec 5.3). Append-only: never update or delete a row here.</summary>
    public DbSet<ModbotEvent> Events => Set<ModbotEvent>();

    /// <summary>Daily aggregates (spec 5.4). Derived from <see cref="Events"/>, kept forever.</summary>
    public DbSet<DailyTotal> DailyTotals => Set<DailyTotal>();

    /// <summary>Where the incremental daily totals run got to.</summary>
    public DbSet<DailyTotalsState> DailyTotalsState => Set<DailyTotalsState>();

    /// <summary>
    /// Rate-limit budgets and penalty state (spec 4.3.2). Persisted rather than held in memory so
    /// that a restart resumes a cold stop instead of walking back into it.
    /// </summary>
    public DbSet<RateLimitBucket> RateLimitBuckets => Set<RateLimitBucket>();

    /// <summary>
    /// ASP.NET Core's data protection key ring, persisted rather than held in memory.
    /// </summary>
    /// <remarks>
    /// Without this the keys are regenerated on every start, which invalidates every auth cookie —
    /// so a redeploy, a crash, or a container restart silently signs out every moderator. On a
    /// platform that restarts containers routinely that is not an edge case, it is Tuesday.
    ///
    /// These keys protect session cookies. They are not the same thing as
    /// <see cref="Entities.ProtectorKey"/>, which encrypts the secrets in
    /// <see cref="Entities.Settings"/> (spec section 8.3) — different keys, different jobs, and
    /// rotating one has nothing to do with the other.
    /// </remarks>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<ClientDeviceRecord> ClientDevices => Set<ClientDeviceRecord>();
    public DbSet<ClientPairingCodeRecord> ClientPairingCodes => Set<ClientPairingCodeRecord>();
    public DbSet<EvidenceBlob> EvidenceBlobs => Set<EvidenceBlob>();

    /// <summary>
    /// Every VRChat user Modbot has ever seen, with their profile as last fetched. Not derived
    /// from facts and not rebuildable from them -- see <see cref="VRChatUser"/>.
    /// </summary>
    public DbSet<VRChatUser> VRChatUsers => Set<VRChatUser>();

    /// <summary>Per-person action counts (spec 5.8.4). Derived from <see cref="Events"/>; a cache.</summary>
    public DbSet<RepeatOffender> RepeatOffenders => Set<RepeatOffender>();

    /// <summary>Moderator patterns opened for a human look (spec 5.8.5).</summary>
    public DbSet<Review> Reviews => Set<Review>();

    /// <summary>What each moderator usually does in a day. Derived from <see cref="DailyTotals"/>; a cache.</summary>
    public DbSet<ModeratorBaseline> ModeratorBaselines => Set<ModeratorBaseline>();

    /// <summary>Where the last detection run got to.</summary>
    public DbSet<ReviewRunState> ReviewRunState => Set<ReviewRunState>();

    /// <summary>How big the data was, one row per day, for the storage chart.</summary>
    public DbSet<StorageDay> StorageDays => Set<StorageDay>();

    /// <summary>The group's member list as last swept. Current state; the history is in <see cref="Events"/>.</summary>
    /// <summary>
    /// Worlds Modbot has seen somebody in, so a place can be shown by name instead of by id.
    /// </summary>
    public DbSet<VRChatWorld> VRChatWorlds => Set<VRChatWorld>();

    /// <summary>
    /// Rooms, each with an id of Modbot's own because VRChat reissues instance numbers.
    /// </summary>
    public DbSet<VRChatInstance> VRChatInstances => Set<VRChatInstance>();

    /// <summary>Every change in a room's head count, keyed on Modbot's own room id.</summary>
    public DbSet<InstanceHeadCount> InstanceHeadCounts => Set<InstanceHeadCount>();

    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();

    /// <summary>The group's ban list as last swept.</summary>
    public DbSet<GroupBan> GroupBans => Set<GroupBan>();

    /// <summary>The reasons a moderator picks from when writing up a ban (spec 5.8.2).</summary>
    public DbSet<BanReason> BanReasons => Set<BanReason>();

    /// <summary>The write-up of each ban: reasons, the moderator's words, the profile at the time (spec 5.8.3).</summary>
    public DbSet<CaseFile> CaseFiles => Set<CaseFile>();

    /// <summary>The Discord server the bot serves, as last seen, so settings can offer its channels and roles.</summary>
    public DbSet<DiscordServer> DiscordServers => Set<DiscordServer>();

    /// <summary>The server's channels and the bot's permissions in each. Removed channels are marked, never deleted.</summary>
    public DbSet<DiscordChannel> DiscordChannels => Set<DiscordChannel>();

    /// <summary>The server's roles and whether the bot could hand each out. Removed roles are marked, never deleted.</summary>
    public DbSet<DiscordRole> DiscordRoles => Set<DiscordRole>();

    /// <summary>AI-written summaries of Modbot's own figures (AI insights design).</summary>
    public DbSet<Insight> Insights => Set<Insight>();

    public DbSet<InsightSettings> InsightSettings => Set<InsightSettings>();

    public DbSet<InsightSchedule> InsightSchedules => Set<InsightSchedule>();

    /// <summary>Conversations on the Chat page, one owner each (AI chat design §6).</summary>
    public DbSet<AiChatConversation> AiChatConversations => Set<AiChatConversation>();

    public DbSet<AiChatMessage> AiChatMessages => Set<AiChatMessage>();
    /// <summary>Rules for which events go to which Discord channel (Discord event routes design).</summary>
    public DbSet<DiscordEventRoute> DiscordEventRoutes => Set<DiscordEventRoute>();

    /// <summary>How far each routed channel has been sent, and its last refusal.</summary>
    public DbSet<DiscordEventChannel> DiscordEventChannels => Set<DiscordEventChannel>();

    /// <summary>AI moderation term lists, local and from Modbot Hub (AI moderation design §2).</summary>
    public DbSet<ModerationTermList> ModerationTermLists => Set<ModerationTermList>();

    public DbSet<ModerationTopic> ModerationTopics => Set<ModerationTopic>();

    /// <summary>What the rules matched, and whether a moderator dismissed it (design §5).</summary>
    public DbSet<ModerationFlag> ModerationFlags => Set<ModerationFlag>();

    /// <summary>Token counts of every AI request, by feature, for spend limits and cost estimates.</summary>
    public DbSet<AiUsage> AiUsage => Set<AiUsage>();

    /// <summary>Each AI feature's spend limit. No row means no limit.</summary>
    public DbSet<AiFeatureLimit> AiFeatureLimits => Set<AiFeatureLimit>();

    /// <summary>Discord and VRChat accounts proved to be the same person. Ended links are kept.</summary>
    public DbSet<DiscordAccountLink> DiscordAccountLinks => Set<DiscordAccountLink>();

    /// <summary>VRChat bio codes waiting to be checked, one per signed-in Discord account.</summary>
    public DbSet<DiscordLinkCode> DiscordLinkCodes => Set<DiscordLinkCode>();

    /// <summary>
    /// Every message in the Discord server, in full (M5 spec §5.1). Partitioned by month; edits and
    /// deletes change the row, and a deleted message keeps it.
    /// </summary>
    public DbSet<DiscordMessage> DiscordMessages => Set<DiscordMessage>();

    /// <summary>Earlier texts of edited messages.</summary>
    public DbSet<DiscordMessageEdit> DiscordMessageEdits => Set<DiscordMessageEdit>();

    /// <summary>How far back each channel and thread has been read.</summary>
    public DbSet<DiscordReadBack> DiscordReadBacks => Set<DiscordReadBack>();

    /// <summary>
    /// Reads the singleton, creating it on first call. Every caller uses this rather than
    /// querying <see cref="Settings"/> directly, so "the row might not exist yet" is handled once.
    /// </summary>
    public async Task<Settings> GetSettingsAsync(CancellationToken ct = default)
    {
        var settings = await Settings.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (settings is not null)
            return settings;

        settings = new Settings { Id = 1 };
        Settings.Add(settings);
        await SaveChangesAsync(ct);

        return settings;
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Every DateTimeOffset reaches PostgreSQL as UTC. Npgsql refuses any other offset for a
        // "timestamp with time zone" -- the column holds an instant, not a clock reading -- and
        // the values that arrive here are not all UTC: the desktop client reports what VRChat's
        // log says, and VRChat's log is in the player's local time. The first live client batch
        // failed the fact writer's dedup lookup with "Cannot write DateTimeOffset with
        // Offset=-05:00". Converting at the boundary means no caller has to remember, and it
        // applies to query parameters compared against these columns as well as to writes.
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<UtcDateTimeOffsetConverter>();

        base.ConfigureConventions(configurationBuilder);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Postgres convention, applied here rather than at every call site so no construction path
        // -- host, tests, `dotnet ef` -- can accidentally produce a differently-named schema.
        // Without it EF emits "OnboardingComplete", which needs double-quoting in every
        // hand-written SQL statement, and the fact log's partitioning is hand-written SQL.
        optionsBuilder.UseSnakeCaseNamingConvention();

        base.OnConfiguring(optionsBuilder);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<Settings>(entity =>
        {
            entity.ToTable("settings", t =>
                t.HasCheckConstraint("ck_settings_singleton", "id = 1"));

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            // Plain words in the database as well as in code (PlainNamesInSchema migration). The
            // one-off walk through existing history is the "catch-up"; the tail poll's unread
            // window is the "backlog". Somebody reading the table should not need a glossary.
            entity.Property(e => e.DiscordOAuthClientId).HasColumnName("discord_oauth_client_id");
            entity.Property(e => e.DiscordOAuthClientSecretEncrypted).HasColumnName("discord_oauth_client_secret_encrypted");
            entity.Property(e => e.AuditLogCatchUpOffset).HasColumnName("audit_log_catch_up_offset");
            entity.Property(e => e.AuditLogCatchUpComplete).HasColumnName("audit_log_catch_up_complete");
            entity.Property(e => e.AuditLogCatchUpVersion).HasColumnName("audit_log_catch_up_version");
            entity.Property(e => e.AuditLogBacklogOffset).HasColumnName("audit_log_backlog_offset");
        });

        builder.Entity<ProtectorKey>(entity =>
        {
            entity.ToTable("protector_key", t =>
                t.HasCheckConstraint("ck_protector_key_singleton", "id = 1"));

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
        });

        builder.Entity<ModbotUser>(entity =>
        {
            entity.ToTable("modbot_user");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.Property(e => e.Username).HasMaxLength(64);
            entity.Property(e => e.UsernameNormalized).HasMaxLength(64);

            // The uniqueness that matters is on the normalised form: without it "Alice" and
            // "alice" are two accounts, and which one a login reaches depends on collation.
            entity.HasIndex(e => e.UsernameNormalized).IsUnique();

            entity.Property(e => e.Email).HasMaxLength(256);

            // VRChat ids are opaque (spec 3.1.1): text, no length assumption. Unique because one
            // VRChat account is one person, and two Modbot accounts claiming it would make the
            // attribution the link exists for ambiguous. Nulls do not collide in PostgreSQL.
            entity.Property(e => e.VRChatUserId).HasColumnType("text");
            entity.Property(e => e.VRChatLinkPendingUserId).HasColumnType("text");
            entity.Property(e => e.VRChatDisplayName).HasMaxLength(128);
            entity.Property(e => e.VRChatLinkCode).HasMaxLength(32);
            entity.HasIndex(e => e.VRChatUserId).IsUnique();
        });

        builder.Entity<ModbotRole>(entity =>
        {
            entity.ToTable("modbot_role");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.Property(e => e.Name).HasMaxLength(64);
            entity.Property(e => e.NameNormalized).HasMaxLength(64);
            entity.Property(e => e.Description).HasMaxLength(256);

            // Same reasoning as usernames: "Moderator" and "moderator" must be one role.
            entity.HasIndex(e => e.NameNormalized).IsUnique();
        });

        builder.Entity<ModbotUserRole>(entity =>
        {
            entity.ToTable("modbot_user_role");

            entity.HasKey(e => new { e.UserId, e.RoleId });

            entity.HasOne(e => e.User)
                .WithMany(u => u.Roles)
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Restrict, not cascade: deleting a role that people still hold is refused by the
            // service, and the database agrees rather than quietly stripping their access.
            entity.HasOne(e => e.Role)
                .WithMany(r => r.Users)
                .HasForeignKey(e => e.RoleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<OneTimeLink>(entity =>
        {
            entity.ToTable("modbot_one_time_link");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.Property(e => e.TokenHash).HasMaxLength(64);
            entity.Property(e => e.RoleIds).HasColumnType("jsonb");

            // Every use of a link is a lookup by hash, and two links must never share one.
            entity.HasIndex(e => e.TokenHash).IsUnique();

            // "This account's outstanding reset links" and "who created this invite" are the
            // other two questions asked of the table.
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.CreatedByUserId);
        });

        builder.Entity<Webhook>(entity =>
        {
            entity.ToTable("api_webhook");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.Property(e => e.Name).HasMaxLength(64);
            entity.Property(e => e.Url).HasMaxLength(2048);
            entity.Property(e => e.EventTypes).HasColumnType("jsonb");
            entity.Property(e => e.SubjectIds).HasColumnType("jsonb");
            entity.Property(e => e.LastError).HasMaxLength(512);
            entity.Property(e => e.DisabledReason).HasMaxLength(640);

            entity.HasIndex(e => e.CreatedByUserId);
        });

        builder.Entity<WebhookDelivery>(entity =>
        {
            entity.ToTable("api_webhook_delivery");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.EventId).HasMaxLength(64);
            entity.Property(e => e.EventType).HasMaxLength(128);
            entity.Property(e => e.Error).HasMaxLength(512);
            entity.Property(e => e.Outcome).HasMaxLength(16);

            // Deleting a webhook takes its log with it; the facts keep the record that it existed.
            entity.HasOne<Webhook>()
                .WithMany()
                .HasForeignKey(e => e.WebhookId)
                .OnDelete(DeleteBehavior.Cascade);

            // "The last fifty attempts for this webhook", newest first.
            entity.HasIndex(e => new { e.WebhookId, e.Id });
        });

        builder.Entity<ApiKey>(entity =>
        {
            entity.ToTable("api_key");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.Property(e => e.Name).HasMaxLength(64);
            entity.Property(e => e.Start).HasMaxLength(16);
            entity.Property(e => e.KeyHash).HasMaxLength(64);

            // Every request made with a key is a lookup by this hash, and two keys must never
            // share one.
            entity.HasIndex(e => e.KeyHash).IsUnique();
            entity.HasIndex(e => e.CreatedByUserId);
        });

        builder.Entity<ClientDeviceRecord>(entity =>
        {
            entity.ToTable("client_device");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.Property(e => e.TokenHash).HasMaxLength(128);
            entity.Property(e => e.ClientVersion).HasMaxLength(32);
            entity.Property(e => e.Platform).HasMaxLength(32);

            // Every authenticated client request resolves a device by this hash, so it is the one
            // index that has to exist. Unique because two devices sharing a token would make
            // "revoke that install" ambiguous.
            entity.HasIndex(e => e.TokenHash).IsUnique();
        });

        builder.Entity<ClientPairingCodeRecord>(entity =>
        {
            entity.ToTable("client_pairing_code");

            entity.HasKey(e => e.CodeHash);
            entity.Property(e => e.CodeHash).HasMaxLength(128);
        });

        builder.Entity<EvidenceBlob>(entity =>
        {
            entity.ToTable("modbot_evidence_blob");

            // Keyed on the hash because the store is content-addressed: the same bytes uploaded
            // twice are one object and one row, cited by two reports.
            entity.HasKey(e => e.Hash);
            entity.Property(e => e.Hash).HasMaxLength(64);

            entity.Property(e => e.ContentType).HasMaxLength(128);
            entity.Property(e => e.FileName).HasMaxLength(256);
            entity.Property(e => e.UploaderId).HasMaxLength(128);
            entity.Property(e => e.ReportId).HasMaxLength(128);
            entity.Property(e => e.DestroyedBy).HasMaxLength(128);
            entity.Property(e => e.DestroyedReason).HasMaxLength(512);

            // Rendering a case file is "every blob for this report", and it must not scan.
            entity.HasIndex(e => e.ReportId);
        });

        builder.Entity<VRChatUser>(entity =>
        {
            entity.ToTable("vrchat_user");

            // Keyed on VRChat's own id, which is opaque text with no length assumption
            // (spec 3.1.1): legacy ids follow no structure at all.
            entity.HasKey(e => e.UserId);
            entity.Property(e => e.UserId).HasColumnType("text");

            // User-authored text, and VRChat's own caps on it have moved before. Unbounded text
            // rather than a guessed varchar that would one day reject a real profile.
            entity.Property(e => e.DisplayName).HasColumnType("text");
            entity.Property(e => e.Bio).HasColumnType("text");
            entity.Property(e => e.StatusDescription).HasColumnType("text");
            entity.Property(e => e.Pronouns).HasColumnType("text");
            entity.Property(e => e.Status).HasMaxLength(32);
            entity.Property(e => e.LastPlatform).HasMaxLength(128);
            entity.Property(e => e.AgeVerificationStatus).HasMaxLength(32);
            entity.Property(e => e.Is18PlusVerifiedSource).HasMaxLength(16);
            entity.Property(e => e.RefreshError).HasMaxLength(512);

            // The snake-case convention would write "is18_plus_verified"; the digit belongs to
            // the next word, not the previous one, and somebody grepping the schema for the flag
            // should find it by the name the design uses.
            entity.Property(e => e.Is18PlusVerified).HasColumnName("is_18_plus_verified");
            entity.Property(e => e.Is18PlusVerifiedAt).HasColumnName("is_18_plus_verified_at");
            entity.Property(e => e.Is18PlusVerifiedSource).HasColumnName("is_18_plus_verified_source");
            entity.Property(e => e.Is18PlusVerifiedByUserId).HasColumnName("is_18_plus_verified_by_user_id");

            // The refresh queue is ordered on these two (user profile sync design §3.2), and a
            // 150,000-row table asked "who is oldest" once a second must not scan.
            entity.HasIndex(e => e.LastRefreshedAt).HasDatabaseName("ix_vrchat_user_last_refreshed");
            entity.HasIndex(e => e.LastSeenAt).HasDatabaseName("ix_vrchat_user_last_seen");
        });

        builder.Entity<VRChatWorld>(entity =>
        {
            entity.ToTable("vrchat_world");

            // VRChat's id, opaque text with no length assumption (spec 3.1.1).
            entity.HasKey(e => e.WorldId);
            entity.Property(e => e.WorldId).HasColumnType("text");

            // Author-written, and VRChat's caps on these have moved before.
            entity.Property(e => e.Name).HasColumnType("text");
            entity.Property(e => e.Description).HasColumnType("text");
            entity.Property(e => e.AuthorId).HasColumnType("text");
            entity.Property(e => e.AuthorName).HasColumnType("text");
            entity.Property(e => e.ImageUrl).HasColumnType("text");
            entity.Property(e => e.ThumbnailImageUrl).HasColumnType("text");
            entity.Property(e => e.ReleaseStatus).HasMaxLength(32);
            entity.Property(e => e.RefreshError).HasMaxLength(512);

            // The sweep asks "which worlds still have no name", and once a group has settled
            // almost none do, so the index covers only those.
            entity.HasIndex(e => e.FirstSeenAt)
                .HasDatabaseName("ix_vrchat_world_unnamed")
                .HasFilter("last_refreshed_at IS NULL");
        });

        builder.Entity<InstanceHeadCount>(entity =>
        {
            entity.ToTable("instance_head_count");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.Property(e => e.Source).HasMaxLength(8);

            // Rows belong to their room and go with it. Rooms are never deleted in normal running,
            // but a test reset or an operator's clean-up must not be blocked by the history.
            entity.HasOne<VRChatInstance>()
                .WithMany()
                .HasForeignKey(e => e.InstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            // "How full was this room, and when", in order -- the only question asked of it.
            entity.HasIndex(e => new { e.InstanceId, e.CountedAt })
                .HasDatabaseName("ix_instance_head_count_room");
        });

        builder.Entity<VRChatInstance>(entity =>
        {
            entity.ToTable("vrchat_instance");

            // Modbot's own id, because VRChat's instance numbers are reissued (see the entity).
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Location).HasColumnType("text");
            entity.Property(e => e.WorldId).HasColumnType("text");
            entity.Property(e => e.VRChatInstanceId).HasColumnType("text");
            entity.Property(e => e.GroupId).HasColumnType("text");
            entity.Property(e => e.Type).HasMaxLength(32);
            entity.Property(e => e.GroupAccessType).HasMaxLength(32);
            entity.Property(e => e.Region).HasMaxLength(32);
            entity.Property(e => e.ClosedBy).HasMaxLength(16);
            entity.Property(e => e.HeadCountSource).HasMaxLength(8);

            // Discord ids are long numbers Modbot never does arithmetic on, so they are text --
            // the same choice the moderation log channel setting already makes.
            entity.Property(e => e.AnnouncementMessageId).HasColumnType("text");
            entity.Property(e => e.AnnouncementChannelId).HasColumnType("text");

            // The question asked on every single presence report, thousands of times an hour:
            // "is there an open room at this location?" It must be an index seek, and because
            // open rooms are a tiny fraction of all rooms ever, the filter keeps it that way.
            entity.HasIndex(e => new { e.Location, e.LastSeenAt })
                .HasDatabaseName("ix_vrchat_instance_open")
                .HasFilter("closed_at IS NULL");

            // "Which rooms did this world have, newest first" -- the world's own history page.
            entity.HasIndex(e => new { e.WorldId, e.OpenedAt })
                .HasDatabaseName("ix_vrchat_instance_world")
                .IsDescending(false, true);

            // "What has the group had open lately", and the sweep that closes rooms the live
            // list stopped carrying.
            entity.HasIndex(e => new { e.GroupId, e.OpenedAt })
                .HasDatabaseName("ix_vrchat_instance_group")
                .IsDescending(false, true);

            // The announcer asks twice a minute "which rooms need their message written or
            // brought up to date", and the answer is almost always none. The filter keeps that
            // question off every finished room Modbot has ever seen.
            entity.HasIndex(e => e.AnnouncementUpdatedAt)
                .HasDatabaseName("ix_vrchat_instance_announcing")
                .HasFilter("announcement_finished = false");
        });

        builder.Entity<GroupMember>(entity =>
        {
            entity.ToTable("group_member");

            // Both halves of the key are VRChat ids: opaque text, no length (spec 3.1.1).
            entity.HasKey(e => new { e.GroupId, e.UserId });
            entity.Property(e => e.GroupId).HasColumnType("text");
            entity.Property(e => e.UserId).HasColumnType("text");
            entity.Property(e => e.MembershipId).HasColumnType("text");

            // VRChat's own words, kept as text rather than an enum: a value this build has not
            // seen is still a real membership.
            entity.Property(e => e.MembershipStatus).HasMaxLength(32);
            entity.Property(e => e.Visibility).HasMaxLength(32);
            entity.Property(e => e.ManagerNotes).HasColumnType("text");

            // The Members page: current members of the group, newest joiners first.
            entity.HasIndex(e => new { e.GroupId, e.LeftAt, e.JoinedAt })
                .HasDatabaseName("ix_group_member_current")
                .IsDescending(false, false, true);

            // The subject pane asks by person, whichever group.
            entity.HasIndex(e => e.UserId).HasDatabaseName("ix_group_member_user");

            // The role filter is a jsonb containment test, and GIN is what answers one.
            entity.HasIndex(e => e.Roles)
                .HasDatabaseName("ix_group_member_roles")
                .HasMethod("gin");

            // The end of every sweep asks "which rows still have a change waiting", and almost
            // none do, so the index covers only those.
            entity.HasIndex(e => e.GroupId)
                .HasDatabaseName("ix_group_member_waiting")
                .HasFilter("waiting_facts IS NOT NULL");
        });

        builder.Entity<GroupBan>(entity =>
        {
            entity.ToTable("group_ban");

            entity.HasKey(e => new { e.GroupId, e.UserId });
            entity.Property(e => e.GroupId).HasColumnType("text");
            entity.Property(e => e.UserId).HasColumnType("text");

            // The Bans page: bans that stand, newest first.
            entity.HasIndex(e => new { e.GroupId, e.LiftedAt, e.BannedAt })
                .HasDatabaseName("ix_group_ban_current")
                .IsDescending(false, false, true);

            entity.HasIndex(e => e.UserId).HasDatabaseName("ix_group_ban_user");

            entity.HasIndex(e => e.GroupId)
                .HasDatabaseName("ix_group_ban_waiting")
                .HasFilter("waiting_facts IS NOT NULL");
        });

        builder.Entity<BanReason>(entity =>
        {
            entity.ToTable("ban_reason");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.Property(e => e.Label).HasMaxLength(64);
            entity.Property(e => e.Description).HasMaxLength(256);

            // The buttons are drawn in this order, every time a ban is written up.
            entity.HasIndex(e => e.SortOrder).HasDatabaseName("ix_ban_reason_order");
        });

        builder.Entity<CaseFile>(entity =>
        {
            entity.ToTable("case_file");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            // VRChat ids are opaque text with no length assumption (spec 3.1.1). The audit entry
            // id is VRChat's too.
            entity.Property(e => e.UserId).HasColumnType("text");
            entity.Property(e => e.GroupId).HasColumnType("text");
            entity.Property(e => e.AuditEntryId).HasColumnType("text");

            entity.Property(e => e.AuthorUsername).HasMaxLength(64);
            entity.Property(e => e.UpdatedByUsername).HasMaxLength(64);
            entity.Property(e => e.WithdrawnByUsername).HasMaxLength(64);
            entity.Property(e => e.WithdrawnNote).HasMaxLength(2000);

            // A moderator's own words, at whatever length they need. Bounded by the API, not by
            // a varchar that would one day cut a real write-up short.
            entity.Property(e => e.WrittenReason).HasColumnType("text");

            // The subject pane and the ban list both ask "this person's case files, newest first".
            entity.HasIndex(e => new { e.UserId, e.CreatedAt })
                .HasDatabaseName("ix_case_file_user")
                .IsDescending(false, true);

            // "Which case file is about this ban" is how the audit-log rows find their badge and
            // how the unwritten list decides a ban is covered. Only rows that know their entry.
            entity.HasIndex(e => e.AuditEntryId)
                .HasDatabaseName("ix_case_file_audit_entry")
                .HasFilter("audit_entry_id IS NOT NULL");

            // The case file list, newest first.
            entity.HasIndex(e => e.CreatedAt)
                .HasDatabaseName("ix_case_file_created")
                .IsDescending();
        });

        builder.Entity<ModbotEvent>(entity =>
        {
            entity.ToTable("modbot_event");

            // Postgres requires the partition key in every unique constraint on a partitioned
            // table, so the key is (id, occurred_at) rather than id alone.
            entity.HasKey(e => new { e.Id, e.OccurredAt })
                .HasName("pk_modbot_event");

            // Serial rather than an identity column: the table is created by hand-written SQL
            // because EF cannot express declarative partitioning, and a plain sequence default
            // is the form that works on a partitioned parent everywhere.
            entity.Property(e => e.Id).UseSerialColumn();

            entity.Property(e => e.Data).HasColumnType("jsonb");

            // Hierarchical text rather than the smallint it was until 2026-09-13. The length cap
            // matches FactType.IsWellFormed, so a malformed value is refused by the writer before
            // the database has to have an opinion about it.
            entity.Property(e => e.Type).HasMaxLength(128);
            entity.Property(e => e.TypeRaw).HasMaxLength(256);

            // Ids are opaque (spec 3.1.1): text, never uuid, and no length assumption.
            entity.Property(e => e.SubjectId).HasColumnType("text");
            entity.Property(e => e.ActorId).HasColumnType("text");
            entity.Property(e => e.WorldId).HasColumnType("text");
            entity.Property(e => e.InstanceId).HasColumnType("text");

            entity.HasIndex(e => new { e.SubjectPlatform, e.SubjectId, e.OccurredAt })
                .HasDatabaseName("ix_modbot_event_subject")
                .IsDescending(false, false, true);

            // Actor-side questions -- "everything this moderator has done" -- which the
            // subject-side index cannot answer efficiently (spec 5.8.5).
            entity.HasIndex(e => new { e.ActorPlatform, e.ActorId, e.OccurredAt })
                .HasDatabaseName("ix_modbot_event_actor")
                .IsDescending(false, false, true);

            entity.HasIndex(e => new { e.Type, e.OccurredAt })
                .HasDatabaseName("ix_modbot_event_type")
                .IsDescending(false, true);

            // The deduplication range check (spec 5.7.1) runs on every client-reported fact, so
            // it gets its own index in the order the check narrows.
            entity.HasIndex(e => new { e.InstanceId, e.SubjectId, e.Type, e.OccurredAt })
                .HasDatabaseName("ix_modbot_event_dedup");

            entity.HasIndex(e => e.Data)
                .HasDatabaseName("ix_modbot_event_data")
                .HasMethod("gin");
        });

        builder.Entity<DailyTotal>(entity =>
        {
            // Named for what it holds, in the database as well as in code -- renamed from
            // modbot_rollup_daily by the PlainNamesInSchema migration.
            entity.ToTable("modbot_daily_total");

            // Spec 5.4's key exactly, with the empty string standing in for "no dimension":
            // PostgreSQL does not allow NULL in a primary key column.
            entity.HasKey(e => new { e.Day, e.Metric, e.Dimension })
                .HasName("pk_modbot_daily_total");

            entity.Property(e => e.Metric).HasColumnType("text");
            entity.Property(e => e.Dimension).HasColumnType("text");

            // Unconstrained numeric: apportioning imprecise facts across days produces fractions,
            // and a fixed scale chosen now would quietly truncate a metric invented later.
            entity.Property(e => e.Value).HasColumnType("numeric");

            // "Everything for this metric over time" is the shape every chart asks for, and the
            // primary key leads with the day, so it cannot serve that query.
            entity.HasIndex(e => new { e.Metric, e.Day })
                .HasDatabaseName("ix_modbot_daily_total_metric");
        });

        builder.Entity<RateLimitBucket>(entity =>
        {
            entity.ToTable("rate_limit_bucket");

            // The name carries a VRChat id for resource buckets, so it is text with no length
            // assumption: ids are opaque and are never validated (spec 3.1.1).
            entity.HasKey(e => e.Name);
            entity.Property(e => e.Name).HasColumnType("text");
            entity.Property(e => e.EndpointClass).HasColumnType("text");
            entity.Property(e => e.ResourceId).HasColumnType("text");

            // "Which buckets are stopped right now" is the gate health query (spec 4.3.3), and it
            // runs on every dashboard load.
            entity.HasIndex(e => e.StoppedUntil)
                .HasDatabaseName("ix_rate_limit_bucket_stopped");
        });

        builder.Entity<DailyTotalsState>(entity =>
        {
            // Same as modbot_daily_total: the C# name moved to plain words, the table did not.
            entity.ToTable("modbot_daily_totals_state", t =>
                t.HasCheckConstraint("ck_modbot_daily_totals_state_singleton", "id = 1"));

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
        });

        builder.Entity<RepeatOffender>(entity =>
        {
            entity.ToTable("modbot_repeat_offender");

            entity.HasKey(e => new { e.SubjectPlatform, e.SubjectId })
                .HasName("pk_modbot_repeat_offender");

            // Ids are opaque (spec 3.1.1): text, no length assumption.
            entity.Property(e => e.SubjectId).HasColumnType("text");
            entity.Property(e => e.LastActorId).HasColumnType("text");
            entity.Property(e => e.LastActionType).HasMaxLength(128);
            entity.Property(e => e.Status).HasMaxLength(32);

            // The snake-case convention would write "actions_last30days"; the digits are a word
            // of their own, as with is_18_plus_verified above.
            entity.Property(e => e.ActionsLast30Days).HasColumnName("actions_last_30_days");
            entity.Property(e => e.ActionsLast90Days).HasColumnName("actions_last_90_days");
            entity.Property(e => e.ModeratorsLast90Days).HasColumnName("moderators_last_90_days");

            // The list is read newest-last-action first, and the job refreshes rows whose
            // windowed counts are about to change; both must not scan.
            entity.HasIndex(e => e.LastActionAt)
                .HasDatabaseName("ix_modbot_repeat_offender_last_action")
                .IsDescending();
            entity.HasIndex(e => e.CountsChangeAt)
                .HasDatabaseName("ix_modbot_repeat_offender_counts_change");
        });

        builder.Entity<Review>(entity =>
        {
            entity.ToTable("modbot_review");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.Property(e => e.ModeratorId).HasColumnType("text");
            entity.Property(e => e.About).HasColumnType("text");
            entity.Property(e => e.Signal).HasMaxLength(64);
            entity.Property(e => e.Summary).HasColumnType("text");
            entity.Property(e => e.ClosedByUsername).HasMaxLength(64);
            entity.Property(e => e.Note).HasMaxLength(2000);

            // The idempotence rule in the database: at most one open review per moderator,
            // signal and thing. Two detection runs racing each other then cannot open two.
            entity.HasIndex(e => new { e.ModeratorPlatform, e.ModeratorId, e.Signal, e.About })
                .HasDatabaseName("ux_modbot_review_open")
                .IsUnique()
                .HasFilter("state = 1");

            // "Every open review" is the page and the nav badge; "reviews about this moderator"
            // is the detection run deciding whether a closed one already covers the evidence.
            entity.HasIndex(e => new { e.State, e.OpenedAt })
                .HasDatabaseName("ix_modbot_review_state");
            entity.HasIndex(e => new { e.ModeratorPlatform, e.ModeratorId, e.Signal, e.About, e.WindowEnd })
                .HasDatabaseName("ix_modbot_review_key");
        });

        builder.Entity<ModeratorBaseline>(entity =>
        {
            entity.ToTable("modbot_moderator_baseline");

            entity.HasKey(e => new { e.Platform, e.ModeratorId })
                .HasName("pk_modbot_moderator_baseline");

            entity.Property(e => e.ModeratorId).HasColumnType("text");
            entity.Property(e => e.Actions).HasColumnType("numeric");
            entity.Property(e => e.ActionsPerActiveDay).HasColumnType("numeric");
        });

        builder.Entity<ReviewRunState>(entity =>
        {
            entity.ToTable("modbot_review_run_state", t =>
                t.HasCheckConstraint("ck_modbot_review_run_state_singleton", "id = 1"));

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
        });

        builder.Entity<StorageDay>(entity =>
        {
            entity.ToTable("modbot_storage_day");

            // The day is the key, so recording a day a second time can only ever update it.
            entity.HasKey(e => e.Day).HasName("pk_modbot_storage_day");
        });

        builder.Entity<DiscordServer>(entity =>
        {
            entity.ToTable("discord_server");
            entity.HasKey(e => e.GuildId);

            // Discord's ids are opaque text here, as everywhere else Modbot stores one.
            entity.Property(e => e.GuildId).HasColumnType("text");
            entity.Property(e => e.Name).HasColumnType("text");
        });

        builder.Entity<DiscordChannel>(entity =>
        {
            entity.ToTable("discord_channel");
            entity.HasKey(e => e.ChannelId);
            entity.Property(e => e.ChannelId).HasColumnType("text");
            entity.Property(e => e.GuildId).HasColumnType("text");
            entity.Property(e => e.Name).HasColumnType("text");
            entity.Property(e => e.Type).HasMaxLength(16);
            entity.Property(e => e.CategoryId).HasColumnType("text");

            // The only question asked of it: every channel in this server.
            entity.HasIndex(e => e.GuildId).HasDatabaseName("ix_discord_channel_guild");
        });

        builder.Entity<DiscordRole>(entity =>
        {
            entity.ToTable("discord_role");
            entity.HasKey(e => e.RoleId);
            entity.Property(e => e.RoleId).HasColumnType("text");
            entity.Property(e => e.GuildId).HasColumnType("text");
            entity.Property(e => e.Name).HasColumnType("text");

            entity.HasIndex(e => e.GuildId).HasDatabaseName("ix_discord_role_guild");
        });

        builder.Entity<Insight>(entity =>
        {
            entity.ToTable("modbot_insight");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.Property(e => e.Kind).HasMaxLength(32);
            entity.Property(e => e.StartedBy).HasMaxLength(16);
            entity.Property(e => e.RequestedByUsername).HasMaxLength(64);
            entity.Property(e => e.Model).HasMaxLength(200);
            entity.Property(e => e.Provider).HasMaxLength(32);
            entity.Property(e => e.Figures).HasColumnType("jsonb");
            entity.Property(e => e.Text).HasColumnType("text");
            entity.Property(e => e.Error).HasMaxLength(1000);
            entity.Property(e => e.DiscordChannelId).HasMaxLength(32);
            entity.Property(e => e.DiscordError).HasMaxLength(1000);

            // "The latest of each kind" and "earlier ones" on the My Group page.
            entity.HasIndex(e => new { e.Kind, e.CreatedAt })
                .HasDatabaseName("ix_modbot_insight_kind_created")
                .IsDescending(false, true);

            // What the Discord poster still has to do. Small, because almost every row is done.
            entity.HasIndex(e => e.CreatedAt)
                .HasDatabaseName("ix_modbot_insight_discord_waiting")
                .HasFilter("discord_channel_id IS NOT NULL AND discord_posted_at IS NULL AND discord_error IS NULL AND text IS NOT NULL");
        });

        builder.Entity<InsightSettings>(entity =>
        {
            entity.ToTable("modbot_insight_settings", t =>
                t.HasCheckConstraint("ck_modbot_insight_settings_singleton", "id = 1"));

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.TimeZone).HasMaxLength(64);
            entity.Property(e => e.Model).HasMaxLength(200);
        });

        builder.Entity<InsightSchedule>(entity =>
        {
            entity.ToTable("modbot_insight_schedule");

            entity.HasKey(e => e.Kind).HasName("pk_modbot_insight_schedule");
            entity.Property(e => e.Kind).HasMaxLength(32);
            entity.Property(e => e.Every).HasMaxLength(8);
            entity.Property(e => e.DiscordChannelId).HasMaxLength(32);
        });

        builder.Entity<AiChatConversation>(entity =>
        {
            entity.ToTable("ai_chat_conversation");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Title).HasMaxLength(200);

            // Deleting an account deletes its conversations: nobody else may read them, so
            // keeping them would keep something nobody can open.
            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.UserId, e.UpdatedAt });
        });

        builder.Entity<AiChatMessage>(entity =>
        {
            entity.ToTable("ai_chat_message");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.Property(e => e.Role).HasMaxLength(16);
            entity.Property(e => e.ToolCalls).HasColumnType("jsonb");
            entity.Property(e => e.Mentioned).HasColumnType("jsonb");
            entity.Property(e => e.ToolCallId).HasColumnType("text");
            entity.Property(e => e.ToolName).HasMaxLength(64);

            entity.HasOne(e => e.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(e => e.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.ConversationId, e.Id });
        });

        builder.Entity<Settings>(entity =>
            entity.Property(e => e.AiChatToolSwitches).HasColumnType("jsonb"));

        builder.Entity<ModerationTermList>(entity =>
        {
            entity.ToTable("ai_term_list");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.Source).HasMaxLength(16);
            entity.Property(e => e.Terms).HasColumnType("jsonb");
            entity.Property(e => e.ExcludedTerms).HasColumnType("jsonb");
            entity.Property(e => e.ActSetByUsername).HasMaxLength(64);
            entity.Property(e => e.HubId).HasMaxLength(100);
            entity.Property(e => e.HubVersion).HasMaxLength(32);
            entity.Property(e => e.HubAvailableVersion).HasMaxLength(32);
            entity.Property(e => e.HubAvailableTerms).HasColumnType("jsonb");
            entity.Property(e => e.HubAvailableChanges).HasColumnType("jsonb");
            entity.Property(e => e.HubError).HasMaxLength(500);

            // A Hub list is subscribed once; a second subscription would flag everything twice.
            entity.HasIndex(e => e.HubId)
                .HasDatabaseName("ux_ai_term_list_hub_id")
                .IsUnique()
                .HasFilter("hub_id IS NOT NULL");
        });

        builder.Entity<ModerationTopic>(entity =>
        {
            entity.ToTable("ai_topic");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.Instructions).HasMaxLength(2000);
            entity.Property(e => e.Sensitivity).HasMaxLength(16);
            entity.Property(e => e.ActSetByUsername).HasMaxLength(64);
        });

        builder.Entity<ModerationFlag>(entity =>
        {
            entity.ToTable("ai_flag");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.Property(e => e.RuleKind).HasMaxLength(16);
            entity.Property(e => e.RuleName).HasMaxLength(100);
            entity.Property(e => e.TermKey).HasMaxLength(200);
            entity.Property(e => e.Term).HasMaxLength(2000);
            entity.Property(e => e.Target).HasMaxLength(32);
            entity.Property(e => e.SubjectId).HasColumnType("text");
            entity.Property(e => e.SubjectName).HasMaxLength(200);
            entity.Property(e => e.ChannelId).HasColumnType("text");
            entity.Property(e => e.MessageId).HasColumnType("text");
            entity.Property(e => e.Matched).HasMaxLength(1000);
            entity.Property(e => e.Reason).HasMaxLength(2000);
            entity.Property(e => e.DismissedByUsername).HasMaxLength(64);

            // The page: open flags, newest first.
            entity.HasIndex(e => new { e.State, e.FlaggedAt })
                .HasDatabaseName("ix_ai_flag_state");

            // "Has this rule and term already been flagged, or dismissed, for this person?" --
            // asked before every flag is written. Also the per-rule dismissal rate.
            entity.HasIndex(e => new { e.RuleId, e.TermKey, e.SubjectPlatform, e.SubjectId })
                .HasDatabaseName("ix_ai_flag_rule_person");

            entity.HasIndex(e => e.MessageId)
                .HasDatabaseName("ix_ai_flag_message")
                .HasFilter("message_id IS NOT NULL");
        });

        builder.Entity<AiUsage>(entity =>
        {
            entity.ToTable("ai_usage");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Feature).HasMaxLength(32);
            entity.Property(e => e.Model).HasMaxLength(200);
            entity.Property(e => e.Provider).HasMaxLength(32);

            // "How much has this feature used this month", asked before each request.
            entity.HasIndex(e => new { e.Feature, e.At })
                .HasDatabaseName("ix_ai_usage_feature_at");
        });

        builder.Entity<AiFeatureLimit>(entity =>
        {
            entity.ToTable("ai_feature_limit");

            entity.HasKey(e => e.Feature).HasName("pk_ai_feature_limit");
            entity.Property(e => e.Feature).HasMaxLength(32);
        });

        builder.Entity<DiscordEventRoute>(entity =>
        {
            entity.ToTable("discord_event_route");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.ChannelId).HasColumnType("text");

            // Short lists read and written whole, like modbot_one_time_link.role_ids.
            entity.Property(e => e.EventTypes).HasColumnType("jsonb");
            entity.Property(e => e.SubjectIds).HasColumnType("jsonb");
            entity.Property(e => e.ActorIds).HasColumnType("jsonb");
            entity.Property(e => e.SubjectVRChatRoleIds).HasColumnType("jsonb");
            entity.Property(e => e.ActorVRChatRoleIds).HasColumnType("jsonb");
            entity.Property(e => e.ActorModbotRoleIds).HasColumnType("jsonb");

            entity.Ignore(e => e.HasPeopleFilters);
        });

        builder.Entity<DiscordEventChannel>(entity =>
        {
            entity.ToTable("discord_event_channel");
            entity.HasKey(e => e.ChannelId);
            entity.Property(e => e.ChannelId).HasColumnType("text");
            entity.Property(e => e.LastError).HasColumnType("text");
        });

        builder.Entity<DiscordAccountLink>(entity =>
        {
            entity.ToTable("discord_account_link");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.DiscordUserId).HasColumnType("text");
            entity.Property(e => e.DiscordUsername).HasColumnType("text");
            entity.Property(e => e.VRChatUserId).HasColumnType("text").HasColumnName("vrchat_user_id");
            entity.Property(e => e.VRChatDisplayName).HasColumnType("text").HasColumnName("vrchat_display_name");
            entity.Property(e => e.StartedFrom).HasMaxLength(16);
            entity.Property(e => e.UnlinkedBy).HasMaxLength(16);
            entity.Property(e => e.LinkedRoleId).HasColumnType("text");
            entity.Property(e => e.EighteenPlusRoleId).HasColumnType("text");
            entity.Property(e => e.RoleError).HasColumnType("text");
            entity.Ignore(e => e.IsActive);

            // One active link per account on each side; ended rows are history and may repeat.
            entity.HasIndex(e => e.DiscordUserId)
                .IsUnique()
                .HasFilter("unlinked_at IS NULL")
                .HasDatabaseName("ux_discord_account_link_discord_active");

            entity.HasIndex(e => e.VRChatUserId)
                .IsUnique()
                .HasFilter("unlinked_at IS NULL")
                .HasDatabaseName("ux_discord_account_link_vrchat_active");
        });

        builder.Entity<DiscordLinkCode>(entity =>
        {
            entity.ToTable("discord_link_code");
            entity.HasKey(e => e.DiscordUserId);
            entity.Property(e => e.DiscordUserId).HasColumnType("text");
            entity.Property(e => e.VRChatUserId).HasColumnType("text").HasColumnName("vrchat_user_id");
            entity.Property(e => e.Code).HasMaxLength(32);
            entity.Property(e => e.StartedFrom).HasMaxLength(16);
        });

        builder.Entity<DiscordMessage>(entity =>
        {
            // Created by hand-written SQL in the migration, like the fact log: EF cannot express
            // declarative partitioning. If you change the columns here, change them there too.
            entity.ToTable("discord_message");

            entity.HasKey(e => new { e.MessageId, e.SentAt }).HasName("pk_discord_message");

            entity.Property(e => e.MessageId).HasColumnType("text");
            entity.Property(e => e.GuildId).HasColumnType("text");
            entity.Property(e => e.ChannelId).HasColumnType("text");
            entity.Property(e => e.ThreadId).HasColumnType("text");
            entity.Property(e => e.AuthorId).HasColumnType("text");
            entity.Property(e => e.AuthorName).HasColumnType("text");
            entity.Property(e => e.Text).HasColumnType("text");
            entity.Property(e => e.Attachments).HasColumnType("jsonb");
            entity.Property(e => e.ReplyToId).HasColumnType("text");

            // An edit or a delete arrives with the message id and nothing else.
            entity.HasIndex(e => e.MessageId).HasDatabaseName("ix_discord_message_id");

            // "The newest message stored in this channel" -- where catching up starts -- and a
            // channel's messages in order.
            entity.HasIndex(e => new { e.ChannelId, e.SentAt })
                .HasDatabaseName("ix_discord_message_channel")
                .IsDescending(false, true);

            // The same question for a thread.
            entity.HasIndex(e => new { e.ThreadId, e.SentAt })
                .HasDatabaseName("ix_discord_message_thread")
                .IsDescending(false, true)
                .HasFilter("thread_id IS NOT NULL");

            // One person's messages: their profile, and purge-user.
            entity.HasIndex(e => new { e.AuthorId, e.SentAt })
                .HasDatabaseName("ix_discord_message_author")
                .IsDescending(false, true);

            // The daily totals ask which days messages stored since their last run fall on.
            entity.HasIndex(e => e.StoredAt).HasDatabaseName("ix_discord_message_stored");
        });

        builder.Entity<DiscordMessageEdit>(entity =>
        {
            entity.ToTable("discord_message_edit");

            entity.HasKey(e => new { e.Id, e.SentAt }).HasName("pk_discord_message_edit");
            entity.Property(e => e.Id).UseSerialColumn();

            entity.Property(e => e.MessageId).HasColumnType("text");
            entity.Property(e => e.Text).HasColumnType("text");

            entity.HasIndex(e => new { e.MessageId, e.ReplacedAt }).HasDatabaseName("ix_discord_message_edit_message");
        });

        builder.Entity<DiscordReadBack>(entity =>
        {
            entity.ToTable("discord_read_back");
            entity.HasKey(e => e.ChannelId);

            entity.Property(e => e.ChannelId).HasColumnType("text");
            entity.Property(e => e.GuildId).HasColumnType("text");
            entity.Property(e => e.ParentChannelId).HasColumnType("text");
            entity.Property(e => e.Name).HasColumnType("text");
            entity.Property(e => e.OldestReadId).HasColumnType("text");
            entity.Property(e => e.StoppedBecause).HasMaxLength(16);
            entity.Property(e => e.LastError).HasColumnType("text");

            entity.HasIndex(e => e.GuildId).HasDatabaseName("ix_discord_read_back_guild");
        });

        base.OnModelCreating(builder);
    }
}
