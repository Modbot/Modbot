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
/// One person's timeline, merged on the server across every account tied to them, and paged and
/// narrowed like the rest of the log.
/// </summary>
/// <remarks>
/// The person popup used to read five pages and merge them in the browser, cut at fifty with no way
/// past it. These pin the three things the merge on the server has to keep: the accounts are tied
/// the way the popup ties them and no further, the order and the cursor are the log's own, and Show
/// only ever narrows.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class PersonTimelineTests
{
    private readonly PostgresFixture _db;

    public PersonTimelineTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Day = new(2026, 5, 6, 18, 0, 0, TimeSpan.Zero);

    private const string Ada = "usr_ada";
    private const string AdaOnDiscord = "100000000000000001";

    private static FactRecord VRChatBan(string subject, DateTimeOffset at, string? actor = null) => new()
    {
        Type = FactType.MemberBanned,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = subject,
        ActorPlatform = actor is null ? null : FactPlatform.VRChat,
        ActorId = actor,
        Source = FactSource.AuditLog,
        Data = new JsonObject(),
    };

    private static FactRecord DiscordBan(string subject, DateTimeOffset at) => new()
    {
        Type = FactType.DiscordMemberBanned,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.Discord,
        SubjectId = subject,
        Source = FactSource.Discord,
        Data = new JsonObject(),
    };

    private static FactRecord Arrived(string subject, DateTimeOffset at) => new()
    {
        Type = FactType.InstanceJoined,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = subject,
        WorldId = "wrld_a",
        InstanceId = "39047",
        Source = FactSource.Companion,
        Data = new JsonObject { ["displayName"] = "Ada" },
    };

    private static async Task LinkAsync(ReadSurfaceTestHost host, string vrchat, string discord)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        db.DiscordAccountLinks.Add(new DiscordAccountLink
        {
            DiscordUserId = discord,
            DiscordUsername = "ada",
            VRChatUserId = vrchat,
            LinkedAt = host.Clock.UtcNow.AddDays(-5),
        });
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Ada's VRChat ban, her Discord ban, a ban she gave, and somebody else's ban.</summary>
    private static async Task SeedAsync(ReadSurfaceTestHost host)
    {
        await host.WriteFactAsync(VRChatBan(Ada, Day), Ct);
        await host.WriteFactAsync(DiscordBan(AdaOnDiscord, Day.AddMinutes(1)), Ct);
        await host.WriteFactAsync(VRChatBan("usr_eve", Day.AddMinutes(2), actor: Ada), Ct);
        await host.WriteFactAsync(VRChatBan("usr_other", Day.AddMinutes(3)), Ct);
        await LinkAsync(host, Ada, AdaOnDiscord);
    }

    private const ModbotPermissions Moderator = ModbotPermissions.ViewAuditLog | ModbotPermissions.ViewProfile;

    [Fact]
    public async Task APersonsTimelineIsEveryLinkedAccount_WhatWasDoneToThemAndWhatTheyDid()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        var page = await host.GetJsonAsync<AuditPage>($"/api/audit?person={Ada}", cookie, Ct);

        // Newest first, the log's own order, across both platforms.
        Assert.Equal(
            [("usr_eve", "VRChat"), (AdaOnDiscord, "Discord"), (Ada, "VRChat")],
            page.Entries.Select(e => (e.SubjectId, e.SubjectPlatform)));
    }

    [Fact]
    public async Task AskingFromTheDiscordAccountReadsTheSamePerson()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        var page = await host.GetJsonAsync<AuditPage>(
            $"/api/audit?person={AdaOnDiscord}&personPlatform=Discord", cookie, Ct);

        Assert.Equal(3, page.Entries.Count);
        Assert.DoesNotContain(page.Entries, e => e.SubjectId == "usr_other");
    }

    [Fact]
    public async Task WithoutSeeProfiles_TheLinkIsNotFollowed()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);
        await SeedAsync(host);

        // The link between the two accounts is See profiles' to tell. Without it the timeline is
        // the account that was named, which the subject and actor filters already give this caller.
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);
        var page = await host.GetJsonAsync<AuditPage>($"/api/audit?person={Ada}", cookie, Ct);

        Assert.DoesNotContain(page.Entries, e => e.SubjectPlatform == "Discord");
        Assert.Equal(2, page.Entries.Count);
    }

    [Fact]
    public async Task TheSameIdOnAnotherPlatformIsNotThem()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        // A Discord account whose id happens to read like Ada's VRChat id is somebody else.
        await host.WriteFactAsync(VRChatBan(Ada, Day), Ct);
        await host.WriteFactAsync(DiscordBan(Ada, Day.AddMinutes(1)), Ct);

        var cookie = await host.SignedInAsync(Moderator, Ct);
        var page = await host.GetJsonAsync<AuditPage>($"/api/audit?person={Ada}", cookie, Ct);

        Assert.Equal([FactType.MemberBanned], page.Entries.Select(e => e.Type));
    }

    [Fact]
    public async Task TheTimelinePagesByTheLogsCursor_WithNothingSkippedOrRepeated()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        // Two in the same second, so a cursor on time alone would drop one at the page edge.
        await host.WriteFactAsync(VRChatBan(Ada, Day), Ct);
        await host.WriteFactAsync(DiscordBan(AdaOnDiscord, Day), Ct);
        await host.WriteFactAsync(VRChatBan("usr_eve", Day.AddMinutes(1), actor: Ada), Ct);
        await host.WriteFactAsync(VRChatBan("usr_fay", Day.AddMinutes(2), actor: Ada), Ct);
        await host.WriteFactAsync(VRChatBan("usr_gus", Day.AddMinutes(3), actor: Ada), Ct);
        await LinkAsync(host, Ada, AdaOnDiscord);

        var cookie = await host.SignedInAsync(Moderator, Ct);

        var all = await host.GetJsonAsync<AuditPage>($"/api/audit?person={Ada}&limit=50", cookie, Ct);
        Assert.Equal(5, all.Entries.Count);

        var read = new List<long>();
        AuditCursor? next = null;

        do
        {
            var path = $"/api/audit?person={Ada}&limit=2"
                + (next is null ? "" : $"&beforeOccurredAt={Uri.EscapeDataString(next.OccurredAt.ToString("O"))}&beforeId={next.Id}");
            var page = await host.GetJsonAsync<AuditPage>(path, cookie, Ct);
            read.AddRange(page.Entries.Select(e => e.Id));
            next = page.Next;
        }
        while (next is not null);

        Assert.Equal(all.Entries.Select(e => e.Id), read);
    }

    [Fact]
    public async Task ModerationOnly_LeavesTheArrivalsOut_AndPresenceIsOnlyTheArrivals()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        await host.WriteFactAsync(Arrived(Ada, Day), Ct);
        await host.WriteFactAsync(VRChatBan(Ada, Day.AddMinutes(30)), Ct);
        await host.WriteFactAsync(Arrived(Ada, Day.AddHours(2)), Ct);

        var cookie = await host.SignedInAsync(Moderator, Ct);

        var moderation = await host.GetJsonAsync<AuditPage>($"/api/audit?person={Ada}&show=moderation", cookie, Ct);
        Assert.Equal([FactType.MemberBanned], moderation.Entries.Select(e => e.Type));

        var presence = await host.GetJsonAsync<AuditPage>($"/api/audit?person={Ada}&show=presence", cookie, Ct);
        Assert.All(presence.Entries, e => Assert.Equal(FactType.InstanceJoined, e.Type));
        Assert.Equal(2, presence.Entries.Count);

        // Everything, said out loud or left out, is everything.
        var everything = await host.GetJsonAsync<AuditPage>($"/api/audit?person={Ada}&show=everything", cookie, Ct);
        Assert.Equal(3, everything.Entries.Count);
    }

    [Fact]
    public async Task ShowNeverWidensWhatACallerMayRead()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        await host.WriteFactAsync(new FactRecord
        {
            Type = FactType.CopyFailed,
            OccurredAt = Day,
            SubjectPlatform = FactPlatform.Discord,
            SubjectId = AdaOnDiscord,
            Source = FactSource.Modbot,
            Data = new JsonObject(),
        }, Ct);

        // A copy that failed is the operational log's, so a moderator asking for Discord still
        // does not get it.
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);
        var page = await host.GetJsonAsync<AuditPage>(
            $"/api/audit?person={AdaOnDiscord}&personPlatform=Discord&show=discord", cookie, Ct);

        Assert.Empty(page.Entries);
    }

    [Fact]
    public async Task TheFilterListSaysWhichShowEachTypeIsIn()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, Ct);
        var filters = await host.GetJsonAsync<AuditFilters>("/api/audit/filters", cookie, Ct);

        IReadOnlyList<string> Of(string type) => filters.Types.Single(t => t.Value == type).Shows;

        Assert.Equal([AuditShow.Moderation], Of(FactType.MemberBanned));
        Assert.Equal([AuditShow.Presence], Of(FactType.InstanceJoined));
        Assert.Equal([AuditShow.Moderation, AuditShow.Discord], Of(FactType.DiscordMemberBanned));
        Assert.Empty(Of(FactType.MemberJoined));
    }

    [Fact]
    public async Task SomebodyWhoMayReadNeitherLogIsRefused()
    {
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(Ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewProfile, Ct);
        var response = await host.GetAsync($"/api/audit?person={Ada}", cookie, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
