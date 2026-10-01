using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Evidence;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Evidence;

/// <summary>
/// A clip a moderator's companion said it saved: offered on the case file of somebody who was
/// there, and attached only when the file is that clip (clips design spec §16).
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EvidenceClipTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const ModbotPermissions Moderator =
        ModbotPermissions.ManageSettings | ModbotPermissions.UploadEvidence | ModbotPermissions.ViewEvidence;

    private static async Task<long> WriteAsync(EvidenceApiTestHost host, FactRecord fact)
    {
        using var scope = host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IFactWriter>().WriteAsync(fact, Ct)).Id;
    }

    /// <summary>The fact a companion's report becomes, for a clip of these bytes saved at this moment.</summary>
    private static Task<long> ClipSavedAsync(EvidenceApiTestHost host, byte[] bytes, DateTimeOffset at, string instance = "98874")
        => WriteAsync(host, new FactRecord
        {
            Type = FactType.InstanceClipSaved,
            OccurredAt = at,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = "usr_mod",
            WorldId = "wrld_cat",
            InstanceId = instance,
            Source = FactSource.Companion,
            Data = new JsonObject
            {
                [ClipKeys.Hash] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                [ClipKeys.Bytes] = bytes.LongLength,
                ["displayName"] = "Alex",
            },
        });

    private static async Task<string> CaseUserAsync(PostgresFixture db, string caseId)
    {
        await using var context = db.NewContext();
        return (await context.CaseFiles.AsNoTracking().SingleAsync(c => c.Id == Guid.Parse(caseId), Ct)).UserId;
    }

    private static async Task<HttpResponseMessage> SendAsClipAsync(
        EvidenceApiTestHost host, string cookie, byte[] bytes, string caseId, long clipId)
    {
        var ticket = await host.ReadAsync<EvidenceUploadTicketView>(
            await host.PostAsync(
                "/api/evidence/uploads",
                cookie,
                new { fileName = "The Black Cat_98874.mp4", contentType = "image/png", length = bytes.Length, reportId = caseId },
                Ct),
            Ct);

        (await host.PutBytesAsync(ticket.TransferUrl, cookie, bytes, Ct)).EnsureSuccessStatusCode();

        return await host.PostAsync($"/api/evidence/uploads/{ticket.UploadId}/commit", cookie, new { clipId }, Ct);
    }

    [Fact]
    public async Task TheClipsOwnBytesAreAttachedAsCapturedWithWhereAndWhenItWasSaved()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var caseId = await host.NewCaseAsync(cookie, Ct);
        var bytes = EvidenceUploads.Png("clip-matches");
        var savedAt = host.Clock.UtcNow.AddMinutes(-30);
        var clipId = await ClipSavedAsync(host, bytes, savedAt);

        var response = await SendAsClipAsync(host, cookie, bytes, caseId, clipId);
        response.EnsureSuccessStatusCode();

        var committed = await host.ReadAsync<EvidenceCommitResponse>(response, Ct);

        await using var context = db.NewContext();
        var blob = await context.EvidenceBlobs.AsNoTracking().SingleAsync(b => b.Hash == committed.Hash, Ct);

        Assert.Equal(EvidenceOriginKind.Captured, blob.Origin);
        Assert.Equal(savedAt, blob.ClipSavedAt);
        Assert.Equal("wrld_cat", blob.ClipWorldId);
        Assert.Equal("98874", blob.ClipInstanceId);
        Assert.Equal("usr_mod", blob.ClipSavedById);
        Assert.Equal("Alex", blob.ClipSavedByName);

        var attached = await context.Events.AsNoTracking()
            .SingleAsync(e => e.Type == FactType.EvidenceAttached && e.SubjectId == caseId, Ct);

        using var data = JsonDocument.Parse(attached.Data);
        Assert.Equal(clipId, data.RootElement.GetProperty("clip").GetProperty("clipId").GetInt64());
    }

    /// <summary>The ticket is the fingerprint: any other file sent as the clip is refused, and nothing is attached.</summary>
    [Fact]
    public async Task AnyOtherFileSentAsTheClipIsRefused()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var caseId = await host.NewCaseAsync(cookie, Ct);
        var clipId = await ClipSavedAsync(host, EvidenceUploads.Png("the-real-clip"), host.Clock.UtcNow.AddMinutes(-30));

        var response = await SendAsClipAsync(host, cookie, EvidenceUploads.Png("some-other-file"), caseId, clipId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("not the clip saved on Alex's PC", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        await using var context = db.NewContext();
        Assert.Equal(0, await context.EvidenceAttachments.CountAsync(a => a.CaseId == caseId, Ct));
    }

    [Fact]
    public async Task AClipThatWasNeverSavedIsNotFound()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var caseId = await host.NewCaseAsync(cookie, Ct);

        var response = await SendAsClipAsync(host, cookie, EvidenceUploads.Png("no-such-clip"), caseId, clipId: 987_654_321);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The case file of somebody who was in the instance offers the clip, and stops offering it
    /// once the case file holds it.
    /// </summary>
    [Fact]
    public async Task TheCaseFileOffersAClipOfAnInstanceThePersonWasInUntilItHoldsIt()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var caseId = await host.NewCaseAsync(cookie, Ct);
        var person = await CaseUserAsync(db, caseId);
        var bytes = EvidenceUploads.Png("clip-offered");

        await WriteAsync(host, new FactRecord
        {
            Type = FactType.InstanceJoined,
            OccurredAt = host.Clock.UtcNow.AddHours(-1),
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = person,
            WorldId = "wrld_cat",
            InstanceId = "98874",
            Source = FactSource.Companion,
        });

        var here = await ClipSavedAsync(host, bytes, host.Clock.UtcNow.AddMinutes(-30));
        await ClipSavedAsync(host, EvidenceUploads.Png("clip-elsewhere"), host.Clock.UtcNow.AddMinutes(-30), instance: "11111");

        var offered = await ClipsOnAsync(host, cookie, caseId);
        Assert.Equal([here], offered);

        (await SendAsClipAsync(host, cookie, bytes, caseId, here)).EnsureSuccessStatusCode();

        Assert.Empty(await ClipsOnAsync(host, cookie, caseId));
    }

    private static async Task<long[]> ClipsOnAsync(EvidenceApiTestHost host, string cookie, string caseId)
    {
        var response = await host.GetAsync($"/api/cases/{caseId}", cookie, Ct);
        response.EnsureSuccessStatusCode();

        using var view = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return [.. view.RootElement.GetProperty("clips").EnumerateArray().Select(c => c.GetProperty("id").GetInt64())];
    }
}
