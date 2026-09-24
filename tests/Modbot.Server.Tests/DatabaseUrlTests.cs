using Modbot.Server.Data;

namespace Modbot.Server.Tests;

/// <summary>
/// The one setting every deployment sets, and the ceiling Modbot puts on it.
/// </summary>
/// <remarks>
/// <para>
/// This suite exists because the ceiling had none. Npgsql's own default is 100 connections, which
/// is also the whole allowance a stock PostgreSQL hands out: measured against a real database,
/// Modbot opening 100 took Postgres from 37 MiB to 187 MiB and then refused the operator's own
/// psql with "sorry, too many clients already". A wrong number here does not fail the build or a
/// request — it quietly takes the database down with it.
/// </para>
/// </remarks>
public sealed class DatabaseUrlTests
{
    private const string Plain = "Host=localhost;Database=modbot;Username=modbot;Password=pw";

    [Fact]
    public void AMachineWithNoNumberOfItsOwnGetsOneWorkedOutForIt()
    {
        var limited = DatabaseUrl.WithConnectionLimit(Plain);

        Assert.Contains("Maximum Pool Size=", limited, StringComparison.Ordinal);
        Assert.Contains(
            DatabaseUrl.ConnectionsFor(Environment.ProcessorCount).ToString(),
            limited,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A small machine asks for the fewest, a large one for as many as Npgsql would have taken by
    /// itself, and nothing in between leaves the two.
    /// </summary>
    [Theory]
    [InlineData(1, 20)]
    [InlineData(4, 20)]
    [InlineData(5, 20)]
    [InlineData(6, 24)]
    [InlineData(16, 64)]
    [InlineData(25, 100)]
    [InlineData(64, 100)]
    public void TheCeilingFollowsTheMachineBetweenTheTwoEnds(int processors, int expected)
        => Assert.Equal(expected, DatabaseUrl.ConnectionsFor(processors));

    [Fact]
    public void ARaspberryPiAsksForTheFewest()
        => Assert.Equal(DatabaseUrl.FewestConnections, DatabaseUrl.ConnectionsFor(4));

    [Fact]
    public void ALargeMachineAsksForWhatNpgsqlWouldHaveTakenAnyway()
        => Assert.Equal(DatabaseUrl.MostConnections, DatabaseUrl.ConnectionsFor(32));

    /// <summary>
    /// The operator's own number wins, in every spelling Npgsql accepts for it. This is the case
    /// the obvious implementation gets wrong: Npgsql's builder answers ContainsKey for every
    /// keyword it knows about, whether or not the string supplied one, so asking it that way
    /// would never find an operator's value and would never overwrite one either.
    /// </summary>
    [Theory]
    [InlineData("Maximum Pool Size=7")]
    [InlineData("MaxPoolSize=7")]
    [InlineData("maximum pool size=7")]
    [InlineData("MAXIMUM POOL SIZE=7")]
    public void ANumberTheOperatorChoseIsLeftAlone(string written)
    {
        var limited = DatabaseUrl.WithConnectionLimit($"{Plain};{written}");

        Assert.Equal(7, new Npgsql.NpgsqlConnectionStringBuilder(limited).MaxPoolSize);
    }

    /// <summary>
    /// Even a number larger than Modbot would have picked. The ceiling is a default for somebody
    /// who said nothing, not a rule imposed on somebody who did.
    /// </summary>
    [Fact]
    public void AnOperatorMayAskForMoreThanModbotWouldHaveChosen()
    {
        var limited = DatabaseUrl.WithConnectionLimit($"{Plain};Maximum Pool Size=250");

        Assert.Equal(250, new Npgsql.NpgsqlConnectionStringBuilder(limited).MaxPoolSize);
    }

    /// <summary>
    /// Something Npgsql cannot read comes back exactly as it went in, so the connection attempt
    /// reports the problem in Npgsql's own words rather than this throwing first and saying
    /// nothing about the variable the operator pasted.
    /// </summary>
    [Fact]
    public void SomethingNpgsqlCannotReadIsHandedBackUntouched()
    {
        const string nonsense = "this is not a connection string at all";

        Assert.Equal(nonsense, DatabaseUrl.WithConnectionLimit(nonsense));
    }

    [Fact]
    public void NothingAtAllIsRefused()
    {
        Assert.Throws<ArgumentException>(() => DatabaseUrl.WithConnectionLimit(string.Empty));
        Assert.Throws<ArgumentException>(() => DatabaseUrl.WithConnectionLimit("   "));
    }

    /// <summary>A URL is still a URL after the ceiling is put on it.</summary>
    [Fact]
    public void APlatformUrlKeepsWhereItPointsAfterTheCeilingGoesOn()
    {
        var limited = DatabaseUrl.WithConnectionLimit(
            DatabaseUrl.ToConnectionString("postgres://someone:secret@db.example.com:5432/modbot"));

        var read = new Npgsql.NpgsqlConnectionStringBuilder(limited);

        Assert.Equal("db.example.com", read.Host);
        Assert.Equal(5432, read.Port);
        Assert.Equal("modbot", read.Database);
        Assert.Equal("someone", read.Username);
        Assert.Equal("secret", read.Password);
        Assert.Equal(DatabaseUrl.ConnectionsFor(Environment.ProcessorCount), read.MaxPoolSize);
    }
}
