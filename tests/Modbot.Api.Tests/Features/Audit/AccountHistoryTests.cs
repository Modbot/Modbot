using System.Net;
using System.Text.Json.Nodes;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Audit;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Audit;

/// <summary>
/// One Modbot account's own history, and the old person address it sits beside (one view per
/// person design §5).
/// </summary>
/// <remarks>
/// An account's sign-ins and role changes are recorded against the subject; the kicks and bans it
/// pressed are recorded against the actor. Neither filter alone answers "what has this account
/// done and what has been done to it", which is the question a moderator opening a colleague's
/// record is asking, so <c>?account=</c> answers both halves at once.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class AccountHistoryTests
{
    private readonly PostgresFixture _db;

    public AccountHistoryTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Day = new(2026, 4, 5, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A sign-in: the account is the subject, because it was done to the account.</summary>
    private static FactRecord SignedIn(Guid accountId, DateTimeOffset at) => new()
    {
        Type = FactType.Login,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.Modbot,
        SubjectId = accountId.ToString(),
        ActorPlatform = FactPlatform.Modbot,
        ActorId = accountId.ToString(),
        Source = FactSource.Modbot,
        Data = new JsonObject { ["actorDisplayName"] = "mira" },
    };

    /// <summary>A ban pressed in Modbot: the account is the actor, and a VRChat person the subject.</summary>
    private static FactRecord BannedSomebody(Guid accountId, string userId, DateTimeOffset at) => new()
    {
        Type = FactType.ActionBan,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = userId,
        ActorPlatform = FactPlatform.Modbot,
        ActorId = accountId.ToString(),
        Source = FactSource.Manual,
        Data = new JsonObject { ["actorDisplayName"] = "mira" },
    };

    /// <summary>Somebody else's ban, so a filter that matched everything would be caught.</summary>
    private static FactRecord SomebodyElsesBan(DateTimeOffset at) => new()
    {
        Type = FactType.ActionBan,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = "usr_other",
        ActorPlatform = FactPlatform.Modbot,
        ActorId = Guid.NewGuid().ToString(),
        Source = FactSource.Manual,
        Data = new JsonObject(),
    };

    private const ModbotPermissions BothLogs =
        ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewOperationalLog;

    [Fact]
    public async Task AnAccountsHistoryIsWhatItDidAndWhatWasDoneToIt()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var account = Guid.CreateVersion7();

        await host.WriteFactAsync(SignedIn(account, Day), Ct);
        await host.WriteFactAsync(BannedSomebody(account, "usr_a", Day.AddMinutes(1)), Ct);
        await host.WriteFactAsync(SomebodyElsesBan(Day.AddMinutes(2)), Ct);

        var cookie = await host.SignedInAsync(BothLogs, Ct);
        var page = await host.GetJsonAsync<AuditPage>($"/api/audit?account={account}", cookie, Ct);

        Assert.Equal([FactType.ActionBan, FactType.Login], page.Entries.Select(e => e.Type));
        Assert.DoesNotContain(page.Entries, e => e.SubjectId == "usr_other");
    }

    [Fact]
    public async Task AModeratorSeesWhatTheAccountDid_AndNotItsSignIns()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var account = Guid.CreateVersion7();

        await host.WriteFactAsync(SignedIn(account, Day), Ct);
        await host.WriteFactAsync(BannedSomebody(account, "usr_a", Day.AddMinutes(1)), Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);
        var page = await host.GetJsonAsync<AuditPage>($"/api/audit?account={account}", cookie, Ct);

        // The narrowing is the audit log's own, unchanged: asking about an account never widens
        // what a caller may read.
        Assert.Equal([FactType.ActionBan], page.Entries.Select(e => e.Type));
    }

    [Fact]
    public async Task AnOperatorSeesTheSignIns_AndNotTheBans()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var account = Guid.CreateVersion7();

        await host.WriteFactAsync(SignedIn(account, Day), Ct);
        await host.WriteFactAsync(BannedSomebody(account, "usr_a", Day.AddMinutes(1)), Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewOperationalLog, Ct);
        var page = await host.GetJsonAsync<AuditPage>($"/api/audit?account={account}", cookie, Ct);

        Assert.Equal([FactType.Login], page.Entries.Select(e => e.Type));
    }

    [Fact]
    public async Task SomebodyWhoMayReadNeitherLogIsRefused()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);
        var response = await host.GetAsync($"/api/audit?account={Guid.CreateVersion7()}", cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TheOldPersonAddressStillMeansWhatItAlwaysMeant()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var account = Guid.CreateVersion7();

        await host.WriteFactAsync(BannedSomebody(account, "usr_a", Day), Ct);
        await host.WriteFactAsync(SomebodyElsesBan(Day.AddMinutes(1)), Ct);

        var cookie = await host.SignedInAsync(BothLogs, Ct);

        // Every Discord card, reset email and pasted link carries ?subject=<vrchat id>, and the
        // address is fixed (foundation §10.2). Resolution happens behind it; the filter does not
        // change.
        var page = await host.GetJsonAsync<AuditPage>("/api/audit?subject=usr_a", cookie, Ct);

        Assert.Equal(["usr_a"], page.Entries.Select(e => e.SubjectId));
    }

    [Fact]
    public async Task AnAccountNothingIsRecordedAgainstIsAnEmptyPage_NotAnError()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var cookie = await host.SignedInAsync(BothLogs, Ct);
        var page = await host.GetJsonAsync<AuditPage>($"/api/audit?account={Guid.CreateVersion7()}", cookie, Ct);

        Assert.Empty(page.Entries);
    }
}
