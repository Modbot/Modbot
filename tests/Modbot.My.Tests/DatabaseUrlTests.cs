using Modbot.My.Configuration;
using Modbot.My.Data;
using Npgsql;

namespace Modbot.My.Tests;

public class DatabaseUrlTests
{
    [Fact]
    public void APostgresUrlBecomesAConnectionString()
    {
        var builder = new NpgsqlConnectionStringBuilder(
            DatabaseUrl.ToConnectionString("postgres://modbot:s3cret@db.internal:6543/modbot_my?sslmode=require"));

        Assert.Equal("db.internal", builder.Host);
        Assert.Equal(6543, builder.Port);
        Assert.Equal("modbot_my", builder.Database);
        Assert.Equal("modbot", builder.Username);
        Assert.Equal("s3cret", builder.Password);
        Assert.Equal(SslMode.Require, builder.SslMode);
    }

    [Fact]
    public void APasswordWithReservedCharactersSurvives()
    {
        var builder = new NpgsqlConnectionStringBuilder(
            DatabaseUrl.ToConnectionString("postgresql://modbot:p%40ss%3Aword@localhost/modbot_my"));

        Assert.Equal("p@ss:word", builder.Password);
        Assert.Equal(5432, builder.Port);
    }

    [Fact]
    public void AKeywordStringIsPassedThrough()
    {
        const string keywords = "Host=localhost;Database=modbot_my;Username=me";

        Assert.Equal(keywords, DatabaseUrl.ToConnectionString(keywords));
    }

    [Fact]
    public void AUrlWithNoDatabaseIsRefused()
    {
        var e = Assert.Throws<FormatException>(() => DatabaseUrl.ToConnectionString("postgres://me@localhost:5432/"));

        Assert.Contains(MyEnvironment.DatabaseUrlVariable, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOptionNpgsqlDoesNotKnowIsRefused()
    {
        Assert.Throws<FormatException>(() => DatabaseUrl.ToConnectionString("postgres://me@localhost/db?nonsense=1"));
    }

    [Fact]
    public void TheEnvironmentTreatsBlankValuesAsUnset()
    {
        var environment = MyEnvironment.Read(name => name switch
        {
            MyEnvironment.DatabaseUrlVariable => "  ",
            MyEnvironment.RootApiKeyVariable => "",
            MyEnvironment.PortVariable => "not-a-port",
            _ => null,
        });

        Assert.Null(environment.DatabaseUrl);
        Assert.Null(environment.RootApiKey);
        Assert.Equal(MyEnvironment.DefaultPort, environment.Port);
    }
}
