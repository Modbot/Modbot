using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Features.Settings;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Spec 4.2.1's settings surface: every rate lowerable, nothing raisable, and the clamp applied
/// on write rather than in the browser.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class SyncSettingsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PostgresFixture _db;

    public SyncSettingsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Reading ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A deployment that has configured nothing is shown — and runs — spec 4.2's table.
    /// </summary>
    [Fact]
    public async Task WithNothingConfigured_ItReportsSpecFourTwosRates()
    {
        await using var host = await StartAsync();
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var settings = await host.GetJsonAsync<SyncSettingsResponse>("/api/settings/sync", cookie, Ct);

        Assert.True(settings.Running);
        Assert.Equal(RateLimitOptions.DefaultFraction, settings.Rates.BudgetFraction);

        // The sum spec 4.2 quotes, and the room left for interactive requests, which it says is not spare capacity.
        Assert.Equal(1.45, settings.Rates.ScheduledTotalPerSecond, 6);
        Assert.Equal(2.0, settings.Rates.GlobalCeilingPerSecond, 6);
        Assert.Equal(0.55, settings.Rates.InteractiveRoomLeftPerSecond, 6);

        var members = Class(settings, VRChatEndpointClass.GroupsMembers);
        Assert.Equal(0.5, members.EffectiveRatePerSecond, 6);
        Assert.Equal(0.5, members.HardMaxPerSecond, 6);
        Assert.False(members.Configured);
        Assert.True(members.Scheduled);

        Assert.Equal(
            AuditLogSyncOptions.PacingFloor.TotalSeconds, settings.AuditLog.PacingFloorSeconds);
    }

    /// <summary>
    /// Every budgeted class is offered, including the ones spec 4.2's table does not list.
    /// </summary>
    /// <remarks>
    /// <c>users.groups</c> and <c>groups.auditlog.types</c> are the unmeasured ones (spec 4.3.4.1
    /// and 4.3.4.2). They are exactly the rates an operator is most likely to want to turn down,
    /// because nobody knows what VRChat allows for them.
    /// </remarks>
    [Fact]
    public async Task EveryBudgetedClassIsOffered()
    {
        await using var host = await StartAsync();
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var settings = await host.GetJsonAsync<SyncSettingsResponse>("/api/settings/sync", cookie, Ct);
        var offered = settings.Rates.Classes.Select(c => c.EndpointClass).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(VRChatRateLimits.Defaults.Keys.ToHashSet(StringComparer.Ordinal), offered);
    }

    /// <summary>The honest answer to the question an operator actually has.</summary>
    [Fact]
    public async Task ItSaysTheRatesAreEditableAndThatNoRestartIsNeeded()
    {
        await using var host = await StartAsync();
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var settings = await host.GetJsonAsync<SyncSettingsResponse>("/api/settings/sync", cookie, Ct);

        Assert.True(settings.Editable);
        Assert.False(settings.RestartRequired);
        Assert.Equal(SyncSettingsEndpoints.HowChangesTakeEffect, settings.EditableExplanation);
        Assert.Empty(settings.Adjustments);
    }

    [Fact]
    public async Task AHostWithNoProducers_ShowsTheDefaultsAndSaysNothingIsRunning()
    {
        await using var host = await StartAsync(withSync: false);
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var settings = await host.GetJsonAsync<SyncSettingsResponse>("/api/settings/sync", cookie, Ct);

        Assert.False(settings.Running);
        Assert.Equal(0.5, Class(settings, VRChatEndpointClass.GroupsMembers).EffectiveRatePerSecond, 6);
    }

    // ── Writing ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARateCanBeLoweredAndIsReadBack()
    {
        await using var host = await StartAsync();
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var written = await PutAsync(host, cookie, new SyncSettingsUpdate(
            ClassCeilingsPerSecond: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [VRChatEndpointClass.GroupsMembers] = 0.2,
            }));

        Assert.Empty(written.Adjustments);
        Assert.Equal(0.12, Class(written, VRChatEndpointClass.GroupsMembers).EffectiveRatePerSecond, 6);
        Assert.True(Class(written, VRChatEndpointClass.GroupsMembers).Configured);

        var read = await host.GetJsonAsync<SyncSettingsResponse>("/api/settings/sync", cookie, Ct);
        Assert.Equal(0.12, Class(read, VRChatEndpointClass.GroupsMembers).EffectiveRatePerSecond, 6);

        // The sum the screen shows moves with it, so an operator can see what they bought.
        Assert.Equal(1.45 - 0.5 + 0.12, read.Rates.ScheduledTotalPerSecond, 6);
    }

    /// <summary>
    /// <strong>The cap is enforced on the write</strong> (spec 4.2.1), and the response says it was.
    /// </summary>
    /// <remarks>
    /// Clamping rather than rejecting, because the operator's intent — "run this class at some
    /// rate" — can be honoured as far as it safely can be. Silently clamping would not do: the
    /// screen's whole job is to say what the rate is, and an operator who believed a slider had
    /// taken effect would be wrong about exactly that.
    /// </remarks>
    [Fact]
    public async Task ARateAboveSpecFourTwosCapIsStoredAtTheCapAndSaidSo()
    {
        await using var host = await StartAsync();
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var written = await PutAsync(host, cookie, new SyncSettingsUpdate(
            ClassCeilingsPerSecond: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [VRChatEndpointClass.GroupsMembers] = 25,
            }));

        Assert.Equal(0.5, Class(written, VRChatEndpointClass.GroupsMembers).EffectiveRatePerSecond, 6);

        var adjustment = Assert.Single(written.Adjustments);
        Assert.Equal($"classCeilingsPerSecond.{VRChatEndpointClass.GroupsMembers}", adjustment.Field);
        Assert.Equal(25, adjustment.Requested);
        Assert.True(adjustment.Stored < adjustment.Requested);
        Assert.NotEmpty(adjustment.Reason);
    }

    /// <summary>
    /// Zero is refused rather than clamped: it is not a gentler setting, it is a stopped one.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public async Task ARateOfZeroOrBelowIsRejected(double rate)
    {
        await using var host = await StartAsync();
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await SendAsync(host, cookie, new SyncSettingsUpdate(
            ClassCeilingsPerSecond: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [VRChatEndpointClass.GroupsMembers] = rate,
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // And nothing was written: a rejected request leaves the deployment where it was.
        var read = await host.GetJsonAsync<SyncSettingsResponse>("/api/settings/sync", cookie, Ct);
        Assert.Equal(0.5, Class(read, VRChatEndpointClass.GroupsMembers).EffectiveRatePerSecond, 6);
    }

    [Fact]
    public async Task ABudgetFractionOfZeroIsRejected()
    {
        await using var host = await StartAsync();
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await SendAsync(host, cookie, new SyncSettingsUpdate(BudgetFraction: 0));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Spec 4.3.4: a class with no budget is a question to ask, not a rate to invent.</summary>
    [Fact]
    public async Task ARateForAnEndpointClassModbotDoesNotBudgetIsRejected()
    {
        await using var host = await StartAsync();
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var response = await SendAsync(host, cookie, new SyncSettingsUpdate(
            ClassCeilingsPerSecond: new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["groups.somethingNobodyAskedAbout"] = 0.1,
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnIntervalBelowThePacingFloorIsRaisedToIt()
    {
        await using var host = await StartAsync();
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var written = await PutAsync(host, cookie, new SyncSettingsUpdate(
            AuditLog: new AuditLogPollRateUpdate(MinIntervalSeconds: 1)));

        Assert.Equal(
            AuditLogSyncOptions.PacingFloor.TotalSeconds, written.AuditLog.MinIntervalSeconds, 6);

        Assert.Equal("auditLogMinIntervalSeconds", Assert.Single(written.Adjustments).Field);
    }

    [Fact]
    public async Task AnIntervalCanAlwaysBeRaised()
    {
        await using var host = await StartAsync();
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        var written = await PutAsync(host, cookie, new SyncSettingsUpdate(
            AuditLog: new AuditLogPollRateUpdate(MaxIntervalSeconds: 3600, CatchUp: false),
            GroupInfo: new GroupInfoPollRateUpdate(IntervalSeconds: 3600)));

        Assert.Empty(written.Adjustments);
        Assert.Equal(3600, written.AuditLog.MaxIntervalSeconds, 6);
        Assert.Equal(3600, written.GroupInfo.IntervalSeconds, 6);
        Assert.False(written.AuditLog.CatchUp);
    }

    /// <summary>
    /// One slider does not wipe the others.
    /// </summary>
    /// <remarks>
    /// The stored document is sparse and the write is a merge, so a screen that sends the field
    /// it changed — or an older client that omits a field it cannot render — leaves everything
    /// else exactly where the operator left it.
    /// </remarks>
    [Fact]
    public async Task APartialWriteLeavesEverythingElseAlone()
    {
        await using var host = await StartAsync();
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await PutAsync(host, cookie, new SyncSettingsUpdate(BudgetFraction: 0.3));

        var written = await PutAsync(host, cookie, new SyncSettingsUpdate(
            GroupInfo: new GroupInfoPollRateUpdate(IntervalSeconds: 1800)));

        Assert.Equal(0.3, written.Rates.BudgetFraction, 6);
        Assert.Equal(1800, written.GroupInfo.IntervalSeconds, 6);

        // Lowering the fraction lowers every class, which is the point of having it.
        Assert.Equal(0.25, Class(written, VRChatEndpointClass.GroupsMembers).EffectiveRatePerSecond, 6);
    }

    [Fact]
    public async Task ResetPutsEverythingBackToSpecFourTwosDefaults()
    {
        await using var host = await StartAsync();
        var cookie = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);

        await PutAsync(host, cookie, new SyncSettingsUpdate(
            BudgetFraction: 0.2,
            AuditLog: new AuditLogPollRateUpdate(MaxIntervalSeconds: 7200)));

        var reset = await PutAsync(host, cookie, new SyncSettingsUpdate(Reset: true));

        Assert.Equal(RateLimitOptions.DefaultFraction, reset.Rates.BudgetFraction);
        Assert.Equal(300, reset.AuditLog.MaxIntervalSeconds, 6);
        Assert.Equal(1.45, reset.Rates.ScheduledTotalPerSecond, 6);

        // And the column is back to null rather than to a document full of this release's
        // defaults, so an untouched deployment goes on tracking the spec.
        Assert.Null(await StoredAsync(host));
    }

    // ── Permissions ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutManageSettings_ReadingIs403()
    {
        await using var host = await StartAsync();
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);

        var response = await host.GetAsync("/api/settings/sync", cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WithoutManageSettings_WritingIs403()
    {
        await using var host = await StartAsync();
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAnalytics, Ct);

        var response = await SendAsync(host, cookie, new SyncSettingsUpdate(BudgetFraction: 0.5));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Scaffolding ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A host with the pacing column cleared.
    /// </summary>
    /// <remarks>
    /// The assembly shares one database and the settings row is a singleton, so a test that
    /// inherited another's pacing would assert on rates it did not write.
    /// </remarks>
    private async Task<ReadSurfaceTestHost> StartAsync(bool withSync = true)
    {
        var host = await ReadSurfaceTestHost.StartAsync(_db, withSync: withSync);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var settings = await db.GetSettingsAsync(Ct);
        settings.SyncPacing = null;
        await db.SaveChangesAsync(Ct);

        return host;
    }

    private static async Task<string?> StoredAsync(ReadSurfaceTestHost host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        return (await db.GetSettingsAsync(Ct)).SyncPacing;
    }

    private static EndpointClassRate Class(SyncSettingsResponse settings, string endpointClass) =>
        settings.Rates.Classes.Single(c => c.EndpointClass == endpointClass);

    private static async Task<HttpResponseMessage> SendAsync(
        ReadSurfaceTestHost host, string cookie, SyncSettingsUpdate body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/settings/sync")
        {
            Content = JsonContent.Create(body, options: Json),
        };

        request.Headers.Add("Cookie", cookie);

        return await host.Client.SendAsync(request, Ct);
    }

    private static async Task<SyncSettingsResponse> PutAsync(
        ReadSurfaceTestHost host, string cookie, SyncSettingsUpdate body)
    {
        var response = await SendAsync(host, cookie, body);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<SyncSettingsResponse>(Json, Ct)
            ?? throw new InvalidOperationException("The sync settings endpoint answered null.");
    }
}
