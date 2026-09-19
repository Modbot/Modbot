using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Analytics.DailyTotals;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Npgsql;

namespace Modbot.Analytics.Retention;

/// <summary>
/// Erases every fact about one subject, on request.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.5. Presence data -- who was where, for how long, wearing what -- is genuinely personal
/// information even though the group could have watched it happen, and the defensible position is
/// that it lives on the group's own server, retention is configurable, and <em>deletion works</em>.
/// This is the part that has to actually work.
/// </para>
/// <para>
/// <strong>This is the one place a DELETE is right.</strong> Retention prunes by dropping
/// partitions because it removes everything below a date; a purge removes one person's rows from
/// every partition at once, and there is no partitioning scheme that makes that a DROP. It is also
/// rare and small: one person's facts, on request.
/// </para>
/// </remarks>
public interface IUserPurger
{
    Task<PurgeResult> PurgeAsync(FactPlatform platform, string subjectId, CancellationToken ct = default);
}

/// <param name="FactsDeleted">Facts erased.</param>
/// <param name="CountedDailyTotalsDeleted">
/// Counted-only daily total rows erased -- the per-user aggregates that have no fact behind them and so
/// would otherwise survive the purge (spec 5.2.1).
/// </param>
/// <param name="DaysRecomputed">Days rebuilt so that no aggregate still includes the facts.</param>
/// <param name="MessagesDeleted">
/// Discord messages the person wrote, erased with every earlier text of them (M5 spec §5.1). Zero
/// for anyone but a Discord user.
/// </param>
/// <param name="GiveawayEntriesDeleted">Standing giveaway entries removed.</param>
/// <param name="GiveawayEntrantsBlanked">
/// Rows in a past draw's frozen entrant list whose name and ids were erased, keeping the place and
/// the weight (giveaways design §6.3).
/// </param>
public sealed record PurgeResult(
    int FactsDeleted,
    int CountedDailyTotalsDeleted,
    int DaysRecomputed,
    int MessagesDeleted = 0,
    int GiveawayEntriesDeleted = 0,
    int GiveawayEntrantsBlanked = 0);

/// <inheritdoc />
public sealed class UserPurger : IUserPurger
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly DailyTotalsJob _dailyTotals;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;

    public UserPurger(
        ModbotContext db,
        IModbotClock clock,
        DailyTotalsJob dailyTotals,
        IFactWriter facts,
        EventPartitionMaintainer partitions)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(dailyTotals);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);

        _db = db;
        _clock = clock;
        _dailyTotals = dailyTotals;
        _facts = facts;
        _partitions = partitions;
    }

    /// <summary>
    /// Erases the subject's facts, the per-user counts that only exist as aggregates, and rebuilds
    /// the daily totals for the days involved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Facts where this person was the <em>actor</em> are kept.</strong> A moderator's ban
    /// is a record about the person who was banned, and erasing it on the moderator's request
    /// would delete someone else's moderation history and destroy the accountability spec 5.8 is
    /// built on. A moderator leaving the group is not grounds to unwrite what they did while they
    /// were in it.
    /// </para>
    /// <para>
    /// Computed daily totals are recomputed rather than deleted. What survives is only what the
    /// surviving facts still say, which is the invariant (spec 5.2) -- deleting derived rows that
    /// a rebuild would put straight back would be theatre. Counted-only rows dimensioned on this
    /// person <em>are</em> deleted: nothing can recompute them, so they are the only copy, and for
    /// a per-user message count that copy is exactly the personal data being erased.
    /// </para>
    /// <para>
    /// One transaction. A purge that deleted the facts and then failed to recompute would leave
    /// aggregates that still counted them, which is the failure that turns "deletion works" into a
    /// claim nobody should have made.
    /// </para>
    /// </remarks>
    public async Task<PurgeResult> PurgeAsync(
        FactPlatform platform,
        string subjectId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectId);

        var ownsTransaction = _db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await _db.Database.BeginTransactionAsync(ct) : null;

        try
        {
            // Collected before the delete: afterwards there is nothing left to ask.
            var days = await _dailyTotals.DaysTouchedBySubjectAsync(platform, subjectId, ct);

            // A Discord user's messages are in the message totals too.
            if (platform == FactPlatform.Discord)
            {
                days = days
                    .Concat(await _dailyTotals.MessageDaysByAuthorAsync(subjectId, ct))
                    .Distinct()
                    .Order()
                    .ToList();
            }

            // Which of this person's facts were one decision, before the facts themselves: the
            // rows name fact ids, and once the facts are gone there is nothing left to match on.
            await ExecuteAsync(
                """
                DELETE FROM modbot_linked_fact l
                USING modbot_event e
                WHERE l.fact_id = e.id AND l.occurred_at = e.occurred_at
                  AND e.subject_platform = @platform AND e.subject_id = @subject
                """,
                ct,
                new NpgsqlParameter("platform", (short)platform),
                new NpgsqlParameter("subject", subjectId));

            // And which other clients reported them, for the same reason.
            await ExecuteAsync(
                """
                DELETE FROM modbot_event_report r
                USING modbot_event e
                WHERE r.fact_id = e.id AND r.occurred_at = e.occurred_at
                  AND e.subject_platform = @platform AND e.subject_id = @subject
                """,
                ct,
                new NpgsqlParameter("platform", (short)platform),
                new NpgsqlParameter("subject", subjectId));

            var factsDeleted = await ExecuteAsync(
                """
                DELETE FROM modbot_event
                WHERE subject_platform = @platform AND subject_id = @subject
                """,
                ct,
                new NpgsqlParameter("platform", (short)platform),
                new NpgsqlParameter("subject", subjectId));

            // The rows that say "this record was imported" name the person too, and leaving
            // them would make a later upload of the same file a silent no-op for exactly the
            // records that were erased (import design §7).
            await ExecuteAsync(
                """
                DELETE FROM import_record
                WHERE subject_platform = @platform AND subject_id = @subject
                """,
                ct,
                new NpgsqlParameter("platform", (short)platform),
                new NpgsqlParameter("subject", subjectId));

            var dailyTotalsDeleted = await ExecuteAsync(
                """
                DELETE FROM modbot_daily_total
                WHERE origin = @origin AND dimension = ANY(@dimensions)
                """,
                ct,
                new NpgsqlParameter("origin", (short)DailyTotalOrigin.Counted),
                new NpgsqlParameter("dimensions", Dimensions(platform, subjectId)));

            var messagesDeleted = platform == FactPlatform.Discord
                ? await DeleteMessagesAsync(subjectId, ct)
                : 0;

            var (entriesDeleted, entrantsBlanked) = await ErasedFromGiveawaysAsync(platform, subjectId, ct);

            if (days.Count > 0)
                await _dailyTotals.RecomputeDaysAsync(days, ct);

            await RecordAsync(factsDeleted, dailyTotalsDeleted, days.Count, messagesDeleted, ct);

            if (transaction is not null)
                await transaction.CommitAsync(ct);

            return new PurgeResult(
                factsDeleted, dailyTotalsDeleted, days.Count, messagesDeleted, entriesDeleted, entrantsBlanked);
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    /// <summary>
    /// The dimension strings a per-user daily total might have been written under.
    /// </summary>
    /// <remarks>
    /// The platform-qualified form is what the daily totals job produces and what callers of
    /// <see cref="IDailyTotalCounter"/> should use, so that a Discord snowflake and a VRChat id cannot
    /// merge into one person. The bare id is included because a counter somewhere may have written
    /// one, and a purge that missed rows on a formatting technicality would be the worst possible
    /// place to be strict.
    /// </remarks>
    private static string[] Dimensions(FactPlatform platform, string subjectId)
        => [DailyTotalDimensions.ForUser(platform, subjectId), subjectId];

    /// <summary>
    /// Records that a purge happened -- and deliberately not who it was about.
    /// </summary>
    /// <remarks>
    /// An erasure log that names the erased person is not an erasure. What is worth keeping is
    /// that data was destroyed and how much, which is what makes the deletion auditable without
    /// undoing it.
    /// </remarks>
    /// <summary>
    /// Deletes a Discord user's messages and every earlier text of them.
    /// </summary>
    /// <remarks>
    /// Messages the person wrote, not messages that mention or reply to them: those are somebody
    /// else's words. Edits first, while the messages still say which ids are theirs.
    /// </remarks>
    private async Task<int> DeleteMessagesAsync(string authorId, CancellationToken ct)
    {
        await ExecuteAsync(
            """
            DELETE FROM discord_message_edit e
            USING discord_message m
            WHERE m.author_id = @author AND e.message_id = m.message_id AND e.sent_at = m.sent_at
            """,
            ct,
            new NpgsqlParameter("author", authorId));

        return await ExecuteAsync(
            "DELETE FROM discord_message WHERE author_id = @author",
            ct,
            new NpgsqlParameter("author", authorId));
    }

    /// <summary>
    /// Takes the person out of every giveaway: their standing entries go, and their name and ids
    /// come off every frozen entrant list they are in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two halves pull opposite ways and both matter. A snapshot is what makes a past draw
    /// checkable (giveaways design §5.1), so deleting the row would make an old result
    /// unverifiable for everybody else; a purge has to actually erase the person, so keeping their
    /// name would make the erasure a claim rather than a fact.
    /// </para>
    /// <para>
    /// What is kept is the place in the list and the weight — the numbers the draw was worked out
    /// from, which are about nobody once the name is gone. Anyone can still reproduce the draw
    /// from the snapshot and the seed; the row that used to be a person now says only "somebody,
    /// with this weight, in this position", and is marked as erased so it does not read as a
    /// missing name.
    /// </para>
    /// </remarks>
    private async Task<(int Entries, int Entrants)> ErasedFromGiveawaysAsync(
        FactPlatform platform, string subjectId, CancellationToken ct)
    {
        var entries = platform == FactPlatform.Discord
            ? await ExecuteAsync(
                "DELETE FROM giveaway_entry WHERE discord_user_id = @subject",
                ct,
                new NpgsqlParameter("subject", subjectId))
            : 0;

        var entrants = await ExecuteAsync(
            """
            UPDATE giveaway_entrant
            SET name = NULL, vrchat_user_id = NULL, discord_user_id = NULL, key = '', purged = TRUE
            WHERE (vrchat_user_id = @subject OR discord_user_id = @subject) AND purged = FALSE
            """,
            ct,
            new NpgsqlParameter("subject", subjectId));

        return (entries, entrants);
    }

    private async Task RecordAsync(int facts, int dailyTotals, int days, int messages, CancellationToken ct)
    {
        await _partitions.EnsureForAsync(_clock.UtcNow, ct);

        await _facts.WriteAsync(
            new FactRecord
            {
                Type = FactType.UserPurged,
                OccurredAt = _clock.UtcNow,
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = "purge",
                Source = FactSource.Modbot,
                Data = new JsonObject
                {
                    ["facts"] = facts,
                    // Key kept as first written: this is fact data already in modbot_event.
                    ["countedDailyTotals"] = dailyTotals,
                    ["daysRecomputed"] = days,
                    ["messages"] = messages,
                },
            },
            ct);
    }

    private async Task<int> ExecuteAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
#pragma warning disable EF1002 // Constant SQL; every value is a parameter.
        return await _db.Database.ExecuteSqlRawAsync(sql, parameters, ct);
#pragma warning restore EF1002
    }
}
