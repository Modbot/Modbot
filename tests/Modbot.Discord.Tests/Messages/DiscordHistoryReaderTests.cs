using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data.Entities;
using Modbot.Discord.Bot;
using Modbot.Discord.Gateway;
using Modbot.Discord.Messages;
using Modbot.Discord.Tests.Fakes;
using Modbot.TestSupport;
using static Modbot.Discord.Tests.Messages.MessageTestData;

namespace Modbot.Discord.Tests.Messages;

/// <summary>
/// Reading back a server's history: newest first, a hundred at a time, stopping at the channel's
/// start or after three pages of messages already stored, and carrying on after a restart.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordHistoryReaderTests
{
    private readonly PostgresFixture _db;

    public DiscordHistoryReaderTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DiscordHistoryReader Reader(TestServices services, Func<TimeSpan, CancellationToken, Task>? delay = null)
        => new(
            services.Provider.GetRequiredService<IServiceScopeFactory>(),
            services.Clock,
            new DiscordBotOptions(),
            delay ?? ((_, _) => Task.CompletedTask));

    private static async Task<DiscordReadBack> RowAsync(TestServices services, string channelId)
    {
        await using var db = services.Database.NewContext();
        return await db.DiscordReadBacks.AsNoTracking().SingleAsync(r => r.ChannelId == channelId, Ct);
    }

    private static async Task<int> StoredCountAsync(TestServices services)
    {
        await using var db = services.Database.NewContext();
        return await db.DiscordMessages.CountAsync(Ct);
    }

    /// <summary>Stores the newest <paramref name="count"/> of a history, as live listening would have.</summary>
    private static async Task PreStoreNewestAsync(TestServices services, List<DiscordMessageSnapshot> history, int count)
    {
        using var scope = services.Scope();
        var store = scope.ServiceProvider.GetRequiredService<DiscordMessageStore>();
        await store.StoreAsync(history.OrderByDescending(m => long.Parse(m.Id)).Take(count).ToList(), Ct);
    }

    [Fact]
    public async Task AChannel_IsReadBackToItsFirstMessage()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await using (var db = services.Database.NewContext())
            await AddChannelAsync(db, "500", "general", ct: Ct);

        var gateway = new FakeGateway();
        gateway.History["500"] = History(250);

        var pass = await Reader(services).ReadBackAsync(gateway, Guild, Ct);

        Assert.Equal(3, pass.Pages);
        Assert.Equal(250, pass.Stored);
        Assert.Equal(250, await StoredCountAsync(services));

        // Newest first, each page starting before the oldest message of the one before.
        Assert.Equal([null, "151", "51"], gateway.Reads.Select(r => r.BeforeId));

        var row = await RowAsync(services, "500");
        Assert.Equal(DiscordReadBackStops.Start, row.StoppedBecause);
        Assert.Equal(250, row.MessagesStored);
        Assert.NotNull(row.FinishedAt);

        // Read back again later: a finished channel costs nothing.
        gateway.Reads.Clear();
        await Reader(services).ReadBackAsync(gateway, Guild, Ct);
        Assert.Empty(gateway.Reads);
    }

    [Fact]
    public async Task ThreePagesOfStoredMessages_StopTheChannel()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await using (var db = services.Database.NewContext())
            await AddChannelAsync(db, "500", "general", ct: Ct);

        var history = History(1000);
        await PreStoreNewestAsync(services, history, 300);

        var gateway = new FakeGateway();
        gateway.History["500"] = history;

        var pass = await Reader(services).ReadBackAsync(gateway, Guild, Ct);

        Assert.Equal(3, pass.Pages);
        Assert.Equal(0, pass.Stored);
        Assert.Equal(300, await StoredCountAsync(services));

        var row = await RowAsync(services, "500");
        Assert.Equal(DiscordReadBackStops.AlreadyStored, row.StoppedBecause);
        Assert.Equal(3, row.StoredPagesInARow);
    }

    [Fact]
    public async Task TwoPagesOfStoredMessages_ThenANewOne_KeepsReading()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await using (var db = services.Database.NewContext())
            await AddChannelAsync(db, "500", "general", ct: Ct);

        var history = History(450);
        await PreStoreNewestAsync(services, history, 200);

        var gateway = new FakeGateway();
        gateway.History["500"] = history;

        var pass = await Reader(services).ReadBackAsync(gateway, Guild, Ct);

        // Two stored pages, then a page with new messages starts the count again, and the channel
        // is read to its start: 100, 100, 100 new, 100 new, 50 new.
        Assert.Equal(5, pass.Pages);
        Assert.Equal(250, pass.Stored);
        Assert.Equal(450, await StoredCountAsync(services));

        var row = await RowAsync(services, "500");
        Assert.Equal(DiscordReadBackStops.Start, row.StoppedBecause);
        Assert.Equal(0, row.StoredPagesInARow);
    }

    [Fact]
    public async Task AfterARestart_ReadingCarriesOnFromThePageItReached()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await using (var db = services.Database.NewContext())
            await AddChannelAsync(db, "500", "general", ct: Ct);

        var gateway = new FakeGateway();
        gateway.History["500"] = History(500);

        // The process stops after two pages: the pause after the second is where it is killed.
        using var stop = new CancellationTokenSource();
        var pauses = 0;
        var firstRun = Reader(services, (_, token) =>
        {
            if (++pauses == 2)
                stop.Cancel();

            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstRun.ReadBackAsync(gateway, Guild, stop.Token));

        var halfway = await RowAsync(services, "500");
        Assert.Null(halfway.FinishedAt);
        Assert.Equal(2, halfway.PagesRead);
        Assert.Equal("301", halfway.OldestReadId);

        // A new process, a new reader: the first page it asks for is the one after page two.
        gateway.Reads.Clear();
        var pass = await Reader(services).ReadBackAsync(gateway, Guild, Ct);

        Assert.Equal("301", gateway.Reads[0].BeforeId);
        Assert.Equal(300, pass.Stored);
        Assert.Equal(500, await StoredCountAsync(services));
        Assert.Equal(DiscordReadBackStops.Start, (await RowAsync(services, "500")).StoppedBecause);
    }

    [Fact]
    public async Task AChannelTheBotMayNotRead_StopsAndIsReopenedWhenAccessIsGiven()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await using (var db = services.Database.NewContext())
            await AddChannelAsync(db, "500", "general", ct: Ct);

        var gateway = new FakeGateway();
        gateway.History["500"] = History(20);
        gateway.NoAccess.Add("500");

        await Reader(services).ReadBackAsync(gateway, Guild, Ct);

        var refused = await RowAsync(services, "500");
        Assert.Equal(DiscordReadBackStops.NoAccess, refused.StoppedBecause);
        Assert.NotNull(refused.LastError);

        gateway.NoAccess.Clear();
        await Reader(services).ReadBackAsync(gateway, Guild, Ct);

        var read = await RowAsync(services, "500");
        Assert.Equal(DiscordReadBackStops.Start, read.StoppedBecause);
        Assert.Null(read.LastError);
        Assert.Equal(20, await StoredCountAsync(services));
    }

    [Fact]
    public async Task ChannelsWithoutReadMessageHistory_AreNotRead()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await using (var db = services.Database.NewContext())
        {
            await AddChannelAsync(db, "500", "general", ct: Ct);
            await AddChannelAsync(db, "501", "staff", canRead: false, ct: Ct);
            await AddChannelAsync(db, "502", "Events", DiscordChannelTypes.Category, ct: Ct);
        }

        var gateway = new FakeGateway();
        gateway.History["500"] = History(5, "500");
        gateway.History["501"] = History(5, "501");

        await Reader(services).ReadBackAsync(gateway, Guild, Ct);

        Assert.Equal(["500"], gateway.Reads.Select(r => r.ChannelId).Distinct());
    }

    [Fact]
    public async Task Threads_AreReadBack_AndArchivedThreadsAreListedOnlyWhileTheirChannelIsUnfinished()
    {
        await using var services = await TestServices.CreateAsync(_db, Ct);
        await using (var db = services.Database.NewContext())
        {
            await AddChannelAsync(db, "500", "general", ct: Ct);
            await AddChannelAsync(db, "600", "help", DiscordChannelTypes.Forum, ct: Ct);
        }

        var gateway = new FakeGateway();
        gateway.History["500"] = History(3, "500");
        gateway.Threads.Add(new DiscordThreadSnapshot("700", "600", "how do I join", Archived: true));
        gateway.Threads.Add(new DiscordThreadSnapshot("701", "500", "side chat", Archived: false));
        gateway.History["700"] = Enumerable.Range(10, 4).Select(i => Message(i, "600", "700")).ToList();
        gateway.History["701"] = Enumerable.Range(20, 2).Select(i => Message(i, "500", "701")).ToList();

        await Reader(services).ReadBackAsync(gateway, Guild, Ct);

        Assert.Equal(9, await StoredCountAsync(services));
        Assert.Equal(["500", "600"], gateway.ArchivedListings[0].Order());

        await using (var db = services.Database.NewContext())
        {
            var inThread = await db.DiscordMessages.AsNoTracking().Where(m => m.ThreadId == "700").ToListAsync(Ct);
            Assert.Equal(4, inThread.Count);
            Assert.All(inThread, m => Assert.Equal("600", m.ChannelId));

            // The forum's own row only says its threads were listed.
            var forum = await db.DiscordReadBacks.AsNoTracking().SingleAsync(r => r.ChannelId == "600", Ct);
            Assert.NotNull(forum.FinishedAt);
            Assert.Equal(0, forum.PagesRead);
        }

        await Reader(services).ReadBackAsync(gateway, Guild, Ct);
        Assert.Empty(gateway.ArchivedListings[1]);
    }
}
