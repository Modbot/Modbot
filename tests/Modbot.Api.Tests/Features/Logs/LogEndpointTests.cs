using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Logs;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Logs;

/// <summary>
/// Reading the stored log: who may, what the filters do, and how the pages join up.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class LogEndpointTests
{
    private readonly PostgresFixture _db;

    public LogEndpointTests(PostgresFixture db) => _db = db;

    private static readonly DateTimeOffset Day = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static LogEntry Line(
        string message,
        string level = "Information",
        string? source = "Modbot.VRChat.Sync.AuditLogProducer",
        string? area = "Sync",
        int minute = 0,
        string properties = "{}") => new()
    {
        At = Day.AddMinutes(minute),
        Level = level,
        Message = message,
        Template = message,
        Source = source,
        Area = area,
        Properties = properties,
    };

    private async Task WriteAsync(ReadSurfaceTestHost host, CancellationToken ct, params LogEntry[] lines)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        await db.Logs.ExecuteDeleteAsync(ct);
        db.Logs.AddRange(lines);
        await db.SaveChangesAsync(ct);
    }

    [Fact]
    public async Task ReadingTheLogNeedsTheOperationalLogPermission()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        using var response = await host.GetAsync("/api/logs", cookie, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task LinesComeBackNewestFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        await WriteAsync(host, ct,
            Line("first", minute: 0),
            Line("second", minute: 1),
            Line("third", minute: 2));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, ct);
        var page = await host.GetJsonAsync<LogPage>("/api/logs", cookie, ct);

        Assert.Equal(["third", "second", "first"], page.Lines.Select(l => l.Message));
        Assert.Null(page.Next);
    }

    [Fact]
    public async Task TheLevelFilterMeansThisLevelAndEveryLevelAboveIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        await WriteAsync(host, ct,
            Line("chatter", level: "Information", minute: 0),
            Line("odd", level: "Warning", minute: 1),
            Line("broken", level: "Error", minute: 2));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, ct);
        var page = await host.GetJsonAsync<LogPage>("/api/logs?level=Warning", cookie, ct);

        Assert.Equal(["broken", "odd"], page.Lines.Select(l => l.Message));
    }

    [Fact]
    public async Task TheTextFilterMatchesTheMessageAndTheException()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var withException = Line("something went wrong", level: "Error", minute: 1);
        withException.Exception = "System.TimeoutException: the group did not answer";

        await WriteAsync(host, ct, Line("a quiet pass", minute: 0), withException);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, ct);

        var byMessage = await host.GetJsonAsync<LogPage>("/api/logs?text=quiet", cookie, ct);
        Assert.Equal(["a quiet pass"], byMessage.Lines.Select(l => l.Message));

        var byException = await host.GetJsonAsync<LogPage>("/api/logs?text=TimeoutException", cookie, ct);
        Assert.Equal(["something went wrong"], byException.Lines.Select(l => l.Message));
    }

    [Fact]
    public async Task APercentSignInTheSearchIsNotAWildcard()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        await WriteAsync(host, ct, Line("budget at 50% of the ceiling", minute: 0), Line("nothing to say", minute: 1));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, ct);
        var page = await host.GetJsonAsync<LogPage>("/api/logs?text=50%25%20of", cookie, ct);

        Assert.Equal(["budget at 50% of the ceiling"], page.Lines.Select(l => l.Message));
    }

    [Fact]
    public async Task ThePagesJoinUpWithoutRepeatingALine()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        await WriteAsync(host, ct, [.. Enumerable.Range(0, 5).Select(i => Line($"line {i}", minute: i))]);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, ct);

        var first = await host.GetJsonAsync<LogPage>("/api/logs?limit=2", cookie, ct);
        Assert.Equal(["line 4", "line 3"], first.Lines.Select(l => l.Message));
        Assert.NotNull(first.Next);

        var second = await host.GetJsonAsync<LogPage>($"/api/logs?limit=2&before={first.Next}", cookie, ct);
        Assert.Equal(["line 2", "line 1"], second.Lines.Select(l => l.Message));
    }

    [Fact]
    public async Task TheFiltersListTheSourcesAndAreasThatHaveWrittenALine()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        await WriteAsync(host, ct,
            Line("one", source: "Modbot.VRChat.Sync.AuditLogProducer", area: "Sync", minute: 0),
            Line("two", source: "Modbot.Discord.Bot", area: "Discord", minute: 1));

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, ct);
        var filters = await host.GetJsonAsync<LogFilters>("/api/logs/filters", cookie, ct);

        Assert.Equal(["Modbot.Discord.Bot", "Modbot.VRChat.Sync.AuditLogProducer"], filters.Sources);
        Assert.Equal(["Discord", "Sync"], filters.Areas);
        Assert.Equal(2, filters.Stored);
        Assert.Equal(Day, filters.Oldest);
    }

    [Fact]
    public async Task ChangingHowLongLogsAreKeptNeedsTheSettingsPermission()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var reader = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, ct);

        using var refused = await host.PutJsonAsync(
            "/api/logs/settings", new { keepDays = 30, sendToCloud = false }, reader, ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task AnImpossibleWindowIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, ct);

        using var response = await host.PutJsonAsync(
            "/api/logs/settings", new { keepDays = -1, sendToCloud = true }, cookie, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
