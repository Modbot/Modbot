using Microsoft.EntityFrameworkCore;
using Modbot.Cloud.Configuration;
using Modbot.Cloud.Data;
using Modbot.Cloud.Engine;

namespace Modbot.Cloud.Tests;

public class CloudEnvironmentTests
{
    private static CloudEnvironment Read(params (string Name, string Value)[] variables)
    {
        var map = variables.ToDictionary(v => v.Name, v => v.Value);
        return CloudEnvironment.Read(name => map.GetValueOrDefault(name));
    }

    [Fact]
    public void BothDatabasesSetIsReady()
    {
        var environment = Read(("DATABASE_URL", "postgres://a:b@main:5432/cloud"), ("DATABASE_ENGINE_URL", "postgres://a:b@engine:5432/engine"));

        Assert.Empty(environment.Problems());
        Assert.Equal(8080, environment.Port);
    }

    [Fact]
    public void AMissingEngineDatabaseIsNamed()
    {
        var problems = Read(("DATABASE_URL", "postgres://a:b@main:5432/cloud")).Problems();

        var problem = Assert.Single(problems);
        Assert.StartsWith("DATABASE_ENGINE_URL is not set", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingMainDatabaseIsNamed()
    {
        var problems = Read(("DATABASE_ENGINE_URL", "postgres://a:b@engine:5432/engine")).Problems();

        var problem = Assert.Single(problems);
        Assert.StartsWith("DATABASE_URL is not set", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void BothMissingAreBothNamed()
    {
        var problems = Read(("DATABASE_URL", "  ")).Problems();

        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public void ADatabaseUrlProblemNamesTheVariableItCameFrom()
    {
        var error = Assert.Throws<FormatException>(() =>
            DatabaseUrl.ToConnectionString("postgres://user:pass@host:5432/", CloudEnvironment.EngineDatabaseUrlVariable));

        Assert.StartsWith("DATABASE_ENGINE_URL", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BothModelsMatchTheirMigrations()
    {
        // The same check as `dotnet ef migrations has-pending-model-changes`, for each context, so a
        // model change without a migration fails the build rather than the next deploy.
        using var cloud = new CloudContextFactory().CreateDbContext([]);
        using var engine = new EngineContextFactory().CreateDbContext([]);

        Assert.False(cloud.Database.HasPendingModelChanges());
        Assert.False(engine.Database.HasPendingModelChanges());
    }

    [Fact]
    public void TheEngineKeepsItsOwnMigrationHistory()
    {
        using var engine = new EngineContextFactory().CreateDbContext([]);
        using var cloud = new CloudContextFactory().CreateDbContext([]);

        Assert.Empty(engine.Database.GetMigrations().Intersect(cloud.Database.GetMigrations()));
        Assert.NotEmpty(engine.Database.GetMigrations());
        Assert.Equal("__engine_migrations_history", EngineContext.MigrationsHistoryTable);
    }
}
