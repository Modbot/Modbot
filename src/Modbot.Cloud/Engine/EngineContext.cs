using Microsoft.EntityFrameworkCore;

namespace Modbot.Cloud.Engine;

/// <summary>
/// The event storage, from <c>DATABASE_ENGINE_URL</c>: log lines, parsed events and their totals.
/// </summary>
/// <remarks>
/// <para>
/// A database of its own because it is most of Cloud's bytes and grows by gigabytes a week, while
/// the main database is a few megabytes of installs and settings (cloud log backup spec 4.1).
/// </para>
/// <para>
/// Rows here name an install by its id and nothing else. There is no foreign key to the main
/// database, which is a different server: a removed install's lines are left to retention.
/// </para>
/// <para>
/// Its migrations are in <c>Engine/Migrations</c> and their history in
/// <c>__engine_migrations_history</c>, so the two databases can never mistake each other's
/// migrations for their own even if somebody points both variables at one server.
/// </para>
/// </remarks>
public sealed class EngineContext(DbContextOptions<EngineContext> options) : DbContext(options)
{
    public const string MigrationsHistoryTable = "__engine_migrations_history";

    public DbSet<LogFile> LogFiles => Set<LogFile>();

    public DbSet<LogLine> LogLines => Set<LogLine>();

    public DbSet<LogEvent> LogEvents => Set<LogEvent>();

    public DbSet<InstallClock> InstallClocks => Set<InstallClock>();

    public DbSet<LineDayTotal> LineDayTotals => Set<LineDayTotal>();

    public DbSet<EventHourTotal> EventHourTotals => Set<EventHourTotal>();

    /// <summary>
    /// The one way this context is pointed at a database, so the app, the design-time factory and
    /// the tests all agree on the history table.
    /// </summary>
    public static DbContextOptionsBuilder<EngineContext> Options(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<EngineContext>();
        Use(builder, connectionString);
        return builder;
    }

    public static void Use(DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseNpgsql(connectionString, o => o.MigrationsHistoryTable(MigrationsHistoryTable));
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new LogFileConfiguration());
        modelBuilder.ApplyConfiguration(new LogLineConfiguration());
        modelBuilder.ApplyConfiguration(new LogEventConfiguration());
        modelBuilder.ApplyConfiguration(new InstallClockConfiguration());
        modelBuilder.ApplyConfiguration(new LineDayTotalConfiguration());
        modelBuilder.ApplyConfiguration(new EventHourTotalConfiguration());
    }
}
