using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Names;
using Modbot.Shared.Names;
using Modbot.TestSupport;
using Serilog;

namespace Modbot.Core.Tests.Data;

/// <summary>
/// The searchable name columns: written beside every name on save by the interceptor, and filled
/// for rows that lack them by the catch-up, against real PostgreSQL.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class SearchableNamesTests
{
    private readonly PostgresFixture _db;

    public SearchableNamesTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset At = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AVRChatNameIsSavedWithItsSearchableFormAndKeptInStep()
    {
        var id = $"usr_{Guid.NewGuid():N}";

        await using (var write = _db.NewContext())
        {
            write.VRChatUsers.Add(new VRChatUser { UserId = id, DisplayName = "༻sᴜɢᴀʀʙᴜɴɴɪᴇ༺", FirstSeenAt = At, LastSeenAt = At });
            await write.SaveChangesAsync(Ct);
        }

        await using (var read = _db.NewContext())
        {
            var row = await read.VRChatUsers.SingleAsync(u => u.UserId == id, Ct);
            Assert.Equal("sugarbunnie", row.DisplayNameSearchable);

            // A save that changes something else leaves the column alone.
            row.DisplayNameSearchable = "stale on purpose";
            await read.SaveChangesAsync(Ct);
            row.LastSeenAt = At.AddHours(1);
            await read.SaveChangesAsync(Ct);
        }

        await using (var check = _db.NewContext())
        {
            var row = await check.VRChatUsers.SingleAsync(u => u.UserId == id, Ct);
            Assert.Equal("stale on purpose", row.DisplayNameSearchable);

            row.DisplayName = "Addеrаll";
            await check.SaveChangesAsync(Ct);
            Assert.Equal("adderall", row.DisplayNameSearchable);

            row.DisplayName = null;
            await check.SaveChangesAsync(Ct);
            Assert.Null(row.DisplayNameSearchable);
        }
    }

    [Fact]
    public async Task ADiscordMembersFourNamesAreSavedWithTheirSearchableForms()
    {
        var guild = $"g{Guid.NewGuid():N}";

        await using (var write = _db.NewContext())
        {
            write.DiscordMembers.Add(new DiscordMember
            {
                GuildId = guild,
                UserId = "1",
                Username = "dragon135_racer",
                DisplayName = "Vᴇɴᴜs ᴅᴇ Gʀᴀᴀɴ",
                GlobalName = "𝕯𝖗𝖆𝖌𝖔𝖓135_𝕽𝖆𝖈𝖊𝖗",
                Nickname = "Vᴇɴᴜs ᴅᴇ Gʀᴀᴀɴ",
                FirstSeenAt = At,
                UpdatedAt = At,
            });
            await write.SaveChangesAsync(Ct);
        }

        await using var read = _db.NewContext();
        var row = await read.DiscordMembers.SingleAsync(m => m.GuildId == guild, Ct);
        Assert.Equal("dragon135_racer", row.UsernameSearchable);
        Assert.Equal("venus de graan", row.DisplayNameSearchable);
        Assert.Equal("dragon135_racer", row.GlobalNameSearchable);
        Assert.Equal("venus de graan", row.NicknameSearchable);

        row.Nickname = null;
        row.DisplayName = "𝕯𝖗𝖆𝖌𝖔𝖓135_𝕽𝖆𝖈𝖊𝖗";
        await read.SaveChangesAsync(Ct);
        Assert.Null(row.NicknameSearchable);
        Assert.Equal("dragon135_racer", row.DisplayNameSearchable);
    }

    [Fact]
    public async Task TheCatchUpFillsRowsThatHaveNoSearchableFormAndRecordsTheVersion()
    {
        var log = new LoggerConfiguration().CreateLogger();
        var id = $"usr_{Guid.NewGuid():N}";
        var guild = $"g{Guid.NewGuid():N}";

        await using (var write = _db.NewContext())
        {
            write.VRChatUsers.Add(new VRChatUser { UserId = id, DisplayName = "𝕬𝖑𝖊𝖝", FirstSeenAt = At, LastSeenAt = At });
            write.DiscordMembers.Add(new DiscordMember
            {
                GuildId = guild, UserId = "1", Username = "ada", DisplayName = "ꜱɪᴇɴɴᴀ", GlobalName = "ꜱɪᴇɴɴᴀ", FirstSeenAt = At, UpdatedAt = At,
            });
            await write.SaveChangesAsync(Ct);

            // Rows from before the columns existed, as the migration leaves them.
            await write.VRChatUsers.Where(u => u.UserId == id)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.DisplayNameSearchable, (string?)null), Ct);
            await write.DiscordMembers.Where(m => m.GuildId == guild)
                .ExecuteUpdateAsync(m => m
                    .SetProperty(x => x.UsernameSearchable, (string?)null)
                    .SetProperty(x => x.DisplayNameSearchable, (string?)null)
                    .SetProperty(x => x.GlobalNameSearchable, (string?)null), Ct);

            var settings = await write.GetSettingsAsync(Ct);
            settings.NameCatchUpVersion = 0;
            await write.SaveChangesAsync(Ct);
        }

        await using (var run = _db.NewContext())
        {
            // Version 0 is behind every build: everything is cleared and made again.
            Assert.True(await NameCatchUp.RestartIfOutOfDateAsync(run, log, Ct));

            var filled = 0;
            int batch;
            do
            {
                batch = await NameCatchUp.FillBatchAsync(run, Ct);
                filled += batch;
            }
            while (batch > 0);

            Assert.True(filled >= 2, $"filled {filled}");
            await NameCatchUp.MarkCompleteAsync(run, Ct);
        }

        await using (var check = _db.NewContext())
        {
            Assert.Equal("alex", (await check.VRChatUsers.SingleAsync(u => u.UserId == id, Ct)).DisplayNameSearchable);

            var member = await check.DiscordMembers.SingleAsync(m => m.GuildId == guild, Ct);
            Assert.Equal("ada", member.UsernameSearchable);
            Assert.Equal("sienna", member.DisplayNameSearchable);
            Assert.Equal("sienna", member.GlobalNameSearchable);
            Assert.Null(member.NicknameSearchable);

            Assert.Equal(NameNormalizer.Version, (await check.GetSettingsAsync(Ct)).NameCatchUpVersion);

            // Up to date now: nothing is cleared, nothing is left to fill.
            Assert.False(await NameCatchUp.RestartIfOutOfDateAsync(check, log, Ct));
            Assert.Equal(0, await NameCatchUp.FillBatchAsync(check, Ct));
        }
    }
}
