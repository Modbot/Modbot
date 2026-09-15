using Modbot.AI.Usage;

namespace Modbot.AI.Calls;

/// <summary>
/// How long each feature waits for the provider before it gives up.
/// </summary>
/// <remarks>
/// <para>
/// One number per feature rather than one for all of them, because the features are not waiting
/// for the same kind of thing. The timeout covers the whole call -- the tool loop for Chat
/// included -- and running out is recorded as an error on that call, never as an empty answer.
/// </para>
/// <para>
/// Chat's is the operator's, on Settings → AI → Chat, because that is the one somebody sits and
/// watches; the rest are fixed, and chosen so a stuck call cannot hold a loop up all day.
/// </para>
/// </remarks>
public static class AiTimeouts
{
    /// <summary>
    /// Moderation: half a minute. It runs behind the profile sync with a queue of profiles waiting,
    /// and a check of four short fields that has not come back in thirty seconds is stuck.
    /// </summary>
    public static readonly TimeSpan Moderation = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Insights: three minutes. One call a day, and a reasoning model asked to read a page of
    /// figures thinks for a long while before it writes anything. The next try is tomorrow.
    /// </summary>
    public static readonly TimeSpan Insights = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The Test button and the model list: half a minute. Somebody is watching a button, and a
    /// provider that has not answered in thirty seconds has told them what they needed to know.
    /// </summary>
    public static readonly TimeSpan Test = TimeSpan.FromSeconds(30);

    /// <summary>Chat's default, kept as the default of <c>settings.ai_chat_time_limit_seconds</c>.</summary>
    public static readonly TimeSpan Chat = TimeSpan.FromSeconds(120);

    /// <summary>The timeout for one feature. Chat's own setting is passed in; without it, the default.</summary>
    public static TimeSpan For(string feature, int? chatTimeLimitSeconds = null) => feature switch
    {
        AiFeatures.Moderation => Moderation,
        AiFeatures.Insights => Insights,
        AiFeatures.Test => Test,
        AiFeatures.Chat => chatTimeLimitSeconds is > 0 ? TimeSpan.FromSeconds(chatTimeLimitSeconds.Value) : Chat,
        _ => Chat,
    };
}
