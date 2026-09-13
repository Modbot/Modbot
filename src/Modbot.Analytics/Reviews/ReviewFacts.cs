using System.Text.Json.Nodes;
using Modbot.Analytics.Facts;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Analytics.Reviews;

/// <summary>
/// Writes the two facts a review produces, so the opening and the closing have one shape wherever
/// they are written from (the detection job opens; the API closes).
/// </summary>
/// <remarks>
/// The subject is the moderator on the VRChat platform, because the review is about their VRChat
/// actions and belongs in their history alongside them. Opening has no actor -- Modbot did it.
/// Closing names the Modbot account, the way every other Modbot-side action does (spec 5.9).
/// </remarks>
public sealed class ReviewFacts
{
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly IModbotClock _clock;

    public ReviewFacts(IFactWriter facts, EventPartitionMaintainer partitions, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(clock);

        _facts = facts;
        _partitions = partitions;
        _clock = clock;
    }

    public async Task OpenedAsync(Review review, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(review);

        var now = _clock.UtcNow;
        await _partitions.EnsureForAsync(now, ct);

        await _facts.WriteAsync(
            new FactRecord
            {
                Type = FactType.ReviewOpened,
                OccurredAt = now,
                SubjectPlatform = review.ModeratorPlatform,
                SubjectId = review.ModeratorId,
                Source = FactSource.Modbot,
                Data = new JsonObject
                {
                    ["reviewId"] = review.Id.ToString(),
                    ["signal"] = review.Signal,
                    ["about"] = review.About,
                    ["description"] = review.Summary,
                    ["evidence"] = JsonNode.Parse(review.Evidence),
                },
            },
            ct);
    }

    public async Task ClosedAsync(Review review, Guid closedByUserId, string closedByUsername, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(review);

        var now = _clock.UtcNow;
        await _partitions.EnsureForAsync(now, ct);

        await _facts.WriteAsync(
            new FactRecord
            {
                Type = FactType.ReviewClosed,
                OccurredAt = now,
                SubjectPlatform = review.ModeratorPlatform,
                SubjectId = review.ModeratorId,
                ActorPlatform = FactPlatform.Modbot,
                ActorId = closedByUserId.ToString(),
                Source = FactSource.Modbot,
                Data = new JsonObject
                {
                    ["reviewId"] = review.Id.ToString(),
                    ["signal"] = review.Signal,
                    ["about"] = review.About,
                    ["note"] = review.Note,
                    ["description"] = $"Review closed by {closedByUsername}: {review.Note}",
                    ["actorDisplayName"] = closedByUsername,
                },
            },
            ct);
    }
}
