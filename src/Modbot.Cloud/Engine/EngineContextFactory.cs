using Microsoft.EntityFrameworkCore.Design;

namespace Modbot.Cloud.Engine;

/// <summary>
/// Design-time factory for <c>dotnet ef migrations add --context EngineContext</c>. The connection
/// string is never used to connect.
/// </summary>
public sealed class EngineContextFactory : IDesignTimeDbContextFactory<EngineContext>
{
    public EngineContext CreateDbContext(string[] args) =>
        new(EngineContext.Options("Host=localhost;Database=modbot_cloud_engine_design_time").Options);
}
