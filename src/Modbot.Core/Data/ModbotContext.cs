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

        base.OnModelCreating(builder);
    }
}
