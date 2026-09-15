namespace Modbot.AI.Chat;

/// <summary>The bounds on Chat's settings (AI chat design §2 and §5), checked on save and on use.</summary>
public static class ChatSettingsRules
{
    public const int MinToolCalls = 0;
    public const int MaxToolCalls = 50;
    public const int MinReplyTokens = 256;
    public const int MaxReplyTokens = 32_000;
    public const int MinTimeLimitSeconds = 10;
    public const int MaxTimeLimitSeconds = 600;
    public const int MaxInstructionsLength = 8000;

    /// <summary>Longest message a person may send.</summary>
    public const int MaxMessageLength = 4000;

    /// <summary>A conversation this long is full; the page offers a new one.</summary>
    public const int MaxMessagesPerConversation = 200;

    /// <summary>Longest tool result sent back to the model. Longer ones are cut.</summary>
    public const int MaxToolResultLength = 20_000;

    /// <summary>Null when the values are acceptable; otherwise what is wrong.</summary>
    public static string? Problem(int maxToolCalls, int maxReplyTokens, int timeLimitSeconds, string? model, string? instructions)
    {
        if (maxToolCalls is < MinToolCalls or > MaxToolCalls)
            return $"Tool calls per reply must be between {MinToolCalls} and {MaxToolCalls}.";

        if (maxReplyTokens is < MinReplyTokens or > MaxReplyTokens)
            return $"Reply length must be between {MinReplyTokens} and {MaxReplyTokens} tokens.";

        if (timeLimitSeconds is < MinTimeLimitSeconds or > MaxTimeLimitSeconds)
            return $"The time limit must be between {MinTimeLimitSeconds} and {MaxTimeLimitSeconds} seconds.";

        if (model is { Length: > AiSettingsRules.MaxModelLength })
            return "The model name is too long.";

        if (instructions is { Length: > MaxInstructionsLength })
            return "The extra instructions are too long.";

        return null;
    }

    /// <summary>The limits for one reply, clamped so a hand-edited row cannot remove them.</summary>
    public static ChatLimits Limits(int maxToolCalls, int maxReplyTokens, int timeLimitSeconds) => new(
        Math.Clamp(maxToolCalls, MinToolCalls, MaxToolCalls),
        Math.Clamp(maxReplyTokens, MinReplyTokens, MaxReplyTokens),
        TimeSpan.FromSeconds(Math.Clamp(timeLimitSeconds, MinTimeLimitSeconds, MaxTimeLimitSeconds)));
}
