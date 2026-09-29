using Modbot.Core.Logging;
using Modbot.Core.Logging.Store;
using Modbot.TestSupport;
using Serilog.Events;

namespace Modbot.Core.Tests.Logging;

/// <summary>
/// The setup code is printed to the console and nowhere else (first-run setup code design §2): not
/// the log files, which anyone with the disk can read, and not the database log, which is shown on
/// the Logs page and sent on to Modbot Cloud.
/// </summary>
public class ConsoleOnlyTests
{
    private const string Code = "ABCD-EFGH-JKMN";

    [Fact]
    public void AConsoleOnlyLineStaysOutOfTheDatabaseLog()
    {
        var sink = new DatabaseLogSink(new FakeClock());

        using (var logger = ModbotLogging.Create(
                   new ModbotLogOptions
                   {
                       ConsoleLevel = LogEventLevel.Fatal,
                       WriteFiles = false,
                       DatabaseSink = sink,
                   },
                   new FakeClock()))
        {
            logger.ForContext(LogArea.Name, LogArea.ConsoleOnly).Information("Setup code: {SetupCode:l}", Code);

            // No database is connected, so every line the sink admitted is still in its queue.
            Assert.Equal(0, sink.Status.Waiting);

            logger.Information("An ordinary line");
            Assert.Equal(1, sink.Status.Waiting);
        }
    }

    [Fact]
    public void AConsoleOnlyLineStaysOutOfTheLogFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"modbot-console-only-{Guid.NewGuid():N}");

        try
        {
            using (var logger = ModbotLogging.Create(
                       new ModbotLogOptions
                       {
                           Directory = directory,
                           Debug = true,
                           Level = LogEventLevel.Debug,
                           ConsoleLevel = LogEventLevel.Fatal,
                           WriteFiles = true,
                       },
                       new FakeClock()))
            {
                logger.ForContext(LogArea.Name, LogArea.ConsoleOnly).Information("Setup code: {SetupCode:l}", Code);
                logger.Information("An ordinary line");
            }

            var files = Directory.GetFiles(directory);
            Assert.NotEmpty(files);

            var everything = string.Concat(files.Select(File.ReadAllText));
            Assert.Contains("An ordinary line", everything, StringComparison.Ordinal);
            Assert.DoesNotContain(Code, everything, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
