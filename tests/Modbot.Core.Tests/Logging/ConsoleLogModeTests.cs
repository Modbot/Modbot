using Modbot.Core.Logging;
using Serilog.Events;

namespace Modbot.Core.Tests.Logging;

/// <summary>
/// CONSOLE_LOG_MODE and LOG_LEVEL: the two variables every Modbot program reads, and what they
/// mean when they are missing, misspelled or typed with a capital letter in the wrong place.
/// </summary>
/// <remarks>
/// Nothing here is allowed to throw. A log setting that stops a deployment coming up would be a
/// worse failure than the one it was set to diagnose.
/// </remarks>
public class ConsoleLogModeTests
{
    private static Func<string, string?> Map(params (string Key, string? Value)[] set)
    {
        var map = set.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        return key => map.GetValueOrDefault(key);
    }

    [Fact]
    public void ReadableTextIsTheDefaultWhenNothingIsSet() =>
        Assert.Equal(ConsoleLogMode.Serilog, ModbotConsoleLog.ReadMode(Map()));

    [Theory]
    [InlineData("serilog", ConsoleLogMode.Serilog)]
    [InlineData("json", ConsoleLogMode.Json)]
    [InlineData("railway_json", ConsoleLogMode.RailwayJson)]
    public void EachOfTheThreeValuesIsRead(string value, ConsoleLogMode expected) =>
        Assert.Equal(expected, ModbotConsoleLog.ReadMode(Map((ModbotConsoleLog.ModeVariable, value))));

    [Theory]
    [InlineData("SERILOG", ConsoleLogMode.Serilog)]
    [InlineData("Json", ConsoleLogMode.Json)]
    [InlineData("RAILWAY_JSON", ConsoleLogMode.RailwayJson)]
    [InlineData("RailwayJson", ConsoleLogMode.RailwayJson)]
    public void CapitalLettersDoNotMatter(string value, ConsoleLogMode expected) =>
        Assert.Equal(expected, ModbotConsoleLog.ReadMode(Map((ModbotConsoleLog.ModeVariable, value))));

    [Theory]
    [InlineData("  json  ")]
    [InlineData("\tjson\n")]
    public void SpacesAroundTheValueAreIgnored(string value) =>
        Assert.Equal(ConsoleLogMode.Json, ModbotConsoleLog.ReadMode(Map((ModbotConsoleLog.ModeVariable, value))));

    [Theory]
    [InlineData("railway json")]
    [InlineData("railway-json")]
    [InlineData("railwayjson")]
    [InlineData(" Railway Json ")]
    public void RailwayIsRecognisedHoweverTheSpaceIsWritten(string value) =>
        Assert.Equal(ConsoleLogMode.RailwayJson, ModbotConsoleLog.ReadMode(Map((ModbotConsoleLog.ModeVariable, value))));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xml")]
    [InlineData("logfmt")]
    [InlineData("railway")]
    public void AnythingElseIsTheDefault(string value) =>
        Assert.Equal(ConsoleLogMode.Serilog, ModbotConsoleLog.ReadMode(Map((ModbotConsoleLog.ModeVariable, value))));

    [Fact]
    public void LogLevelFallsBackWhenItIsNotSet() =>
        Assert.Equal(
            LogEventLevel.Information,
            ModbotConsoleLog.ReadLevel(LogEventLevel.Information, Map()));

    [Theory]
    [InlineData("Verbose", LogEventLevel.Verbose)]
    [InlineData("debug", LogEventLevel.Debug)]
    [InlineData("INFORMATION", LogEventLevel.Information)]
    [InlineData(" Warning ", LogEventLevel.Warning)]
    [InlineData("error", LogEventLevel.Error)]
    [InlineData("fatal", LogEventLevel.Fatal)]
    public void EveryLevelIsRead(string value, LogEventLevel expected) =>
        Assert.Equal(
            expected,
            ModbotConsoleLog.ReadLevel(LogEventLevel.Information, Map((ModbotConsoleLog.LevelVariable, value))));

    [Theory]
    [InlineData("")]
    [InlineData("loud")]
    [InlineData("11")]
    public void AnUnknownLevelFallsBackRatherThanFailing(string value) =>
        Assert.Equal(
            LogEventLevel.Warning,
            ModbotConsoleLog.ReadLevel(LogEventLevel.Warning, Map((ModbotConsoleLog.LevelVariable, value))));
}
