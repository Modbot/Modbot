using System.Text.Json;
using Modbot.Core.Logging;
using Serilog.Events;
using Serilog.Parsing;

namespace Modbot.Core.Tests.Logging;

/// <summary>
/// What a line actually looks like in each of the three console modes.
/// </summary>
/// <remarks>
/// The shape is the contract. Railway reads <c>message</c> and <c>level</c> and nothing else
/// (https://docs.railway.com/observability/logs#structured-logs), and a log explorer reading the
/// compact format expects Serilog's <c>@</c>-prefixed keys. Both are easy to break by accident and
/// impossible to notice without looking at a real line, which is what these tests do.
/// </remarks>
public class ConsoleFormatTests
{
    private static LogEvent Event(
        LogEventLevel level = LogEventLevel.Information,
        string template = "Modbot starting on port {Port}",
        Exception? error = null,
        params (string Name, object Value)[] properties)
    {
        var parsed = new MessageTemplateParser().Parse(template);
        LogEventProperty[] carried = properties.Length == 0
            ? [new LogEventProperty("Port", new ScalarValue(8080))]
            : [.. properties.Select(p => new LogEventProperty(p.Name, ToValue(p.Value)))];

        return new LogEvent(
            new DateTimeOffset(2026, 9, 15, 12, 30, 0, TimeSpan.Zero),
            level,
            error,
            parsed,
            carried);

        static LogEventPropertyValue ToValue(object value) => value switch
        {
            string[] many => new SequenceValue(many.Select(m => new ScalarValue(m))),
            _ => new ScalarValue(value),
        };
    }

    private static string Write(ConsoleLogMode mode, LogEvent logEvent)
    {
        var formatter = ModbotConsoleLog.Formatter(mode);
        Assert.NotNull(formatter);

        var writer = new StringWriter();
        formatter.Format(logEvent, writer);
        return writer.ToString();
    }

    private static JsonElement OneLineOfJson(ConsoleLogMode mode, LogEvent logEvent)
    {
        var written = Write(mode, logEvent);

        // Railway does not parse a log object that spans lines at all, and neither does anything
        // else reading a stream one line at a time.
        var lines = written.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);

        return JsonDocument.Parse(lines[0]).RootElement.Clone();
    }

    // ── railway_json ────────────────────────────────────────────────────────

    [Fact]
    public void RailwayLinesCarryTheMessageAndLevelRailwayReads()
    {
        var line = OneLineOfJson(ConsoleLogMode.RailwayJson, Event());

        Assert.Equal("Modbot starting on port 8080", line.GetProperty("message").GetString());
        Assert.Equal("info", line.GetProperty("level").GetString());
    }

    [Theory]
    [InlineData(LogEventLevel.Verbose, "debug")]
    [InlineData(LogEventLevel.Debug, "debug")]
    [InlineData(LogEventLevel.Information, "info")]
    [InlineData(LogEventLevel.Warning, "warn")]
    [InlineData(LogEventLevel.Error, "error")]
    [InlineData(LogEventLevel.Fatal, "error")]
    public void SerilogsSixLevelsBecomeTheFourRailwayKnows(LogEventLevel level, string expected)
    {
        var line = OneLineOfJson(ConsoleLogMode.RailwayJson, Event(level));

        Assert.Equal(expected, line.GetProperty("level").GetString());

        // Fatal and Verbose have nowhere to go in Railway's four, so the real level is kept too.
        Assert.Equal(level.ToString(), line.GetProperty("logLevel").GetString());
    }

    [Fact]
    public void RailwayLinesKeepEveryPropertyAsATopLevelKey()
    {
        var line = OneLineOfJson(
            ConsoleLogMode.RailwayJson,
            Event(properties: [("Port", 8080), ("Service", "Modbot.Cloud"), ("Roles", new[] { "editor", "viewer" })]));

        Assert.Equal(8080, line.GetProperty("Port").GetInt32());
        Assert.Equal("Modbot.Cloud", line.GetProperty("Service").GetString());

        // An array stays an array, because Railway can filter on @Roles[0] but only if it is one.
        Assert.Equal(JsonValueKind.Array, line.GetProperty("Roles").ValueKind);
        Assert.Equal("editor", line.GetProperty("Roles")[0].GetString());
    }

    [Fact]
    public void RailwayLinesCarryTheExceptionAndTheTime()
    {
        var line = OneLineOfJson(
            ConsoleLogMode.RailwayJson,
            Event(LogEventLevel.Error, error: new InvalidOperationException("no database")));

        Assert.Contains("no database", line.GetProperty("exception").GetString());
        Assert.StartsWith("2026-09-15T12:30:00", line.GetProperty("timestamp").GetString());
    }

    [Fact]
    public void APropertyCalledMessageCannotOverwriteTheMessage()
    {
        var line = OneLineOfJson(
            ConsoleLogMode.RailwayJson,
            Event(template: "Startup finished", properties: [("message", "something else"), ("level", "loud")]));

        Assert.Equal("Startup finished", line.GetProperty("message").GetString());
        Assert.Equal("info", line.GetProperty("level").GetString());
    }

    // ── json ────────────────────────────────────────────────────────────────

    [Fact]
    public void JsonLinesAreSerilogsCompactFormat()
    {
        var line = OneLineOfJson(ConsoleLogMode.Json, Event(LogEventLevel.Warning));

        // @t time, @mt template, @l level, @x exception -- the compact format's own keys.
        Assert.StartsWith("2026-09-15T12:30:00", line.GetProperty("@t").GetString());
        Assert.Equal("Modbot starting on port {Port}", line.GetProperty("@mt").GetString());
        Assert.Equal("Warning", line.GetProperty("@l").GetString());
        Assert.Equal(8080, line.GetProperty("Port").GetInt32());
    }

    [Fact]
    public void JsonLinesCarryTheException()
    {
        var line = OneLineOfJson(
            ConsoleLogMode.Json,
            Event(LogEventLevel.Error, error: new InvalidOperationException("no database")));

        Assert.Contains("no database", line.GetProperty("@x").GetString());
    }

    // ── serilog ─────────────────────────────────────────────────────────────

    [Fact]
    public void ReadableTextHasNoFormatterBecauseItIsATemplate()
    {
        Assert.Null(ModbotConsoleLog.Formatter(ConsoleLogMode.Serilog));
        Assert.Contains("{Message:lj}", ModbotConsoleLog.Template);
    }
}
