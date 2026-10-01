using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Gateway;
using Modbot.Discord.Interactions;
using Modbot.Discord.ModerationLog;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Interactions;

/// <summary>
/// Which buttons a log card carries (acting from Discord design §7), and that only a message of one
/// card carries any.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class CardButtonsTests
{
    private const string Target = "usr_c9094d86-1846-43eb-b79d-7e3dc318f42a";
    private const string Actor = "usr_2a323be9-ac4e-4502-af07-357d79c48ccf";
    private const string Channel = "1234567890";

    private readonly PostgresFixture _db;

    public CardButtonsTests(PostgresFixture db) => _db = db;

    private static string[] Ids(CardButtonSet set) => [.. set.Actions.Select(a => a.Id)];

    [Fact]
    public void AWarnCard_OffersANote_AKick_AndABan()
    {
        var set = CardButtons.For(FactType.GroupInstanceWarn, FactPlatform.VRChat, Target);

        Assert.Equal(
            new[]
            {
                StaffMenus.NoteButtonFor(onDiscord: false, Target),
                StaffMenus.ActButtonFor(StaffActionWords.Kick, Target),
                StaffMenus.ActButtonFor(StaffActionWords.Ban, Target),
            },
            Ids(set));
        Assert.Empty(set.Links);

        // Grey on the card; the red one is the confirmation's.
        Assert.All(set.Actions, a => Assert.Equal(DiscordButtonStyle.Plain, a.Style));
    }

    [Fact]
    public void ABanCard_OffersANote_AndItsCaseFile_ButNoSecondBan()
    {
        var set = CardButtons.For(FactType.MemberBanned, FactPlatform.VRChat, Target, "https://modbot.example.com/cases/abc");

        Assert.Equal(new[] { StaffMenus.NoteButtonFor(onDiscord: false, Target) }, Ids(set));
        var link = Assert.Single(set.Links);
        Assert.Equal(CardButtons.OpenCaseLabel, link.Label);
        Assert.Equal("https://modbot.example.com/cases/abc", link.Url);
    }

    [Fact]
    public void AJoinRequestCard_OffersApproveAndReject()
    {
        var set = CardButtons.For(FactType.JoinRequestCreated, FactPlatform.VRChat, Target);

        Assert.Equal(
            new[]
            {
                StaffMenus.ActButtonFor(StaffActionWords.Approve, Target),
                StaffMenus.ActButtonFor(StaffActionWords.Reject, Target),
                StaffMenus.NoteButtonFor(onDiscord: false, Target),
            },
            Ids(set));
    }

    [Fact]
    public void ADiscordTimeoutCard_OffersOnlyANote()
    {
        var set = CardButtons.For(FactType.DiscordMemberTimedOut, FactPlatform.Discord, "445566778899");

        Assert.Equal(new[] { StaffMenus.NoteButtonFor(onDiscord: true, "445566778899") }, Ids(set));
    }

    [Fact]
    public void CardsNotAboutAPerson_OfferNothing()
    {
        Assert.True(CardButtons.For(FactType.GroupInstanceCreated, FactPlatform.VRChat, "wrld_1:123").IsEmpty);
        Assert.True(CardButtons.For(FactType.GroupInfoChanged, FactPlatform.VRChat, "grp_1").IsEmpty);
        Assert.True(CardButtons.For(FactType.SettingsChanged, FactPlatform.Modbot, "settings").IsEmpty);
    }

    [Fact]
    public void AnIdTooLongForDiscord_LeavesTheButtonOff_RatherThanCuttingIt()
    {
        var legacy = new string('x', 95);

        var set = CardButtons.For(FactType.GroupInstanceWarn, FactPlatform.VRChat, legacy);

        Assert.All(set.Actions, a => Assert.True(a.Id.Length <= StaffMenus.MaxIdLength));
        Assert.DoesNotContain(set.Actions, a => a.Id.StartsWith(StaffMenus.ActButton, StringComparison.Ordinal));
    }

    [Fact]
    public void IdsReadBack_WhateverTheIdHolds()
    {
        var odd = "legacy:id:with:colons";

        Assert.True(StaffMenus.TryReadAction(StaffMenus.ActButtonFor(StaffActionWords.Ban, odd), out var action, out var id));
        Assert.Equal((StaffActionWords.Ban, odd), (action, id));

        Assert.True(StaffMenus.TryReadPerson(StaffMenus.NoteButtonFor(onDiscord: true, odd), StaffMenus.NoteButton, out var onDiscord, out var person));
        Assert.True(onDiscord);
        Assert.Equal(odd, person);

        Assert.False(StaffMenus.TryReadAction(StaffMenus.ActButton + "unban:" + Target, out _, out _));
    }

    [Fact]
    public async Task TheLog_PutsButtonsUnderAMessageOfOneCard_AndNoneUnderABatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var services = await TestServices.CreateAsync(_db, ct);
        var gateway = new FakeGateway();

        await services.AddRouteAsync(Channel, ct: ct);
        await RunAsync(services, gateway, ct);

        // One event: one card, with buttons.
        await services.WriteAuditFactAsync(FactType.GroupInstanceWarn, Target, Actor, "E-Ray", ct: ct);
        await RunAsync(services, gateway, ct);

        var single = gateway.Messages[^1];
        Assert.Single(single.Embeds);
        Assert.Contains(gateway.ActionsSent[single.MessageId], a => a.Id == StaffMenus.ActButtonFor(StaffActionWords.Ban, Target));

        // Two at once: one message of two cards, and no buttons that could not say which.
        await services.WriteAuditFactAsync(FactType.GroupInstanceWarn, Target, Actor, "E-Ray", ct: ct);
        await services.WriteAuditFactAsync(FactType.GroupInstanceKick, Target, Actor, "E-Ray", ct: ct);
        await RunAsync(services, gateway, ct);

        var batch = gateway.Messages[^1];
        Assert.Equal(2, batch.Embeds.Count);
        Assert.Empty(gateway.ActionsSent[batch.MessageId]);
    }

    private static async Task RunAsync(TestServices services, FakeGateway gateway, CancellationToken ct)
    {
        using var scope = services.Scope();
        await scope.ServiceProvider.GetRequiredService<ModerationLogPoster>()
            .RunOnceAsync(gateway, (_, _) => Task.CompletedTask, ct);
    }
}
