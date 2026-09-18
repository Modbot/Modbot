using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Auth;
using Modbot.Api.Features.Settings;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Settings → Purge a person. The capability has existed since M2 with nothing able to call it;
/// these are the tests of the way in.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class PurgeTests
{
    private const string Path = "/api/settings/purge";
    private const string Subject = "usr_erase_me";
    private const string Bystander = "usr_stay";

    private readonly PostgresFixture _db;

    public PurgeTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Query(string subjectId, string platform = "VRChat")
        => $"{Path}?platform={platform}&subjectId={Uri.EscapeDataString(subjectId)}";

    private static object Body(string subjectId, string confirmation, string platform = "VRChat")
        => new { platform, subjectId, confirmation };

    /// <summary>
    /// Administrator, not a permission of its own, and not Manage settings -- which is the
    /// permission every other tab on this page asks for, and the one somebody would expect to be
    /// enough (evidence storage design §14).
    /// </summary>
    [Fact]
    public async Task ManageSettingsIsNotEnough()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var cookie = await host.SignedInAsync(
            ModbotPermissions.ManageSettings | ModbotPermissions.ViewProfile, Ct);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.GetAsync(Query(Subject), cookie, Ct)).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await host.PostJsonAsync(Path, Body(Subject, Subject), cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheCountsSayWhatWouldGoAndWhatWouldStay()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var t = host.Clock.UtcNow;

        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, Subject, t.AddDays(-3)), Ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceKick, Subject, t.AddDays(-1)), Ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, Bystander, t.AddDays(-2)), Ct);

        await AddCaseFileAsync(host, Subject, Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);
        var preview = await host.GetJsonAsync<PurgePreviewResponse>(Query(Subject), cookie, Ct);

        Assert.Equal("VRChat", preview.Platform);
        Assert.Equal(2, preview.Facts);
        Assert.Equal(1, preview.CaseFilesKept);
        Assert.Equal(0, preview.Messages);
    }

    /// <summary>
    /// The typed id is the whole confirmation. Without it nothing happens, and nothing is a state
    /// that has to be checked rather than assumed.
    /// </summary>
    [Fact]
    public async Task WithoutTheTypedId_NothingIsRemoved()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, Subject, host.Clock.UtcNow), Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var wrong = await host.PostJsonAsync(Path, Body(Subject, "usr_something_else"), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var empty = await host.PostJsonAsync(Path, Body(Subject, ""), cookie, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        Assert.Equal(1, await FactsAboutAsync(host, Subject, Ct));
        Assert.Empty(await PurgeFactsAsync(host, Ct));
    }

    [Fact]
    public async Task AnUnknownPlatformIsRefused()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await host.GetAsync($"{Path}?platform=Modbot&subjectId=x", cookie, Ct)).StatusCode);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await host.PostJsonAsync(Path, Body("x", "x", "Modbot"), cookie, Ct)).StatusCode);
    }

    [Fact]
    public async Task WithTheTypedId_EverythingAboutThemGoes()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var t = host.Clock.UtcNow;

        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, Subject, t.AddDays(-3)), Ct);
        await host.WriteFactAsync(AuditFact(FactType.GroupInstanceKick, Subject, t.AddDays(-1)), Ct);
        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, Bystander, t.AddDays(-2)), Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var response = await host.PostJsonAsync(Path, Body(Subject, Subject), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var receipt = await response.Content.ReadFromJsonAsync<PurgeReceipt>(Ct);

        Assert.Equal(2, receipt!.Facts);
        Assert.Equal(0, await FactsAboutAsync(host, Subject, Ct));
        Assert.Equal(1, await FactsAboutAsync(host, Bystander, Ct));
    }

    /// <summary>
    /// The three things a purge deliberately keeps: what they did to somebody else, the group's
    /// own record of a decision it made about them, and the record that the purge happened
    /// (spec 5.5, evidence storage design §15.1).
    /// </summary>
    [Fact]
    public async Task WhatSurvivesIsWhatTheSpecSaysSurvives()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var t = host.Clock.UtcNow;

        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, Subject, t.AddDays(-3)), Ct);
        await host.WriteFactAsync(
            AuditFact(FactType.MemberBanned, Bystander, t.AddDays(-2), actor: Subject), Ct);

        await AddCaseFileAsync(host, Subject, Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var response = await host.PostJsonAsync(Path, Body(Subject, Subject), cookie, Ct);
        var receipt = await response.Content.ReadFromJsonAsync<PurgeReceipt>(Ct);

        Assert.Equal(1, receipt!.CaseFilesKept);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        // The ban they carried out is somebody else's moderation history.
        Assert.Equal(
            1, await db.Events.AsNoTracking().CountAsync(e => e.ActorId == Subject, Ct));

        Assert.Equal(1, await db.CaseFiles.AsNoTracking().CountAsync(c => c.UserId == Subject, Ct));
        Assert.Single(await PurgeFactsAsync(host, Ct));
    }

    /// <summary>
    /// The purge's own record names the account that ran it and says how much went, and nothing
    /// in it points back at the person. A record that named them would not be an erasure.
    /// </summary>
    [Fact]
    public async Task ThePurgeLeavesAFactNamingWhoDidIt_AndNotWhoItWasAbout()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        await host.WriteFactAsync(AuditFact(FactType.MemberJoined, Subject, host.Clock.UtcNow), Ct);

        var name = $"u_{Guid.NewGuid():N}";
        var admin = await host.CreateUserAsync(name, "hunter2", ModbotPermissions.Administrator, Ct);

        // Signed in as the account whose id and name the fact has to carry.
        var cookie = await SignInAsync(host, name, Ct);

        await host.PostJsonAsync(Path, Body(Subject, Subject), cookie, Ct);

        var fact = Assert.Single(await PurgeFactsAsync(host, Ct));

        Assert.Equal(admin.Id.ToString(), fact.ActorId);
        Assert.Equal(FactPlatform.Modbot, fact.ActorPlatform);
        Assert.Equal(host.Clock.UtcNow, fact.OccurredAt);
        Assert.Contains(name, fact.Data, StringComparison.Ordinal);
        Assert.Contains("\"facts\": 1", fact.Data, StringComparison.Ordinal);

        Assert.DoesNotContain(Subject, fact.Data, StringComparison.Ordinal);
        Assert.DoesNotContain(Subject, fact.SubjectId, StringComparison.Ordinal);
    }

    /// <summary>
    /// An id nobody has ever seen is a legitimate thing to ask about -- a person can request
    /// erasure from a group that never recorded them -- and answering with an error would make
    /// the operator think something went wrong.
    /// </summary>
    [Fact]
    public async Task PurgingSomebodyModbotHasNeverSeenIsNotAnError()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        var preview = await host.GetJsonAsync<PurgePreviewResponse>(Query("usr_nobody"), cookie, Ct);
        Assert.Equal(0, preview.Facts);
        Assert.Null(preview.Name);
        Assert.Null(preview.IsMember);

        var response = await host.PostJsonAsync(Path, Body("usr_nobody", "usr_nobody"), cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var receipt = await response.Content.ReadFromJsonAsync<PurgeReceipt>(Ct);
        Assert.Equal(0, receipt!.Facts);
        Assert.Equal(0, receipt.Messages);
    }

    private static async Task<string> SignInAsync(
        ReadSurfaceTestHost host, string username, CancellationToken ct)
    {
        var response = await host.Client.PostAsync(
            "/api/auth/login",
            JsonContent.Create(new { username, password = "hunter2" }),
            ct);

        response.EnsureSuccessStatusCode();

        var cookie = response.Headers.GetValues("Set-Cookie")
            .First(v => v.StartsWith(ModbotAuth.CookieName, StringComparison.Ordinal));

        return cookie.Split(';')[0];
    }

    private static async Task AddCaseFileAsync(
        ReadSurfaceTestHost host, string userId, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        db.CaseFiles.Add(new CaseFile
        {
            UserId = userId,
            AuthorUserId = Guid.CreateVersion7(),
            AuthorUsername = "mod",
            WrittenReason = "The group's own record of its own decision.",
            CreatedAt = host.Clock.UtcNow,
            UpdatedAt = host.Clock.UtcNow,
            SnapshotTakenAt = host.Clock.UtcNow,
        });

        await db.SaveChangesAsync(ct);
    }

    private static async Task<int> FactsAboutAsync(
        ReadSurfaceTestHost host, string subjectId, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        return await db.Events.AsNoTracking().CountAsync(e => e.SubjectId == subjectId, ct);
    }

    private static async Task<List<ModbotEvent>> PurgeFactsAsync(
        ReadSurfaceTestHost host, CancellationToken ct)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        return await db.Events.AsNoTracking()
            .Where(e => e.Type == FactType.UserPurged)
            .ToListAsync(ct);
    }
}
