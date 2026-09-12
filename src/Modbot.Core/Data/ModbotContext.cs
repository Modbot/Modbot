using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Data;

public class ModbotContext : DbContext
{
    public ModbotContext(DbContextOptions<ModbotContext> options) : base(options) { }

    public DbSet<Settings> Settings => Set<Settings>();

    public DbSet<ProtectorKey> ProtectorKeys => Set<ProtectorKey>();

    public DbSet<ModbotUser> Users => Set<ModbotUser>();

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

        base.OnModelCreating(builder);
    }
}
