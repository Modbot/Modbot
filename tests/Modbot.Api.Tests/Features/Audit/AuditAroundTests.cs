using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Audit;

/// <summary>
/// "Around this" and the links an entry carries to its case file or review.
/// </summary>
/// <remarks>
/// Finding 20 of the 2026-09-25 UX review: working out what a moderator did meant knowing ids, and
/// records did not link to each other.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class AuditAroundTests
{
    private readonly PostgresFixture _db;

    public AuditAroundTests(PostgresFixture db) => _db = db;

    private static FactRecord Ban(string moderator, string person, DateTimeOffset at)
        => new()
        {
            Type = FactType.MemberBanned,
            OccurredAt = at,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = person,
            ActorPlatform = FactPlatform.VRChat,
            ActorId = moderator,
            Source = FactSource.AuditLog,
        };

    private static FactRecord Joined(string person, DateTimeOffset at)
        => new()
        {
            Type = FactType.MemberJoined,
            OccurredAt = at,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = person,
            Source = FactSource.AuditLog,
        };

    private static async Task<IReadOnlyList<AuditEntry>> EntriesAsync(ReadSurfaceTestHost host, string cookie, CancellationToken ct)
        => (await host.GetJsonAsync<AuditPage>("/api/audit?limit=200", cookie, ct)).Entries;

    /// <summary>
    /// Twelve bans by one moderator, the sixth opened: five after it, the ban itself, five before,
    /// newest first. The bans at either end are the sixth ones out and are left off.
    /// </summary>
    [Fact]
    public async Task ByActor_IsFiveEitherSide_WithTheEntryInItsPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var start = host.Clock.UtcNow.AddHours(-2);
        for (var i = 0; i < 12; i++)
            await host.WriteFactAsync(Ban("usr_mod", $"usr_p{i:00}", start.AddMinutes(i)), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var opened = (await EntriesAsync(host, cookie, ct)).Single(e => e.SubjectId == "usr_p06");

        var around = await host.GetJsonAsync<AuditAround>($"/api/audit/entries/{opened.Id}/around", cookie, ct);

        Assert.Equal(
            ["usr_p11", "usr_p10", "usr_p09", "usr_p08", "usr_p07", "usr_p06", "usr_p05", "usr_p04", "usr_p03", "usr_p02", "usr_p01"],
            around.ByActor!.Select(e => e.SubjectId));
    }

    /// <summary>The same person's facts, whoever did them, and only theirs.</summary>
    [Fact]
    public async Task AboutSubject_IsThatPersonsFacts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var at = host.Clock.UtcNow.AddHours(-1);
        await host.WriteFactAsync(Joined("usr_eve", at), ct);
        await host.WriteFactAsync(Joined("usr_bob", at.AddMinutes(1)), ct);
        await host.WriteFactAsync(Ban("usr_mod", "usr_eve", at.AddMinutes(2)), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var ban = (await EntriesAsync(host, cookie, ct)).Single(e => e.Type == FactType.MemberBanned);

        var around = await host.GetJsonAsync<AuditAround>($"/api/audit/entries/{ban.Id}/around", cookie, ct);

        Assert.All(around.AboutSubject!, e => Assert.Equal("usr_eve", e.SubjectId));
        Assert.Equal([FactType.MemberBanned, FactType.MemberJoined], around.AboutSubject!.Select(e => e.Type));
    }

    /// <summary>A join nobody did has nobody to list actions for.</summary>
    [Fact]
    public async Task AnEntryNobodyDid_HasNoActorList()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        await host.WriteFactAsync(Joined("usr_eve", host.Clock.UtcNow.AddMinutes(-5)), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var join = Assert.Single(await EntriesAsync(host, cookie, ct));

        var around = await host.GetJsonAsync<AuditAround>($"/api/audit/entries/{join.Id}/around", cookie, ct);

        Assert.Null(around.ByActor);
        Assert.NotNull(around.AboutSubject);
    }

    [Fact]
    public async Task AnEntryThatIsNotThere_Answers404()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        using var response = await host.GetAsync("/api/audit/entries/987654321/around", cookie, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>A ban with a case file written for it leads to that case file.</summary>
    [Fact]
    public async Task ABanWithACaseFile_CarriesItsId()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        await host.WriteFactAsync(Ban("usr_mod", "usr_eve", host.Clock.UtcNow.AddMinutes(-5)), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewProfile, ct);
        var ban = Assert.Single(await EntriesAsync(host, cookie, ct));

        var caseFile = await WriteCaseFileAsync(host, "usr_eve", ban.Id, ct);

        var again = Assert.Single(await EntriesAsync(host, cookie, ct));
        Assert.Equal(caseFile, again.CaseFileId);
    }

    /// <summary>Somebody who may not open case files is not shown a link to one.</summary>
    [Fact]
    public async Task ABanWithACaseFile_WithoutViewProfile_CarriesNone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        await host.WriteFactAsync(Ban("usr_mod", "usr_eve", host.Clock.UtcNow.AddMinutes(-5)), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var ban = Assert.Single(await EntriesAsync(host, cookie, ct));
        await WriteCaseFileAsync(host, "usr_eve", ban.Id, ct);

        var again = Assert.Single(await EntriesAsync(host, cookie, ct));
        Assert.Null(again.CaseFileId);
    }

    /// <summary>A review fact carries its review's id in the payload, and that is the link.</summary>
    [Fact]
    public async Task AReviewFact_CarriesItsReviewId()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var review = Guid.NewGuid();
        await host.WriteFactAsync(new FactRecord
        {
            Type = FactType.ReviewOpened,
            OccurredAt = host.Clock.UtcNow.AddMinutes(-5),
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = "usr_mod",
            Source = FactSource.Modbot,
            Data = new JsonObject { ["reviewId"] = review.ToString() },
        }, ct);

        var cookie = await host.SignedInAsync(
            ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewOperationalLog | ModbotPermissions.ReviewTickets, ct);
        var entry = Assert.Single(await EntriesAsync(host, cookie, ct), e => e.Type == FactType.ReviewOpened);

        Assert.Equal(review, entry.ReviewId);
    }

    private static async Task<Guid> WriteCaseFileAsync(ReadSurfaceTestHost host, string person, long banFactId, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var row = new CaseFile
        {
            UserId = person,
            BanFactId = banFactId,
            BannedAt = host.Clock.UtcNow.AddMinutes(-5),
            AuthorUserId = Guid.NewGuid(),
            AuthorUsername = "ada",
            CreatedAt = host.Clock.UtcNow,
            UpdatedAt = host.Clock.UtcNow,
            SnapshotTakenAt = host.Clock.UtcNow,
        };

        context.CaseFiles.Add(row);
        await context.SaveChangesAsync(ct);
        return row.Id;
    }
}
