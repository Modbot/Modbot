using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Reviews;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Cards;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.ModerationLog;
using Modbot.Discord.Tests.Fakes;
using Modbot.Core.Discord;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// <c>/me</c> (Discord /me design): off unless switched on, only ever about the caller, never shows
/// what other people or moderators wrote, and "Ask to delete my data" opens one review per person,
/// at most twenty a day, and tells the alerts channel.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class MeCommandTests
{
    private const string Group = "grp_me_test";
    private const string Guild = "424242";
    private const string Caller = "7001";
    private const string CallerVRChat = "usr_7d9c1f0e-me-caller";
    private const string Other = "7002";
    private const string OtherVRChat = "usr_7d9c1f0e-me-other";
    private const string LinkedRole = "901";
    private const string SyncedRole = "902";
    private const string HandRole = "903";

    private readonly PostgresFixture _db;

    public MeCommandTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DiscordCommandCall Call(string discordUserId, string username = "kiri")
        => new(discordUserId, username, DiscordCommands.Me, new Dictionary<string, string>(), (_, _) => Task.CompletedTask);

    private static DiscordButtonPress Press(string discordUserId, string buttonId, string username = "kiri")
        => new(discordUserId, username, buttonId, (_, _) => Task.CompletedTask);

    private static async Task<DiscordReply> RunAsync(TestServices services, DiscordCommandCall call)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().HandleAsync(call, Ct);
    }

    private static async Task<DiscordReply> PressAsync(TestServices services, DiscordButtonPress press, IDiscordGateway? gateway = null)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordCommandHandler>().HandleButtonAsync(press, gateway, Ct);
    }

    private async Task<TestServices> ServicesAsync(bool on = true)
    {
        var services = await TestServices.CreateAsync(_db, Ct);
        await services.ConfigureAsync(s =>
        {
            s.SwitchCommand("me", on);
            s.DiscordGuildId = Guild;
            s.ManagedGroupId = Group;
            s.ManagedGroupName = "Kiri's Group";
        }, Ct);
        return services;
    }

    private static async Task LinkAsync(TestServices services, string discordUserId, string vrchatUserId, string name, string? linkedRoleId = null)
    {
        await using var db = services.Database.NewContext();
        db.DiscordAccountLinks.Add(new DiscordAccountLink
        {
            DiscordUserId = discordUserId,
            DiscordUsername = "kiri",
            VRChatUserId = vrchatUserId,
            VRChatDisplayName = name,
            LinkedAt = services.Clock.UtcNow.AddDays(-10),
            LinkedRoleId = linkedRoleId,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task JoinGroupAsync(TestServices services, string vrchatUserId, DateTimeOffset joined, string? notes = null)
    {
        await using var db = services.Database.NewContext();
        db.GroupMembers.Add(new GroupMember
        {
            GroupId = Group,
            UserId = vrchatUserId,
            JoinedAt = joined,
            ManagerNotes = notes,
            FirstSeenAt = joined,
            LastSeenAt = services.Clock.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task AddRoleAsync(TestServices services, string roleId, string name, int position)
    {
        await using var db = services.Database.NewContext();
        db.DiscordRoles.Add(new DiscordRole
        {
            RoleId = roleId,
            GuildId = Guild,
            Name = name,
            Position = position,
            BotCanAssign = true,
            FirstSeenAt = services.Clock.UtcNow,
            UpdatedAt = services.Clock.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task InServerAsync(TestServices services, string discordUserId, params string[] roles)
    {
        await using var db = services.Database.NewContext();
        db.DiscordMembers.Add(new DiscordMember
        {
            GuildId = Guild,
            UserId = discordUserId,
            Username = "kiri",
            DisplayName = "Kiri",
            Roles = JsonSerializer.Serialize(roles),
            FirstSeenAt = services.Clock.UtcNow,
            UpdatedAt = services.Clock.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task CopyRecordAsync(TestServices services, string discordUserId, string roleId, string kind, DateTimeOffset at)
    {
        await using var db = services.Database.NewContext();
        db.CopiedActions.Add(new CopiedAction
        {
            Direction = CopyDirections.ToDiscord,
            Kind = kind,
            SubjectId = discordUserId,
            RoleId = roleId,
            StartedAt = at,
            FinishedAt = at,
            Done = true,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task SetAlertsChannelAsync(TestServices services, string channelId)
    {
        await using var db = services.Database.NewContext();
        var row = await db.AlertSettings.FirstOrDefaultAsync(s => s.Id == 1, Ct);
        if (row is null)
        {
            row = new AlertSettings { Id = 1 };
            db.AlertSettings.Add(row);
        }

        row.DiscordChannelId = channelId;
        await db.SaveChangesAsync(Ct);
    }

    private static DiscordEmbedField Field(DiscordEmbedContent card, string name)
        => card.Fields.Single(f => f.Name == name);

    // ── Registration ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MeIsRegisteredOnlyWhileSwitchedOn()
    {
        Assert.DoesNotContain(DiscordCommands.For(DiscordCommandSwitches.Empty), c => c.Name == DiscordCommands.Me);
        Assert.Contains(DiscordCommands.For(DiscordCommandSwitches.With(null, DiscordCommands.Me, true)), c => c.Name == DiscordCommands.Me);
        Assert.Equal(DiscordCommands.All.Count - 1, DiscordCommands.For(DiscordCommandSwitches.Empty).Count);
        Assert.True(DiscordCommands.IsForEveryone(DiscordCommands.Me));
        Assert.Null(DiscordCommands.Requires(DiscordCommands.Me));
    }

    // ── The switch ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhileOff_MeAnswersNothingAboutThePerson_AndSaysItIsOff()
    {
        await using var services = await ServicesAsync(on: false);
        await LinkAsync(services, Caller, CallerVRChat, "Kiri");

        var reply = await RunAsync(services, Call(Caller));

        Assert.Equal(MeCommand.OffMessage, reply.Text);
        Assert.Empty(reply.Embeds);
        Assert.Null(reply.Actions);

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct));
        Assert.Equal("off", JsonDocument.Parse(fact.Data).RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task WhileOff_TheButtonsDoNothing()
    {
        await using var services = await ServicesAsync(on: false);

        Assert.Equal(MeCommand.OffMessage, (await PressAsync(services, Press(Caller, MeCommand.KeepsButton))).Text);
        Assert.Equal(MeCommand.OffMessage, (await PressAsync(services, Press(Caller, MeCommand.DeleteButton))).Text);

        await using var db = services.Database.NewContext();
        Assert.False(await db.Reviews.AnyAsync(Ct));
    }

    [Fact]
    public async Task InADemo_MeIsOff_EvenWithTheSwitchOn()
    {
        await using var services = await ServicesAsync(on: true);

        var demo = new DemoMode { Requested = true };
        Assert.True(demo.Decide(hasStaffAccount: false, onboardingComplete: false, holdsDemoData: true));

        using var scope = services.Scope();
        var me = new MeCommand(
            scope.ServiceProvider.GetRequiredService<ModbotContext>(),
            scope.ServiceProvider.GetRequiredService<IFactWriter>(),
            scope.ServiceProvider.GetRequiredService<EventPartitionMaintainer>(),
            scope.ServiceProvider.GetRequiredService<ReviewFacts>(),
            services.Clock,
            new MemberCommandLimits(),
            demo);

        Assert.False(await me.IsOnAsync(Ct));
    }

    // ── What the card shows ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnlinkedMember_SeesNotLinked_ALinkButton_AndBothButtons()
    {
        await using var services = await ServicesAsync();
        await services.ConfigureAsync(s =>
        {
            s.PublicAddress = "https://modbot.example.com";
            s.DiscordOAuthClientId = "1234567890";
            s.DiscordOAuthClientSecretEncrypted = services.Protector.Protect("secret");
        }, Ct);

        var reply = await RunAsync(services, Call(Caller));

        var card = Assert.Single(reply.Embeds);
        Assert.Equal("Kiri's Group", card.Title);
        Assert.Equal("Not linked", Field(card, "VRChat").Value);
        Assert.Equal("kiri", Field(card, "Discord").Value);
        Assert.DoesNotContain(card.Fields, f => f.Name == "Member since");
        Assert.Equal("None", Field(card, "Roles from Modbot").Value);
        Assert.Equal("Good", Field(card, "Standing").Value);

        var link = Assert.Single(reply.Links!);
        Assert.Equal(MeCommand.LinkLabel, link.Label);
        Assert.Equal("https://modbot.example.com/link", link.Url);

        Assert.Equal(
            new[]
            {
                (MeCommand.KeepsLabel, MeCommand.KeepsButton),
                (MeCommand.DeleteLabel, MeCommand.DeleteButton),
                (MeCommand.InvitesOnLabel, MeCommand.InvitesOnButton),
            },
            reply.Actions!.Select(a => (a.Label, a.Id)));

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct));
        Assert.Equal(Caller, fact.SubjectId);
        Assert.Equal("answered", JsonDocument.Parse(fact.Data).RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task WithoutLinkingSetUp_ThereIsNoLinkButton()
    {
        await using var services = await ServicesAsync();

        var reply = await RunAsync(services, Call(Caller));

        Assert.Null(reply.Links);
    }

    [Fact]
    public async Task ALinkedMember_SeesTheirVRChatName_WhenTheyJoined_AndTheRolesModbotGave()
    {
        await using var services = await ServicesAsync();
        var joined = new DateTimeOffset(2026, 3, 3, 18, 0, 0, TimeSpan.Zero);

        await LinkAsync(services, Caller, CallerVRChat, "Kiri", linkedRoleId: LinkedRole);
        await services.AddProfileAsync(CallerVRChat, "Kiri", ct: Ct);
        await JoinGroupAsync(services, CallerVRChat, joined);
        await AddRoleAsync(services, LinkedRole, "Linked", position: 1);
        await AddRoleAsync(services, SyncedRole, "Staff", position: 5);
        await AddRoleAsync(services, HandRole, "Helper", position: 3);
        await InServerAsync(services, Caller, LinkedRole, SyncedRole, HandRole);
        await CopyRecordAsync(services, Caller, SyncedRole, CopyKinds.RoleGiven, services.Clock.UtcNow.AddDays(-2));

        var reply = await RunAsync(services, Call(Caller));

        var card = Assert.Single(reply.Embeds);
        Assert.Equal("Kiri (linked)", Field(card, "VRChat").Value);
        Assert.Equal(DiscordTime.LongDay(joined), Field(card, "Member since").Value);

        // Highest first, and the role somebody gave by hand is not Modbot's.
        Assert.Equal("Staff, Linked", Field(card, "Roles from Modbot").Value);
        Assert.Equal("Good", Field(card, "Standing").Value);
        Assert.Equal(CardColour.Violet, card.Color);
        Assert.Null(reply.Links);
    }

    [Fact]
    public async Task ARoleSyncRoleTakenBack_IsNotListed()
    {
        await using var services = await ServicesAsync();
        await LinkAsync(services, Caller, CallerVRChat, "Kiri");
        await AddRoleAsync(services, SyncedRole, "Staff", position: 5);
        await InServerAsync(services, Caller, SyncedRole);
        await CopyRecordAsync(services, Caller, SyncedRole, CopyKinds.RoleGiven, services.Clock.UtcNow.AddDays(-3));
        await CopyRecordAsync(services, Caller, SyncedRole, CopyKinds.RoleTaken, services.Clock.UtcNow.AddDays(-1));

        var card = Assert.Single((await RunAsync(services, Call(Caller))).Embeds);

        Assert.Equal("None", Field(card, "Roles from Modbot").Value);
    }

    [Fact]
    public async Task AnEndedLink_DoesNotCount()
    {
        await using var services = await ServicesAsync();

        await using (var db = services.Database.NewContext())
        {
            db.DiscordAccountLinks.Add(new DiscordAccountLink
            {
                DiscordUserId = Caller,
                DiscordUsername = "kiri",
                VRChatUserId = CallerVRChat,
                VRChatDisplayName = "Kiri",
                LinkedAt = services.Clock.UtcNow.AddDays(-10),
                UnlinkedAt = services.Clock.UtcNow.AddDays(-1),
                UnlinkedBy = LinkEndedBy.Member,
            });
            await db.SaveChangesAsync(Ct);
        }

        var card = Assert.Single((await RunAsync(services, Call(Caller))).Embeds);

        Assert.Equal("Not linked", Field(card, "VRChat").Value);
    }

    [Fact]
    public async Task ALinkedAccountThatLeftTheGroup_SaysNotInTheGroup()
    {
        await using var services = await ServicesAsync();
        await LinkAsync(services, Caller, CallerVRChat, "Kiri");

        var card = Assert.Single((await RunAsync(services, Call(Caller))).Embeds);

        Assert.Equal("Not in the group", Field(card, "Member since").Value);
    }

    [Fact]
    public async Task BannedFromTheGroup_ReadsBanned()
    {
        await using var services = await ServicesAsync();
        await LinkAsync(services, Caller, CallerVRChat, "Kiri");

        await using (var db = services.Database.NewContext())
        {
            db.GroupBans.Add(new GroupBan
            {
                GroupId = Group,
                UserId = CallerVRChat,
                BannedAt = services.Clock.UtcNow.AddDays(-1),
                FirstSeenAt = services.Clock.UtcNow.AddDays(-1),
                LastSeenAt = services.Clock.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        var card = Assert.Single((await RunAsync(services, Call(Caller))).Embeds);

        Assert.Equal("Banned", Field(card, "Standing").Value);
        Assert.Equal(CardColour.Red, card.Color);
    }

    [Fact]
    public async Task BannedFromTheDiscordServer_ReadsBanned_AndALiftedBanDoesNot()
    {
        await using var services = await ServicesAsync();

        await using (var db = services.Database.NewContext())
        {
            db.DiscordBans.Add(new DiscordBan
            {
                GuildId = Guild,
                UserId = Caller,
                Reason = "posted slurs",
                FirstSeenAt = services.Clock.UtcNow.AddDays(-1),
                UpdatedAt = services.Clock.UtcNow,
            });
            db.DiscordBans.Add(new DiscordBan
            {
                GuildId = Guild,
                UserId = Other,
                FirstSeenAt = services.Clock.UtcNow.AddDays(-5),
                LiftedAt = services.Clock.UtcNow.AddDays(-1),
                UpdatedAt = services.Clock.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        var banned = Assert.Single((await RunAsync(services, Call(Caller))).Embeds);
        Assert.Equal("Banned", Field(banned, "Standing").Value);

        var lifted = Assert.Single((await RunAsync(services, Call(Other))).Embeds);
        Assert.Equal("Good", Field(lifted, "Standing").Value);
    }

    [Fact]
    public async Task TheCardNeverShowsOtherPeople_ModeratorText_OrBanReasons()
    {
        await using var services = await ServicesAsync();
        await LinkAsync(services, Caller, CallerVRChat, "Kiri");
        await services.AddProfileAsync(CallerVRChat, "Kiri", ct: Ct);
        await JoinGroupAsync(services, CallerVRChat, services.Clock.UtcNow.AddDays(-30), notes: "moderator note: watch this one");

        // Somebody else, linked, banned and in the group: none of it may reach the caller.
        await LinkAsync(services, Other, OtherVRChat, "Somebody Else");
        await services.AddProfileAsync(OtherVRChat, "Somebody Else", ct: Ct);
        await JoinGroupAsync(services, OtherVRChat, services.Clock.UtcNow.AddDays(-60));

        await using (var db = services.Database.NewContext())
        {
            db.DiscordBans.Add(new DiscordBan
            {
                GuildId = Guild,
                UserId = Other,
                Reason = "secret ban reason",
                FirstSeenAt = services.Clock.UtcNow,
                UpdatedAt = services.Clock.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        await services.WriteAuditFactAsync(FactType.MemberBanned, CallerVRChat, OtherVRChat, "Somebody Else", "a moderator's reason", ct: Ct);

        var reply = await RunAsync(services, Call(Caller));
        var card = Assert.Single(reply.Embeds);

        // Exactly the five labels, nothing else.
        Assert.Equal(["VRChat", "Discord", "Member since", "Roles from Modbot", "Standing", "Event invites"], card.Fields.Select(f => f.Name));

        var everything = string.Join('\n', [card.Title, card.Description ?? string.Empty, .. card.Fields.SelectMany(f => new[] { f.Name, f.Value })]);
        Assert.DoesNotContain("Somebody Else", everything, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherVRChat, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("moderator note", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("secret ban reason", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("a moderator's reason", everything, StringComparison.Ordinal);
        Assert.Equal("Good", Field(card, "Standing").Value);
    }

    // ── Limits ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OverTheLimit_MeIsRefused_AndNotRecorded()
    {
        await using var services = await ServicesAsync();

        for (var i = 0; i < MemberCommandLimits.PerMinute; i++)
            Assert.Single((await RunAsync(services, Call(Caller))).Embeds);

        var refused = await RunAsync(services, Call(Caller));
        Assert.Equal(MeCommand.TooFastMessage, refused.Text);
        Assert.Equal(MemberCommandLimits.PerMinute, (await services.FactsOfTypeAsync(FactType.DiscordCommandRun, Ct)).Count);

        // Somebody else is not held up by it, and a minute later the caller is not either.
        Assert.Single((await RunAsync(services, Call(Other))).Embeds);
        services.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Single((await RunAsync(services, Call(Caller))).Embeds);
    }

    [Fact]
    public void TheLimiter_CountsOnlyAllowedUses()
    {
        var limits = new MemberCommandLimits();
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < MemberCommandLimits.PerMinute; i++)
            Assert.True(limits.TryUse("1", now.AddSeconds(i)));

        Assert.False(limits.TryUse("1", now.AddSeconds(30)));
        Assert.False(limits.TryUse("1", now.AddSeconds(59)));

        // The first use has aged out, and refused ones never counted.
        Assert.True(limits.TryUse("1", now.AddSeconds(60)));
        Assert.True(limits.TryUse("2", now.AddSeconds(30)));
    }

    // ── What Modbot keeps ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhatModbotKeeps_IsTheFixedList()
    {
        await using var services = await ServicesAsync();

        var reply = await PressAsync(services, Press(Caller, MeCommand.KeepsButton));

        var card = Assert.Single(reply.Embeds);
        Assert.Equal(WhatModbotKeeps.Title, card.Title);
        Assert.Equal("What Modbot can keep", card.Title);
        Assert.Null(card.Description);
        Assert.Equal(WhatModbotKeeps.Kinds.Select(k => k.Heading), card.Fields.Select(f => f.Name));

        foreach (var kind in WhatModbotKeeps.Kinds)
        {
            var value = Field(card, kind.Heading).Value;
            Assert.All(kind.Lines, line => Assert.Contains("• " + line, value, StringComparison.Ordinal));
            Assert.True(value.Length <= 1024);
        }

        Assert.Empty(await services.FactsOfTypeAsync(FactType.DataDeletionAsked, Ct));
    }

    [Fact]
    public async Task AnUnknownButton_IsNotAnswered()
    {
        await using var services = await ServicesAsync();

        var reply = await PressAsync(services, Press(Caller, DiscordActionButton.Prefix + "something-else"));

        Assert.Equal("Modbot does not know that button.", reply.Text);
    }

    // ── Ask to delete my data ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AskingToDelete_OpensAReview_RecordsIt_AndTellsTheAlertsChannel()
    {
        await using var services = await ServicesAsync();
        await services.ConfigureAsync(s => s.PublicAddress = "https://modbot.example.com", Ct);
        await LinkAsync(services, Caller, CallerVRChat, "Kiri");
        await services.AddProfileAsync(CallerVRChat, "Kiri", ct: Ct);
        await SetAlertsChannelAsync(services, "5550001");

        var gateway = new FakeGateway { State = DiscordGatewayState.Ready };

        var reply = await PressAsync(services, Press(Caller, MeCommand.DeleteButton, "kiri"), gateway);

        Assert.Equal(MeCommand.SentMessage, reply.Text);

        await using var db = services.Database.NewContext();
        var review = Assert.Single(await db.Reviews.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(ReviewSignal.DataDeletion, review.Signal);
        Assert.Equal(FactPlatform.Discord, review.ModeratorPlatform);
        Assert.Equal(Caller, review.ModeratorId);
        Assert.Equal(Caller, review.About);
        Assert.Equal(ReviewState.Open, review.State);
        Assert.Equal("Kiri asked for their data to be deleted.", review.Summary);

        var evidence = JsonDocument.Parse(review.Evidence).RootElement;
        Assert.Equal(Caller, evidence.GetProperty("discordUserId").GetString());
        Assert.Equal("kiri", evidence.GetProperty("discordUsername").GetString());
        Assert.Equal(CallerVRChat, evidence.GetProperty("vrchatUserId").GetString());
        Assert.Equal("Kiri", evidence.GetProperty("vrchatDisplayName").GetString());

        var asked = Assert.Single(await services.FactsOfTypeAsync(FactType.DataDeletionAsked, Ct));
        Assert.Equal(FactPlatform.Discord, asked.SubjectPlatform);
        Assert.Equal(Caller, asked.SubjectId);
        Assert.Null(asked.ActorId);
        Assert.Equal(review.Id.ToString(), JsonDocument.Parse(asked.Data).RootElement.GetProperty("reviewId").GetString());

        var opened = Assert.Single(await services.FactsOfTypeAsync(FactType.ReviewOpened, Ct));
        Assert.Equal(Caller, opened.SubjectId);

        var message = Assert.Single(gateway.Messages);
        Assert.Equal("5550001", message.ChannelId);
        Assert.Equal("**Kiri** (Discord: kiri) asked the staff to delete their data.", message.Text);
        var button = Assert.Single(message.Links);
        Assert.Equal($"https://modbot.example.com/reviews?review={review.Id}", button.Url);
    }

    [Fact]
    public async Task WithNoAlertsChannel_TheRequestStillOpensAReview()
    {
        await using var services = await ServicesAsync();
        var gateway = new FakeGateway { State = DiscordGatewayState.Ready };

        var reply = await PressAsync(services, Press(Caller, MeCommand.DeleteButton), gateway);

        Assert.Equal(MeCommand.SentMessage, reply.Text);
        Assert.Empty(gateway.Messages);

        await using var db = services.Database.NewContext();
        var review = Assert.Single(await db.Reviews.AsNoTracking().ToListAsync(Ct));
        Assert.Equal("kiri asked for their data to be deleted.", review.Summary);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(review.Evidence).RootElement.GetProperty("vrchatUserId").ValueKind);
    }

    [Fact]
    public async Task WhenTheAlertsChannelRefuses_TheMemberIsStillToldItWasSent()
    {
        await using var services = await ServicesAsync();
        await SetAlertsChannelAsync(services, "5550001");

        var gateway = new FakeGateway { State = DiscordGatewayState.Ready };
        gateway.FailNextPost("Missing Access", permanent: true);

        var reply = await PressAsync(services, Press(Caller, MeCommand.DeleteButton), gateway);

        Assert.Equal(MeCommand.SentMessage, reply.Text);
        Assert.Empty(gateway.Messages);

        await using var db = services.Database.NewContext();
        Assert.Single(await db.Reviews.AsNoTracking().ToListAsync(Ct));
        Assert.Single(await services.FactsOfTypeAsync(FactType.DataDeletionAsked, Ct));
    }

    [Fact]
    public async Task ASecondRequest_WhileTheFirstIsOpen_IsAlreadyAsked()
    {
        await using var services = await ServicesAsync();

        Assert.Equal(MeCommand.SentMessage, (await PressAsync(services, Press(Caller, MeCommand.DeleteButton))).Text);
        Assert.Equal(MeCommand.AlreadyAskedMessage, (await PressAsync(services, Press(Caller, MeCommand.DeleteButton))).Text);

        await using (var db = services.Database.NewContext())
        {
            Assert.Equal(1, await db.Reviews.CountAsync(Ct));

            // Once the staff close it, the member may ask again.
            var review = await db.Reviews.SingleAsync(Ct);
            review.State = ReviewState.Closed;
            review.ClosedAt = services.Clock.UtcNow;
            review.Note = "Purged.";
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(MeCommand.SentMessage, (await PressAsync(services, Press(Caller, MeCommand.DeleteButton))).Text);
        Assert.Equal(2, (await services.FactsOfTypeAsync(FactType.DataDeletionAsked, Ct)).Count);
    }

    [Fact]
    public async Task TheServerTakesAtMostTwentyRequestsInAny24Hours()
    {
        await using var services = await ServicesAsync();

        await using (var db = services.Database.NewContext())
        {
            for (var i = 0; i < MeCommand.DailyCap; i++)
            {
                var id = (8000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
                db.Reviews.Add(new Review
                {
                    ModeratorPlatform = FactPlatform.Discord,
                    ModeratorId = id,
                    Signal = ReviewSignal.DataDeletion,
                    About = id,
                    WindowStart = services.Clock.UtcNow.AddHours(-1),
                    WindowEnd = services.Clock.UtcNow.AddHours(-1),
                    Summary = "asked",
                    OpenedAt = services.Clock.UtcNow.AddHours(-1),
                    UpdatedAt = services.Clock.UtcNow.AddHours(-1),
                });
            }

            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(MeCommand.CapMessage, (await PressAsync(services, Press(Caller, MeCommand.DeleteButton))).Text);
        Assert.Empty(await services.FactsOfTypeAsync(FactType.DataDeletionAsked, Ct));

        // 24 hours on, the old ones no longer count.
        services.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(MeCommand.SentMessage, (await PressAsync(services, Press(Caller, MeCommand.DeleteButton))).Text);
    }

    [Fact]
    public async Task GetEventInvites_IsKeptWithTheLinkedVRChatAccount_AndRecorded()
    {
        await using var services = await ServicesAsync();
        await LinkAsync(services, Caller, CallerVRChat, "Kiri");

        Assert.Equal(MeCommand.InvitesOnMessage, (await PressAsync(services, Press(Caller, MeCommand.InvitesOnButton))).Text);

        await using (var db = services.Database.NewContext())
        {
            var choice = await db.EventInviteChoices.AsNoTracking().SingleAsync(c => c.DiscordUserId == Caller, Ct);
            Assert.True(choice.Wants);
            Assert.Equal(CallerVRChat, choice.VRChatUserId);
            Assert.Equal(services.Clock.UtcNow, choice.ChangedAt);
        }

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.EventInvitesWanted, Ct));
        Assert.Equal(Caller, fact.SubjectId);
        Assert.DoesNotContain("Kiri", fact.Data, StringComparison.Ordinal);

        // The card now says so, and offers the way out instead.
        var reply = await RunAsync(services, Call(Caller));
        Assert.Equal("On", Field(reply.Embeds[0], "Event invites").Value);
        Assert.Contains(reply.Actions!, a => a.Id == MeCommand.InvitesOffButton);
        Assert.DoesNotContain(reply.Actions!, a => a.Id == MeCommand.InvitesOnButton);

        // Pressed again: nothing changes and nothing more is recorded.
        await PressAsync(services, Press(Caller, MeCommand.InvitesOnButton));
        Assert.Single(await services.FactsOfTypeAsync(FactType.EventInvitesWanted, Ct));
    }

    [Fact]
    public async Task StopEventInvites_TurnsItOff_AndIsRecorded()
    {
        await using var services = await ServicesAsync();

        await PressAsync(services, Press(Caller, MeCommand.InvitesOnButton));
        Assert.Equal(MeCommand.InvitesOffMessage, (await PressAsync(services, Press(Caller, MeCommand.InvitesOffButton))).Text);

        await using (var db = services.Database.NewContext())
            Assert.False((await db.EventInviteChoices.AsNoTracking().SingleAsync(c => c.DiscordUserId == Caller, Ct)).Wants);

        Assert.Single(await services.FactsOfTypeAsync(FactType.EventInvitesStopped, Ct));
        Assert.Equal("Off", Field((await RunAsync(services, Call(Caller))).Embeds[0], "Event invites").Value);
    }

    [Fact]
    public async Task GetEventInvites_FollowsTheSwitch()
    {
        await using var services = await ServicesAsync(on: false);

        Assert.Equal(MeCommand.OffMessage, (await PressAsync(services, Press(Caller, MeCommand.InvitesOnButton))).Text);

        await using var db = services.Database.NewContext();
        Assert.Empty(await db.EventInviteChoices.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task StopEventInvites_WorksEvenWithMeSwitchedOff()
    {
        await using var services = await ServicesAsync();
        await PressAsync(services, Press(Caller, MeCommand.InvitesOnButton));

        // The operator switches /me off; the member still has the old reply's button.
        await services.ConfigureAsync(s => s.SwitchCommand("me", false), Ct);

        Assert.Equal(MeCommand.InvitesOffMessage, (await PressAsync(services, Press(Caller, MeCommand.InvitesOffButton))).Text);

        await using var db = services.Database.NewContext();
        Assert.False((await db.EventInviteChoices.AsNoTracking().SingleAsync(c => c.DiscordUserId == Caller, Ct)).Wants);
    }

    [Fact]
    public async Task TwoFirstPressesAtOnce_MakeOneChoice()
    {
        await using var services = await ServicesAsync();

        // Two scopes, as two interactions arriving together would have.
        await Task.WhenAll(
            PressAsync(services, Press(Caller, MeCommand.InvitesOnButton)),
            PressAsync(services, Press(Caller, MeCommand.InvitesOnButton)));

        await using var db = services.Database.NewContext();
        Assert.True(Assert.Single(await db.EventInviteChoices.AsNoTracking().ToListAsync(Ct)).Wants);
        Assert.Single(await services.FactsOfTypeAsync(FactType.EventInvitesWanted, Ct));
    }

    [Fact]
    public void TheStaffLine_EscapesNames_AndSkipsASameDiscordName()
    {
        Assert.Equal("**kiri** asked the staff to delete their data.", MeCommand.StaffLine("kiri", "kiri"));
        Assert.Equal("**\\*Kiri\\*** (Discord: kiri) asked the staff to delete their data.", MeCommand.StaffLine("*Kiri*", "kiri"));
        Assert.Null(MeCommand.ReviewLink(Guid.Empty, null));
    }
}
