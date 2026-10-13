using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Gateway;
using Modbot.Discord.Tests.Fakes;
using Modbot.Discord.Tickets;
using Modbot.TestSupport;
using static Modbot.Discord.Tests.Messages.MessageTestData;

namespace Modbot.Discord.Tests.Tickets;

/// <summary>
/// Member tickets: opening one private thread of each type per member, closing with a transcript for
/// the mod log, and deleting by staff alone.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordTicketsTests
{
    private const string Panel = "500";
    private const string TranscriptChannel = "600";
    private const string StaffRole = "777";
    private const string Member = "900";
    private const string Staff = "901";
    private const string Stranger = "902";

    private readonly PostgresFixture _db;

    public DiscordTicketsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DiscordButtonPress Press(
        string user, string buttonId, string? channel, string[]? roles = null, bool manageThreads = false)
        => new(user, "user" + user, buttonId, (_, _) => Task.CompletedTask)
        {
            CardChannelId = channel,
            MemberRoleIds = roles ?? [],
            MemberCanManageThreads = manageThreads,
        };

    private static async Task<DiscordTicketType> AddTypeAsync(TestServices services, bool privateImages = false)
    {
        await using var db = services.Database.NewContext();
        var type = new DiscordTicketType
        {
            Name = "Verify",
            StaffRoleId = StaffRole,
            OpeningMessage = "Tell us how we can help.",
            HoldsPrivateImages = privateImages,
            CreatedAt = services.Clock.UtcNow,
            UpdatedAt = services.Clock.UtcNow,
        };
        db.DiscordTicketTypes.Add(type);
        await db.SaveChangesAsync(Ct);
        return type;
    }

    private static Task SetTranscriptChannelAsync(TestServices services, string? channel)
        => services.ConfigureAsync(s => s.DiscordTicketTranscriptChannelId = channel, Ct);

    private static async Task<DiscordReply> PressAsync(
        TestServices services, FakeGateway? gateway, DiscordButtonPress press, bool messageTextOn = true)
    {
        using var scope = services.Scope();
        return await scope.ServiceProvider.GetRequiredService<DiscordTickets>()
            .HandleButtonAsync(gateway, press, messageTextOn, Ct);
    }

    private static async Task<DiscordTicket> OnlyTicketAsync(TestServices services)
    {
        await using var db = services.Database.NewContext();
        return await db.DiscordTickets.AsNoTracking().SingleAsync(Ct);
    }

    private static async Task<List<ModbotEvent>> FactsAsync(TestServices services)
    {
        await using var db = services.Database.NewContext();
        return await db.Events.AsNoTracking()
            .Where(e => e.Type.StartsWith("discord.ticket."))
            .OrderBy(e => e.Id)
            .ToListAsync(Ct);
    }

    /// <summary>A member opens a ticket; returns the gateway and the thread's id.</summary>
    private static async Task<(FakeGateway Gateway, string ThreadId, DiscordTicketType Type)> OpenedAsync(
        TestServices services, bool privateImages = false)
    {
        var type = await AddTypeAsync(services, privateImages);
        var gateway = new FakeGateway();
        var reply = await PressAsync(services, gateway, Press(Member, DiscordTickets.OpenButtonId(type.Id), Panel));
        Assert.Contains("Your ticket is open", reply.Text, StringComparison.Ordinal);
        return (gateway, (await OnlyTicketAsync(services)).ThreadId, type);
    }

    // ── Open ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Opening_MakesAThreadUnderThePanel_SavesTheTicket_AndWritesAFact()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var type = await AddTypeAsync(services);
        var gateway = new FakeGateway();

        var reply = await PressAsync(services, gateway, Press(Member, DiscordTickets.OpenButtonId(type.Id), Panel));

        var opening = Assert.Single(gateway.TicketsOpened);
        Assert.Equal(Panel, opening.ChannelId);
        Assert.Equal("Verify-user900", opening.ThreadName);
        Assert.Equal(Member, opening.MemberId);
        Assert.Equal(StaffRole, opening.StaffRoleId);
        Assert.Equal("Tell us how we can help.", opening.Text);
        Assert.Equal(["Close", "Delete"], opening.Actions.Select(a => a.Label));
        Assert.Equal(DiscordButtonStyle.Danger, opening.Actions[1].Style);

        var ticket = await OnlyTicketAsync(services);
        Assert.Equal(DiscordTicketStates.Open, ticket.State);
        Assert.Equal(Member, ticket.DiscordUserId);
        Assert.Contains($"<#{ticket.ThreadId}>", reply.Text, StringComparison.Ordinal);

        var fact = Assert.Single(await FactsAsync(services));
        Assert.Equal("discord.ticket.open", fact.Type);
        Assert.Equal(Member, fact.SubjectId);
    }

    [Fact]
    public void ALongThreadName_IsCutAtOneHundredCharacters()
    {
        Assert.Equal(100, DiscordTickets.ThreadName(new string('a', 90), new string('b', 90)).Length);
    }

    /// <summary>A photo posted before the ticket row is saved is stored with its attachments, then stripped.</summary>
    [Fact]
    public async Task APhotoPostedBeforeTheTicketWasSaved_LosesItsAttachments_WhenTheTicketOfAPrivateImageTypeIsSaved()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var type = await AddTypeAsync(services, privateImages: true);
        var gateway = new FakeGateway
        {
            WhileTicketThreadIsNew = async threadId =>
            {
                using var scope = services.Scope();
                await scope.ServiceProvider.GetRequiredService<Modbot.Discord.Messages.DiscordMessageStore>()
                    .StoreAsync([Message(10, thread: threadId, text: "my ID")], Ct);
            },
        };

        await PressAsync(services, gateway, Press(Member, DiscordTickets.OpenButtonId(type.Id), Panel));

        await using var db = services.Database.NewContext();
        var row = await db.DiscordMessages.AsNoTracking().SingleAsync(Ct);

        Assert.Equal("my ID", row.Text);
        Assert.Equal("[]", row.Attachments);
    }

    [Fact]
    public async Task APhotoPostedBeforeTheTicketWasSaved_KeepsItsAttachments_ForAnOrdinaryType()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var type = await AddTypeAsync(services);
        var gateway = new FakeGateway
        {
            WhileTicketThreadIsNew = async threadId =>
            {
                using var scope = services.Scope();
                await scope.ServiceProvider.GetRequiredService<Modbot.Discord.Messages.DiscordMessageStore>()
                    .StoreAsync([Message(10, thread: threadId, text: "a photo")], Ct);
            },
        };

        await PressAsync(services, gateway, Press(Member, DiscordTickets.OpenButtonId(type.Id), Panel));

        await using var db = services.Database.NewContext();
        Assert.Contains("cat.png", (await db.DiscordMessages.AsNoTracking().SingleAsync(Ct)).Attachments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMemberWithAnOpenTicketOfTheType_IsSentToIt_AndNoSecondThreadIsMade()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (gateway, threadId, type) = await OpenedAsync(services);

        var reply = await PressAsync(services, gateway, Press(Member, DiscordTickets.OpenButtonId(type.Id), Panel));

        Assert.Single(gateway.TicketsOpened);
        Assert.Contains($"<#{threadId}>", reply.Text, StringComparison.Ordinal);
        Assert.Contains("already have an open ticket", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOpenTicketWhoseThreadIsGone_IsMarkedDeleted_AndANewOneIsMade()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (gateway, threadId, type) = await OpenedAsync(services);
        gateway.GoneThreads.Add(threadId);

        var reply = await PressAsync(services, gateway, Press(Member, DiscordTickets.OpenButtonId(type.Id), Panel));

        Assert.Equal(2, gateway.TicketsOpened.Count);
        Assert.Contains("Your ticket is open", reply.Text, StringComparison.Ordinal);

        await using var db = services.Database.NewContext();
        var states = await db.DiscordTickets.AsNoTracking().OrderBy(t => t.OpenedAt).Select(t => t.State).ToListAsync(Ct);
        Assert.Equal(2, states.Count);
        Assert.Contains(DiscordTicketStates.Deleted, states);
        Assert.Contains(DiscordTicketStates.Open, states);
    }

    [Fact]
    public async Task AFailedThread_SaysWhyInTheReply_AndSavesNothing()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var type = await AddTypeAsync(services);
        var gateway = new FakeGateway { OpenTicketError = "The bot needs Create Private Threads in that channel." };

        var reply = await PressAsync(services, gateway, Press(Member, DiscordTickets.OpenButtonId(type.Id), Panel));

        Assert.Equal("The bot needs Create Private Threads in that channel.", reply.Text);

        await using var db = services.Database.NewContext();
        Assert.Empty(await db.DiscordTickets.ToListAsync(Ct));
    }

    [Fact]
    public async Task ARemovedType_AndAPressWithNoOnlineBot_AreAnsweredPlainly()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var type = await AddTypeAsync(services);

        var gone = await PressAsync(services, new FakeGateway(), Press(Member, DiscordTickets.OpenButtonId(Guid.NewGuid()), Panel));
        var offline = await PressAsync(services, null, Press(Member, DiscordTickets.OpenButtonId(type.Id), Panel));

        Assert.Equal(DiscordTickets.GoneTypeMessage, gone.Text);
        Assert.Equal(DiscordTickets.OfflineMessage, offline.Text);
    }

    // ── Close ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Closing_PostsTheTranscriptFile_ClosesTheThread_AndWritesAFactWithTheCloser()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await SetTranscriptChannelAsync(services, TranscriptChannel);
        var (gateway, threadId, _) = await OpenedAsync(services);
        gateway.History[threadId] = [Message(1, channel: Panel, thread: threadId, author: Member, text: "I need help")];

        var reply = await PressAsync(services, gateway, Press(Member, DiscordTickets.CloseId, threadId));

        Assert.Contains($"<#{TranscriptChannel}>", reply.Text, StringComparison.Ordinal);

        var post = Assert.Single(gateway.Messages, m => m.ChannelId == TranscriptChannel);
        var file = Assert.Single(gateway.PicturesSent[post.MessageId]!);
        Assert.StartsWith("ticket-", file.Name, StringComparison.Ordinal);
        Assert.EndsWith(".txt", file.Name, StringComparison.Ordinal);
        Assert.Contains("I need help", Encoding.UTF8.GetString(file.Bytes), StringComparison.Ordinal);

        var closed = Assert.Single(gateway.TicketsClosed);
        Assert.Equal(threadId, closed.ThreadId);
        Assert.Equal($"Closed by <@{Member}>.", closed.Line);

        var ticket = await OnlyTicketAsync(services);
        Assert.Equal(DiscordTicketStates.Closed, ticket.State);
        Assert.Equal(Member, ticket.ClosedByDiscordUserId);

        var fact = (await FactsAsync(services)).Single(f => f.Type == "discord.ticket.close");
        Assert.Equal(Member, fact.SubjectId);
        Assert.Equal(Member, fact.ActorId);
    }

    [Fact]
    public async Task Closing_ByStaffRole_IsAllowed_AndTheFactNamesTheStaffMember()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await SetTranscriptChannelAsync(services, TranscriptChannel);
        var (gateway, threadId, _) = await OpenedAsync(services);

        await PressAsync(services, gateway, Press(Staff, DiscordTickets.CloseId, threadId, roles: [StaffRole]));

        Assert.Single(gateway.TicketsClosed);
        var fact = (await FactsAsync(services)).Single(f => f.Type == "discord.ticket.close");
        Assert.Equal(Member, fact.SubjectId);
        Assert.Equal(Staff, fact.ActorId);
    }

    [Fact]
    public async Task Closing_ByAnotherMember_IsRefused()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (gateway, threadId, _) = await OpenedAsync(services);

        var reply = await PressAsync(services, gateway, Press(Stranger, DiscordTickets.CloseId, threadId, roles: ["1"]));

        Assert.Equal(DiscordTickets.NotYoursMessage, reply.Text);
        Assert.Empty(gateway.TicketsClosed);
        Assert.Equal(DiscordTicketStates.Open, (await OnlyTicketAsync(services)).State);
    }

    [Fact]
    public async Task Closing_WithNoTranscriptChannel_StillCloses_AndSaysNoTranscriptWasKept()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (gateway, threadId, _) = await OpenedAsync(services);

        var reply = await PressAsync(services, gateway, Press(Member, DiscordTickets.CloseId, threadId));

        Assert.Equal("Ticket closed. No transcript was kept.", reply.Text);
        Assert.Single(gateway.TicketsClosed);
        Assert.DoesNotContain(gateway.Messages, m => m.ChannelId == TranscriptChannel);

        var fact = (await FactsAsync(services)).Single(f => f.Type == "discord.ticket.close");
        Assert.Contains("\"transcript\":\"none\"", fact.Data.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closing_WhenTheTranscriptCannotBePosted_LeavesTheTicketOpen()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await SetTranscriptChannelAsync(services, TranscriptChannel);
        var (gateway, threadId, _) = await OpenedAsync(services);
        gateway.FailNextPost("Missing Access");

        var reply = await PressAsync(services, gateway, Press(Member, DiscordTickets.CloseId, threadId));

        Assert.Contains("transcript could not be posted", reply.Text, StringComparison.Ordinal);
        Assert.Empty(gateway.TicketsClosed);
        Assert.Equal(DiscordTicketStates.Open, (await OnlyTicketAsync(services)).State);
    }

    [Fact]
    public async Task Closing_AnAlreadyClosedTicket_IsAnsweredWithoutDoingItAgain()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (gateway, threadId, _) = await OpenedAsync(services);
        await PressAsync(services, gateway, Press(Member, DiscordTickets.CloseId, threadId));

        var reply = await PressAsync(services, gateway, Press(Member, DiscordTickets.CloseId, threadId));

        Assert.Equal(DiscordTickets.NoOpenTicketMessage, reply.Text);
        Assert.Single(gateway.TicketsClosed);
    }

    [Fact]
    public async Task Closing_ATicketThatHoldsPrivateImages_KeepsNoAttachmentInTheTranscript()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await SetTranscriptChannelAsync(services, TranscriptChannel);
        var (gateway, threadId, _) = await OpenedAsync(services, privateImages: true);
        gateway.History[threadId] = [Message(1, channel: Panel, thread: threadId, author: Member, text: "my ID is attached")];

        await PressAsync(services, gateway, Press(Member, DiscordTickets.CloseId, threadId));

        var post = Assert.Single(gateway.Messages, m => m.ChannelId == TranscriptChannel);
        var transcript = Encoding.UTF8.GetString(Assert.Single(gateway.PicturesSent[post.MessageId]!).Bytes);
        Assert.Contains(TicketTranscript.ImageRemoved, transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("cat.png", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn.discordapp.com", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closing_WithMessageTextOff_SaysTheTextIsMissing()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await SetTranscriptChannelAsync(services, TranscriptChannel);
        var (gateway, threadId, _) = await OpenedAsync(services);

        await PressAsync(services, gateway, Press(Member, DiscordTickets.CloseId, threadId), messageTextOn: false);

        var post = Assert.Single(gateway.Messages, m => m.ChannelId == TranscriptChannel);
        var transcript = Encoding.UTF8.GetString(Assert.Single(gateway.PicturesSent[post.MessageId]!).Bytes);
        Assert.Contains(TicketTranscript.TextMissing, transcript, StringComparison.Ordinal);
    }

    // ── Delete ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deleting_ByAMember_IsRefused_EvenTheOneWhoOpenedIt()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (gateway, threadId, _) = await OpenedAsync(services);

        var reply = await PressAsync(services, gateway, Press(Member, DiscordTickets.DeleteId, threadId));

        Assert.Equal(DiscordTickets.StaffOnlyMessage, reply.Text);
        Assert.Empty(gateway.TicketsDeleted);
        Assert.Equal(DiscordTicketStates.Open, (await OnlyTicketAsync(services)).State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deleting_ByStaff_RemovesTheThread_MarksItDeleted_AndWritesAFact(bool byRole)
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (gateway, threadId, _) = await OpenedAsync(services);

        var press = byRole
            ? Press(Staff, DiscordTickets.DeleteId, threadId, roles: [StaffRole])
            : Press(Staff, DiscordTickets.DeleteId, threadId, manageThreads: true);

        var reply = await PressAsync(services, gateway, press);

        Assert.Equal("Ticket deleted.", reply.Text);
        Assert.Equal(threadId, Assert.Single(gateway.TicketsDeleted).ThreadId);

        var ticket = await OnlyTicketAsync(services);
        Assert.Equal(DiscordTicketStates.Deleted, ticket.State);
        Assert.NotNull(ticket.DeletedAt);

        var fact = (await FactsAsync(services)).Single(f => f.Type == "discord.ticket.delete");
        Assert.Equal(Member, fact.SubjectId);
        Assert.Equal(Staff, fact.ActorId);
    }

    [Fact]
    public async Task Deleting_AThreadAlreadyGone_StillMarksTheTicketDeleted()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (gateway, threadId, _) = await OpenedAsync(services);
        gateway.DeleteTicketNotFound = true;

        await PressAsync(services, gateway, Press(Staff, DiscordTickets.DeleteId, threadId, manageThreads: true));

        Assert.Equal(DiscordTicketStates.Deleted, (await OnlyTicketAsync(services)).State);
    }

    [Fact]
    public async Task Deleting_WhenDiscordRefuses_LeavesTheTicketAsItWas()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        var (gateway, threadId, _) = await OpenedAsync(services);
        gateway.DeleteTicketError = "Missing Permissions";

        var reply = await PressAsync(services, gateway, Press(Staff, DiscordTickets.DeleteId, threadId, manageThreads: true));

        Assert.Contains("could not be deleted", reply.Text, StringComparison.Ordinal);
        Assert.Equal(DiscordTicketStates.Open, (await OnlyTicketAsync(services)).State);
    }
}
