using Microsoft.EntityFrameworkCore;
using Modbot.AI.Calls;
using Modbot.AI.Usage;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using OpenAI.Chat;

namespace Modbot.AI.Insights;

/// <summary>Who or what started an insight, and where a scheduled one is posted.</summary>
public sealed record InsightStart(string StartedBy, Guid? UserId = null, string? Username = null, string? DiscordChannelId = null)
{
    public static InsightStart Schedule(string? discordChannelId) => new(InsightKinds.StartedBySchedule, DiscordChannelId: discordChannelId);

    /// <summary>Generate now. Never posted to Discord: somebody pressing the button is checking what it looks like.</summary>
    public static InsightStart Button(Guid userId, string username) => new(InsightKinds.StartedByButton, userId, username);
}

/// <summary>What came of asking for one insight.</summary>
/// <param name="Insight">The stored insight -- with its text, or with the error when the call failed.</param>
/// <param name="NotAsked">Why the model was not asked at all: AI is off, or a spend limit is reached,
/// in which case it is that limit's sentence. Nothing is stored then.</param>
public sealed record InsightAttempt(Insight? Insight, string? NotAsked)
{
    public const string AiOff = "AI is off. Turn it on under Base.";
}

/// <summary>
/// Gathers the figures for one insight, asks the model to write about them, and stores both.
/// </summary>
/// <remarks>
/// Never acts on anything it writes (M8 §2). The only thing that leaves this class is a stored row.
/// </remarks>
public sealed class InsightWriter(
    ModbotContext db, IAiClients ai, IAiUsage usage, IModbotClock clock, InsightFigureReader reader, AiCallRunner runner)
{
    /// <summary>
    /// Room for a reasoning model's thinking as well as its answer. The answer itself is asked to be
    /// short; a budget sized for the answer alone comes back empty from models that think first.
    /// </summary>
    private const int MaxOutputTokens = 4000;

    /// <summary>
    /// Writes one insight for the stretch ending the day before <paramref name="today"/>.
    /// </summary>
    public async Task<InsightAttempt> WriteAsync(string kind, string every, DateOnly today, InsightStart start, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(start);

        if (!InsightKinds.IsKnown(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a kind of insight.");

        var chat = await ai.GetChatAsync(ct);
        if (chat is null)
            return new InsightAttempt(null, InsightAttempt.AiOff);

        // Read through the shared place every AI feature asks: the limit for everyone and the one for
        // insights (design §6).
        if (await usage.LimitReachedAsync(AiFeatures.Insights, ct) is { } reached)
        {
            await runner.RecordLimitedAsync(AiFeatures.Insights, chat.Model, chat.Provider, reached.Message, start.UserId, ct);
            return new InsightAttempt(null, reached.Message);
        }

        var settings = await db.InsightSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var model = string.IsNullOrWhiteSpace(settings?.Model) ? chat.Model : settings.Model.Trim();

        var figures = await reader.ReadAsync(kind, InsightPeriod.Ending(today, every), ct);

        var insight = new Insight
        {
            Kind = kind,
            FirstDay = figures.FirstDay,
            LastDay = figures.LastDay,
            StartedBy = start.StartedBy,
            RequestedByUserId = start.UserId,
            RequestedByUsername = start.Username,
            Model = model,
            Provider = chat.Provider,
            Figures = figures.ToJson(),
            DiscordChannelId = start.DiscordChannelId,
        };

        // The instructions never change for a kind of insight; the figures do. Instructions first,
        // so a provider that caches prefixes charges for them once.
        var instructions = InsightPrompt.Instructions(kind);
        var question = InsightPrompt.Figures(figures);

        var plan = new AiCallPlan(
            AiFeatures.Insights, chat, model,
            Prompt: $"{instructions}\n\n{question}",
            UserId: start.UserId,
            Username: start.Username,
            // Somebody pressed Generate now: they are looking at what the model was asked.
            KeepText: start.StartedBy == InsightKinds.StartedByButton);

        var result = await runner.RunAsync(plan, async (client, token) =>
        {
            var options = new ChatCompletionOptions { MaxOutputTokenCount = MaxOutputTokens };
            AiReportedCost.AskFor(options, chat.Provider);

            ChatCompletion completion = await client.CompleteChatAsync(
                [AiPromptCache.Instructions(instructions, chat.Provider), new UserChatMessage(question)],
                options,
                token);

            var answer = string.Concat(completion.Content
                .Where(p => p.Kind == ChatMessageContentPartKind.Text)
                .Select(p => p.Text)).Trim();

            return new AiCallAnswer<string>(answer, completion.Model, completion.Usage, answer);
        }, ct);

        if (!result.Answered)
        {
            insight.Error = result.Error;
        }
        else
        {
            var text = result.Value ?? string.Empty;

            if (text.Length == 0)
                insight.Error = "The model answered with no text.";
            else
                insight.Text = text.Length <= InsightPrompt.MaxTextLength ? text : string.Concat(text.AsSpan(0, InsightPrompt.MaxTextLength), "…");

            // The model that answered, which may be the fallback rather than the one asked for.
            if (result.Model.Length <= AiSettingsRules.MaxModelLength)
                insight.Model = result.Model;
        }

        // Discord is never told about an attempt that has nothing to say.
        if (insight.Text is null)
            insight.DiscordChannelId = null;

        if (insight.Error is { Length: > 1000 })
            insight.Error = string.Concat(insight.Error.AsSpan(0, 999), "…");

        insight.CreatedAt = clock.UtcNow;
        db.Insights.Add(insight);
        await db.SaveChangesAsync(ct);

        return new InsightAttempt(insight, null);
    }
}
