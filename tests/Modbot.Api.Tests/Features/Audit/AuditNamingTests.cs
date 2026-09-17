using System.Text.Json.Nodes;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Audit;
using Modbot.Api.Tests.Features.Places;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using static Modbot.Api.Tests.Features.Analytics.AnalyticsFacts;

namespace Modbot.Api.Tests.Features.Audit;

/// <summary>
/// A log that prints ids is a log nobody reads. These pin the names, the typed subject and the
/// instance a row links to.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AuditNamingTests
{
    private readonly PostgresFixture _db;

    public AuditNamingTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task APersonsRow_CarriesTheirStoredName_AndTheWorldsName()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var t = host.Clock.UtcNow.AddHours(-1);

        await PlacesFixtures.PersonAsync(host, "usr_a", "Ada", t, ct);
        await PlacesFixtures.WorldAsync(host, "wrld_a", "The Black Cat", t, ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_a", t, "wrld_a", "1"), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var page = await host.GetJsonAsync<AuditPage>("/api/audit?limit=10", cookie, ct);

        var entry = Assert.Single(page.Entries);
        Assert.Equal(SubjectKind.Person, entry.SubjectKind);
        Assert.Equal("Ada", entry.SubjectName);
        Assert.Equal("The Black Cat", entry.WorldName);
    }

    /// <summary>
    /// The subject of an instance event is the location string, not a person. Clicking it must
    /// open the instance, and the instance it opens is the one whose life the fact falls inside.
    /// </summary>
    [Fact]
    public async Task AnInstanceEvent_HasAnInstanceSubject_AndLinksToTheInstanceItHappenedIn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var monday = host.Clock.UtcNow.AddDays(-7);
        var tonight = host.Clock.UtcNow.AddHours(-2);

        await PlacesFixtures.WorldAsync(host, "wrld_a", "The Black Cat", monday, ct);
        await PlacesFixtures.InstanceAsync(host, "wrld_a", "39047", monday, monday.AddHours(1), monday.AddHours(1), ct);
        var tonights = await PlacesFixtures.InstanceAsync(host, "wrld_a", "39047", tonight, tonight.AddMinutes(30), null, ct);

        await host.WriteFactAsync(
            AuditFact(FactType.GroupInstanceCreated, "wrld_a:39047", tonight,
                actor: "usr_mod", actorName: "Mod", worldId: "wrld_a", instanceId: "39047"),
            ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var page = await host.GetJsonAsync<AuditPage>("/api/audit?limit=10", cookie, ct);

        var entry = Assert.Single(page.Entries);
        Assert.Equal(SubjectKind.Instance, entry.SubjectKind);
        Assert.Equal(tonights.Id, entry.ModbotInstanceId);

        // The sentence reads "The Black Cat #39047", so the world's name rides beside its id.
        Assert.Equal("wrld_a", entry.WorldId);
        Assert.Equal("39047", entry.InstanceId);
        Assert.Equal("The Black Cat", entry.WorldName);

        // The name recorded at the time is still the actor's name; the lookup only fills gaps.
        Assert.Equal("Mod", entry.ActorName);
    }

    /// <summary>
    /// An event Modbot had no name for when it was recorded keeps VRChat's own word, so a screen
    /// that learns the word later reads every old row correctly — with nothing rewritten.
    /// </summary>
    [Fact]
    public async Task AnUnrecognisedEvent_KeepsTheSourcesOwnWord()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        await host.WriteFactAsync(
            new FactRecord
            {
                Type = FactType.Unrecognised,
                TypeRaw = "group.member.something.new",
                OccurredAt = host.Clock.UtcNow.AddMinutes(-5),
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = "usr_a",
                Source = FactSource.AuditLog,
                Data = new JsonObject { ["eventType"] = "group.member.something.new" },
            },
            ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var page = await host.GetJsonAsync<AuditPage>("/api/audit?limit=10", cookie, ct);

        var entry = Assert.Single(page.Entries);
        Assert.Equal(FactType.Unrecognised, entry.Type);
        Assert.Equal("group.member.something.new", entry.TypeRaw);
    }
}
