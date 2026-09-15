using Microsoft.EntityFrameworkCore;
using Modbot.AI.Usage;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using OpenAI.Chat;

namespace Modbot.AI.Calls;

/// <summary>One call, as it is handed to the call log.</summary>
/// <param name="ModelAnswered">What the provider said answered. Null when nothing did.</param>
/// <param name="Fallback">The fallback model answered because the main one did not.</param>
/// <param name="Prompt">Everything the model was sent, as one piece of text. Stored only when <paramref name="KeepText"/>.</param>
/// <param name="Answer">What the model answered, as it answered. Stored only when <paramref name="KeepText"/>.</param>
/// <param name="KeepText">
/// Whether the prompt and answer are stored now. True for a call somebody started from a button;
/// a moderation call sets it later, through <see cref="IAiCallLog.KeepTextAsync"/>, once it knows
/// it produced a flag.
/// </param>
public sealed record AiCallEntry(
    string Feature,
    string ModelAsked,
    string? ModelAnswered,
    string? Provider,
    string Outcome,
    int DurationMs,
    bool Fallback = false,
    string? Error = null,
    ChatTokenUsage? Usage = null,
    Guid? UserId = null,
    string? Username = null,
    string? Prompt = null,
    string? Answer = null,
    bool KeepText = false);

/// <summary>
/// Where every AI call is recorded, whatever came of it (the call log).
/// </summary>
public interface IAiCallLog
{
    /// <summary>Writes one row and answers its id, so a flag can point back at it.</summary>
    Task<Guid> RecordAsync(AiCallEntry entry, CancellationToken ct);

    /// <summary>
    /// Marks a call as having produced a flag and stores what it was sent and what it answered.
    /// Does nothing when the row has gone.
    /// </summary>
    Task KeepTextAsync(Guid callId, string? prompt, string? answer, CancellationToken ct);
}

/// <summary>The call log against the database.</summary>
public sealed class AiCallLog : IAiCallLog
{
    /// <summary>The most of a prompt or an answer that is kept. Longer is cut, with an ellipsis.</summary>
    public const int MaxTextLength = 20_000;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;

    public AiCallLog(ModbotContext db, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
    }

    public async Task<Guid> RecordAsync(AiCallEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var now = _clock.UtcNow;
        var keep = entry.KeepText;

        var row = new AiCall
        {
            Id = Guid.CreateVersion7(now),
            At = now,
            Feature = Clip(entry.Feature, 32)!,
            ModelAsked = Clip(entry.ModelAsked, 200) ?? string.Empty,
            ModelAnswered = Clip(entry.ModelAnswered, 200),
            Provider = Clip(entry.Provider, 32),
            Fallback = entry.Fallback,
            Outcome = entry.Outcome,
            Error = Clip(entry.Error, 1000),
            InputTokens = entry.Usage?.InputTokenCount ?? 0,
            CachedInputTokens = entry.Usage?.InputTokenDetails?.CachedTokenCount ?? 0,
            OutputTokens = entry.Usage?.OutputTokenCount ?? 0,
            ReportedCost = AiReportedCost.Of(entry.Usage, entry.Provider),
            DurationMs = entry.DurationMs,
            UserId = entry.UserId,
            Username = Clip(entry.Username, 64),
            Prompt = keep ? Clip(entry.Prompt, MaxTextLength) : null,
            Answer = keep ? Clip(entry.Answer, MaxTextLength) : null,
        };

        _db.AiCalls.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return row.Id;
    }

    public async Task KeepTextAsync(Guid callId, string? prompt, string? answer, CancellationToken ct)
    {
        await _db.AiCalls
            .Where(c => c.Id == callId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(c => c.Flagged, true)
                .SetProperty(c => c.Prompt, Clip(prompt, MaxTextLength))
                .SetProperty(c => c.Answer, Clip(answer, MaxTextLength)), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes rows older than the operator's keep-for setting. Answers how many went. 0 days keeps
    /// everything.
    /// </summary>
    public static async Task<int> PruneAsync(ModbotContext db, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var days = await db.Settings.AsNoTracking().Where(s => s.Id == 1)
            .Select(s => (int?)s.AiCallLogKeepDays)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? 0;

        if (days <= 0)
            return 0;

        var cutoff = now.AddDays(-days);

        return await db.AiCalls.Where(c => c.At < cutoff).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    private static string? Clip(string? text, int max) =>
        text is null ? null : text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");
}

/// <summary>A call log that records nothing, for a host that runs an AI class without a database.</summary>
public sealed class NoAiCallLog : IAiCallLog
{
    public Task<Guid> RecordAsync(AiCallEntry entry, CancellationToken ct) => Task.FromResult(Guid.Empty);

    public Task KeepTextAsync(Guid callId, string? prompt, string? answer, CancellationToken ct) => Task.CompletedTask;
}
