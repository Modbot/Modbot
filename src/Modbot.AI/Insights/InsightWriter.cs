using Microsoft.EntityFrameworkCore;
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
public sealed class InsightWriter(ModbotContext db, IAiClients ai, IAiUsage usage, IModbotClock clock, InsightFigureReader reader)
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
            return new InsightAttempt(null, reached.Message);

        var settings = await db.InsightSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var model = string.IsNullOrWhiteSpace(settings?.Model) ? chat.Model : settings.Model.Trim();
        var client = model == chat.Model ? chat.Chat : chat.Client.GetChatClient(model);

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

        try
        {
            var options = new ChatCompletionOptions { MaxOutputTokenCount = MaxOutputTokens };
            AiReportedCost.AskFor(options, chat.Provider);

            ChatCompletion completion = await client.CompleteChatAsync(
                [
                    new SystemChatMessage(InsightPrompt.Instructions(kind)),
                    new UserChatMessage(InsightPrompt.Figures(figures)),
                ],
                options,
                ct);

            var text = string.Concat(completion.Content
                .Where(p => p.Kind == ChatMessageContentPartKind.Text)
                .Select(p => p.Text)).Trim();

            if (text.Length == 0)
                insight.Error = "The model answered with no text.";
            else
                insight.Text = text.Length <= InsightPrompt.MaxTextLength ? text : string.Concat(text.AsSpan(0, InsightPrompt.MaxTextLength), "…");

            // Under the model asked for, which is what a price is saved under; the person only when
            // somebody pressed the button.
            await usage.RecordAsync(AiFeatures.Insights, start.UserId, model, chat.Provider, completion.Usage, ct);

            if (!string.IsNullOrWhiteSpace(completion.Model))
                insight.Model = completion.Model.Length <= AiSettingsRules.MaxModelLength ? completion.Model : model;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            var endpoint = await db.Settings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.AiEndpoint).FirstOrDefaultAsync(ct);
            insight.Error = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                ? AiClients.Describe(e, uri)
                : e.Message;
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
