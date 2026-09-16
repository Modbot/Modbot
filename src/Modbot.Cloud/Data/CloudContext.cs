using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Features.Accounts;
using Modbot.Cloud.Features.Admin;
using Modbot.Cloud.Features.Installs;
using Modbot.Cloud.Features.PublicRooms;
using Modbot.Cloud.Features.Registry;
using Modbot.Cloud.Features.Retention;
using Modbot.Cloud.Features.Site;

namespace Modbot.Cloud.Data;

/// <summary>
/// Cloud's main database, from <c>DATABASE_URL</c>.
/// </summary>
/// <remarks>
/// <para>
/// Small, and meant to stay small: installs, admin sessions and settings today, and accounts,
/// the instance registry, term lists and showcases later. The events clients back up live in the
/// other database (<see cref="Engine.EngineContext"/>), so this one can be backed up, restored
/// and migrated without touching the event storage.
/// </para>
/// <para>
/// Nothing here has a foreign key into the engine database or from it. They are separate servers.
/// </para>
/// </remarks>
public sealed class CloudContext(DbContextOptions<CloudContext> options) : DbContext(options)
{
    /// <summary>Clients that registered to send their logs.</summary>
    public DbSet<Install> Installs => Set<Install>();

    /// <summary>People who signed up on Cloud.</summary>
    public DbSet<Account> Accounts => Set<Account>();

    /// <summary>Signed-in account browsers.</summary>
    public DbSet<AccountSession> AccountSessions => Set<AccountSession>();

    /// <summary>The one-time tokens Cloud emailed.</summary>
    public DbSet<AccountToken> AccountTokens => Set<AccountToken>();

    /// <summary>Modbot servers that registered themselves.</summary>
    public DbSet<RegisteredServer> RegisteredServers => Set<RegisteredServer>();

    /// <summary>Every report those servers have sent.</summary>
    public DbSet<ServerReport> ServerReports => Set<ServerReport>();

    /// <summary>Modbot addresses noted by a my.modbot.co page visit.</summary>
    public DbSet<PageInstance> PageInstances => Set<PageInstance>();

    /// <summary>Modbot addresses opened from each visitor's IP address.</summary>
    public DbSet<VisitorInstance> VisitorInstances => Set<VisitorInstance>();

    /// <summary>Signed-in <c>/admin</c> browsers.</summary>
    public DbSet<AdminSession> AdminSessions => Set<AdminSession>();

    /// <summary>Modbot servers that report which of their group's rooms anyone can join.</summary>
    public DbSet<RoomsServer> RoomsServers => Set<RoomsServer>();

    /// <summary>Those groups' open public rooms, as last reported.</summary>
    public DbSet<PublicRoom> PublicRooms => Set<PublicRoom>();

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
        modelBuilder.ApplyConfiguration(new RoomsServerConfiguration());
        modelBuilder.ApplyConfiguration(new PublicRoomConfiguration());
        modelBuilder.ApplyConfiguration(new AccountConfiguration());
        modelBuilder.ApplyConfiguration(new AccountSessionConfiguration());
        modelBuilder.ApplyConfiguration(new AccountTokenConfiguration());
        modelBuilder.ApplyConfiguration(new RegisteredServerConfiguration());
        modelBuilder.ApplyConfiguration(new ServerReportConfiguration());
        modelBuilder.ApplyConfiguration(new PageInstanceConfiguration());
        modelBuilder.ApplyConfiguration(new VisitorInstanceConfiguration());
    }
}
