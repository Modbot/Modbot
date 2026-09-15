using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modbot.Cloud.Data;

/// <summary>
/// Design-time factory for <c>dotnet ef migrations add --context CloudContext</c>. The connection
/// string is never used to connect; EF only needs a provider to generate SQL.
/// </summary>
public sealed class CloudContextFactory : IDesignTimeDbContextFactory<CloudContext>
{
    public CloudContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CloudContext>()
            .UseNpgsql("Host=localhost;Database=modbot_cloud_design_time")
            .Options);
}
