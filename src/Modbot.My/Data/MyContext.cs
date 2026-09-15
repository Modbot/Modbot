using Microsoft.EntityFrameworkCore;
using Modbot.My.Features.Admin;
using Modbot.My.Features.Instances;
using Modbot.My.Features.RegisterPage;
using Modbot.My.Features.Visits;

namespace Modbot.My.Data;

/// <summary>
/// The one database. Each table's shape is declared in its own feature folder and picked up here.
/// </summary>
public sealed class MyContext(DbContextOptions<MyContext> options) : DbContext(options)
{
    /// <summary>Deployments that registered themselves through the API.</summary>
    public DbSet<RegisteredInstance> RegisteredInstances => Set<RegisteredInstance>();

    /// <summary>The IP addresses registered deployments called from.</summary>
    public DbSet<RegisteredInstanceIp> RegisteredInstanceIps => Set<RegisteredInstanceIp>();

    /// <summary>Instance URLs noted by someone opening a page for them.</summary>
    public DbSet<RegisterPageInstance> RegisterPageInstances => Set<RegisterPageInstance>();

    /// <summary>Instance URLs opened from each visitor IP address.</summary>
    public DbSet<VisitorInstance> VisitorInstances => Set<VisitorInstance>();

    /// <summary>Signed-in <c>/admin</c> browsers.</summary>
    public DbSet<AdminSession> AdminSessions => Set<AdminSession>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MyContext).Assembly);
}
