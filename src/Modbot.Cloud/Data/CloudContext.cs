using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Features.Admin;
using Modbot.Cloud.Features.Installs;
using Modbot.Cloud.Features.Retention;

namespace Modbot.Cloud.Data;

/// <summary>
/// Cloud's main database, from <c>DATABASE_URL</c>.
/// </summary>
/// <remarks>
/// <para>
/// Small, and meant to stay small: installs, admin sessions and settings today, and accounts,
/// the instance registry, term lists and showcases later. Everything VRChat's logs produce lives in
/// the other database (<see cref="Engine.EngineContext"/>), so this one can be backed up, restored
/// and migrated without touching a hundred gigabytes of log lines.
/// </para>
/// <para>
/// Nothing here has a foreign key into the engine database or from it. They are separate servers.
/// </para>
/// </remarks>
public sealed class CloudContext(DbContextOptions<CloudContext> options) : DbContext(options)
{
    /// <summary>Clients that registered to send their logs.</summary>
    public DbSet<Install> Installs => Set<Install>();

    /// <summary>Signed-in <c>/admin</c> browsers.</summary>
    public DbSet<AdminSession> AdminSessions => Set<AdminSession>();

    /// <summary>The one row of settings an admin can change.</summary>
    public DbSet<CloudSettings> Settings => Set<CloudSettings>();

    /// <summary>The settings, or the defaults when none have been saved.</summary>
    public async Task<CloudSettings> GetSettingsAsync(CancellationToken ct) =>
        await Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == CloudSettings.SingleRowId, ct)
        ?? new CloudSettings();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Named one by one rather than scanned from the assembly: the engine database's tables
        // live in the same assembly and must never end up in this model.
        modelBuilder.ApplyConfiguration(new InstallConfiguration());
        modelBuilder.ApplyConfiguration(new AdminSessionConfiguration());
        modelBuilder.ApplyConfiguration(new CloudSettingsConfiguration());
    }
}
