using Modbot.Core.Data;
using Modbot.Core.Posts;

namespace Modbot.Core.Bluesky;

/// <summary>
/// Deletes a post on Bluesky for the Marketing tab (posts design §4.2c): <c>deleteRecord</c> on the
/// account in Settings, with the session's sign-in.
/// </summary>
/// <remarks>
/// One delete, made at once. A token that ended is refreshed and the delete asked once more, since a
/// refused token did nothing; a rate limit stops the Bluesky lane and is said, never waited out or
/// sent again (CLAUDE.md). A post already gone counts as deleted.
/// </remarks>
public sealed class BlueskyPostActions(ModbotContext db, BlueskySession session, BlueskyClient client) : IBlueskyPostActions
{
    public const string OtherAccount = "That post is on another Bluesky account.";
    public const string NotSetUp = "Bluesky is not set up.";

    public async Task<PostSiteOutcome> DeleteAsync(string did, string recordKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(did);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordKey);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var signIn = await session.AccessAsync(db, prove: false, ct).ConfigureAwait(false);

            if (signIn.Access is not { } access)
            {
                return signIn.Hold == BlueskyHold.NotSetUp
                    ? new PostSiteOutcome(false, NotSetUp, BotOffline: true)
                    : PostSiteOutcome.Failed(signIn.Problem ?? BlueskyErrors.Unreachable);
            }

            if (!string.Equals(access.Did, did, StringComparison.Ordinal))
                return PostSiteOutcome.Failed(OtherAccount);

            var deleted = await client.DeleteRecordAsync(access.Server, access.AccessJwt, did, BlueskyClient.PostCollection, recordKey, ct)
                .ConfigureAwait(false);

            if (deleted.Failure is not { } failure || failure.Problem == BlueskyProblem.RecordNotFound)
                return PostSiteOutcome.Ok;

            if (failure.StopsTheLane)
            {
                await session.StopAsync(db, failure, ct).ConfigureAwait(false);
                return PostSiteOutcome.Failed(BlueskyErrors.Limited);
            }

            if (failure.Problem is BlueskyProblem.TokenExpired or BlueskyProblem.TokenRefused && attempt == 0)
            {
                await session.ExpiredAsync(db, access.AccessJwt, ct).ConfigureAwait(false);
                continue;
            }

            return PostSiteOutcome.Failed(BlueskyErrors.Sentence(failure));
        }

        return PostSiteOutcome.Failed(BlueskyErrors.Unreachable);
    }
}
