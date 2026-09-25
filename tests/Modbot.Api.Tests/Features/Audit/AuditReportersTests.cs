using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Audit;

/// <summary>
/// Whose client reported an entry, on the log that shows it.
/// </summary>
/// <remarks>
/// The device id has always been in the payload and has always meant nothing to a moderator. What
/// reaches the screen is the account the client was issued to, and where several clients saw the
/// same thing, all of them -- two clients agreeing is better evidence than one.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class AuditReportersTests
{
    private readonly PostgresFixture _db;

    public AuditReportersTests(PostgresFixture db) => _db = db;

    /// <summary>A moderator with a paired client, the way the desktop app is paired.</summary>
    private static async Task<Guid> ClientOfAsync(ReadSurfaceTestHost host, string username, CancellationToken ct)
    {
        var user = await host.CreateUserAsync(username, "hunter2", ModbotPermissions.None, ct);

        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        var device = Guid.NewGuid();

        context.CompanionDevices.Add(new CompanionDeviceRecord
        {
            Id = device,
            TokenHash = Guid.NewGuid().ToString("n"),
            CompanionVersion = "2026.9.0",
            Platform = "windows",
            IssuedToUserId = user.Id,
            IssuedAt = host.Clock.UtcNow,
        });

        await context.SaveChangesAsync(ct);
        return device;
    }

    private static FactRecord Seen(string subject, DateTimeOffset at, Guid? device)
        => new()
        {
            Type = FactType.InstanceJoined,
            OccurredAt = at,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = subject,
            WorldId = "wrld_a",
            InstanceId = "39047",
            Source = device is null ? FactSource.AuditLog : FactSource.Companion,
            Data = device is { } reporter
                ? new JsonObject { [ClientReport.DeviceIdKey] = reporter.ToString() }
                : null,
        };

    private static async Task<AuditEntry> OnlyEntryAsync(ReadSurfaceTestHost host, CancellationToken ct)
    {
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var page = await host.GetJsonAsync<AuditPage>("/api/audit?limit=10", cookie, ct);
        return Assert.Single(page.Entries);
    }

    [Fact]
    public async Task AFactOneClientReported_NamesThatClientsModerator()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var ada = await ClientOfAsync(host, "ada", ct);
        await host.WriteFactAsync(Seen("usr_cid", host.Clock.UtcNow.AddMinutes(-5), ada), ct);

        var reporter = Assert.Single((await OnlyEntryAsync(host, ct)).ReportedBy!);
        Assert.Equal("ada", reporter.Name);
    }

    /// <summary>
    /// Three clients saw one arrival. Deduplication keeps one fact, and all three are named on it:
    /// the one whose report became the fact first, then the ones folded into it.
    /// </summary>
    [Fact]
    public async Task AFactSeveralClientsReported_NamesAllOfThem_OldestFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var ada = await ClientOfAsync(host, "ada", ct);
        var ben = await ClientOfAsync(host, "ben", ct);
        var cid = await ClientOfAsync(host, "cid", ct);

        var at = host.Clock.UtcNow.AddMinutes(-5);

        // Jittered the way real clients are after clock synchronisation: inside the window, never
        // identical, so all three are reports of the one arrival.
        await host.WriteFactAsync(Seen("usr_dee", at, ada), ct);
        await host.WriteFactAsync(Seen("usr_dee", at.AddSeconds(0.8), ben), ct);
        await host.WriteFactAsync(Seen("usr_dee", at.AddSeconds(-1.3), cid), ct);

        var entry = await OnlyEntryAsync(host, ct);

        Assert.Equal(["ada", "ben", "cid"], entry.ReportedBy!.Select(r => r.Name));
    }

    [Fact]
    public async Task AFactNoClientReported_NamesNobody()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        await host.WriteFactAsync(Seen("usr_cid", host.Clock.UtcNow.AddMinutes(-5), device: null), ct);

        var entry = await OnlyEntryAsync(host, ct);
        Assert.True(entry.ReportedBy is null or { Count: 0 });
    }

    /// <summary>
    /// A client whose device row Modbot cannot put an account to is left out rather than shown as
    /// a machine id, which names nobody and can be acted on by nobody.
    /// </summary>
    [Fact]
    public async Task AFactFromAnUnknownClient_NamesNobody()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        await host.WriteFactAsync(Seen("usr_cid", host.Clock.UtcNow.AddMinutes(-5), Guid.NewGuid()), ct);

        var entry = await OnlyEntryAsync(host, ct);
        Assert.True(entry.ReportedBy is null or { Count: 0 });
    }
}
