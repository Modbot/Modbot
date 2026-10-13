using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Users;
using Modbot.Discord.AgeChecks;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;

namespace Modbot.Discord.Tests.Commands;

/// <summary>
/// <c>/ageverified</c>: staff only and off by default; it answers first, then deletes the ticket if
/// the server switched that on; and the delete only ever touches a Modbot ticket thread or a channel
/// directly inside the category the server picked.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AgeVerifiedCommandTests
{
    private const string Caller = "100";
    private const string Member = "555";
    private const string Ticket = "9001";
    private const string Category = "9500";
    private const string InCategory = "9501";
    private const string Elsewhere = "9777";

    private readonly PostgresFixture _db;

    public AgeVerifiedCommandTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The check, answering what it is told to and remembering what it was asked.</summary>
    private sealed class FakeCheck(bool done) : IAgeCheck
    {
        public List<AgeCheckRequest> Asked { get; } = [];

        public Task<AgeCheckAnswer> RunAsync(AgeCheckRequest request, CancellationToken ct = default)
        {
            Asked.Add(request);
            return Task.FromResult(done
                ? new AgeCheckAnswer(true, ["Marked 18+ verified (badge)."])
                : AgeCheckAnswer.Refused("This member has no linked VRChat account. Add their VRChat profile link."));
        }
    }

    /// <summary>The deletes asked for, in order, with who asked; the bot's own category rules are tested apart.</summary>
    private sealed class RecordingActions : IDiscordAgeActions
    {
        public List<(string ChannelId, string Actor)> Deleted { get; } = [];

        public DiscordAgeOutcome Result { get; set; } = DiscordAgeOutcome.Ok;

        public List<string> Order { get; } = [];

        public Task<DiscordAgeOutcome> ChangeRoleAsync(string guildId, string userId, string roleId, bool add, CancellationToken ct = default)
            => Task.FromResult(DiscordAgeOutcome.Ok);

        public Task<DiscordAgeOutcome> DeleteTicketAsync(string channelId, string actorDiscordUserId, CancellationToken ct = default)
        {
            Order.Add("deleted");
            Deleted.Add((channelId, actorDiscordUserId));
            return Task.FromResult(Result);
        }
    }

    private static DiscordCommandCall Call(List<string> order, params (string Name, string Value)[] options)
        => new(
            Caller,
            "someone",
            DiscordCommands.AgeVerified,
            options.ToDictionary(o => o.Name, o => o.Value, StringComparer.Ordinal),
            (reply, _) =>
            {
                order.Add("replied: " + reply.Text);
                return Task.CompletedTask;
            })
        {
            ChannelId = Ticket,
        };

    private static (string, string)[] Typed(string? method = AgeCheckMethods.Badge) =>
        method is null
            ? [(DiscordCommands.MemberOption, Member)]
            : [(DiscordCommands.MemberOption, Member), (DiscordCommands.AgeMethodOption, method)];

    private async Task<(AgeVerifiedCommand Command, StaffCommandAnswer? Answer, List<string> Order, FakeCheck Check, RecordingActions Actions)> RunAsync(
        bool deleteTicket, bool checkDone = true, string? method = AgeCheckMethods.Badge, bool omitMember = false, DiscordAgeOutcome? deleteResult = null)
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await services.ConfigureAsync(s => s.AgeCheckDeleteTicket = deleteTicket, Ct);

        await using var context = services.Database.NewContext();
        var check = new FakeCheck(checkDone);
        var actions = new RecordingActions();

        if (deleteResult is not null)
            actions.Result = deleteResult;

        var command = new AgeVerifiedCommand(context, actions, check);
        var order = actions.Order;
        var call = Call(order, omitMember ? Typed(method).Skip(1).ToArray() : Typed(method));

        var answer = await command.RunAsync(call, new ModbotUser { Id = Guid.NewGuid() }, Ct);

        return (command, answer, order, check, actions);
    }

    [Fact]
    public void ItIsAModeratorCommand_NeedsEditAge18Verified_AndIsOffByDefault()
    {
        var command = Assert.Single(DiscordCommands.All, c => c.Name == DiscordCommands.AgeVerified);

        Assert.Equal(DiscordShownTo.Moderators, command.ShownTo);
        Assert.Equal(ModbotPermissions.EditAgeVerification, DiscordCommands.Requires(DiscordCommands.AgeVerified));
        Assert.False(DiscordCommandSwitches.Find(DiscordCommands.AgeVerified)?.OnByDefault);
        Assert.DoesNotContain(DiscordCommands.For(DiscordCommandSwitches.Empty), c => c.Name == DiscordCommands.AgeVerified);

        Assert.True(command.Options.Single(o => o.Name == DiscordCommands.MemberOption).Required);
        Assert.True(command.Options.Single(o => o.Name == DiscordCommands.AgeMethodOption).Required);
        Assert.False(command.Options.Single(o => o.Name == DiscordCommands.VRChatOption).Required);
    }

    [Fact]
    public async Task WithDeleteOff_TheCheckAnswers_AndTheTicketIsLeftAlone()
    {
        var (_, answer, order, check, actions) = await RunAsync(deleteTicket: false);

        Assert.Equal("answered", answer!.Outcome);
        Assert.False(answer.Replied);
        Assert.Empty(actions.Deleted);
        Assert.Empty(order);
        Assert.Equal(AgeCheckMethods.Badge, Assert.Single(check.Asked).Method);
        Assert.Equal(Member, check.Asked[0].DiscordUserId);
    }

    [Fact]
    public async Task WithDeleteOn_TheReplyIsSentFirst_ThenTheTicketIsDeleted_WithTheCheckerAsActor()
    {
        var (_, answer, order, _, actions) = await RunAsync(deleteTicket: true);

        Assert.True(answer!.Replied);
        Assert.Equal((Ticket, Caller), Assert.Single(actions.Deleted));
        Assert.StartsWith("replied: ", order[0], StringComparison.Ordinal);
        Assert.Equal("deleted", order[1]);
        Assert.Equal(2, order.Count);
    }

    [Fact]
    public async Task ATicketThatCannotBeDeleted_IsSaidSo_AfterTheAnswer()
    {
        var (_, answer, order, _, _) = await RunAsync(
            deleteTicket: true, deleteResult: new DiscordAgeOutcome(false, DiscordAgeActions.NotATicketMessage, NotAllowed: true));

        Assert.True(answer!.Replied);
        Assert.Equal(3, order.Count);
        Assert.Contains("was not deleted", order[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedCheck_NeverDeletesAnything()
    {
        var (_, answer, order, _, actions) = await RunAsync(deleteTicket: true, checkDone: false);

        Assert.Equal("refused", answer!.Outcome);
        Assert.False(answer.Replied);
        Assert.Empty(actions.Deleted);
        Assert.Empty(order);
    }

    [Fact]
    public async Task WithoutAMethod_ItAsksForOne_AndChecksNobody()
    {
        var (_, answer, _, check, actions) = await RunAsync(deleteTicket: true, method: "passport");

        Assert.Equal("invalid", answer!.Outcome);
        Assert.Equal(AgeVerifiedCommand.PickMethodMessage, answer.Reply.Text);
        Assert.Empty(check.Asked);
        Assert.Empty(actions.Deleted);
    }

    // ── What may be deleted ────────────────────────────────────────────────────────────────────

    private async Task<(DiscordAgeActions Actions, FakeGateway Gateway, TestServices Services)> ActionsAsync(string? category)
    {
        var services = await TestServices.CreateAsync(_db, Ct);
        await services.ConfigureAsync(s => s.AgeCheckTicketCategoryId = category, Ct);

        var gateway = new FakeGateway();
        gateway.ChannelCategories[InCategory] = Category;
        gateway.ChannelCategories[Elsewhere] = "9999";

        var actions = new DiscordAgeActions(
            () => gateway,
            services.Provider.GetRequiredService<IServiceScopeFactory>(),
            services.Clock);

        return (actions, gateway, services);
    }

    private static async Task AddTicketAsync(TestServices services)
    {
        await using var db = services.Database.NewContext();
        var type = new DiscordTicketType
        {
            Name = "Verify",
            OpeningMessage = "Hello.",
            CreatedAt = services.Clock.UtcNow,
            UpdatedAt = services.Clock.UtcNow,
        };

        db.DiscordTicketTypes.Add(type);
        db.DiscordTickets.Add(new DiscordTicket
        {
            TypeId = type.Id,
            TypeName = type.Name,
            DiscordUserId = Member,
            ThreadId = Ticket,
            ChannelId = "9002",
            OpenedAt = services.Clock.UtcNow,
        });

        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task AModbotTicketThread_IsDeleted_MarkedDeleted_AndLoggedLikeTheDeleteButton()
    {
        var (actions, gateway, services) = await ActionsAsync(category: null);
        await using var _ = services;
        await AddTicketAsync(services);

        var outcome = await actions.DeleteTicketAsync(Ticket, Caller, Ct);

        Assert.True(outcome.Done);
        Assert.Equal(Ticket, Assert.Single(gateway.TicketsDeleted).ThreadId);

        await using var db = services.Database.NewContext();
        var ticket = await db.DiscordTickets.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(DiscordTicketStates.Deleted, ticket.State);
        Assert.NotNull(ticket.DeletedAt);

        var fact = Assert.Single(await services.FactsOfTypeAsync(FactType.DiscordTicketDeleted, Ct));
        Assert.Equal(Caller, fact.ActorId);
        Assert.Equal(Member, fact.SubjectId);
        Assert.Equal(Ticket, JsonDocument.Parse(fact.Data).RootElement.GetProperty("threadId").GetString());
    }

    [Fact]
    public async Task AChannelInsideTheChosenCategory_IsDeleted()
    {
        var (actions, gateway, services) = await ActionsAsync(Category);
        await using var _ = services;

        var outcome = await actions.DeleteTicketAsync(InCategory, Caller, Ct);

        Assert.True(outcome.Done);
        Assert.Equal(InCategory, Assert.Single(gateway.CategoryChannelsDeleted).ChannelId);
    }

    [Fact]
    public async Task AnyOtherChannel_IsLeftAlone_AndTheAnswerSaysSo()
    {
        var (actions, gateway, services) = await ActionsAsync(Category);
        await using var _ = services;

        var outcome = await actions.DeleteTicketAsync(Elsewhere, Caller, Ct);

        Assert.False(outcome.Done);
        Assert.True(outcome.NotAllowed);
        Assert.Equal(DiscordAgeActions.NotATicketMessage, outcome.Error);
        Assert.Empty(gateway.CategoryChannelsDeleted);
        Assert.Empty(gateway.TicketsDeleted);
    }

    [Fact]
    public async Task WithNoCategoryChosen_OnlyATicketThreadCanBeDeleted()
    {
        var (actions, gateway, services) = await ActionsAsync(category: null);
        await using var _ = services;

        var outcome = await actions.DeleteTicketAsync(InCategory, Caller, Ct);

        Assert.False(outcome.Done);
        Assert.True(outcome.NotAllowed);
        Assert.Empty(gateway.CategoryChannelsDeleted);
    }

    [Fact]
    public async Task WithTheBotOffline_NothingIsDeleted()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);

        var actions = new DiscordAgeActions(() => null, services.Provider.GetRequiredService<IServiceScopeFactory>(), services.Clock);

        var outcome = await actions.DeleteTicketAsync(Ticket, Caller, Ct);

        Assert.False(outcome.Done);
        Assert.Equal(DiscordAgeOutcome.Offline.Error, outcome.Error);
    }
}
