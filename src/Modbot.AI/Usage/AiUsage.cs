using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using OpenAI.Chat;

namespace Modbot.AI.Usage;

/// <summary>The feature names usage is recorded under.</summary>
public static class AiFeatures
{
    public const string Moderation = "moderation";

    /// <summary>AI insights (AI insights design §6). A scheduled one has no user.</summary>
    public const string Insights = "insights";
}

/// <summary>
/// Where every AI feature records what a request used and asks whether it may make another.
/// </summary>
public interface IAiUsage
{
    /// <summary>Records one request's token counts. Does nothing when the provider sent none.</summary>
    Task RecordAsync(string feature, Guid? userId, string model, string? provider, ChatTokenUsage? usage, CancellationToken ct);

    /// <summary>Whether the feature has used its limit for this month. False when it has no limit.</summary>
    Task<bool> LimitReachedAsync(string feature, CancellationToken ct);
}

public sealed class AiUsageLedger : IAiUsage
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;

    public AiUsageLedger(ModbotContext db, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
    }

    public async Task RecordAsync(
        string feature, Guid? userId, string model, string? provider, ChatTokenUsage? usage, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);

        if (usage is null)
            return;

        _db.AiUsage.Add(new AiUsage
        {
            At = _clock.UtcNow,
            Feature = feature,
            UserId = userId,
            Model = model.Length <= 200 ? model : model[..200],
            Provider = provider,
            InputTokens = usage.InputTokenCount,
            OutputTokens = usage.OutputTokenCount,
            CachedInputTokens = usage.InputTokenDetails?.CachedTokenCount ?? 0,
        });

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> LimitReachedAsync(string feature, CancellationToken ct)
    {
        var limit = await _db.AiFeatureLimits.AsNoTracking()
            .Where(l => l.Feature == feature)
            .Select(l => l.MonthlyTokenLimit)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (limit is null)
            return false;

        var now = _clock.UtcNow.ToUniversalTime();
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        var used = await _db.AiUsage.AsNoTracking()
            .Where(u => u.Feature == feature && u.At >= monthStart)
            .SumAsync(u => (long)u.InputTokens + u.OutputTokens, ct).ConfigureAwait(false);

        return used >= limit.Value;
    }
}
