using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Evidence;
using Modbot.Core.Configuration;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Evidence.Health;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Evidence;

/// <summary>
/// Evidence design §8.5 and §16: choosing a backend, proving it before it is saved, and the
/// warning that asks rather than refuses.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class EvidenceSettingsTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly HostPlatform Ephemeral = new("railway", true, "RAILWAY_ENVIRONMENT");

    /// <summary>
    /// A deployment that has chosen nothing says so, and refuses uploads rather than pretending.
    /// </summary>
    [Fact]
    public async Task AnUnconfiguredDeploymentSaysSoAndRefusesUploads()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(
            ModbotPermissions.ManageSettings | ModbotPermissions.UploadEvidence, Ct);

        var settings = await host.ReadAsync<EvidenceSettingsResponse>(
            await host.GetAsync("/api/settings/evidence", cookie, Ct), Ct);

        Assert.Equal("None", settings.Backend.Backend);
        Assert.False(settings.Health.UploadsAllowed);

        // NotConfigured is not the lock. Nothing is wrong; nothing has been chosen.
        Assert.Equal(nameof(EvidenceStoreState.NotConfigured), settings.Health.State);
        Assert.False(settings.Health.Locked);

        var begun = await host.PostAsync(
            "/api/evidence/uploads", cookie, new { fileName = "proof.png" }, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, begun.StatusCode);
    }

    /// <summary>
    /// Saving a backend sets it up, and the choice takes effect without a restart.
    /// </summary>
    /// <remarks>
    /// This is the whole point of the change. A settings page that needed a redeploy to take
    /// effect would hand back most of what design §8.4 protects by keeping Modbot running through
    /// a lost store: the operator staring at the banner fixes the store, saves, and nothing
    /// happens. So the assertion is not that the row changed but that an upload begun a moment
    /// later is accepted.
    /// </remarks>
    [Fact]
    public async Task SavingABackendSetsItUpAndTakesEffectWithoutARestart()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(
            ModbotPermissions.ManageSettings | ModbotPermissions.UploadEvidence, Ct);

        var saved = await host.ReadAsync<EvidenceSetupResponse>(
            await host.PutAsync(
                "/api/settings/evidence/backend",
                cookie,
                new { backend = "Filesystem", root = host.Root },
                Ct),
            Ct);

        Assert.True(saved.Succeeded, saved.Message);
        Assert.NotNull(saved.StoreId);

        // The store marker is on disk, and it is the id Settings now records. Two copies in two
        // places is the entire mechanism §8.2 rests on.
        Assert.True(File.Exists(Path.Combine(host.Root, ".modbot-store")));

        await using (var context = db.NewContext())
        {
            var row = await context.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, Ct);
            Assert.Equal(saved.StoreId, row.EvidenceStoreId);
            Assert.Equal(host.Root, row.EvidenceRoot);
        }

        var begun = await host.PostAsync(
            "/api/evidence/uploads", cookie, new { fileName = "proof.png" }, Ct);

        Assert.Equal(HttpStatusCode.OK, begun.StatusCode);
    }

    /// <summary>
    /// A directory that cannot be written to is refused, and nothing is persisted.
    /// </summary>
    /// <remarks>
    /// The one case with nothing to decide. No acknowledgement can make an unwritable directory
    /// hold a file, so this refuses — and it refuses on a fact rather than on an inference about
    /// the platform, which is the distinction the whole durability design turns on.
    /// </remarks>
    [Fact]
    public async Task ADirectoryThatCannotBeWrittenToIsRefused()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        // A file where a directory would have to be. Nothing can create a subdirectory of a file.
        var blocker = Path.Combine(Path.GetTempPath(), $"modbot-blocker-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(blocker, "not a directory", Ct);

        try
        {
            var result = await host.ReadAsync<EvidenceSetupResponse>(
                await host.PutAsync(
                    "/api/settings/evidence/backend",
                    cookie,
                    new { backend = "Filesystem", root = Path.Combine(blocker, "evidence") },
                    Ct),
                Ct);

            Assert.False(result.Succeeded);
            Assert.Equal("root", result.FailedStep);

            // Not an acknowledgement question. There is nothing here for an operator to agree to.
            Assert.False(result.RequiresAcknowledgement);

            await using var context = db.NewContext();
            var row = await context.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, Ct);
            Assert.Equal(0, row.EvidenceBackend);
            Assert.Null(row.EvidenceStoreId);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    /// <summary>
    /// On a platform whose disk is assumed ephemeral, Modbot warns and asks. It does not refuse.
    /// </summary>
    /// <remarks>
    /// Platform detection can only ever be a suspicion: Railway, Fly.io and Render all support
    /// mountable volumes, and the operator is the only party who knows whether they mounted one.
    /// An earlier draft of the spec had Modbot refuse here; that was corrected, and this test is
    /// what keeps the correction from being quietly undone.
    /// </remarks>
    [Fact]
    public async Task AnUnprovenDirectoryAsksRatherThanRefusing()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db, Ephemeral);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var asked = await host.ReadAsync<EvidenceSetupResponse>(
            await host.PutAsync(
                "/api/settings/evidence/backend",
                cookie,
                new { backend = "Filesystem", root = host.Root },
                Ct),
            Ct);

        Assert.False(asked.Succeeded);
        Assert.True(asked.RequiresAcknowledgement);
        Assert.Equal(EvidenceDurabilityAdvisor.UnprovenWarning, asked.Message);
        Assert.NotNull(asked.Durability);
        Assert.True(asked.Durability.IsSuspicion);

        // Echoing the warning back is the "use anyway", and it is enough. Modbot never had a
        // reason to stop them; it had a reason to tell them.
        var accepted = await host.ReadAsync<EvidenceSetupResponse>(
            await host.PutAsync(
                "/api/settings/evidence/backend",
                cookie,
                new
                {
                    backend = "Filesystem",
                    root = host.Root,
                    acknowledgeWarning = EvidenceDurabilityAdvisor.UnprovenWarning,
                },
                Ct),
            Ct);

        Assert.True(accepted.Succeeded, accepted.Message);
    }

    /// <summary>
    /// The acknowledgement records who, when, and the warning itself word for word.
    /// </summary>
    /// <remarks>
    /// Stored verbatim rather than as a reference to the current wording. If this is ever pointed
    /// at in a dispute it has to be the sentence that was actually on their screen — a reworded
    /// warning must not retroactively change what somebody agreed to.
    /// </remarks>
    [Fact]
    public async Task TheAcknowledgementIsRecordedWordForWord()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db, Ephemeral);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.PutAsync(
            "/api/settings/evidence/backend",
            cookie,
            new
            {
                backend = "Filesystem",
                root = host.Root,
                acknowledgeWarning = EvidenceDurabilityAdvisor.UnprovenWarning,
            },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var context = db.NewContext();
        var row = await context.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, Ct);

        Assert.True(row.EvidenceDiskAcknowledged);
        Assert.NotNull(row.EvidenceDiskAcknowledgedBy);
        Assert.Equal(host.Clock.UtcNow, row.EvidenceDiskAcknowledgedAt);
        Assert.Equal(EvidenceDurabilityAdvisor.UnprovenWarning, row.EvidenceDiskWarningShown);
    }

    /// <summary>
    /// A warning the operator was never shown cannot be used as their consent.
    /// </summary>
    /// <remarks>
    /// The acknowledgement is an echo of the text on the screen, so a client that sends something
    /// else has not proved anybody read anything. Accepting it would record consent to words
    /// nobody wrote.
    /// </remarks>
    [Fact]
    public async Task SomeOtherSentenceIsNotAnAcknowledgement()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db, Ephemeral);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var result = await host.ReadAsync<EvidenceSetupResponse>(
            await host.PutAsync(
                "/api/settings/evidence/backend",
                cookie,
                new { backend = "Filesystem", root = host.Root, acknowledgeWarning = "sure, whatever" },
                Ct),
            Ct);

        Assert.False(result.Succeeded);
        Assert.True(result.RequiresAcknowledgement);
    }

    /// <summary>
    /// Switching the backend while objects are stored is refused, with the reason stated.
    /// </summary>
    /// <remarks>
    /// Changing the setting does not move the bytes. A migration between backends copies every
    /// object, verifies it by hash and only then repoints — it is an explicit, resumable job and
    /// never a side effect of saving a setting. Until it exists, the honest answer is no.
    /// </remarks>
    [Fact]
    public async Task SwitchingBackendsWithObjectsStoredIsRefused()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(
            ModbotPermissions.ManageSettings | ModbotPermissions.UploadEvidence, Ct);

        await EvidenceUploads.ConfigureAsync(host, cookie, Ct);
        await EvidenceUploads.UploadAsync(host, cookie, EvidenceUploads.Png("switch-test"), "a.png", "report-1", Ct);

        var elsewhere = Path.Combine(Path.GetTempPath(), $"modbot-elsewhere-{Guid.NewGuid():N}");

        var result = await host.ReadAsync<EvidenceSetupResponse>(
            await host.PutAsync(
                "/api/settings/evidence/backend",
                cookie,
                new { backend = "Filesystem", root = elsewhere },
                Ct),
            Ct);

        Assert.False(result.Succeeded);
        Assert.Equal("switch", result.FailedStep);
        Assert.Contains("does not move it", result.Message, StringComparison.Ordinal);

        // Re-saving the store it is already using is not a switch, so a key rotation or a typo fix
        // is never blocked by evidence the deployment holds.
        var same = await host.ReadAsync<EvidenceSetupResponse>(
            await host.PutAsync(
                "/api/settings/evidence/backend",
                cookie,
                new { backend = "Filesystem", root = host.Root },
                Ct),
            Ct);

        Assert.True(same.Succeeded, same.Message);
    }

    /// <summary>The stored S3 secret is never sent back to the browser.</summary>
    /// <remarks>
    /// The settings page has no reason to see it again, and a secret that travels on every page
    /// load is a secret in a great many more logs than it needs to be. It says only whether one is
    /// on file, which is the question the form actually has.
    /// </remarks>
    [Fact]
    public async Task TheStoredS3SecretIsNeverSentBack()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var protector = host.Services.GetRequiredService<ISecretProtector>();

        await using (var context = db.NewContext())
        {
            var row = await context.GetSettingsAsync(Ct);
            row.EvidenceS3Bucket = "evidence";
            row.EvidenceS3Endpoint = "https://s3.example.com";
            row.EvidenceS3AccessKeyId = "AKIAEXAMPLE";
            row.EvidenceS3SecretAccessKeyEncrypted = protector.Protect("correct-horse-battery-staple");
            await context.SaveChangesAsync(Ct);
        }

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var response = await host.GetAsync("/api/settings/evidence", cookie, Ct);
        var json = await response.Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain("correct-horse-battery-staple", json, StringComparison.Ordinal);

        var settings = await host.ReadAsync<EvidenceSettingsResponse>(response, Ct);

        Assert.True(settings.Backend.SecretStored);
        Assert.Equal("AKIAEXAMPLE", settings.Backend.S3.AccessKeyId);
    }

    /// <summary>
    /// Uploading evidence does not carry the right to decide where a group's evidence lives.
    /// </summary>
    [Fact]
    public async Task WithoutManageSettings_Is403()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(
            ModbotPermissions.UploadEvidence | ModbotPermissions.ViewEvidence, Ct);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.GetAsync("/api/settings/evidence", cookie, Ct)).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.PutAsync(
                "/api/settings/evidence/backend",
                cookie,
                new { backend = "Filesystem", root = host.Root },
                Ct)).StatusCode);
    }

    /// <summary>A per-report total below the per-file cap could never be satisfied.</summary>
    [Fact]
    public async Task LimitsThatContradictEachOtherAreRefused()
    {
        await EvidenceApiTestHost.ResetAsync(db, Ct);
        await using var host = await EvidenceApiTestHost.StartAsync(db);

        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await host.PutAsync(
            "/api/settings/evidence/limits",
            cookie,
            new
            {
                maxFileBytes = 10_000_000,
                maxReportBytes = 1_000,
                maxDeploymentBytes = 0,
                directDeliveryEnabled = true,
            },
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
