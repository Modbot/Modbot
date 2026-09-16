using System.Text.Json;
using Modbot.Core.Logging;
using Modbot.Core.Logging.Store;
using Serilog.Events;
using Serilog.Parsing;

namespace Modbot.Core.Tests.Logging;

/// <summary>
/// What a log line looks like once it is a row.
/// </summary>
/// <remarks>
/// The rows are read in the browser by anyone holding <c>ViewOperationalLog</c> and are sent to
/// Modbot Cloud, which is a wider audience than a file on the host's disk. So the tests that matter
/// here are the ones about what must never reach a row: a password, a bearer token, a connection
/// string. The rest pin the shape the Logs page reads.
/// </remarks>
public class LogRowTests
{
    private static LogEvent Event(
        string template = "Signed in as {Account}",
        Exception? error = null,
        LogEventLevel level = LogEventLevel.Information,
        params (string Name, object Value)[] properties)
    {
        var parsed = new MessageTemplateParser().Parse(template);

        return new LogEvent(
            new DateTimeOffset(2026, 9, 16, 12, 30, 0, TimeSpan.Zero),
            level,
            error,
            parsed,
            [.. properties.Select(p => new LogEventProperty(p.Name, new ScalarValue(p.Value)))]);
    }

    [Fact]
    public void TheRowCarriesTheRenderedLineTheTemplateAndTheLevel()
    {
        var row = LogRow.From(Event(properties: ("Account", "rin")));

        Assert.Equal("Signed in as \"rin\"", row.Message);
        Assert.Equal("Signed in as {Account}", row.Template);
        Assert.Equal("Information", row.Level);
        Assert.Equal(new DateTimeOffset(2026, 9, 16, 12, 30, 0, TimeSpan.Zero), row.At);
    }

    [Fact]
    public void TheSourceAndTheAreaBecomeTheirOwnColumnsAndLeaveTheProperties()
    {
        var row = LogRow.From(Event(properties:
        [
            ("SourceContext", "Modbot.VRChat.Sync.AuditLogProducer"),
            (LogArea.Name, LogArea.Sync),
            ("Count", 3),
        ]));

        Assert.Equal("Modbot.VRChat.Sync.AuditLogProducer", row.Source);
        Assert.Equal(LogArea.Sync, row.Area);

        using var properties = JsonDocument.Parse(row.Properties);
        Assert.False(properties.RootElement.TryGetProperty("SourceContext", out _));
        Assert.False(properties.RootElement.TryGetProperty(LogArea.Name, out _));
        Assert.Equal(3, properties.RootElement.GetProperty("Count").GetInt32());
    }

    [Theory]
    [InlineData("Password")]
    [InlineData("SmtpPassword")]
    [InlineData("apiKey")]
    [InlineData("api_key")]
    [InlineData("Authorization")]
    [InlineData("vrchat_auth_cookie")]
    [InlineData("RefreshToken")]
    public void APropertyWhoseNameLooksLikeASecretIsNeverStored(string name)
    {
        var row = LogRow.From(Event(properties: (name, "hunter2-the-real-one")));

        Assert.DoesNotContain("hunter2", row.Properties, StringComparison.Ordinal);

        using var properties = JsonDocument.Parse(row.Properties);
        Assert.Equal(LogSecrets.Replacement, properties.RootElement.GetProperty(name).GetString());
    }

    [Fact]
    public void ABearerTokenIsTakenOutWhateverItWasCalled()
    {
        var row = LogRow.From(Event(
            "Sending {Header}",
            properties: ("Header", "Bearer abcdefghijklmnop0123456789")));

        Assert.DoesNotContain("abcdefghijklmnop", row.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop", row.Properties, StringComparison.Ordinal);
    }

    [Fact]
    public void AConnectionStringPasswordIsTakenOutOfTheMessage()
    {
        var row = LogRow.From(Event(
            "Connecting with {Target}",
            properties: ("Target", "Host=db;Username=modbot;Password=s3cret-value;Database=modbot")));

        Assert.DoesNotContain("s3cret-value", row.Message, StringComparison.Ordinal);
        Assert.Contains("Host=db", row.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExceptionIsKeptWholeAndScrubbed()
    {
        var error = new InvalidOperationException("Refused with Bearer abcdefghijklmnop0123456789");
        var row = LogRow.From(Event(error: error));

        Assert.NotNull(row.Exception);
        Assert.Contains("InvalidOperationException", row.Exception, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop", row.Exception, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnusuallyLongLineIsCutRatherThanStoredWhole()
    {
        var row = LogRow.From(Event(
            "Answer: {Answer}",
            properties: ("Answer", new string('x', LogRow.MaxMessageLength * 2))));

        Assert.Equal(LogRow.MaxMessageLength, row.Message.Length);
    }

    [Fact]
    public void ALineWithNoPropertiesStoresAnEmptyObject()
    {
        var row = LogRow.From(Event("Modbot starting"));

        Assert.Equal("{}", row.Properties);
        Assert.Null(row.Source);
        Assert.Null(row.Area);
        Assert.Null(row.Exception);
    }

    [Fact]
    public void ANullByteIsRemovedBecauseJsonbRefusesOne()
    {
        var row = LogRow.From(Event(properties: ("Account", "rin\0bad")));

        Assert.DoesNotContain("\\u0000", row.Properties, StringComparison.Ordinal);

        // Still valid JSON, which is the whole point: jsonb would refuse the row otherwise.
        using var properties = JsonDocument.Parse(row.Properties);
        Assert.Equal("rinbad", properties.RootElement.GetProperty("Account").GetString());
    }
}
