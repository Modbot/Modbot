using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Evidence.Health;
using Modbot.Evidence.Storage;
using Modbot.Evidence.Upload;

namespace Modbot.Demo;

/// <summary>
/// Fills in the demo's history, and puts it back when somebody asks or the clock says to.
/// </summary>
/// <remarks>
/// <para>
/// Stops immediately on anything that is not a demo. On one that is, its first pass writes the year
/// of facts and messages the startup seed left out; after that it wakes every few seconds to see
/// whether a reset has been asked for, and every <c>MODBOT_DEMO_RESET_HOURS</c> it asks for one
/// itself (demo mode design §6).
/// </para>
/// <para>
/// A reset is a wipe and a re-seed, with the progress line saying so the whole way through. It is
/// not clever about it: a demo is made-up data, so there is nothing to preserve and nothing to
/// migrate, and the simplest thing that cannot leave half a group behind is to empty the tables and
/// write them again.
/// </para>
/// </remarks>
public sealed class DemoDataService : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly DemoMode _demo;
    private readonly DemoState _state;
    private readonly DemoPlanHolder _plans;
    private readonly IModbotClock _clock;
    private readonly ILogger<DemoDataService> _log;

    public DemoDataService(
        IServiceScopeFactory scopes,
        DemoMode demo,
        DemoState state,
        DemoPlanHolder plans,
        IModbotClock clock,
        ILogger<DemoDataService> log)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);

        _scopes = scopes;
        _demo = demo;
        _state = state;
        _plans = plans;
        _clock = clock;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_demo.IsOn)
            return;

        // Whatever the startup seed left unfinished. On a restart of a demo that already has its
        // history this finds nothing to do and costs one query.
        await CatchUpAsync(stoppingToken);

        ScheduleNext();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Poll, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var asked = _state.TakeResetRequest();
            var due = _state.NextResetAt is { } next && _clock.UtcNow >= next;

            if (asked is null && !due)
                continue;

            await ResetAsync(asked ?? "the reset schedule", stoppingToken);
            ScheduleNext();
        }
    }

    /// <summary>Writes the history when it is missing, and leaves it alone when it is not.</summary>
    /// <remarks>
    /// The plan startup built is the one the history is written from, so both halves describe the
    /// same group down to the minute. Without it — a process killed between the two halves, say —
    /// the quick half is simply done again, which costs a couple of seconds and cannot leave the
    /// two disagreeing.
    /// </remarks>
    private async Task CatchUpAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        if (await db.Events.AnyAsync(ct))
        {
            _state.Finish(_clock.UtcNow);
            return;
        }

        var started = _clock.UtcNow;

        if (_plans.Plan is null)
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DemoSeeder>();
            await seeder.WipeAsync(ct);
            _plans.Plan = await seeder.SeedCoreAsync(ct);
        }

        await WriteHistoryAsync(scope.ServiceProvider, _plans.Plan, ct);

        _log.LogInformation(
            "Demo history written in {Seconds:F0}s.", (_clock.UtcNow - started).TotalSeconds);
    }

    private async Task ResetAsync(string by, CancellationToken ct)
    {
        _log.LogInformation("Resetting the demo, asked for by {By}.", by);

        var started = _clock.UtcNow;

        try
        {
            using (var scope = _scopes.CreateScope())
            {
                _state.Begin("Clearing the demo");

                var seeder = scope.ServiceProvider.GetRequiredService<DemoSeeder>();
                await seeder.WipeAsync(ct);

                _state.Begin("Filling the demo back in");
                _plans.Plan = await seeder.SeedCoreAsync(ct);
            }

            using (var scope = _scopes.CreateScope())
            {
                await WriteHistoryAsync(scope.ServiceProvider, _plans.Plan!, ct);
            }

            _log.LogInformation("Demo reset in {Seconds:F0}s.", (_clock.UtcNow - started).TotalSeconds);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Never fatal. A demo with half a group in it is still a demo, and the next reset --
            // or the next restart -- puts it right.
            _log.LogError(e, "The demo reset did not finish.");
            _state.Finish(_clock.UtcNow);
        }
    }

    /// <summary>
    /// The heavy half: the year of facts and messages, the totals computed from them, and the
    /// evidence files. The plan is rebuilt from the database's own seed so the two halves agree.
    /// </summary>
    private async Task WriteHistoryAsync(IServiceProvider services, DemoPlan plan, CancellationToken ct)
    {
        var db = services.GetRequiredService<ModbotContext>();
        var settings = await db.GetSettingsAsync(ct);

        if (!settings.DemoData)
        {
            _state.Finish(_clock.UtcNow);
            return;
        }

        await services.GetRequiredService<DemoHistory>().WriteAsync(plan, _state, ct);

        _state.Begin("Closing some reviews");
        await CloseSomeReviewsAsync(db, ct);

        // Evidence only when a store is wired up. A host that maps the demo without the evidence
        // stack still gets every case file, just with nothing attached.
        if (services.GetService<IEvidenceStore>() is { } store
            && services.GetService<IEvidenceMetadata>() is { } metadata)
        {
            _state.Begin("Attaching the evidence");
            await new DemoEvidence(db, store, metadata).WriteAsync(plan, ct);

            // The store marker was written a moment ago, and the verdict this process is holding
            // was taken at startup, before there was one. Without this re-read every piece of
            // evidence stays behind the lost-store lock until the next restart.
            if (services.GetService<EvidenceStoreMonitor>() is { } monitor)
                await monitor.CheckAsync(ct);
        }

        _state.Finish(_clock.UtcNow);
    }

    /// <summary>
    /// Leaves a few reviews open and closes the rest, so the Reviews page shows both.
    /// </summary>
    private static async Task CloseSomeReviewsAsync(ModbotContext db, CancellationToken ct)
    {
        var open = await db.Reviews.Where(r => r.State == ReviewState.Open).ToListAsync(ct);

        var administrator = await db.Users
            .Where(u => u.Id == DemoMode.AdministratorId)
            .Select(u => new { u.Id, u.Username })
            .FirstOrDefaultAsync(ct);

        if (administrator is null)
            return;

        for (var index = 3; index < open.Count; index++)
        {
            var review = open[index];

            review.State = ReviewState.Closed;
            review.ClosedAt = review.OpenedAt.AddDays(1);
            review.ClosedByUserId = administrator.Id;
            review.ClosedByUsername = administrator.Username;
            review.Outcome = index % 4 == 0 ? ReviewOutcome.Wrong : ReviewOutcome.Right;
            review.Note = index % 4 == 0
                ? "Spoke to them. It should have been a warning, not a kick."
                : "Looked through it. Nothing out of order.";
            review.UpdatedAt = review.ClosedAt.Value;
        }

        await db.SaveChangesAsync(ct);
    }

    private void ScheduleNext()
    {
        _state.NextResetAt = _demo.ResetHours > 0
            ? _clock.UtcNow.AddHours(_demo.ResetHours)
            : null;
    }
}
