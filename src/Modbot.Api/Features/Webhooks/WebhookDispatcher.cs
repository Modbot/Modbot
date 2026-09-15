using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Api.Auth;
using Modbot.Api.Features.Events;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Api.Features.Webhooks;

/// <summary>
/// One pass of webhook delivery: every webhook that is on and due sends what it has waiting, in
/// fact order (API keys design §6.3, §6.6).
/// </summary>
/// <remarks>
/// <para>
/// <strong>At least once, in order.</strong> The cursor moves only after an event went out or was
/// given up on, and is saved after every request, so a crash repeats at most one event.
/// </para>
/// <para>
/// A failure holds the webhook's later events behind it and schedules the next attempt; the pass
/// moves on to the next webhook. Every answer that is not a 2xx extends the run of failures, and a
/// run that has lasted <see cref="WebhookOptions.TurnOffAfter"/> turns the webhook off with the
/// reason recorded.
/// </para>
/// </remarks>
public sealed class WebhookDispatcher
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly WebhookSender _sender;
    private readonly ISecretProtector _protector;
    private readonly ApiCallers _callers;
    private readonly AccountFacts _facts;
    private readonly WebhookOptions _options;

    public WebhookDispatcher(
        ModbotContext db,
        IModbotClock clock,
        WebhookSender sender,
        ISecretProtector protector,
        ApiCallers callers,
        AccountFacts facts,
        WebhookOptions options)
    {
        _db = db;
        _clock = clock;
        _sender = sender;
        _protector = protector;
        _callers = callers;
        _facts = facts;
        _options = options;
    }

    /// <returns>How many requests were made.</returns>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;

        var due = await _db.Webhooks
            .Where(w => w.Enabled && (w.NextAttemptAt == null || w.NextAttemptAt <= now))
            .OrderBy(w => w.CreatedAt)
            .ToListAsync(ct);

        if (due.Count == 0)
            return 0;

        var allowPrivate = await AllowPrivateAsync(_db, ct);
        var sent = 0;

        foreach (var hook in due)
            sent += await DeliverAsync(hook, allowPrivate, ct);

        return sent;
    }

    public static async Task<bool> AllowPrivateAsync(ModbotContext db, CancellationToken ct)
        => await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => (bool?)s.WebhooksAllowPrivateAddresses)
            .FirstOrDefaultAsync(ct) ?? false;

    private async Task<int> DeliverAsync(Webhook hook, bool allowPrivate, CancellationToken ct)
    {
        var owner = await _callers.ForUserAsync(hook.CreatedByUserId, ct);
        if (owner is null)
        {
            await TurnOffAsync(hook, "The account that set it up is disabled.", ct);
            return 0;
        }

        if (!EventFilter.TryCreate(hook.EventTypes, hook.SubjectIds, out var filter, out _)
            || !Uri.TryCreate(hook.Url, UriKind.Absolute, out var url))
        {
            await TurnOffAsync(hook, "Its address or event types are not valid.", ct);
            return 0;
        }

        var secret = _protector.Unprotect(hook.SecretEncrypted) ?? string.Empty;
        var feed = new FactFeed(_db, _options.GapWait);
        var sent = 0;

        while (sent < _options.EventsPerPass)
        {
            var page = await feed.ReadAsync(hook.DeliveredThrough, _options.PageSize, _clock.UtcNow, ct);
            if (page.Facts.Count == 0)
                break;

            foreach (var fact in page.Facts)
            {
                if (!filter.Matches(fact) || !EventVisibility.CanSee(owner.Permissions, fact.Type))
                {
                    hook.DeliveredThrough = fact.Id;
                    continue;
                }

                if (sent >= _options.EventsPerPass)
                    break;

                var result = await _sender.SendAsync(url, secret, EventEnvelopes.From(fact), allowPrivate, ct);
                sent++;

                var now = _clock.UtcNow;
                await RecordAsync(hook, fact.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), fact.Type, hook.FailedAttempts + 1, result, test: false, now, ct);

                if (result.Outcome == WebhookSendOutcome.Delivered)
                {
                    hook.DeliveredThrough = fact.Id;
                    hook.FailedAttempts = 0;
                    hook.FailingSince = null;
                    hook.NextAttemptAt = null;
                    hook.LastError = null;
                    hook.LastSuccessAt = now;
                    await _db.SaveChangesAsync(ct);
                    continue;
                }

                hook.FailingSince ??= now;
                hook.LastError = result.Error;

                if (result.Outcome == WebhookSendOutcome.Skip)
                {
                    hook.DeliveredThrough = fact.Id;
                    hook.FailedAttempts = 0;
                }
                else
                {
                    hook.FailedAttempts++;
                    hook.NextAttemptAt = now + _options.RetryDelay(hook.FailedAttempts, result.RetryAfter);
                }

                if (now - hook.FailingSince.Value >= _options.TurnOffAfter)
                {
                    await TurnOffAsync(
                        hook,
                        $"Failing since {hook.FailingSince.Value:yyyy-MM-dd HH:mm} UTC: {hook.LastError}",
                        ct);
                    return sent;
                }

                await _db.SaveChangesAsync(ct);

                if (result.Outcome == WebhookSendOutcome.Retry)
                    return sent;
            }

            await _db.SaveChangesAsync(ct);

            if (page.Facts.Count < _options.PageSize)
                break;
        }

        await _db.SaveChangesAsync(ct);
        return sent;
    }

    /// <summary>Writes one attempt to the delivery log and trims the log to the newest few.</summary>
    public async Task RecordAsync(
        Webhook hook, string eventId, string eventType, int attempt, WebhookSendResult result, bool test, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(hook);
        ArgumentNullException.ThrowIfNull(result);

        _db.WebhookDeliveries.Add(new WebhookDelivery
        {
            WebhookId = hook.Id,
            EventId = eventId,
            EventType = eventType.Length <= 128 ? eventType : eventType[..128],
            AttemptedAt = now,
            Attempt = attempt,
            StatusCode = result.StatusCode,
            DurationMs = (int)Math.Min(int.MaxValue, Math.Max(0, result.Duration.TotalMilliseconds)),
            Error = result.Error is { Length: > 512 } tooLong ? tooLong[..512] : result.Error,
            Test = test,
            Outcome = result.Outcome switch
            {
                WebhookSendOutcome.Delivered => "delivered",
                WebhookSendOutcome.Retry => "retrying",
                _ => "skipped",
            },
        });

        await _db.SaveChangesAsync(ct);

        var oldestKept = await _db.WebhookDeliveries.AsNoTracking()
            .Where(d => d.WebhookId == hook.Id)
            .OrderByDescending(d => d.Id)
            .Skip(_options.DeliveriesKept - 1)
            .Select(d => (long?)d.Id)
            .FirstOrDefaultAsync(ct);

        if (oldestKept is { } keep)
        {
            await _db.WebhookDeliveries
                .Where(d => d.WebhookId == hook.Id && d.Id < keep)
                .ExecuteDeleteAsync(ct);
        }
    }

    private async Task TurnOffAsync(Webhook hook, string reason, CancellationToken ct)
    {
        var now = _clock.UtcNow;

        hook.Enabled = false;
        hook.DisabledAt = now;
        hook.DisabledReason = reason.Length <= 640 ? reason : reason[..640];
        hook.NextAttemptAt = null;

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await _db.SaveChangesAsync(ct);

        await _facts.RecordAsync(
            FactType.WebhookDisabled,
            hook.Id.ToString(),
            null,
            new JsonObject { ["name"] = hook.Name, ["url"] = hook.Url, ["reason"] = hook.DisabledReason },
            ct);

        await transaction.CommitAsync(ct);

        Log.Warning("Webhook {Name} turned off: {Reason}", hook.Name, hook.DisabledReason);
    }
}

/// <summary>Runs <see cref="WebhookDispatcher"/> on a timer, one scope per pass.</summary>
public sealed class WebhookDeliveryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly WebhookOptions _options;

    public WebhookDeliveryService(IServiceScopeFactory scopes, WebhookOptions options)
    {
        _scopes = scopes;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<WebhookDispatcher>().RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                Log.Error(e, "A webhook delivery pass failed; trying again shortly");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
