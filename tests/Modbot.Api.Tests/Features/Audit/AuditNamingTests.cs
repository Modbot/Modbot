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
    /// An instance opened with a name is shown by that name rather than its number, so the row
    /// carries the name of the instance it matched -- and only that one: the same number on another
    /// evening, with another name, must not lend it.
    /// </summary>
    [Fact]
    public async Task AnInstanceWithAName_CarriesIt_FromTheInstanceItMatched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var monday = host.Clock.UtcNow.AddDays(-7);
        var tonight = host.Clock.UtcNow.AddHours(-2);

        await PlacesFixtures.WorldAsync(host, "wrld_a", "Murder 4", monday, ct);
        await PlacesFixtures.InstanceAsync(host, "wrld_a", "16354", monday, monday.AddHours(1), monday.AddHours(1), ct, name: "Monday murders");
        await PlacesFixtures.InstanceAsync(host, "wrld_a", "16354", tonight, tonight.AddMinutes(30), null, ct, name: "6 killed 7");

        await host.WriteFactAsync(PresenceFact(FactType.InstanceLeft, "usr_a", tonight.AddMinutes(10), "wrld_a", "16354"), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var page = await host.GetJsonAsync<AuditPage>("/api/audit?limit=10", cookie, ct);

        var entry = Assert.Single(page.Entries);
        Assert.Equal("6 killed 7", entry.InstanceName);
        Assert.Equal("16354", entry.InstanceId);
    }

    [Fact]
    public async Task AnInstanceWithNoName_OrNoMatch_CarriesNoName()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var tonight = host.Clock.UtcNow.AddHours(-2);

        await PlacesFixtures.WorldAsync(host, "wrld_a", "Murder 4", tonight, ct);
        await PlacesFixtures.InstanceAsync(host, "wrld_a", "16354", tonight, tonight.AddMinutes(30), null, ct);

        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_a", tonight.AddMinutes(5), "wrld_a", "16354"), ct);
        await host.WriteFactAsync(PresenceFact(FactType.InstanceJoined, "usr_b", tonight.AddMinutes(6), "wrld_a", "99999"), ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var page = await host.GetJsonAsync<AuditPage>("/api/audit?limit=10", cookie, ct);

        Assert.Equal(2, page.Entries.Count);
        Assert.All(page.Entries, e => Assert.Null(e.InstanceName));
    }

    /// <summary>
    /// A Modbot account that acted is named from the accounts, even when the fact kept no name for
    /// it. Its id is an account id, so looking it up among VRChat's people found nobody.
    /// </summary>
    [Fact]
    public async Task AModbotAccountThatActed_IsNamed_EvenWhenTheFactKeptNoName()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ReadSurfaceTestHost.StartAsync(_db);
        await host.ResetAsync(ct);

        var moderator = await host.CreateUserAsync("mira", "hunter2", ModbotPermissions.ViewAuditLog, ct);

        await host.WriteFactAsync(
            new FactRecord
            {
                Type = FactType.AutoModFlagConfirmed,
                OccurredAt = host.Clock.UtcNow.AddMinutes(-5),
                SubjectPlatform = FactPlatform.VRChat,
                SubjectId = "usr_a",
                ActorPlatform = FactPlatform.Modbot,
                ActorId = moderator.Id.ToString(),
                Source = FactSource.Modbot,
                Data = new JsonObject { ["ruleName"] = "Scams", ["username"] = "mira" },
            },
            ct);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewAuditLog, ct);
        var page = await host.GetJsonAsync<AuditPage>("/api/audit?limit=10", cookie, ct);

        Assert.Equal("mira", Assert.Single(page.Entries).ActorName);
    }

    /// <summary>
    /// A giveaway, a paused rule and an alert are not people, so their rows must not open a person
    /// popup on their ids; an operator's own AutoMod change is about their account.
    /// </summary>
    [Theory]
    [InlineData(FactType.GiveawayCreated, SubjectKind.Other)]
    [InlineData(FactType.GiveawayDrawn, SubjectKind.Other)]
    [InlineData(FactType.GiveawayPublishFailed, SubjectKind.Other)]
    [InlineData(FactType.GiveawayEntered, SubjectKind.Person)]
    [InlineData(FactType.AutoModRulePaused, SubjectKind.Other)]
    [InlineData(FactType.InsightAlert, SubjectKind.Other)]
    [InlineData(FactType.AutoModRuleChanged, SubjectKind.Account)]
    [InlineData(FactType.AiAcknowledged, SubjectKind.Account)]
    public void WhatAFactIsAbout_ComesFromItsType(string type, SubjectKind kind)
        => Assert.Equal(kind, FactSubjects.For(type));

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
