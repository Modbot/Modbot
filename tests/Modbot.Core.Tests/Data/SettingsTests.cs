using Microsoft.EntityFrameworkCore;

namespace Modbot.Core.Tests.Data;

[Collection(nameof(PostgresCollection))]
public class SettingsTests
{
    private readonly PostgresFixture _db;

    public SettingsTests(PostgresFixture db) => _db = db;

    [Fact]
    public async Task GetSettingsAsync_CreatesTheSingletonOnFirstCall()
    {
        await using var context = _db.NewContext();

        var settings = await context.GetSettingsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, settings.Id);
    }

    [Fact]
    public async Task GetSettingsAsync_ReturnsTheSameRowEveryTime()
    {
        await using (var write = _db.NewContext())
        {
            var settings = await write.GetSettingsAsync(TestContext.Current.CancellationToken);
            settings.ManagedGroupId = "grp_test_0001";
            await write.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = _db.NewContext();
        var reloaded = await read.GetSettingsAsync(TestContext.Current.CancellationToken);

        Assert.Equal("grp_test_0001", reloaded.ManagedGroupId);
        Assert.Equal(1, await read.Settings.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASecondSettingsRow_IsRejectedByTheDatabase()
    {
        await using var context = _db.NewContext();
        await context.GetSettingsAsync(TestContext.Current.CancellationToken);

        // Bypass EF's change tracker -- the guarantee must live in the database, not in C#.
        // Every non-nullable column is supplied so the row is rejected by the check constraint
        // rather than by a NOT NULL violation, which would prove nothing about the singleton.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO settings (
                    id, onboarding_complete,
                    moderation_fact_retention_days, presence_fact_retention_days,
                    dedup_window_seconds, require_moderation_classification)
                VALUES (2, false, 0, 90, 5, false)
                """,
                TestContext.Current.CancellationToken));

        Assert.Contains("ck_settings_singleton", ex.ToString());
    }
}
