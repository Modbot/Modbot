using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Reviews;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Analytics.Tests.Reviews;

/// <summary>
/// The repeat-offender and review suites share the analytics base (private database, fixed
/// clock, facts through the real writer) and add the job under test and a few lookups.
/// </summary>
public abstract class ReviewTestBase : AnalyticsTestBase
{
    protected ReviewTestBase(PostgresFixture fixture) : base(fixture) { }

    protected ReviewJob NewReviewJob(ModbotContext context) => new(
        context,
        new ReviewFacts(new FactWriter(context, Clock), new EventPartitionMaintainer(context, Clock), Clock),
        Clock);

    /// <summary>Runs the daily totals and then the detection run, the way the host does.</summary>
    protected async Task<ReviewRunResult> RunAsync(bool rebuild = false)
    {
        await using var context = Database.NewContext();
        await NewJob(context).RunIncrementalAsync(Ct);

        return rebuild
            ? await NewReviewJob(context).RebuildAsync(Ct)
            : await NewReviewJob(context).RunIncrementalAsync(Ct);
    }

    protected async Task<RepeatOffender?> OffenderAsync(string subjectId)
    {
        await using var context = Database.NewContext();
        return await context.RepeatOffenders.AsNoTracking()
            .FirstOrDefaultAsync(r => r.SubjectId == subjectId, Ct);
    }

    protected async Task<List<Review>> ReviewsAsync(string? moderatorId = null)
    {
        await using var context = Database.NewContext();
        return await context.Reviews.AsNoTracking()
            .Where(r => moderatorId == null || r.ModeratorId == moderatorId)
            .OrderBy(r => r.OpenedAt).ThenBy(r => r.Id)
            .ToListAsync(Ct);
    }

    /// <summary>Closes a review the way the API does: state, who, when, note.</summary>
    protected async Task CloseAsync(Guid id, string note = "Looked at it; fine.")
    {
        await using var context = Database.NewContext();
        var review = await context.Reviews.FirstAsync(r => r.Id == id, Ct);
        review.State = ReviewState.Closed;
        review.ClosedAt = Clock.UtcNow;
        review.ClosedByUserId = Guid.NewGuid();
        review.ClosedByUsername = "owner";
        review.Note = note;
        await context.SaveChangesAsync(Ct);
    }

    protected async Task<List<ModbotEvent>> FactsAsync(string type)
    {
        await using var context = Database.NewContext();
        return await context.Events.AsNoTracking()
            .Where(e => e.Type == type)
            .OrderBy(e => e.Id)
            .ToListAsync(Ct);
    }

    protected async Task SetThresholdsAsync(ReviewThresholds thresholds)
    {
        await using var context = Database.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        settings.ReviewThresholds = thresholds.ToJson();
        await context.SaveChangesAsync(Ct);
    }

    protected static System.Text.Json.JsonElement Evidence(Review review)
        => System.Text.Json.JsonDocument.Parse(review.Evidence).RootElement.Clone();
}
