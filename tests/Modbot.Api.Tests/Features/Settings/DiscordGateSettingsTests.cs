using Microsoft.EntityFrameworkCore;
using Modbot.Api.Features.Settings;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Settings;

/// <summary>
/// Changing the join gate's removal time never puts anybody past the deadline at once (join gate
/// design §6): everybody waiting goes back to no later than halfway and is warned again.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class DiscordGateSettingsTests
{
    private readonly PostgresFixture _db;

    public DiscordGateSettingsTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset At = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private async Task<Guid> AddAsync(int minutesCounted, bool warned, bool closed = false)
    {
        await using var context = _db.NewContext();

        var entry = new DiscordGateEntry
        {
            GuildId = "700",
            DiscordUserId = Guid.NewGuid().ToString("n"),
            DiscordUsername = "newcomer",
            JoinedAt = At,
            MinutesCounted = minutesCounted,
            WarnedAt = warned ? At : null,
            ClosedAt = closed ? At : null,
            Outcome = closed ? DiscordGateOutcomes.Passed : null,
        };

        context.DiscordGateEntries.Add(entry);
        await context.SaveChangesAsync(Ct);
        return entry.Id;
    }

    private async Task<DiscordGateEntry> ReadAsync(Guid id)
    {
        await using var context = _db.NewContext();
        return await context.DiscordGateEntries.AsNoTracking().FirstAsync(e => e.Id == id, Ct);
    }

    [Fact]
    public async Task AShorterRemovalTime_PutsEverybodyBackToHalfway_AndWarnsThemAgain()
    {
        await using (var context = _db.NewContext())
            await context.DiscordGateEntries.ExecuteDeleteAsync(Ct);

        var late = await AddAsync(minutesCounted: 600, warned: true);
        var early = await AddAsync(minutesCounted: 4, warned: false);
        var done = await AddAsync(minutesCounted: 600, warned: true, closed: true);

        await using (var context = _db.NewContext())
            await DiscordGateSettingsEndpoints.RestartClocksAsync(context, 30, Ct);

        var lateNow = await ReadAsync(late);
        Assert.Equal(15, lateNow.MinutesCounted);
        Assert.Null(lateNow.WarnedAt);

        Assert.Equal(4, (await ReadAsync(early)).MinutesCounted);

        // A row that already ended is history and stays as it was.
        var doneNow = await ReadAsync(done);
        Assert.Equal(600, doneNow.MinutesCounted);
        Assert.NotNull(doneNow.WarnedAt);
    }

    [Fact]
    public async Task RemovalTurnedOff_ClearsTheTimeCounted()
    {
        await using (var context = _db.NewContext())
            await context.DiscordGateEntries.ExecuteDeleteAsync(Ct);

        var waiting = await AddAsync(minutesCounted: 20, warned: true);

        await using (var context = _db.NewContext())
            await DiscordGateSettingsEndpoints.RestartClocksAsync(context, null, Ct);

        var now = await ReadAsync(waiting);
        Assert.Equal(0, now.MinutesCounted);
        Assert.Null(now.WarnedAt);
    }
}
