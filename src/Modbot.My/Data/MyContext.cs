using Microsoft.EntityFrameworkCore;
using Modbot.My.Features.Instances;
using Modbot.My.Features.RegisterPage;

namespace Modbot.My.Data;

/// <summary>
/// The one database. Each table's shape is declared in its own feature folder and picked up here.
/// </summary>
public sealed class MyContext(DbContextOptions<MyContext> options) : DbContext(options)
{
    /// <summary>Deployments that registered themselves through the API.</summary>
    public DbSet<RegisteredInstance> RegisteredInstances => Set<RegisteredInstance>();

    /// <summary>Instance URLs noted by someone opening the register page.</summary>
    public DbSet<RegisterPageInstance> RegisterPageInstances => Set<RegisterPageInstance>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MyContext).Assembly);
}
