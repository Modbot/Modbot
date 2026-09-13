using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Data;

public class ModbotContext : DbContext
{
    public ModbotContext(DbContextOptions<ModbotContext> options) : base(options) { }

    public DbSet<Settings> Settings => Set<Settings>();

    public DbSet<ProtectorKey> ProtectorKeys => Set<ProtectorKey>();

    public DbSet<ModbotUser> Users => Set<ModbotUser>();

    /// <summary>The fact log (spec 5.3). Append-only: never update or delete a row here.</summary>
    public DbSet<ModbotEvent> Events => Set<ModbotEvent>();

    /// <summary>Daily aggregates (spec 5.4). Derived from <see cref="Events"/>, kept forever.</summary>
    public DbSet<RollupDaily> RollupDaily => Set<RollupDaily>();

    /// <summary>Where the incremental rollup run got to.</summary>
    public DbSet<RollupState> RollupState => Set<RollupState>();

    /// <summary>
    /// Rate-limit budgets and penalty state (spec 4.3.2). Persisted rather than held in memory so
    /// that a restart resumes a cold stop instead of walking back into it.
    /// </summary>
    public DbSet<RateLimitBucket> RateLimitBuckets => Set<RateLimitBucket>();

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

        builder.Entity<RollupDaily>(entity =>
        {
            entity.ToTable("modbot_rollup_daily");

            // Spec 5.4's key exactly, with the empty string standing in for "no dimension":
            // PostgreSQL does not allow NULL in a primary key column.
            entity.HasKey(e => new { e.Day, e.Metric, e.Dimension })
                .HasName("pk_modbot_rollup_daily");

            entity.Property(e => e.Metric).HasColumnType("text");
            entity.Property(e => e.Dimension).HasColumnType("text");

            // Unconstrained numeric: apportioning imprecise facts across days produces fractions,
            // and a fixed scale chosen now would quietly truncate a metric invented later.
            entity.Property(e => e.Value).HasColumnType("numeric");

            // "Everything for this metric over time" is the shape every chart asks for, and the
            // primary key leads with the day, so it cannot serve that query.
            entity.HasIndex(e => new { e.Metric, e.Day })
                .HasDatabaseName("ix_modbot_rollup_daily_metric");
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

        builder.Entity<RollupState>(entity =>
        {
            entity.ToTable("modbot_rollup_state", t =>
                t.HasCheckConstraint("ck_modbot_rollup_state_singleton", "id = 1"));

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
        });

        base.OnModelCreating(builder);
    }
}
