using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;
using Modbot.Core.Security;
using Modbot.Core.Time;

namespace Modbot.Analytics.Giveaways;

/// <summary>What a draw produced, or why there was not one.</summary>
/// <param name="Draw">The draw, once it is written. Null when <paramref name="Problem"/> is set.</param>
/// <param name="Winners">Who won, best first.</param>
/// <param name="Problem">Why no draw was made, in the words a person should see.</param>
public sealed record GiveawayDrawResult(
    GiveawayDraw? Draw,
    IReadOnlyList<GiveawayEntrant> Winners,
    string? Problem = null)
{
    public static GiveawayDrawResult Cannot(string problem) => new(null, [], problem);
}

/// <summary>
/// Makes a draw: freezes the entrant list, reveals the seed, picks the winners, and records all
/// three as one fact.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A draw is never re-run in place</strong> (giveaways design §5.3). Drawing a second time
/// writes a second draw with the next number, its own seed and its own frozen list, and leaves the
/// first exactly where it was. Somebody who did not like the result cannot make it go away; they
/// can only draw again where everyone can see that they did.
/// </para>
/// <para>
/// <strong>The seed is promised before and revealed after.</strong> The giveaway carries the
/// promise and the sealed seed from the moment it is created; this unseals it, writes it into the
/// draw in the open, and puts a fresh sealed seed and promise on the giveaway — so the promise
/// for the next draw is standing before anybody has decided to make one, which is the only order
/// in which a promise means anything.
/// </para>
/// <para>
/// One transaction. A draw whose winners were saved and whose entrant list was not would be a
/// result nobody could check, which is worse than no result.
/// </para>
/// </remarks>
public sealed class GiveawayDrawer
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly ISecretProtector _protector;
    private readonly GiveawayRuleChecker _checker;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;

    public GiveawayDrawer(
        ModbotContext db,
        IModbotClock clock,
        ISecretProtector protector,
        GiveawayRuleChecker checker,
        IFactWriter facts,
        EventPartitionMaintainer partitions)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(checker);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);

        _db = db;
        _clock = clock;
        _protector = protector;
        _checker = checker;
        _facts = facts;
        _partitions = partitions;
    }

    /// <summary>Puts a fresh promise and its sealed seed on a giveaway. Called when one is created.</summary>
    public void Promise(Giveaway giveaway)
    {
        ArgumentNullException.ThrowIfNull(giveaway);

        var seed = GiveawayDraws.NewSeed();
        giveaway.SeedPromise = GiveawayDraws.Promise(seed);
        giveaway.SeedEncrypted = _protector.Protect(seed);
    }

    /// <summary>
    /// Draws <paramref name="giveaway"/>, writing the draw, its frozen entrant list and its fact.
    /// </summary>
    /// <param name="drawnByUserId">The account that pressed Draw, or null when the time came round.</param>
    public async Task<GiveawayDrawResult> DrawAsync(
        Giveaway giveaway, Guid? drawnByUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(giveaway);

        if (giveaway.State is GiveawayStates.Draft or GiveawayStates.Cancelled || giveaway.DeletedAt is not null)
            return GiveawayDrawResult.Cannot("That giveaway is not open.");

        var rules = GiveawayRules.ReadStored(giveaway.Rules);
        var exclusions = GiveawayExclusions.ReadStored(giveaway.Exclusions);

        // Re-checked here and not trusted from when somebody reacted: a person can qualify on
        // Monday and not on Friday, and the draw is the moment that matters (giveaways design §4.2).
        var entrants = giveaway.EntryWay == GiveawayEntryWays.React
            ? await EntriesAsync(giveaway.Id, ct)
            : null;

        var match = await _checker.SnapshotAsync(
            rules, exclusions, giveaway.Weighting, giveaway.WeightCap, entrants, ct);

        if (match.Unanswerable is { } why)
            return GiveawayDrawResult.Cannot(why);

        if (match.InDraw == 0)
            return GiveawayDrawResult.Cannot("Nobody is in this giveaway.");

        var now = _clock.UtcNow;
        var seed = giveaway.SeedEncrypted.Length == 0 ? null : _protector.Unprotect(giveaway.SeedEncrypted);
        var promise = giveaway.SeedPromise;

        // The sealed seed could not be read -- the protector's key is gone. Drawing anyway would
        // mean drawing on a seed that does not match the promise everybody was shown, which is the
        // one thing this whole mechanism exists to prevent (giveaways design §5).
        if (seed is null || !GiveawayDraws.Keeps(promise, seed))
            return GiveawayDrawResult.Cannot("Modbot cannot read the seed it promised for this giveaway.");

        var draw = new GiveawayDraw
        {
            Id = Guid.CreateVersion7(),
            GiveawayId = giveaway.Id,
            Number = giveaway.DrawCount + 1,
            DrawnAt = now,
            DrawnByUserId = drawnByUserId,
            Seed = seed,
            SeedPromise = promise,
            WinnerCount = giveaway.WinnerCount,
            Rules = giveaway.Rules,
            Exclusions = giveaway.Exclusions,
            Weighting = giveaway.Weighting,
            WeightCap = giveaway.WeightCap,
            EntrantCount = match.Total,
            InDrawCount = match.InDraw,
            TotalWeight = match.TotalWeight,
            FromPolledData = match.FromPolledData,
            CloseCalls = match.CloseCalls,
        };

        var rows = new List<GiveawayEntrant>(match.People.Count);
        var position = 0;

        foreach (var listing in match.People)
        {
            rows.Add(new GiveawayEntrant
            {
                DrawId = draw.Id,
                Position = position++,
                Key = listing.Key,
                VRChatUserId = listing.VRChatUserId,
                DiscordUserId = listing.DiscordUserId,
                Name = listing.Name,
                Weight = listing.Weight,
                Measured = listing.Measured,
                KeptOut = listing.KeptOut,
                Because = listing.Because,
                FromPolledData = listing.FromPolledData,
                CloseCall = listing.CloseCall,
            });
        }

        var tickets = rows
            .Select(r => new GiveawayTicket(r.Position, r.Key, r.Weight))
            .ToList();

        var winners = GiveawayDraws.Draw(tickets, giveaway.WinnerCount, seed);

        foreach (var winner in winners)
            rows[winner.Position].WinnerRank = winner.Rank;

        var ownsTransaction = _db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await _db.Database.BeginTransactionAsync(ct) : null;

        try
        {
            _db.GiveawayDraws.Add(draw);
            _db.GiveawayEntrants.AddRange(rows);

            giveaway.DrawCount = draw.Number;
            giveaway.State = GiveawayStates.Drawn;
            giveaway.UpdatedAt = now;

            if (giveaway.ClosedAt is null)
                giveaway.ClosedAt = now;

            // The next promise is made now, so a re-draw cannot choose its seed after seeing who
            // would win under it.
            Promise(giveaway);

            await _db.SaveChangesAsync(ct);
            await RecordAsync(giveaway, draw, rows, drawnByUserId, now, ct);

            if (transaction is not null)
                await transaction.CommitAsync(ct);
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }

        return new GiveawayDrawResult(draw, [.. rows.Where(r => r.WinnerRank is not null).OrderBy(r => r.WinnerRank)]);
    }

    /// <summary>The standing reactions on a giveaway: Discord id to "still there".</summary>
    private async Task<Dictionary<string, bool>> EntriesAsync(Guid giveawayId, CancellationToken ct)
    {
        var entries = await _db.GiveawayEntries.AsNoTracking()
            .Where(e => e.GiveawayId == giveawayId)
            .Select(e => new { e.DiscordUserId, e.WithdrawnAt })
            .ToListAsync(ct);

        return entries.ToDictionary(e => e.DiscordUserId, e => e.WithdrawnAt is null, StringComparer.Ordinal);
    }

    /// <summary>
    /// Records the draw, with everything somebody would need to check it themselves.
    /// </summary>
    /// <remarks>
    /// The seed and the promise both go in the fact, not just the winners. A record of who won is
    /// a claim; a record of who won and what it was drawn from is a claim anybody can test.
    /// </remarks>
    private async Task RecordAsync(
        Giveaway giveaway,
        GiveawayDraw draw,
        IReadOnlyList<GiveawayEntrant> rows,
        Guid? drawnByUserId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await _partitions.EnsureForAsync(now, ct);

        var winners = new JsonArray();

        foreach (var winner in rows.Where(r => r.WinnerRank is not null).OrderBy(r => r.WinnerRank))
        {
            winners.Add(new JsonObject
            {
                ["rank"] = winner.WinnerRank,
                ["key"] = winner.Key,
                ["name"] = winner.Name,
                ["weight"] = winner.Weight,
            });
        }

        await _facts.WriteAsync(
            new FactRecord
            {
                Type = FactType.GiveawayDrawn,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = giveaway.Id.ToString(),
                ActorPlatform = drawnByUserId is null ? null : FactPlatform.Modbot,
                ActorId = drawnByUserId?.ToString(),
                Source = FactSource.Modbot,
                Data = new JsonObject
                {
                    ["name"] = giveaway.Name,
                    ["drawNumber"] = draw.Number,
                    ["drawId"] = draw.Id.ToString(),
                    ["seed"] = draw.Seed,
                    ["seedPromise"] = draw.SeedPromise,
                    ["entrants"] = draw.EntrantCount,
                    ["inDraw"] = draw.InDrawCount,
                    ["totalWeight"] = draw.TotalWeight,
                    ["weighting"] = draw.Weighting,
                    ["weightCap"] = draw.WeightCap,
                    ["fromPolledData"] = draw.FromPolledData,
                    ["winners"] = winners,
                },
            },
            ct);
    }
}
