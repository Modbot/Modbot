using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Analytics.Giveaways;

/// <param name="Closed">Giveaways whose closing time had come.</param>
/// <param name="Drawn">Giveaways whose draw time had come and which were drawn.</param>
/// <param name="Problems">Giveaways whose draw time had come and which could not be drawn, and why.</param>
public sealed record GiveawaySchedulerPass(
    int Closed, int Drawn, IReadOnlyList<(Guid Id, string Problem)> Problems);

/// <summary>
/// Closes giveaways when their closing time comes and draws them when their draw time does.
/// </summary>
/// <remarks>
/// <para>
/// Here rather than beside the Discord post, because neither closing nor drawing is a Discord
/// thing: a giveaway with no channel still closes, and the announcement is what the Discord side
/// does about a draw that has already happened.
/// </para>
/// <para>
/// A draw time that has already passed when Modbot comes back up is still drawn, once. A giveaway
/// that could not be drawn — nobody in it, or a rule that is no longer answerable — is left closed
/// with the reason recorded rather than retried into the ground: the reason will not fix itself,
/// and somebody has to decide what to do about it.
/// </para>
/// </remarks>
public sealed class GiveawayScheduler
{
    /// <summary>How many giveaways one pass will draw. A draw is the heaviest read Modbot does.</summary>
    public const int DrawsPerPass = 3;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly GiveawayDrawer _drawer;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;

    public GiveawayScheduler(
        ModbotContext db,
        IModbotClock clock,
        GiveawayDrawer drawer,
        IFactWriter facts,
        EventPartitionMaintainer partitions)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(drawer);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);

        _db = db;
        _clock = clock;
        _drawer = drawer;
        _facts = facts;
        _partitions = partitions;
    }

    public async Task<GiveawaySchedulerPass> RunOnceAsync(CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var closed = 0;

        var toClose = await _db.Giveaways
            .Where(g => g.DeletedAt == null && g.State == GiveawayStates.Open && g.ClosesAt <= now)
            .ToListAsync(ct);

        foreach (var giveaway in toClose)
        {
            giveaway.State = GiveawayStates.Closed;
            giveaway.ClosedAt = now;
            giveaway.UpdatedAt = now;
            closed++;

            await RecordAsync(FactType.GiveawayClosed, giveaway, now, new JsonObject(), ct);
        }

        if (closed > 0)
            await _db.SaveChangesAsync(ct);

        var toDraw = await _db.Giveaways
            .Where(g => g.DeletedAt == null
                && g.State == GiveawayStates.Closed
                && g.DrawAt != null
                && g.DrawAt <= now)
            .OrderBy(g => g.DrawAt)
            .Take(DrawsPerPass)
            .ToListAsync(ct);

        var drawn = 0;
        var problems = new List<(Guid, string)>();

        foreach (var giveaway in toDraw)
        {
            var result = await _drawer.DrawAsync(giveaway, drawnByUserId: null, ct);

            if (result.Problem is { } problem)
            {
                problems.Add((giveaway.Id, problem));

                // Cleared so the pass does not come back to it every twenty seconds. The giveaway
                // stays closed and somebody draws it by hand once they have dealt with the reason.
                giveaway.DrawAt = null;
                giveaway.UpdatedAt = now;
                await _db.SaveChangesAsync(ct);

                await RecordAsync(
                    FactType.GiveawayPublishFailed, giveaway, now, new JsonObject { ["error"] = problem }, ct);

                continue;
            }

            drawn++;
        }

        return new GiveawaySchedulerPass(closed, drawn, problems);
    }

    private async Task RecordAsync(
        string type, Giveaway giveaway, DateTimeOffset now, JsonObject data, CancellationToken ct)
    {
        await _partitions.EnsureForAsync(now, ct);

        data["name"] = giveaway.Name;

        await _facts.WriteAsync(
            new FactRecord
            {
                Type = type,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = giveaway.Id.ToString(),
                Source = FactSource.Modbot,
                Data = data,
            },
            ct);
    }
}
