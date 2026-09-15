using System.Text.Json;
using Modbot.Core.Data;
using Modbot.Core.Time;
using OpenAI.Chat;

namespace Modbot.AI.Usage;

/// <summary>The feature names usage is recorded and limited under.</summary>
public static class AiFeatures
{
    public const string Moderation = "moderation";

    /// <summary>AI insights (AI insights design §6). A scheduled one has no user.</summary>
    public const string Insights = "insights";

    public const string Chat = "chat";

    /// <summary>The Test button on Settings → AI → Base.</summary>
    public const string Test = "test";

    /// <summary>Every feature, in the order the limits page lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Moderation, Insights, Chat, Test];

    public static string LabelOf(string feature) => feature switch
    {
        Moderation => "Moderation",
        Insights => "Insights",
        Chat => "Chat",
        Test => "Connection test",
        _ => feature,
    };

    /// <summary>
    /// Whether limits set on a person or a role apply. Only Chat: it is the one feature a person
    /// drives call after call, and the other features that name a person do so for one button press.
    /// </summary>
    public static bool HasPersonLimits(string feature) => feature == Chat;
}

/// <summary>
/// Where every AI feature records what a request used and asks whether it may make another.
/// </summary>
public interface IAiUsage
{
    /// <summary>
    /// Records one request's token counts, and the provider's own cost when it sent one. Does nothing
    /// when the provider sent no counts.
    /// </summary>
    Task RecordAsync(string feature, Guid? userId, string model, string? provider, ChatTokenUsage? usage, CancellationToken ct);

    /// <summary>
    /// The limit that stops this feature from making another request -- the one for everyone or the
    /// feature's own -- or null when it may go on. For a request somebody makes in Chat, use
    /// <see cref="AiSpendLimits.CheckAsync"/>, which adds their own limits.
    /// </summary>
    Task<AiLimitReached?> LimitReachedAsync(string feature, CancellationToken ct);
}

public sealed class AiUsageLedger : IAiUsage
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly AiSpendLimits _limits;

    public AiUsageLedger(ModbotContext db, IModbotClock clock, AiSpendLimits limits)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(limits);

        _db = db;
        _clock = clock;
        _limits = limits;
    }

    public async Task RecordAsync(
        string feature, Guid? userId, string model, string? provider, ChatTokenUsage? usage, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);

        if (usage is null)
            return;

        _db.AiUsage.Add(new Core.Data.Entities.AiUsage
        {
            At = _clock.UtcNow,
            Feature = feature,
            UserId = userId,
            Model = model.Length <= 200 ? model : model[..200],
            Provider = provider,
            InputTokens = usage.InputTokenCount,
            OutputTokens = usage.OutputTokenCount,
            CachedInputTokens = usage.InputTokenDetails?.CachedTokenCount ?? 0,
            ReportedCost = AiReportedCost.Of(usage, provider),
        });

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<AiLimitReached?> LimitReachedAsync(string feature, CancellationToken ct)
        => _limits.CheckAsync(feature, userId: null, held: default, ct);
}

/// <summary>
/// The cost OpenRouter reports for a request (AI chat design §10.1).
/// </summary>
/// <remarks>
/// OpenRouter puts the cost in US dollars at <c>usage.cost</c> when a request asks for usage
/// accounting with <c>"usage": {"include": true}</c>. It is not part of the OpenAI format, so it is
/// read from the SDK's store of fields it has no property for, and only asked for and believed from
/// OpenRouter: another server's <c>cost</c> may mean something else, and OpenAI refuses a request
/// with a field it does not know.
/// </remarks>
public static class AiReportedCost
{
    private static readonly BinaryData IncludeUsage = BinaryData.FromString("""{"include":true}""");

    /// <summary>Asks OpenRouter to say what the request cost. Does nothing for any other provider.</summary>
    public static void AskFor(ChatCompletionOptions options, string? provider)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (provider != AiProviders.OpenRouter.Id)
            return;

#pragma warning disable SCME0001 // JsonPatch is marked experimental; it is the SDK's only way to send a field it has no property for.
        options.Patch.Set("$.usage"u8, IncludeUsage);
#pragma warning restore SCME0001
    }

    /// <summary>The cost the provider reported, or null when it reported none.</summary>
    public static decimal? Of(ChatTokenUsage? usage, string? provider)
    {
        if (usage is null || provider != AiProviders.OpenRouter.Id)
            return null;

        try
        {
#pragma warning disable SCME0001
            return usage.Patch.TryGetValue("$.cost"u8, out decimal cost) && cost >= 0 ? cost : null;
#pragma warning restore SCME0001
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or JsonException or OverflowException)
        {
            return null;
        }
    }
}
