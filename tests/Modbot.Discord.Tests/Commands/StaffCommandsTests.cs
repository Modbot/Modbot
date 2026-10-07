using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Users;
using Modbot.Discord.Cards;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// <c>/note</c>, <c>/watch</c> and <c>/live</c> (Discord commands design §4, step 2): who may run
/// them, the one-person rule, what each one writes, and what <c>/live</c> may show.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class StaffCommandsTests
{
    private const string Caller = "100";
    private const string Member = "555";
    private const string Vrchat = "usr_c9094d86-1846-43eb-b79d-7e3dc318f42a";
    private const string World = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";
    private const string Group = "grp_0a17232e-6ad4-4889-8e1e-6e0c5fa815fd";
    private const string Address = "https://modbot.example.com";

    private readonly PostgresFixture _db;

    public StaffCommandsTests(PostgresFixture db) => _db = db;

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

    private static async Task<IReadOnlyList<DiscordSuggestion>> SuggestAsync(
        TestServices services, string command, string option, string caller, string typed, CancellationToken ct)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().SuggestAsync(
            new DiscordSuggestionAsk(caller, command, option, typed, (_, _) => Task.CompletedTask),
            ct);
    }

    private static async Task<JsonElement> LastCommandFactAsync(TestServices services, CancellationToken ct)
    {
        var facts = await services.FactsOfTypeAsync(FactType.DiscordCommandRun, ct);
        return JsonDocument.Parse(facts[^1].Data).RootElement;
    }

    /// <summary>An enabled account with a Discord id proved and no VRChat account linked.</summary>
    private static async Task LinkedWithoutVRChatAsync(TestServices services, string discordUserId, ModbotPermissions permissions, CancellationToken ct)
    {
        await using var db = services.Database.NewContext();
        var user = await TestAccounts.CreateAsync(
            db, "user_" + Guid.NewGuid().ToString("n")[..8], TestAccounts.Password, permissions, linked: false, ct);
        user.DiscordUserId = discordUserId;
        user.DiscordUsername = "someone";
        user.DiscordVerifiedAt = services.Clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // ── What is registered ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TheThreeCommands_AreShownToModerators_OnByDefault_AndNeedTheWebAppsPermissions()
    {
        foreach (var name in new[] { DiscordCommands.Note, DiscordCommands.Watch, DiscordCommands.Live })
        {
            var command = Assert.Single(DiscordCommands.All, c => c.Name == name);
            Assert.Equal(DiscordShownTo.Moderators, command.ShownTo);
            Assert.Equal(DiscordReplyKind.Private, command.Reply);
            Assert.True(DiscordCommandSwitches.Find(name)?.OnByDefault);
            Assert.Contains(DiscordCommands.For(DiscordCommandSwitches.Empty), c => c.Name == name);
        }

        Assert.Equal(ModbotPermissions.WriteNotes, DiscordCommands.Requires(DiscordCommands.Note));
        Assert.Equal(ModbotPermissions.WriteNotes, DiscordCommands.Requires(DiscordCommands.Watch));
        Assert.Equal(ModbotPermissions.ViewLiveInstances, DiscordCommands.Requires(DiscordCommands.Live));
        Assert.Equal("Write notes", DiscordCommands.Label(ModbotPermissions.WriteNotes));
        Assert.Equal("See live instances", DiscordCommands.Label(ModbotPermissions.ViewLiveInstances));
    }

    [Fact]
    public void NoteAndWatch_TakeAMemberOrAVRChatPerson_WithSuggestionsForTheVRChatOne()
    {
        foreach (var name in new[] { DiscordCommands.Note, DiscordCommands.Watch })
        {
            var command = Assert.Single(DiscordCommands.All, c => c.Name == name);

            var member = command.Options.Single(o => o.Name == DiscordCommands.MemberOption);
            Assert.Equal(DiscordOptionKind.Member, member.Kind);
            Assert.False(member.Required);

            var vrchat = command.Options.Single(o => o.Name == DiscordCommands.VRChatOption);
            Assert.Equal(DiscordOptionKind.Text, vrchat.Kind);
            Assert.True(vrchat.Suggests);
            Assert.False(vrchat.Required);
        }

        Assert.Empty(DiscordCommands.All.Single(c => c.Name == DiscordCommands.Live).Options);
    }

    [Fact]
    public void TheNoteIsOneToTwoThousandCharacters_AndTheReasonOneToTwoHundred()
    {
        var text = DiscordCommands.All.Single(c => c.Name == DiscordCommands.Note).Options.Single(o => o.Name == DiscordCommands.NoteTextOption);
        Assert.True(text.Required);
        Assert.Equal(1, text.Min);
        Assert.Equal(2000, text.Max);

        var reason = DiscordCommands.All.Single(c => c.Name == DiscordCommands.Watch).Options.Single(o => o.Name == DiscordCommands.WatchReasonOption);
        Assert.True(reason.Required);
        Assert.Equal(1, reason.Min);
        Assert.Equal(200, reason.Max);
    }

    [Fact]
    public void TheWatchOffersHowLongAndAFollowUp_InTheDesignsWords()
    {
        var watch = DiscordCommands.All.Single(c => c.Name == DiscordCommands.Watch);

        var howLong = watch.Options.Single(o => o.Name == DiscordCommands.WatchForOption);
        Assert.Equal(DiscordOptionKind.Choice, howLong.Kind);
        Assert.True(howLong.Required);
        Assert.Equal(["1 day", "1 week", "30 days", "Until stopped"], howLong.Choices!.Select(c => c.Name));

        var followUp = watch.Options.Single(o => o.Name == DiscordCommands.WatchFollowUpOption);
        Assert.Equal(DiscordOptionKind.Choice, followUp.Kind);
        Assert.False(followUp.Required);
        Assert.Equal(["None", "Tomorrow", "In a week"], followUp.Choices!.Select(c => c.Name));
    }

    // ── Who may run them ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DiscordCommands.Note)]
    [InlineData(DiscordCommands.Watch)]
    [InlineData(DiscordCommands.Live)]
    public async Task AnUnlinkedCaller_IsToldToLink_AndNothingIsDone(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);

        var reply = await HandleAsync(services, Call("999", command, ("text", "x"), ("reason", "x"), ("member", Member)), ct);

        Assert.Equal(DiscordCommandHandler.NotLinkedMessage, reply.Text);
        Assert.Empty(services.Staff.Notes);
        Assert.Empty(services.Staff.Watches);

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordCommandRun, ct));
        Assert.Equal("not-linked", JsonDocument.Parse(fact.Data).RootElement.GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData(DiscordCommands.Note)]
    [InlineData(DiscordCommands.Watch)]
    [InlineData(DiscordCommands.Live)]
    public async Task ADisabledAccount_IsRefused(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.Administrator, disabled: true, ct: ct);

        var reply = await HandleAsync(services, Call(Caller, command, ("text", "x"), ("reason", "x"), ("member", Member)), ct);

        Assert.Equal("Your Modbot account is disabled.", reply.Text);
        Assert.Empty(services.Staff.Notes);
        Assert.Empty(services.Staff.Watches);
        Assert.Equal("disabled", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData(DiscordCommands.Note, "Write notes")]
    [InlineData(DiscordCommands.Watch, "Write notes")]
    [InlineData(DiscordCommands.Live, "See live instances")]
    public async Task WithoutThePermission_TheCommandIsRefusedByName_AndRecorded(string command, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var account = await services.LinkedAccountAsync(Caller, ModbotPermissions.ViewProfile | ModbotPermissions.ViewAuditLog, ct: ct);

        var reply = await HandleAsync(services, Call(Caller, command, ("text", "x"), ("reason", "x"), ("for", "7d"), ("member", Member)), ct);

        Assert.Equal($"You need the \"{label}\" permission in Modbot to use /{command}.", reply.Text);
        Assert.Empty(services.Staff.Notes);
        Assert.Empty(services.Staff.Watches);

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordCommandRun, ct));
        Assert.Equal(account.Id.ToString(), fact.ActorId);
        Assert.Equal("no-permission", JsonDocument.Parse(fact.Data).RootElement.GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData(DiscordCommands.Note)]
    [InlineData(DiscordCommands.Watch)]
    public async Task ACommandThatWrites_NeedsAVRChatLink_LikeTheWebApp(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await LinkedWithoutVRChatAsync(services, Caller, ModbotPermissions.WriteNotes, ct);

        var reply = await HandleAsync(
            services, Call(Caller, command, ("text", "x"), ("reason", "x"), ("for", "7d"), ("member", Member)), ct);

        Assert.Equal("Link your VRChat account in Modbot first.", reply.Text);
        Assert.Empty(services.Staff.Notes);
        Assert.Empty(services.Staff.Watches);
        Assert.Equal("no-vrchat", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Live_NeedsNoVRChatLink_BecauseItOnlyReads()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await LinkedWithoutVRChatAsync(services, Caller, ModbotPermissions.ViewLiveInstances, ct);

        var reply = await HandleAsync(services, Call(Caller, DiscordCommands.Live), ct);

        Assert.Equal(StaffCommands.NoGroupMessage, reply.Text);
        Assert.Equal("answered", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task AnAdministrator_MayRunAllThree()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.Administrator, ct: ct);

        var note = await HandleAsync(services, Call(Caller, DiscordCommands.Note, ("text", "hello"), ("member", Member)), ct);
        var watch = await HandleAsync(services, Call(Caller, DiscordCommands.Watch, ("reason", "why"), ("for", "7d"), ("member", Member)), ct);
        var live = await HandleAsync(services, Call(Caller, DiscordCommands.Live), ct);

        Assert.Equal(StaffCommands.NoteSavedMessage, note.Text);
        Assert.StartsWith("Watching", watch.Text, StringComparison.Ordinal);
        Assert.Equal(StaffCommands.NoGroupMessage, live.Text);
        Assert.Equal(3, (await services.FactsOfTypeAsync(FactType.DiscordCommandRun, ct)).Count);
    }

    [Theory]
    [InlineData(DiscordCommands.Note, "/note is turned off on this server.")]
    [InlineData(DiscordCommands.Watch, "/watch is turned off on this server.")]
    [InlineData(DiscordCommands.Live, "/live is turned off on this server.")]
    public async Task ASwitchedOffCommand_IsRefusedThroughRunAsync(string command, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.Administrator, ct: ct);
        await services.ConfigureAsync(s => s.SwitchCommand(command, false), ct);

        using var scope = services.Scope();
        var reply = await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>()
            .RunAsync(Call(Caller, command, ("text", "x"), ("member", Member)), ct);

        Assert.Equal(expected, reply?.Text);
        Assert.Empty(services.Staff.Notes);
        Assert.Equal("off", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());
    }

    // ── The member-or-VRChat rule ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DiscordCommands.Note)]
    [InlineData(DiscordCommands.Watch)]
    public async Task NamingNobody_IsRefused_AndNothingIsWritten(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.WriteNotes, ct: ct);

        var reply = await HandleAsync(services, Call(Caller, command, ("text", "x"), ("reason", "x"), ("for", "7d")), ct);

        Assert.Equal("Pick a VRChat name or a Discord member.", reply.Text);
        Assert.Empty(services.Staff.Notes);
        Assert.Empty(services.Staff.Watches);
        Assert.Equal("invalid", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData(DiscordCommands.Note)]
    [InlineData(DiscordCommands.Watch)]
    public async Task NamingBoth_IsRefused_AndNothingIsWritten(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.WriteNotes, ct: ct);
        await services.AddProfileAsync(Vrchat, "jessie", ct: ct);

        var reply = await HandleAsync(
            services, Call(Caller, command, ("text", "x"), ("reason", "x"), ("for", "7d"), ("member", Member), ("vrchat", Vrchat)), ct);

        Assert.Equal("Pick a VRChat name or a Discord member, not both.", reply.Text);
        Assert.Empty(services.Staff.Notes);
        Assert.Empty(services.Staff.Watches);
    }

    [Fact]
    public async Task ANameNobodyHas_IsSaidSo_AndASharedNameListsTheIds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.WriteNotes | ModbotPermissions.ViewProfile, ct: ct);
        await services.AddProfileAsync("usr_a", "Sam", ct: ct);
        await services.AddProfileAsync("usr_b", "Sam", ct: ct);

        var nobody = await HandleAsync(services, Call(Caller, DiscordCommands.Note, ("text", "x"), ("vrchat", "Nobody Here")), ct);
        Assert.StartsWith("Nobody in Modbot's records matches \"Nobody Here\".", nobody.Text, StringComparison.Ordinal);

        var several = await HandleAsync(services, Call(Caller, DiscordCommands.Note, ("text", "x"), ("vrchat", "Sam")), ct);
        Assert.StartsWith("Several people match \"Sam\".", several.Text, StringComparison.Ordinal);
        Assert.Contains("`usr_a`", several.Text, StringComparison.Ordinal);
        Assert.Contains("`usr_b`", several.Text, StringComparison.Ordinal);

        Assert.Empty(services.Staff.Notes);
    }

    // ── /note ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ANoteAboutAMember_IsSaved_WithALinkToTheirProfile_AndRecorded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var account = await services.LinkedAccountAsync(Caller, ModbotPermissions.WriteNotes, ct: ct);
        await services.ConfigureAsync(s => s.PublicAddress = Address, ct);

        var reply = await HandleAsync(services, Call(Caller, DiscordCommands.Note, ("text", "  Spam in voice.  "), ("member", Member)), ct);

        Assert.Equal("Note saved.", reply.Text);
        Assert.Empty(reply.Embeds);

        var link = Assert.Single(reply.Links!);
        Assert.Equal("Open in Modbot", link.Label);
        Assert.Equal(CardLink.UrlFor(CardSubject.DiscordPerson, Member, Address), link.Url);

        var note = Assert.Single(services.Staff.Notes);
        Assert.Equal(FactPlatform.Discord, note.Platform);
        Assert.Equal(Member, note.UserId);
        Assert.Equal("Spam in voice.", note.Text);
        Assert.Equal(account.Id, note.By.UserId);
        Assert.Equal(account.Username, note.By.Username);
        Assert.True(note.By.Held.HasFlag(ModbotPermissions.WriteNotes));

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordCommandRun, ct));
        Assert.Equal(Caller, fact.SubjectId);
        Assert.Equal(FactPlatform.Discord, fact.SubjectPlatform);
        Assert.Equal(account.Id.ToString(), fact.ActorId);

        var data = JsonDocument.Parse(fact.Data).RootElement;
        Assert.Equal("note", data.GetProperty("command").GetString());
        Assert.Equal("answered", data.GetProperty("outcome").GetString());
        Assert.Equal(Member, data.GetProperty("targetDiscord").GetString());
        Assert.False(data.TryGetProperty("target", out _));
    }

    [Fact]
    public async Task ANoteAboutAVRChatPerson_IsFoundByName_AndWrittenOnVRChat()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.WriteNotes | ModbotPermissions.ViewProfile, ct: ct);
        await services.AddProfileAsync(Vrchat, "jessie", ct: ct);
        await services.ConfigureAsync(s => s.PublicAddress = Address, ct);

        var reply = await HandleAsync(services, Call(Caller, DiscordCommands.Note, ("text", "Friendly."), ("vrchat", "jessie")), ct);

        Assert.Equal("Note saved.", reply.Text);
        Assert.Equal(CardLink.UrlFor(CardSubject.Person, Vrchat, Address), Assert.Single(reply.Links!).Url);

        var note = Assert.Single(services.Staff.Notes);
        Assert.Equal(FactPlatform.VRChat, note.Platform);
        Assert.Equal(Vrchat, note.UserId);

        var data = await LastCommandFactAsync(services, ct);
        Assert.Equal(Vrchat, data.GetProperty("target").GetString());
    }

    [Fact]
    public async Task ANoteWithNoPublicAddress_HasNoLink()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.WriteNotes, ct: ct);

        var reply = await HandleAsync(services, Call(Caller, DiscordCommands.Note, ("text", "Hi."), ("member", Member)), ct);

        Assert.Equal("Note saved.", reply.Text);
        Assert.Null(reply.Links);
    }

    [Fact]
    public async Task ANoteThatIsEmptyOrTooLong_IsRefused_AndOneOfTwoThousandIsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.WriteNotes, ct: ct);

        var empty = await HandleAsync(services, Call(Caller, DiscordCommands.Note, ("text", "   "), ("member", Member)), ct);
        Assert.Equal("A note needs something in it.", empty.Text);

        var tooLong = await HandleAsync(services, Call(Caller, DiscordCommands.Note, ("text", new string('x', 2001)), ("member", Member)), ct);
        Assert.Equal("That note is too long (at most 2,000 characters).", tooLong.Text);

        Assert.Empty(services.Staff.Notes);

        var biggest = await HandleAsync(services, Call(Caller, DiscordCommands.Note, ("text", new string('x', 2000)), ("member", Member)), ct);
        Assert.Equal("Note saved.", biggest.Text);
        Assert.Single(services.Staff.Notes);
    }

    // ── /watch ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1d", "none", 1, null)]
    [InlineData("7d", "tomorrow", 7, 1)]
    [InlineData("30d", "week", 30, 7)]
    [InlineData("until-stopped", "week", null, 7)]
    [InlineData("until-stopped", null, null, null)]
    public async Task AWatch_IsStartedWithItsReason_ItsEnd_AndItsFollowUp(string forHowLong, string? followUp, int? endsInDays, int? followUpInDays)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var account = await services.LinkedAccountAsync(Caller, ModbotPermissions.WriteNotes | ModbotPermissions.ViewProfile, ct: ct);
        await services.AddProfileAsync(Vrchat, "jessie", ct: ct);
        await services.ConfigureAsync(s => s.PublicAddress = Address, ct);
        var now = services.Clock.UtcNow;

        var options = new List<(string, string)>
        {
            ("reason", "  Keeps coming back after a kick.  "),
            ("for", forHowLong),
            ("vrchat", Vrchat),
        };

        if (followUp is not null)
            options.Add(("follow-up", followUp));

        var reply = await HandleAsync(services, Call(Caller, DiscordCommands.Watch, [.. options]), ct);

        var watch = Assert.Single(services.Staff.Watches);
        Assert.Equal(FactPlatform.VRChat, watch.Platform);
        Assert.Equal(Vrchat, watch.UserId);
        Assert.Equal("Keeps coming back after a kick.", watch.Reason);
        DateTimeOffset? expectedEnd = endsInDays is { } e ? now.AddDays(e) : null;
        DateTimeOffset? expectedFollowUp = followUpInDays is { } f ? now.AddDays(f) : null;
        Assert.Equal(expectedEnd, watch.EndsAt);
        Assert.Equal(expectedFollowUp, watch.FollowUpAt);
        Assert.Equal(account.Id, watch.By.UserId);

        Assert.StartsWith("Watching ", reply.Text, StringComparison.Ordinal);
        Assert.Contains("jessie", reply.Text, StringComparison.Ordinal);
        Assert.Equal(CardLink.UrlFor(CardSubject.Person, Vrchat, Address), Assert.Single(reply.Links!).Url);

        var data = await LastCommandFactAsync(services, ct);
        Assert.Equal("watch", data.GetProperty("command").GetString());
        Assert.Equal("answered", data.GetProperty("outcome").GetString());
        Assert.Equal(Vrchat, data.GetProperty("target").GetString());
    }

    [Fact]
    public async Task AWatchOnAMember_IsOnTheirDiscordAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.WriteNotes, ct: ct);

        await HandleAsync(services, Call(Caller, DiscordCommands.Watch, ("reason", "Raids."), ("for", "until-stopped"), ("member", Member)), ct);

        var watch = Assert.Single(services.Staff.Watches);
        Assert.Equal(FactPlatform.Discord, watch.Platform);
        Assert.Equal(Member, watch.UserId);
        Assert.Equal(Member, (await LastCommandFactAsync(services, ct)).GetProperty("targetDiscord").GetString());
    }

    [Fact]
    public async Task AWatchTheServiceRefuses_SaysWhy_AndIsRecordedAsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.WriteNotes, ct: ct);
        services.Staff.WatchRefusal = "Somebody is already watching this person.";

        var reply = await HandleAsync(services, Call(Caller, DiscordCommands.Watch, ("reason", "x"), ("for", "7d"), ("member", Member)), ct);

        Assert.Equal("Somebody is already watching this person.", reply.Text);
        Assert.Null(reply.Links);
        Assert.Empty(services.Staff.Watches);
        Assert.Equal("refused", (await LastCommandFactAsync(services, ct)).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task AWatchNeedsAReasonAndAHowLong()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.WriteNotes, ct: ct);

        var noReason = await HandleAsync(services, Call(Caller, DiscordCommands.Watch, ("reason", "  "), ("for", "7d"), ("member", Member)), ct);
        Assert.Equal("A watch needs a reason.", noReason.Text);

        var tooLong = await HandleAsync(services, Call(Caller, DiscordCommands.Watch, ("reason", new string('x', 201)), ("for", "7d"), ("member", Member)), ct);
        Assert.Equal("That reason is too long (at most 200 characters).", tooLong.Text);

        var noEnd = await HandleAsync(services, Call(Caller, DiscordCommands.Watch, ("reason", "x"), ("member", Member)), ct);
        Assert.Equal("Pick how long to watch.", noEnd.Text);

        Assert.Empty(services.Staff.Watches);
    }

    // ── /live ───────────────────────────────────────────────────────────────────────────────

    private static async Task<Guid> OpenInstanceAsync(
        TestServices services, string number, int people, string? name = null, CancellationToken ct = default, string world = World, int openedMinutesAgo = 90)
    {
        await using var db = services.Database.NewContext();

        var instance = new VRChatInstance
        {
            Id = Guid.CreateVersion7(),
            Location = $"{world}:{number}~group({Group})~groupAccessType(plus)~region(us)",
            WorldId = world,
            VRChatInstanceId = number,
            GroupId = Group,
            Type = "group",
            GroupAccessType = "plus",
            Region = "us",
            Name = name,
            OpenedAt = services.Clock.UtcNow.AddMinutes(-openedMinutesAgo),
            LastSeenAt = services.Clock.UtcNow,
            LastUserCount = people,
            PeakUserCount = people,
            SeenInGroupList = true,
        };

        db.VRChatInstances.Add(instance);
        await db.SaveChangesAsync(ct);
        return instance.Id;
    }

    private static async Task AddWorldAsync(TestServices services, string id, string name, int? capacity, CancellationToken ct)
    {
        await using var db = services.Database.NewContext();
        db.VRChatWorlds.Add(new VRChatWorld
        {
            WorldId = id,
            Name = name,
            Capacity = capacity,
            FirstSeenAt = services.Clock.UtcNow,
            LastSeenAt = services.Clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A moderator's paired client reporting the instance as watched, with these people there.</summary>
    private static async Task WatchedAsync(
        TestServices services, string number, CancellationToken ct, params (string Id, string Name)[] people)
    {
        Guid device;
        string moderator;
        var at = services.Clock.UtcNow;

        await using (var db = services.Database.NewContext())
        {
            var user = await TestAccounts.CreateAsync(
                db, "mod_" + Guid.NewGuid().ToString("n")[..8], TestAccounts.Password, ModbotPermissions.None, linked: true, ct);

            device = Guid.NewGuid();
            db.CompanionDevices.Add(new CompanionDeviceRecord
            {
                Id = device,
                TokenHash = Guid.NewGuid().ToString("n"),
                CompanionVersion = "2026.9.0",
                Platform = "windows",
                IssuedToUserId = user.Id,
                IssuedAt = at,
            });

            await db.SaveChangesAsync(ct);
            moderator = user.VRChatUserId!;
        }

        await services.WriteFactAsync(Presence(FactType.InstanceJoined, moderator, "Mod", number, at, device), ct);

        foreach (var (id, name) in people)
            await services.WriteFactAsync(Presence(FactType.InstancePresenceObserved, id, name, number, at, device), ct);
    }

    private static FactRecord Presence(string type, string subject, string name, string number, DateTimeOffset at, Guid device) => new()
    {
        Type = type,
        OccurredAt = at,
        SubjectPlatform = FactPlatform.VRChat,
        SubjectId = subject,
        WorldId = World,
        InstanceId = number,
        Source = FactSource.Companion,
        Data = new JsonObject { ["deviceId"] = device.ToString(), ["displayName"] = name },
    };

    private static async Task<TestServices> LiveServicesAsync(PostgresFixture db, CancellationToken ct)
    {
        var services = await TestServices.CreateAsync(db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.ViewLiveInstances, ct: ct);
        await services.ConfigureAsync(s =>
        {
            s.ManagedGroupId = Group;
            s.ManagedGroupName = "Pug Club";
            s.PublicAddress = Address;
        }, ct);
        return services;
    }

    [Fact]
    public async Task Live_WithNoGroup_OrNothingOpen_SaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.ViewLiveInstances, ct: ct);

        Assert.Equal("No group is set up in Modbot yet.", (await HandleAsync(services, Call(Caller, DiscordCommands.Live), ct)).Text);

        await services.ConfigureAsync(s => s.ManagedGroupId = Group, ct);

        var none = await HandleAsync(services, Call(Caller, DiscordCommands.Live), ct);
        Assert.Equal("No instances are open right now.", none.Text);
        Assert.Empty(none.Embeds);
    }

    [Fact]
    public async Task Live_ShowsACardPerOpenInstance_WithWorldCountAndOpeningTime_AndALinkToLive()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await LiveServicesAsync(_db, ct);
        await AddWorldAsync(services, World, "The Great Pug", 40, ct);
        await OpenInstanceAsync(services, "68681", 12, ct: ct);
        await OpenInstanceAsync(services, "70002", 0, name: "Movie night", ct: ct, world: "wrld_other", openedMinutesAgo: 60);

        var reply = await HandleAsync(services, Call(Caller, DiscordCommands.Live), ct);

        Assert.Equal(2, reply.Embeds.Count);

        var first = reply.Embeds[0];
        Assert.Equal("The Great Pug", first.Title);
        Assert.Equal("12/40", first.Fields.Single(f => f.Name == "People").Value);

        var opened = services.Clock.UtcNow.AddMinutes(-90).ToUnixTimeSeconds();
        Assert.Equal($"<t:{opened}:R>", first.Fields.Single(f => f.Name == "Opened").Value);

        // A world Modbot has not read is its id, with a count and no capacity.
        var second = reply.Embeds[1];
        Assert.Equal("wrld_other", second.Title);
        Assert.Equal("0", second.Fields.Single(f => f.Name == "People").Value);
        Assert.Equal("Movie night", second.Description);

        var link = Assert.Single(reply.Links!);
        Assert.Equal("Open Live", link.Label);
        Assert.Equal($"{Address}/live", link.Url);

        var data = await LastCommandFactAsync(services, ct);
        Assert.Equal("live", data.GetProperty("command").GetString());
        Assert.Equal("answered", data.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Live_ShowsNoFlagsAndNoWatchedTags_OnlyTheDesignsFields()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await LiveServicesAsync(_db, ct);
        await OpenInstanceAsync(services, "68681", 3, ct: ct);
        await WatchedAsync(services, "68681", ct, ("usr_ada", "Ada"));

        var reply = await HandleAsync(services, Call(Caller, DiscordCommands.Live), ct);

        var card = Assert.Single(reply.Embeds);
        Assert.All(card.Fields, f => Assert.Contains(f.Name, new[] { "People", "Opened", "Who is here" }));
        Assert.DoesNotContain("lag", string.Join(' ', card.Fields.Select(f => f.Name + f.Value)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Live_ShowsNoNames_WhileNobodyIsWatching()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await LiveServicesAsync(_db, ct);
        await OpenInstanceAsync(services, "68681", 3, ct: ct);

        var reply = await HandleAsync(services, Call(Caller, DiscordCommands.Live), ct);

        var card = Assert.Single(reply.Embeds);
        Assert.DoesNotContain(card.Fields, f => f.Name == "Who is here");
        Assert.Equal("3", card.Fields.Single(f => f.Name == "People").Value);
    }

    [Fact]
    public async Task Live_ListsWhoIsHere_OnlyWhileAModeratorsCompanionIsWatching_Escaped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await LiveServicesAsync(_db, ct);
        await OpenInstanceAsync(services, "68681", 3, ct: ct);
        await OpenInstanceAsync(services, "70002", 5, ct: ct);
        await WatchedAsync(services, "68681", ct, ("usr_ada", "Ada"), ("usr_bob", "**Bob**"));

        var reply = await HandleAsync(services, Call(Caller, DiscordCommands.Live), ct);

        // The watched instance lists its people; the other one, with the same world, does not.
        var watched = reply.Embeds.Single(c => c.Fields.Any(f => f.Name == "Who is here"));
        var lines = watched.Fields.Single(f => f.Name == "Who is here").Value.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Contains("Ada", lines);
        Assert.Contains("Mod", lines);
        Assert.Contains(@"\*\*Bob\*\*", lines);

        var unwatched = reply.Embeds.Single(c => c != watched);
        Assert.DoesNotContain(unwatched.Fields, f => f.Name == "Who is here");
    }

    [Fact]
    public async Task Live_ListsAtMostTwentyNames_ThenSaysHowManyMore()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await LiveServicesAsync(_db, ct);
        await OpenInstanceAsync(services, "68681", 30, ct: ct);

        var crowd = Enumerable.Range(1, 25).Select(i => ($"usr_{i:00}", $"Guest {i:00}")).ToArray();
        await WatchedAsync(services, "68681", ct, crowd);

        var reply = await HandleAsync(services, Call(Caller, DiscordCommands.Live), ct);

        var lines = Assert.Single(reply.Embeds).Fields.Single(f => f.Name == "Who is here").Value.Split('\n');

        // 25 guests and the moderator are in; twenty names, then the rest counted.
        Assert.Equal(21, lines.Length);
        Assert.Equal("and 6 more", lines[^1]);
        Assert.All(lines[..20], line => Assert.DoesNotContain("more", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Live_NeverGoesPastDiscordsLimits_OnABusyNight()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await LiveServicesAsync(_db, ct);

        for (var i = 0; i < 12; i++)
            await OpenInstanceAsync(services, (70000 + i).ToString(), 40, ct: ct);

        var reply = await HandleAsync(services, Call(Caller, DiscordCommands.Live), ct);

        Assert.Equal(StaffCommands.MostCards, reply.Embeds.Count);
        Assert.Equal("and 2 more open", reply.Text);
    }

    [Fact]
    public void WhenTheCardsAreTooLong_TheNamesGoFromTheLastOnesFirst_AndEveryCardKeepsItsCount()
    {
        var names = Enumerable.Range(1, 20).Select(i => (string?)new string((char)('a' + i % 26), 40)).ToList();

        var cards = Enumerable.Range(0, 10)
            .Select(i => StaffCommands.Card(
                new VRChatInstance { WorldId = "wrld_" + i, OpenedAt = DateTimeOffset.UnixEpoch, LastUserCount = 30 },
                null,
                names))
            .ToList();

        var fitted = StaffCommands.FitTogether(cards);

        Assert.Equal(10, fitted.Count);
        Assert.All(fitted, c => Assert.Contains(c.Fields, f => f.Name == "People"));
        Assert.Contains(fitted[0].Fields, f => f.Name == "Who is here");
        Assert.DoesNotContain(fitted[^1].Fields, f => f.Name == "Who is here");

        var total = fitted.Sum(c => c.Title.Length + (c.Footer?.Length ?? 0) + c.Fields.Sum(f => f.Name.Length + f.Value.Length));
        Assert.True(total <= StaffCommands.CardCharacterLimit, $"{total} characters");
    }

    [Fact]
    public void ACountTakenFromTheUnsureNumber_CarriesAQuestionMark()
    {
        var card = StaffCommands.Card(
            new VRChatInstance { WorldId = World, OpenedAt = DateTimeOffset.UnixEpoch, HeadCount = 80, HeadCountUnsure = true },
            new VRChatWorld { WorldId = World, Name = "Club", Capacity = 80 },
            names: null);

        Assert.Equal("80?/80", card.Fields.Single(f => f.Name == "People").Value);
    }

    // ── A caller who may write notes but not see profiles ──────────────────────────────────
    //
    // A VRChat name is a profile's: the web app shows it only with See profiles
    // (PersonSight.VRChatName). So /note and /watch must not become a way to search names.

    [Theory]
    [InlineData(DiscordCommands.Note)]
    [InlineData(DiscordCommands.Watch)]
    public async Task WithoutSeeProfiles_ANameANoMatchAndSeveralMatches_AreRefusedAlike_AndNameNobody(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.WriteNotes, ct: ct);
        await services.AddProfileAsync("usr_one", "Unique Jessie", ct: ct);
        await services.AddProfileAsync("usr_a", "Sam", ct: ct);
        await services.AddProfileAsync("usr_b", "Sam", ct: ct);

        var replies = new List<DiscordReply>();

        foreach (var query in new[] { "Unique Jessie", "Sam", "Nobody Here", "usr_unknown", "jessie" })
        {
            replies.Add(await HandleAsync(
                services, Call(Caller, command, ("text", "x"), ("reason", "x"), ("for", "7d"), ("vrchat", query)), ct));
        }

        // One sentence for all of them, and it quotes nothing back.
        Assert.All(replies, r => Assert.Equal(StaffCommands.NeedsAnIdMessage, r.Text));
        Assert.All(replies, r => Assert.Null(r.Links));
        Assert.Empty(services.Staff.Notes);
        Assert.Empty(services.Staff.Watches);

        var facts = await services.FactsOfTypeAsync(FactType.DiscordCommandRun, ct);
        Assert.All(facts, f => Assert.Equal("invalid", JsonDocument.Parse(f.Data).RootElement.GetProperty("outcome").GetString()));
    }

    [Theory]
    [InlineData(DiscordCommands.Note)]
    [InlineData(DiscordCommands.Watch)]
    public async Task WithoutSeeProfiles_AnExactId_IsAccepted_AndNoReplyNamesThem(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.WriteNotes, ct: ct);
        await services.AddProfileAsync(Vrchat, "Secret Display Name", ct: ct);
        await services.ConfigureAsync(s => s.PublicAddress = Address, ct);

        var reply = await HandleAsync(
            services, Call(Caller, command, ("text", "Hello."), ("reason", "Why."), ("for", "7d"), ("vrchat", Vrchat)), ct);

        Assert.Empty(reply.Embeds);
        Assert.DoesNotContain("Secret Display Name", reply.Text, StringComparison.Ordinal);
        Assert.Equal(CardLink.UrlFor(CardSubject.Person, Vrchat, Address), Assert.Single(reply.Links!).Url);

        if (command == DiscordCommands.Note)
            Assert.Equal(Vrchat, Assert.Single(services.Staff.Notes).UserId);
        else
            Assert.Equal(Vrchat, Assert.Single(services.Staff.Watches).UserId);
    }

    [Fact]
    public async Task WithSeeProfiles_ANameStillFindsThePerson_AndTheReplyNamesThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync(Caller, ModbotPermissions.WriteNotes | ModbotPermissions.ViewProfile, ct: ct);
        await services.AddProfileAsync(Vrchat, "Jessie Pug", ct: ct);
        await services.AddProfileAsync("usr_a", "Sam", ct: ct);
        await services.AddProfileAsync("usr_b", "Sam", ct: ct);

        var watch = await HandleAsync(
            services, Call(Caller, DiscordCommands.Watch, ("reason", "Why."), ("for", "7d"), ("vrchat", "Jessie Pug")), ct);
        Assert.Contains("Jessie Pug", watch.Text, StringComparison.Ordinal);

        var several = await HandleAsync(services, Call(Caller, DiscordCommands.Note, ("text", "x"), ("vrchat", "Sam")), ct);
        Assert.StartsWith("Several people match \"Sam\".", several.Text, StringComparison.Ordinal);

        var none = await HandleAsync(services, Call(Caller, DiscordCommands.Note, ("text", "x"), ("vrchat", "Nobody Here")), ct);
        Assert.StartsWith("Nobody in Modbot's records matches", none.Text, StringComparison.Ordinal);
    }

    // ── Suggestions ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DiscordCommands.Note)]
    [InlineData(DiscordCommands.Watch)]
    public async Task TheVRChatNames_AreSuggestedOnlyToCallersTheCommandWouldAnswer(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.AddProfileAsync("usr_a", "Sam One", ct: ct);
        await services.AddProfileAsync("usr_b", "Sam Two", ct: ct);

        await services.LinkedAccountAsync("200", ModbotPermissions.ViewProfile | ModbotPermissions.ViewAuditLog, ct: ct);
        await services.LinkedAccountAsync("300", ModbotPermissions.WriteNotes, disabled: true, ct: ct);
        await LinkedWithoutVRChatAsync(services, "400", ModbotPermissions.WriteNotes, ct);
        await services.LinkedAccountAsync("500", ModbotPermissions.WriteNotes | ModbotPermissions.ViewProfile, ct: ct);

        // Write notes without See profiles: a name is a profile's, so nothing.
        await services.LinkedAccountAsync("600", ModbotPermissions.WriteNotes, ct: ct);
        Assert.Empty(await SuggestAsync(services, command, DiscordCommands.VRChatOption, "600", "sam", ct));

        // Not linked at all, no permission, disabled, no VRChat link: nothing.
        Assert.Empty(await SuggestAsync(services, command, DiscordCommands.VRChatOption, "999", "sam", ct));
        Assert.Empty(await SuggestAsync(services, command, DiscordCommands.VRChatOption, "200", "sam", ct));
        Assert.Empty(await SuggestAsync(services, command, DiscordCommands.VRChatOption, "300", "sam", ct));
        Assert.Empty(await SuggestAsync(services, command, DiscordCommands.VRChatOption, "400", "sam", ct));

        var suggested = await SuggestAsync(services, command, DiscordCommands.VRChatOption, "500", "sam", ct);
        Assert.Equal(["usr_a", "usr_b"], suggested.Select(s => s.Value).Order(StringComparer.Ordinal));
        Assert.Contains(suggested, s => s.Name == "Sam One");

        // Nothing typed, and no other option, suggest nothing.
        Assert.Empty(await SuggestAsync(services, command, DiscordCommands.VRChatOption, "500", "  ", ct));
        Assert.Empty(await SuggestAsync(services, command, DiscordCommands.NoteTextOption, "500", "sam", ct));
        Assert.Empty(await SuggestAsync(services, command, DiscordCommands.MemberOption, "500", "sam", ct));
    }

    [Fact]
    public async Task Live_SuggestsNothing_AndSuggestingIsNeverRecorded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.AddProfileAsync("usr_a", "Sam One", ct: ct);
        await services.LinkedAccountAsync("500", ModbotPermissions.Administrator, ct: ct);

        Assert.Empty(await SuggestAsync(services, DiscordCommands.Live, DiscordCommands.VRChatOption, "500", "sam", ct));
        Assert.NotEmpty(await SuggestAsync(services, DiscordCommands.Note, DiscordCommands.VRChatOption, "500", "sam", ct));

        Assert.Empty(await services.FactsOfTypeAsync(FactType.DiscordCommandRun, ct));
    }

    // ── /help ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Help_ListsEachOfTheThree_OnlyToACallerWhoCouldRunIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        await services.LinkedAccountAsync("100", ModbotPermissions.WriteNotes, ct: ct);
        await services.LinkedAccountAsync("200", ModbotPermissions.ViewLiveInstances, ct: ct);
        await LinkedWithoutVRChatAsync(services, "300", ModbotPermissions.WriteNotes | ModbotPermissions.ViewLiveInstances, ct);

        var writer = (await HandleAsync(services, Call("100", DiscordCommands.Help), ct)).Text;
        Assert.Contains("`/note text: member: vrchat:`", writer, StringComparison.Ordinal);
        Assert.Contains("`/watch ", writer, StringComparison.Ordinal);
        Assert.DoesNotContain("/live", writer, StringComparison.Ordinal);

        var viewer = (await HandleAsync(services, Call("200", DiscordCommands.Help), ct)).Text;
        Assert.Contains("`/live` — Who is in the group's instances now", viewer, StringComparison.Ordinal);
        Assert.DoesNotContain("/note", viewer, StringComparison.Ordinal);
        Assert.DoesNotContain("/watch", viewer, StringComparison.Ordinal);

        // No VRChat link: the writing commands would refuse, so they are not listed.
        var unlinked = (await HandleAsync(services, Call("300", DiscordCommands.Help), ct)).Text;
        Assert.Contains("`/live`", unlinked, StringComparison.Ordinal);
        Assert.DoesNotContain("/note", unlinked, StringComparison.Ordinal);
        Assert.DoesNotContain("/watch", unlinked, StringComparison.Ordinal);
    }
}
