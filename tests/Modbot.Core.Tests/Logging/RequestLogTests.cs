using Modbot.Core.Logging;
using Serilog.Events;

namespace Modbot.Core.Tests.Logging;

/// <summary>
/// What level one request is written at: Debug for a request that worked, Warning for one that did
/// not. A container's health probe asks every thirty seconds forever, and an ordinary request is
/// the same problem at a larger scale, so neither is Information.
/// </summary>
public class RequestLogTests
{
    [Fact]
    public void AnOrdinaryRequestIsDebugSoTheRecordIsWhatModbotDid() =>
        Assert.Equal(LogEventLevel.Debug, ModbotRequestLog.LevelFor("/api/bans", 200, failed: false));

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    [InlineData("/HEALTH/READY")]
    public void AHealthCheckIsDebugSoItDoesNotFillTheConsole(string path) =>
        Assert.Equal(LogEventLevel.Debug, ModbotRequestLog.LevelFor(path, 200, failed: false));

    [Fact]
    public void AServerErrorIsAWarningEvenOnTheHealthPath() =>
        Assert.Equal(LogEventLevel.Warning, ModbotRequestLog.LevelFor("/health", 503, failed: false));

    [Fact]
    public void AThrownRequestIsAWarning() =>
        Assert.Equal(LogEventLevel.Warning, ModbotRequestLog.LevelFor("/api/bans", 200, failed: true));

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public void EveryServerErrorIsAWarning(int statusCode) =>
        Assert.Equal(LogEventLevel.Warning, ModbotRequestLog.LevelFor("/api/bans", statusCode, failed: false));

    [Fact]
    public void ARefusedRequestIsStillOrdinary() =>
        Assert.Equal(LogEventLevel.Debug, ModbotRequestLog.LevelFor("/api/bans", 403, failed: false));

    [Fact]
    public void NothingAboutARequestIsWrittenAtInformation()
    {
        LogEventLevel[] levels =
        [
            ModbotRequestLog.LevelFor("/api/bans", 200, failed: false),
            ModbotRequestLog.LevelFor("/health", 200, failed: false),
            ModbotRequestLog.LevelFor("/api/bans", 404, failed: false),
            ModbotRequestLog.LevelFor("/api/bans", 500, failed: false),
            ModbotRequestLog.LevelFor("/api/bans", 200, failed: true),
        ];

        Assert.DoesNotContain(LogEventLevel.Information, levels);
    }

    [Fact]
    public void TheTemplateNamesTheFourThingsWorthFilteringOn()
    {
        Assert.Contains("{RequestMethod}", ModbotRequestLog.MessageTemplate);
        Assert.Contains("{RequestPath}", ModbotRequestLog.MessageTemplate);
        Assert.Contains("{StatusCode}", ModbotRequestLog.MessageTemplate);
        Assert.Contains("{Elapsed", ModbotRequestLog.MessageTemplate);
    }
}
