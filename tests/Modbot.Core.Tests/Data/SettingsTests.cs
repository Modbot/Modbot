using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

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

    /// <summary>The Google Calendar columns (Google Calendar design §3.1) are written and read back.</summary>
    [Fact]
    public async Task TheGoogleCalendarColumnsRoundTrip()
    {
        var ct = TestContext.Current.CancellationToken;
        var checkedAt = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

        await using (var write = _db.NewContext())
        {
            var settings = await write.GetSettingsAsync(ct);
            settings.GoogleClientEmail = "modbot@test-project.iam.gserviceaccount.com";
            settings.GoogleKeyId = "0123456789abcdef";
            settings.GoogleProjectId = "test-project";
            settings.GooglePrivateKeyEncrypted = "encrypted-pem";
            settings.GoogleCalendarId = "c_abc123@group.calendar.google.com";
            settings.GoogleCheckedAt = checkedAt;
            settings.GoogleCalendarName = "Group events";
            settings.GoogleCalendarTimeZone = "Europe/London";
            settings.GoogleCanChange = true;
            settings.GooglePublic = "all";
            settings.GoogleProblem = "Google is limiting Modbot.";
            settings.GoogleStoppedUntil = checkedAt.AddMinutes(15);
            await write.SaveChangesAsync(ct);
        }

        await using var read = _db.NewContext();
        var reloaded = await read.Settings.AsNoTracking().SingleAsync(ct);

        Assert.Equal("modbot@test-project.iam.gserviceaccount.com", reloaded.GoogleClientEmail);
        Assert.Equal("0123456789abcdef", reloaded.GoogleKeyId);
        Assert.Equal("test-project", reloaded.GoogleProjectId);
        Assert.Equal("encrypted-pem", reloaded.GooglePrivateKeyEncrypted);
        Assert.Equal("c_abc123@group.calendar.google.com", reloaded.GoogleCalendarId);
        Assert.Equal(checkedAt, reloaded.GoogleCheckedAt);
        Assert.Equal("Group events", reloaded.GoogleCalendarName);
        Assert.Equal("Europe/London", reloaded.GoogleCalendarTimeZone);
        Assert.True(reloaded.GoogleCanChange);
        Assert.Equal("all", reloaded.GooglePublic);
        Assert.Equal("Google is limiting Modbot.", reloaded.GoogleProblem);
        Assert.Equal(checkedAt.AddMinutes(15), reloaded.GoogleStoppedUntil);
    }

    /// <summary>
    /// The columns Google Calendar's step 2 added: Sending and removing on the settings row, the tick
    /// on the event, the calendar on the place, and a date's own Google state.
    /// </summary>
    [Fact]
    public async Task TheGoogleSendingColumnsRoundTrip()
    {
        var ct = TestContext.Current.CancellationToken;
        var at = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var eventId = Guid.CreateVersion7();

        await using (var write = _db.NewContext())
        {
            var settings = await write.GetSettingsAsync(ct);
            settings.GoogleSendingOn = true;
            settings.GoogleRemovingEvents = true;

            write.CalendarEvents.Add(new CalendarEvent
            {
                Id = eventId,
                Title = "Movie night",
                StartsAt = at,
                EndsAt = at.AddHours(2),
                PublishToGoogle = true,
                CreatedAt = at,
                UpdatedAt = at,
                DateChanges =
                [
                    new CalendarDateChange
                    {
                        Id = Guid.CreateVersion7(),
                        EventId = eventId,
                        PlannedStartsAt = at.AddDays(7),
                        GoogleSentFingerprint = "sent",
                        GoogleFailedFingerprint = "failed",
                        GoogleError = "Could not find this date on Google.",
                        GoogleErrorAt = at,
                        CreatedAt = at,
                        UpdatedAt = at,
                    },
                ],
            });

            write.CalendarEventPlaces.Add(new CalendarEventPlace
            {
                EventId = eventId,
                Place = CalendarPlaces.Google,
                ExternalId = "mb0123456789abcdefghijklmnop0",
                GoogleCalendarId = "c_abc123@group.calendar.google.com",
                UpdatedAt = at,
            });

            await write.SaveChangesAsync(ct);
        }

        await using var read = _db.NewContext();
        var reloaded = await read.Settings.AsNoTracking().SingleAsync(ct);
        Assert.True(reloaded.GoogleSendingOn);
        Assert.True(reloaded.GoogleRemovingEvents);

        var e = await read.CalendarEvents.AsNoTracking().SingleAsync(x => x.Id == eventId, ct);
        Assert.True(e.PublishToGoogle);

        var date = Assert.Single(e.DateChanges);
        Assert.Equal("sent", date.GoogleSentFingerprint);
        Assert.Equal("failed", date.GoogleFailedFingerprint);
        Assert.Equal("Could not find this date on Google.", date.GoogleError);
        Assert.Equal(at, date.GoogleErrorAt);

        var place = await read.CalendarEventPlaces.AsNoTracking().SingleAsync(p => p.EventId == eventId, ct);
        Assert.Equal("c_abc123@group.calendar.google.com", place.GoogleCalendarId);
    }
}
