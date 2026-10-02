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
        ModbotPermissions.ManageSettings | ModbotPermissions.UploadEvidence | ModbotPermissions.ViewEvidence | ModbotPermissions.ViewProfile;

    /// <summary>The account the reporting device was paired to, as the ingest endpoint writes it onto the fact.</summary>
    private static readonly Guid Owner = Guid.Parse("0192d4a0-0000-7000-8000-00000000a1e5");

    private static readonly Guid Device = Guid.Parse("0192d4a0-0000-7000-8000-0000000de71c");

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
                ["displayName"] = "Somebody Else",
                [ClientReport.DeviceIdKey] = Device.ToString(),
                [ClipKeys.SavedByUserId] = Owner.ToString(),
                [ClipKeys.SavedByUsername] = "alex",
            },
        });

    /// <summary>The case file's person joining the instance the clips are saved in, so the case file offers them.</summary>
    private static async Task PersonWasThereAsync(EvidenceApiTestHost host, string person, string instance = "98874")
        => await WriteAsync(host, new FactRecord
        {
            Type = FactType.InstanceJoined,
            OccurredAt = host.Clock.UtcNow.AddHours(-1),
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = person,
            WorldId = "wrld_cat",
            InstanceId = instance,
            Source = FactSource.Companion,
        });

    private static async Task<string> CaseUserAsync(PostgresFixture db, string caseId)
    {
        await using var context = db.NewContext();
        return (await context.CaseFiles.AsNoTracking().SingleAsync(c => c.Id == Guid.Parse(caseId), Ct)).UserId;
    }

    private static async Task<HttpResponseMessage> SendAsClipAsync(
        EvidenceApiTestHost host, string cookie, byte[] bytes, string caseId, long clipId, bool caseAtBegin = true)
    {
        var ticket = await host.ReadAsync<EvidenceUploadTicketView>(
            await host.PostAsync(
                "/api/evidence/uploads",
                cookie,
                new { fileName = "The Black Cat_98874.mp4", contentType = "image/png", length = bytes.Length, reportId = caseAtBegin ? caseId : null },
                Ct),
            Ct);

        (await host.PutBytesAsync(ticket.TransferUrl, cookie, bytes, Ct)).EnsureSuccessStatusCode();

        return await host.PostAsync(
            $"/api/evidence/uploads/{ticket.UploadId}/commit", cookie, new { clipId, reportId = caseAtBegin ? null : caseId }, Ct);
    }

    [Fact]
    public async Task TheClipsOwnBytesAreAttachedAsCapturedWithWhereAndWhenItWasSaved()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var caseId = await host.NewCaseAsync(cookie, Ct);
        await PersonWasThereAsync(host, await CaseUserAsync(db, caseId));
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

        // Credited to the device's owner, as the server wrote it from the pairing, never to the
        // display name the companion's event carried.
        Assert.Equal(Owner, blob.ClipSavedByUserId);
        Assert.Equal("alex", blob.ClipSavedByName);
        Assert.Equal(Device, blob.ClipDeviceId);

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
        await PersonWasThereAsync(host, await CaseUserAsync(db, caseId));
        var clipId = await ClipSavedAsync(host, EvidenceUploads.Png("the-real-clip"), host.Clock.UtcNow.AddMinutes(-30));

        var response = await SendAsClipAsync(host, cookie, EvidenceUploads.Png("some-other-file"), caseId, clipId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("not the clip saved on alex's PC", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

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
    /// A clip the case file does not offer, because it was saved in an instance the person was never
    /// in, is not found: callers cannot try clip ids to learn which exist.
    /// </summary>
    [Fact]
    public async Task AClipTheCaseFileDoesNotOfferIsNotFound()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);

        var caseId = await host.NewCaseAsync(cookie, Ct);
        await PersonWasThereAsync(host, await CaseUserAsync(db, caseId));

        var bytes = EvidenceUploads.Png("clip-elsewhere");
        var elsewhere = await ClipSavedAsync(host, bytes, host.Clock.UtcNow.AddMinutes(-30), instance: "11111");

        var response = await SendAsClipAsync(host, cookie, bytes, caseId, elsewhere);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var context = db.NewContext();
        Assert.Equal(0, await context.EvidenceAttachments.CountAsync(a => a.CaseId == caseId, Ct));
    }

    /// <summary>
    /// Whether a clip exists is not told to somebody who may not attach to the case file: the case
    /// file is checked first, and they get the case file's refusal whatever the clip id.
    /// </summary>
    [Fact]
    public async Task TheCaseFileIsCheckedBeforeTheClip()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var author = await host.SignedInAsync(Moderator, Ct);
        await EvidenceUploads.ConfigureAsync(host, author, Ct);
        var caseId = await host.NewCaseAsync(author, Ct);

        // May upload, did not write this case file, and may not ban.
        var stranger = await host.SignedInAsync(ModbotPermissions.UploadEvidence | ModbotPermissions.ViewEvidence, Ct);
        var clipId = await ClipSavedAsync(host, EvidenceUploads.Png("not-yours"), host.Clock.UtcNow.AddMinutes(-30));

        // Begun with no case file, so the case file is first named, and checked, at commit.
        var missing = await SendAsClipAsync(host, stranger, EvidenceUploads.Png("not-yours-a"), caseId, clipId: 987_654_321, caseAtBegin: false);
        var real = await SendAsClipAsync(host, stranger, EvidenceUploads.Png("not-yours-b"), caseId, clipId, caseAtBegin: false);

        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, real.StatusCode);
    }

    /// <summary>
    /// The case file of somebody who was in the instance offers the clip, and stops offering it
    /// once the case file holds it.
    /// </summary>
    [Fact]
    public async Task TheCaseFileOffersAClipOfAnInstanceThePersonWasInUntilItHoldsIt()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);

        // The reset clears the evidence, not the facts: the clips the other tests here saved in this
        // same instance are still on record, and would be offered too.
        await using (var context = db.NewContext())
            await context.Events.Where(e => e.Type == FactType.InstanceClipSaved).ExecuteDeleteAsync(Ct);

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
