using Modbot.Core.Logging;
using Serilog.Events;

namespace Modbot.Core.Tests.Logging;

/// <summary>
/// What level one request is written at. The health probe is the reason this is not simply
/// Information: a container asks every thirty seconds, forever.
/// </summary>
public class RequestLogTests
{
    [Fact]
    public void AnOrdinaryRequestIsInformation() =>
        Assert.Equal(LogEventLevel.Information, ModbotRequestLog.LevelFor("/api/bans", 200, failed: false));

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

    [Fact]
    public void ARefusedRequestIsStillOrdinary() =>
        Assert.Equal(LogEventLevel.Information, ModbotRequestLog.LevelFor("/api/bans", 403, failed: false));

    [Fact]
    public void TheTemplateNamesTheFourThingsWorthFilteringOn()
    {
        Assert.Contains("{RequestMethod}", ModbotRequestLog.MessageTemplate);
        Assert.Contains("{RequestPath}", ModbotRequestLog.MessageTemplate);
        Assert.Contains("{StatusCode}", ModbotRequestLog.MessageTemplate);
        Assert.Contains("{Elapsed", ModbotRequestLog.MessageTemplate);
    }
}
