using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// Who may ask what: unlinked callers get one sentence, linked callers get exactly what the
/// equivalent web page would show them, and every ask is recorded.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordCommandHandlerTests
{
    private const string Target = "usr_c9094d86-1846-43eb-b79d-7e3dc318f42a";
    private const string Actor = "usr_2a323be9-ac4e-4502-af07-357d79c48ccf";

    private readonly PostgresFixture _db;

    public DiscordCommandHandlerTests(PostgresFixture db) => _db = db;

    private static DiscordCommandCall Call(string discordUserId, string command, params (string Name, string Value)[] options)
        => new(
            discordUserId,
            "someone",
            command,
            options.ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal),
            (_, _) => Task.CompletedTask);

    private static async Task<DiscordReply> HandleAsync(TestServices services, DiscordCommandCall call, CancellationToken ct)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().HandleAsync(call, ct);
    }

    private static async Task<JsonElement> LastCommandFactAsync(TestServices services, CancellationToken ct)
    {
        var facts = await services.FactsOfTypeAsync(FactType.DiscordCommandRun, ct);
        return JsonDocument.Parse(facts[^1].Data).RootElement;
    }

    [Fact]
    public async Task AnUnlinkedDiscordUser_IsToldToLink_AndNothingElse()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.AddProfileAsync(Target, "jessie", ct: ct);

        var reply = await HandleAsync(services, Call("999", DiscordCommands.Lookup, ("user", "jessie")), ct);

        Assert.Equal(DiscordCommandHandler.NotLinkedMessage, reply.Text);
        Assert.Empty(reply.Embeds);

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordCommandRun, ct));
        Assert.Equal("999", fact.SubjectId);
        Assert.Equal(FactPlatform.Discord, fact.SubjectPlatform);
        Assert.Null(fact.ActorId);
        var data = JsonDocument.Parse(fact.Data).RootElement;
        Assert.Equal("not-linked", data.GetProperty("outcome").GetString());
        Assert.Equal("lookup", data.GetProperty("command").GetString());
        Assert.False(data.TryGetProperty("target", out _));
    }

    [Fact]
    public async Task TheStatusCommand_IsAlsoWithheldFromUnlinkedUsers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);

        var reply = await HandleAsync(services, Call("999", DiscordCommands.Modbot), ct);

        Assert.Equal(DiscordCommandHandler.NotLinkedMessage, reply.Text);
    }

    [Fact]
    public async Task ALinkedUserWithoutSeeProfiles_IsRefusedLookup_ByName()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var account = await services.LinkedAccountAsync("100", ModbotPermissions.ViewMembers, ct: ct);

        var reply = await HandleAsync(services, Call("100", DiscordCommands.Lookup, ("user", Target)), ct);

        Assert.Equal("You need the \"See profiles\" permission in Modbot to use /lookup.", reply.Text);

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordCommandRun, ct));
        Assert.Equal(account.Id.ToString(), fact.ActorId);
        Assert.Equal(FactPlatform.Modbot, fact.ActorPlatform);
        Assert.Equal("no-permission", JsonDocument.Parse(fact.Data).RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task ALinkedUserWithSeeProfiles_GetsTheStoredProfile()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.ViewProfile, ct: ct);
        await services.ConfigureAsync(s => s.PublicAddress = "https://modbot.example.com", ct);

        var verifiedAt = services.Clock.UtcNow.AddDays(-30);
        await services.AddProfileAsync(Target, "jessie", u =>
        {
            u.Is18PlusVerified = true;
            u.Is18PlusVerifiedAt = verifiedAt;
            u.Is18PlusVerifiedSource = AgeVerificationSource.VRChat;
        }, ct);
        await services.AddProfileAsync(Actor, "E-Ray", ct: ct);

        await services.WriteAuditFactAsync(FactType.GroupInstanceWarn, Target, Actor, at: services.Clock.UtcNow.AddDays(-3), ct: ct);
        await services.WriteAuditFactAsync(FactType.GroupInstanceKick, Target, Actor, at: services.Clock.UtcNow.AddDays(-2), ct: ct);
        await services.WriteAuditFactAsync(FactType.MemberBanned, Target, Actor, at: services.Clock.UtcNow.AddDays(-1), ct: ct);

        var reply = await HandleAsync(services, Call("100", DiscordCommands.Lookup, ("user", Target)), ct);

        var card = Assert.Single(reply.Embeds);
        Assert.Equal("jessie", card.Title);
        Assert.Equal($"https://modbot.example.com/audit?subject={Target}", card.Url);

        Assert.Contains($"Yes, since <t:{verifiedAt.ToUnixTimeSeconds()}:d>", card.Fields.Single(f => f.Name == "18+ verified").Value, StringComparison.Ordinal);
        Assert.StartsWith("Banned", card.Fields.Single(f => f.Name == "Ban status").Value, StringComparison.Ordinal);
        Assert.Equal("1 · 1 · 1", card.Fields.Single(f => f.Name == "Bans · kicks · warns").Value);

        var recent = card.Fields.Single(f => f.Name == "Recent moderation events").Value;
        Assert.Contains("**Banned**", recent, StringComparison.Ordinal);
        Assert.Contains("**E-Ray**", recent, StringComparison.Ordinal);
        // Newest first.
        Assert.True(recent.IndexOf("Banned", StringComparison.Ordinal) < recent.IndexOf("Warned", StringComparison.Ordinal));

        var data = await LastCommandFactAsync(services, ct);
        Assert.Equal("answered", data.GetProperty("outcome").GetString());
        Assert.Equal(Target, data.GetProperty("target").GetString());
    }

    [Fact]
    public async Task Lookup_ByDisplayName_SearchesStoredProfilesOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.ViewProfile, ct: ct);
        await services.AddProfileAsync(Target, "Jessie Q", ct: ct);

        var exact = await HandleAsync(services, Call("100", DiscordCommands.Lookup, ("user", "jessie q")), ct);
        Assert.Equal("Jessie Q", Assert.Single(exact.Embeds).Title);

        var partial = await HandleAsync(services, Call("100", DiscordCommands.Lookup, ("user", "essie")), ct);
        Assert.Equal("Jessie Q", Assert.Single(partial.Embeds).Title);

        var nobody = await HandleAsync(services, Call("100", DiscordCommands.Lookup, ("user", "zzz")), ct);
        Assert.StartsWith("Nobody in Modbot's records matches", nobody.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lookup_WithSeveralMatches_ListsThem_AndAsksForTheId()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.ViewProfile, ct: ct);
        await services.AddProfileAsync("usr_a", "Sam One", ct: ct);
        await services.AddProfileAsync("usr_b", "Sam Two", ct: ct);

        var reply = await HandleAsync(services, Call("100", DiscordCommands.Lookup, ("user", "sam")), ct);

        Assert.Empty(reply.Embeds);
        Assert.StartsWith("Several people match", reply.Text, StringComparison.Ordinal);
        Assert.Contains("`usr_a`", reply.Text, StringComparison.Ordinal);
        Assert.Contains("`usr_b`", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lookup_OfAnIdKnownOnlyFromFacts_StillAnswers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.ViewProfile, ct: ct);
        await services.WriteAuditFactAsync(FactType.MemberKicked, "usr_legacy_no_profile", Actor, ct: ct);

        var reply = await HandleAsync(services, Call("100", DiscordCommands.Lookup, ("user", "usr_legacy_no_profile")), ct);

        var card = Assert.Single(reply.Embeds);
        Assert.Equal("usr_legacy_no_profile", card.Title);
        Assert.Contains("has not fetched the profile yet", card.Description, StringComparison.Ordinal);
        Assert.Equal("0 · 1 · 0", card.Fields.Single(f => f.Name == "Bans · kicks · warns").Value);
    }

    [Fact]
    public async Task Recent_NeedsSeeTheAuditLog_NotSeeProfiles()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.ViewProfile, ct: ct);
        await services.LinkedAccountAsync("200", ModbotPermissions.ViewAuditLog, ct: ct);

        await services.WriteAuditFactAsync(FactType.MemberBanned, Target, Actor, "E-Ray", at: services.Clock.UtcNow.AddMinutes(-2), ct: ct);
        await services.WriteAuditFactAsync(FactType.MemberJoined, "usr_x", at: services.Clock.UtcNow.AddMinutes(-1), ct: ct);
        await services.WriteAuditFactAsync(FactType.GroupInstanceKick, "usr_y", Actor, "E-Ray", ct: ct);

        var refused = await HandleAsync(services, Call("100", DiscordCommands.Recent), ct);
        Assert.Equal("You need the \"See the audit log\" permission in Modbot to use /recent.", refused.Text);

        var reply = await HandleAsync(services, Call("200", DiscordCommands.Recent, ("count", "1")), ct);
        var card = Assert.Single(reply.Embeds);
        Assert.Equal("The latest moderation event", card.Title);
        Assert.Contains("Kicked from an instance", card.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("Banned", card.Description, StringComparison.Ordinal);

        // A join is membership, not moderation, and never appears here.
        var all = await HandleAsync(services, Call("200", DiscordCommands.Recent), ct);
        Assert.Equal("The latest 2 moderation events", Assert.Single(all.Embeds).Title);
    }

    [Fact]
    public async Task Modbot_TellsALinkedUserTheStateAndTheWebAddress()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.None, ct: ct);
        await services.ConfigureAsync(s => s.PublicAddress = "https://modbot.example.com", ct);
        services.Status.Connected(services.Clock.UtcNow, 3);
        services.Status.LogChannel(true);

        var reply = await HandleAsync(services, Call("100", DiscordCommands.Modbot), ct);

        Assert.Contains("connected", reply.Text, StringComparison.Ordinal);
        Assert.Contains("3 slash commands", reply.Text, StringComparison.Ordinal);
        Assert.Contains("being posted to Discord channels", reply.Text, StringComparison.Ordinal);
        Assert.Contains("Web app: https://modbot.example.com", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAdministrator_MayUseEverything()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.Administrator, ct: ct);
        await services.AddProfileAsync(Target, "jessie", ct: ct);

        var lookup = await HandleAsync(services, Call("100", DiscordCommands.Lookup, ("user", Target)), ct);
        Assert.Single(lookup.Embeds);

        var recent = await HandleAsync(services, Call("100", DiscordCommands.Recent), ct);
        Assert.Equal("No moderation events are recorded yet.", recent.Text);
    }

    [Fact]
    public async Task ADisabledAccount_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.Administrator, disabled: true, ct: ct);

        var reply = await HandleAsync(services, Call("100", DiscordCommands.Modbot), ct);

        Assert.Equal("Your Modbot account is disabled.", reply.Text);
        Assert.Equal("disabled", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task AnUnknownCommand_IsRefused_NotCrashed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.Administrator, ct: ct);

        var reply = await HandleAsync(services, Call("100", "ban"), ct);

        Assert.Equal("Modbot does not know that command.", reply.Text);
    }
}
