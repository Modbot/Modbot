using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Cards;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// <c>/lookup</c> answers for the whole person, by either account; <c>/help</c> lists what the
/// caller can use; and names are suggested only to somebody the command would answer.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class WholePersonLookupTests
{
    private const string Guild = "800000000000000001";
    private const string Target = "usr_c9094d86-1846-43eb-b79d-7e3dc318f42a";
    private const string Member = "900000000000000777";
    private const string Caller = "100";

    private readonly PostgresFixture _db;

    public WholePersonLookupTests(PostgresFixture db) => _db = db;

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

    private static async Task<IReadOnlyList<DiscordSuggestion>> SuggestAsync(TestServices services, string caller, string typed, CancellationToken ct)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().SuggestAsync(
            new DiscordSuggestionAsk(caller, DiscordCommands.Lookup, DiscordCommands.LookupUserOption, typed, (_, _) => Task.CompletedTask),
            ct);
    }

    private static string? Field(DiscordEmbedContent card, string name)
        => card.Fields.FirstOrDefault(f => f.Name == name)?.Value;

    private static async Task AddMemberAsync(
        TestServices services, string userId, string name, Action<DiscordMember>? more = null, CancellationToken ct = default)
    {
        await using var db = services.Database.NewContext();
        var member = new DiscordMember
        {
            GuildId = Guild,
            UserId = userId,
            Username = name.ToLowerInvariant(),
            DisplayName = name,
            AvatarUrl = $"https://cdn.discordapp.com/avatars/{userId}/a.png",
            FirstSeenAt = services.Clock.UtcNow,
            UpdatedAt = services.Clock.UtcNow,
        };
        more?.Invoke(member);
        db.DiscordMembers.Add(member);
        await db.SaveChangesAsync(ct);
    }

    private static async Task LinkAsync(TestServices services, string discordUserId, string vrchatUserId, CancellationToken ct)
    {
        await using var db = services.Database.NewContext();
        db.DiscordAccountLinks.Add(new DiscordAccountLink
        {
            DiscordUserId = discordUserId,
            DiscordUsername = "jessie_q",
            VRChatUserId = vrchatUserId,
            LinkedAt = services.Clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    private static Task<long> DiscordFactAsync(TestServices services, string type, string subject, JsonObject? data = null, CancellationToken ct = default)
        => services.WriteFactAsync(new FactRecord
        {
            Type = type,
            OccurredAt = services.Clock.UtcNow,
            SubjectPlatform = FactPlatform.Discord,
            SubjectId = subject,
            Source = FactSource.Discord,
            Data = data,
        }, ct);

    private static Task<long> NoteAsync(TestServices services, FactPlatform platform, string subject, string text, CancellationToken ct)
        => services.WriteFactAsync(new FactRecord
        {
            Type = FactType.NoteAdded,
            OccurredAt = services.Clock.UtcNow,
            SubjectPlatform = platform,
            SubjectId = subject,
            Source = FactSource.Manual,
            Data = new JsonObject { ["text"] = text, ["description"] = text },
        }, ct);

    // ── By either account ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADiscordMemberWhoLinked_IsAnsweredWithTheirVRChatProfile_AndBothHistories()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.ViewProfile, ct: ct);

        await services.AddProfileAsync(Target, "jessie", ct: ct);
        await AddMemberAsync(services, Member, "Jessie Q", m => m.TimedOutUntil = services.Clock.UtcNow.AddHours(2), ct);
        await LinkAsync(services, Member, Target, ct);

        await services.WriteAuditFactAsync(FactType.GroupInstanceWarn, Target, at: services.Clock.UtcNow.AddDays(-2), ct: ct);
        await DiscordFactAsync(services, FactType.DiscordMemberTimedOut, Member, new JsonObject { ["reason"] = "Cool off" }, ct);
        await DiscordFactAsync(services, FactType.DiscordMemberTimedOut, Member, ct: ct);

        var reply = await HandleAsync(services, Call(Caller, DiscordCommands.Lookup, (DiscordCommands.LookupDiscordOption, Member)), ct);

        var card = Assert.Single(reply.Embeds);
        Assert.Equal("jessie", card.Title);
        Assert.Contains("Jessie Q", Field(card, "Discord"), StringComparison.Ordinal);
        Assert.Equal("0 · 0 · 1", Field(card, "Bans · kicks · warns"));
        Assert.Equal("0 · 0 · 2", Field(card, "Discord bans · kicks · timeouts"));
        Assert.Contains("Timed out on Discord until", Field(card, "Ban status"), StringComparison.Ordinal);

        var recent = Field(card, "Recent moderation events")!;
        Assert.Contains("Timed out on Discord", recent, StringComparison.Ordinal);
        Assert.Contains("“Cool off”", recent, StringComparison.Ordinal);
        Assert.Contains("Warned", recent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVRChatNameWhoLinked_ShowsTheirDiscordBanAndItsReason()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.ViewProfile, ct: ct);

        await services.AddProfileAsync(Target, "jessie", ct: ct);
        await LinkAsync(services, Member, Target, ct);

        await using (var db = services.Database.NewContext())
        {
            db.DiscordBans.Add(new DiscordBan
            {
                GuildId = Guild,
                UserId = Member,
                DisplayName = "Jessie Q",
                Reason = "Raiding",
                BannedAt = services.Clock.UtcNow.AddDays(-1),
                FirstSeenAt = services.Clock.UtcNow.AddDays(-1),
                UpdatedAt = services.Clock.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        await DiscordFactAsync(services, FactType.DiscordMemberBanned, Member, new JsonObject { ["reason"] = "Raiding" }, ct);

        var reply = await HandleAsync(services, Call(Caller, DiscordCommands.Lookup, ("user", "jessie")), ct);

        var card = Assert.Single(reply.Embeds);
        Assert.Equal(CardColour.Red, card.Color);
        Assert.Contains("Banned on Discord", Field(card, "Ban status"), StringComparison.Ordinal);
        Assert.Contains("Raiding", Field(card, "Ban status"), StringComparison.Ordinal);
        Assert.Equal("1 · 0 · 0", Field(card, "Discord bans · kicks · timeouts"));
        Assert.Contains("(not in the server)", Field(card, "Discord"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADiscordMemberWhoNeverLinked_GetsACardOfTheirOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.ConfigureAsync(s =>
        {
            s.DiscordGuildId = Guild;
            s.PublicAddress = "https://modbot.example.com";
        }, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.ViewProfile, ct: ct);
        await AddMemberAsync(services, Member, "Jessie Q", ct: ct);

        var reply = await HandleAsync(services, Call(Caller, DiscordCommands.Lookup, (DiscordCommands.LookupDiscordOption, Member)), ct);

        var card = Assert.Single(reply.Embeds);
        Assert.Equal("Jessie Q", card.Title);
        Assert.Equal($"https://modbot.example.com/discord/members?subject=discord-person%3A{Member}", card.Url);
        Assert.Equal($"https://cdn.discordapp.com/avatars/{Member}/a.png", card.ThumbnailUrl);
        Assert.Equal("Not linked", Field(card, "VRChat"));
        Assert.Null(Field(card, "Bans · kicks · warns"));
        Assert.Equal("Not banned on Discord", Field(card, "Ban status"));

        var fact = (await services.FactsOfTypeAsync(FactType.DiscordCommandRun, ct))[^1];
        Assert.Contains(Member, fact.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADiscordAccountModbotNeverSaw_IsSaidSo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.ConfigureAsync(s => s.DiscordGuildId = Guild, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.ViewProfile, ct: ct);

        var reply = await HandleAsync(services, Call(Caller, DiscordCommands.Lookup, (DiscordCommands.LookupDiscordOption, "900000000000000999")), ct);

        Assert.Equal("Modbot has no records of that Discord member.", reply.Text);
        Assert.Empty(reply.Embeds);
    }

    [Fact]
    public async Task BothOptionsOrNeither_AreAskedToPickOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.ViewProfile, ct: ct);

        var both = await HandleAsync(
            services,
            Call(Caller, DiscordCommands.Lookup, ("user", "jessie"), (DiscordCommands.LookupDiscordOption, Member)),
            ct);
        Assert.Equal("Pick a VRChat name or a Discord member, not both.", both.Text);

        var neither = await HandleAsync(services, Call(Caller, DiscordCommands.Lookup), ct);
        Assert.Equal("Pick a VRChat name or a Discord member.", neither.Text);
    }

    // ── What each caller may see ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Notes_AreShownOnlyToSomebodyWhoMayReadTheAuditLog_AndATakenBackNoteIsNotOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.ViewProfile, ct: ct);
        await services.LinkedAccountAsync("200", ModbotPermissions.ViewProfile | ModbotPermissions.ViewAuditLog, ct: ct);
        await services.AddProfileAsync(Target, "jessie", ct: ct);

        await NoteAsync(services, FactPlatform.VRChat, Target, "Keeps arguing in the lobby", ct);
        var withdrawn = await NoteAsync(services, FactPlatform.VRChat, Target, "Wrong person", ct);
        await services.WriteFactAsync(new FactRecord
        {
            Type = FactType.NoteTakenBack,
            OccurredAt = services.Clock.UtcNow,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = Target,
            Source = FactSource.Manual,
            Data = new JsonObject { ["noteFactId"] = withdrawn, ["text"] = "Wrong person" },
        }, ct);

        var without = Assert.Single((await HandleAsync(services, Call("100", DiscordCommands.Lookup, ("user", Target)), ct)).Embeds);
        Assert.Null(Field(without, "Notes"));
        Assert.DoesNotContain("lobby", Field(without, "Recent moderation events"), StringComparison.Ordinal);

        var with = Assert.Single((await HandleAsync(services, Call("200", DiscordCommands.Lookup, ("user", Target)), ct)).Embeds);
        Assert.Equal("1", Field(with, "Notes"));
        Assert.Contains("“Keeps arguing in the lobby”", Field(with, "Recent moderation events"), StringComparison.Ordinal);
        Assert.DoesNotContain("Wrong person", Field(with, "Recent moderation events"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JoinRequests_AreShownOnlyWithThePermissionForThem_AndFlagsToAnyoneWhoMayLookUp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.ViewProfile, ct: ct);
        await services.LinkedAccountAsync("200", ModbotPermissions.ViewProfile | ModbotPermissions.ViewJoinRequests, ct: ct);
        await services.AddProfileAsync(Target, "jessie", ct: ct);

        await services.WriteAuditFactAsync(FactType.JoinRequestCreated, Target, ct: ct);
        await services.WriteAuditFactAsync(FactType.JoinRequestCreated, Target, ct: ct);
        await services.WriteFactAsync(new FactRecord
        {
            Type = FactType.AutoModFlag,
            OccurredAt = services.Clock.UtcNow,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = Target,
            Source = FactSource.Modbot,
            Data = new JsonObject { ["reason"] = "Slur in the bio" },
        }, ct);

        var without = Assert.Single((await HandleAsync(services, Call("100", DiscordCommands.Lookup, ("user", Target)), ct)).Embeds);
        Assert.Null(Field(without, "Join requests"));
        Assert.Equal("1", Field(without, "Flags"));

        var with = Assert.Single((await HandleAsync(services, Call("200", DiscordCommands.Lookup, ("user", Target)), ct)).Embeds);
        Assert.Equal("2", Field(with, "Join requests"));
        Assert.Contains("Asked to join the group", Field(with, "Recent moderation events"), StringComparison.Ordinal);
    }

    // ── /help ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Help_ToAMemberWithNoAccount_ListsOnlyTheCommandsForEveryone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);

        var reply = await HandleAsync(services, Call("999", DiscordCommands.Help), ct);

        Assert.Contains("`/link`", reply.Text, StringComparison.Ordinal);
        Assert.Contains("`/help`", reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/lookup", reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/recent", reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/modbot", reply.Text, StringComparison.Ordinal);

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordCommandRun, ct));
        Assert.Contains("\"help\"", fact.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_ToAModerator_ListsTheStaffCommandsTheirPermissionsAllow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.ViewProfile, ct: ct);

        var reply = await HandleAsync(services, Call(Caller, DiscordCommands.Help), ct);

        Assert.Contains("`/lookup user: discord:` — Look up a person in Modbot's records", reply.Text, StringComparison.Ordinal);
        Assert.Contains("`/modbot`", reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/recent", reply.Text, StringComparison.Ordinal);
    }

    // ── Suggestions ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Names_AreSuggestedOnlyToSomebodyWhoMayLookPeopleUp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.ViewMembers, ct: ct);
        await services.LinkedAccountAsync("200", ModbotPermissions.ViewProfile, ct: ct);
        await services.AddProfileAsync("usr_a", "Sam One", ct: ct);
        await services.AddProfileAsync("usr_b", "Sam Two", ct: ct);
        await services.AddProfileAsync("usr_c", "Jessie", ct: ct);

        Assert.Empty(await SuggestAsync(services, "999", "sam", ct));
        Assert.Empty(await SuggestAsync(services, "100", "sam", ct));

        var suggested = await SuggestAsync(services, "200", "sam", ct);
        Assert.Equal(["usr_a", "usr_b"], suggested.Select(s => s.Value).Order(StringComparer.Ordinal));
        Assert.Contains(suggested, s => s.Name == "Sam One");

        Assert.Empty(await SuggestAsync(services, "200", "  ", ct));
    }

    // ── How the commands are registered ─────────────────────────────────────────────────────

    [Fact]
    public void TheStaffCommands_AreHiddenFromMembers_AndTheMemberCommandsAreNot()
    {
        var staff = DiscordCommands.All.Where(c => c.StaffOnly).Select(c => c.Name).Order(StringComparer.Ordinal);
        Assert.Equal([DiscordCommands.Lookup, DiscordCommands.Modbot, DiscordCommands.Recent], staff);

        foreach (var name in new[] { DiscordCommands.Link, DiscordCommands.Me, DiscordCommands.Help })
            Assert.False(DiscordCommands.All.Single(c => c.Name == name).StaffOnly);
    }

    [Fact]
    public void Lookup_TakesAVRChatNameWithSuggestions_OrADiscordMember()
    {
        var lookup = DiscordCommands.All.Single(c => c.Name == DiscordCommands.Lookup);

        var user = lookup.Options.Single(o => o.Name == DiscordCommands.LookupUserOption);
        Assert.Equal(DiscordOptionKind.Text, user.Kind);
        Assert.True(user.Suggests);
        Assert.False(user.Required);

        var discord = lookup.Options.Single(o => o.Name == DiscordCommands.LookupDiscordOption);
        Assert.Equal(DiscordOptionKind.Member, discord.Kind);
        Assert.False(discord.Required);
    }
}
