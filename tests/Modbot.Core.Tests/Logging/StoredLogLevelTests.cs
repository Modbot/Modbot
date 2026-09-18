using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Modbot.Core.Data;
using Modbot.Core.Logging;
using Modbot.Core.Logging.Store;
using Modbot.TestSupport;
using Serilog.Events;

namespace Modbot.Core.Tests.Logging;

/// <summary>
/// The two halves of "available at Debug rather than hidden" (2026-09-18): the line Entity
/// Framework writes for every statement is filed under Debug rather than thrown away, and the Logs
/// page follows the level that was asked for rather than stopping at Information.
/// </summary>
public class StoredLogLevelTests
{
    private static WarningsConfiguration Warnings(DbContextOptions options) =>
        options.FindExtension<CoreOptionsExtension>()!.WarningsConfiguration;

    [Theory]
    [InlineData("CommandExecuted")]
    [InlineData("CommandError")]
    [InlineData("ConnectionError")]
    [InlineData("ContextInitialized")]
    public void TheNoisyEventsAreWrittenAtDebug(string which)
    {
        var eventId = which switch
        {
            "CommandExecuted" => RelationalEventId.CommandExecuted,
            "CommandError" => RelationalEventId.CommandError,
            "ConnectionError" => RelationalEventId.ConnectionError,
            _ => CoreEventId.ContextInitialized,
        };

        var options = new DbContextOptionsBuilder().LogQueriesAtDebug().Options;

        Assert.Equal(LogLevel.Debug, Warnings(options).GetLevel(eventId));
    }

    /// <summary>
    /// Nothing else is touched. The migration Entity Framework is about to apply is the first thing
    /// an operator wants to read on a first boot, and it keeps the level Entity Framework chose.
    /// </summary>
    [Fact]
    public void EverythingElseKeepsTheLevelEntityFrameworkChose()
    {
        var options = new DbContextOptionsBuilder().LogQueriesAtDebug().Options;

        Assert.Null(Warnings(options).GetLevel(RelationalEventId.MigrateUsingConnection));
    }

    /// <summary>
    /// Configured on the context rather than at one call site, so the host, the test suites and
    /// <c>dotnet ef</c> all agree.
    /// </summary>
    [Fact]
    public void EveryWayOfBuildingTheContextGetsIt()
    {
        using var db = new ModbotContext(
            new DbContextOptionsBuilder<ModbotContext>().UseNpgsql("Host=unused").Options);

        var options = db.GetService<IDbContextOptions>();

        Assert.Equal(
            LogLevel.Debug,
            options.FindExtension<CoreOptionsExtension>()!.WarningsConfiguration
                .GetLevel(RelationalEventId.CommandExecuted));
    }

    // ── The Logs page's floor.

    private static LogStoreStatus AfterWriting(LogEventLevel configured, LogEventLevel wrote)
    {
        var sink = new DatabaseLogSink(new FakeClock());

        // No database is connected, so every line the floor admits is still in the sink's queue.
        using var logger = ModbotLogging.Create(
            new ModbotLogOptions
            {
                Level = configured,
                ConsoleLevel = LogEventLevel.Fatal,
                WriteFiles = false,
                DatabaseSink = sink,
            },
            new FakeClock());

        logger.Write(wrote, "one line");

        return sink.Status;
    }

    [Fact]
    public void TheTableStillHoldsInformationWhenNobodyAskedForMore() =>
        Assert.Equal(1, AfterWriting(LogEventLevel.Information, LogEventLevel.Information).Waiting);

    [Fact]
    public void ADebugLineIsNotStoredAtTheDefaultLevel() =>
        Assert.Equal(0, AfterWriting(LogEventLevel.Information, LogEventLevel.Debug).Waiting);

    [Fact]
    public void AskingForDebugPutsDebugOnTheLogsPage() =>
        Assert.Equal(1, AfterWriting(LogEventLevel.Debug, LogEventLevel.Debug).Waiting);

    [Fact]
    public void AndVerboseWhenThatIsWhatWasAskedFor() =>
        Assert.Equal(1, AfterWriting(LogEventLevel.Verbose, LogEventLevel.Verbose).Waiting);

    [Fact]
    public void AVerboseLineIsStillLeftOutAtDebug() =>
        Assert.Equal(0, AfterWriting(LogEventLevel.Debug, LogEventLevel.Verbose).Waiting);

    /// <summary>
    /// The two changes meeting: the request record is a Debug event, and Debug reaches the table,
    /// so the operator who turns the level down reads their requests on the Logs page.
    /// </summary>
    [Fact]
    public void ARequestReachesTheLogsPageOnceSomebodyAsksForDebug()
    {
        var level = ModbotRequestLog.LevelFor("/api/bans", 200, failed: false);

        Assert.Equal(0, AfterWriting(LogEventLevel.Information, level).Waiting);
        Assert.Equal(1, AfterWriting(LogEventLevel.Debug, level).Waiting);
    }
}
