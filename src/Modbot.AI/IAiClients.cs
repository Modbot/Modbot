namespace Modbot.AI;

/// <summary>
/// Where every AI feature gets its client. Take a dependency on this; never construct an
/// <c>OpenAIClient</c> directly.
/// </summary>
/// <remarks>
/// <para>
/// Settings are read on every call rather than at startup, so a key or model changed on the
/// settings page applies to the next request without a restart. Scoped, because it reads the
/// settings row through the request's <c>ModbotContext</c>.
/// </para>
/// <para>
/// M8 §2 applies to everything built on this. A model's output never leads to an action on VRChat
/// -- no kick, ban or group removal. The one exception to "a human decides" is an AI moderation
/// rule on Discord chat, and only once the group's own operator has set that rule to act, with
/// every action recorded as a fact.
/// </para>
/// </remarks>
public interface IAiClients
{
    /// <summary>
    /// A client for the saved settings, or null when AI is switched off or not fully set up.
    /// </summary>
    /// <remarks>
    /// Null is the ordinary answer on most deployments, not an error: a feature that gets no
    /// client does nothing.
    /// </remarks>
    Task<AiChat?> GetChatAsync(CancellationToken ct);

    /// <summary>One short chat completion through <paramref name="connection"/>, reporting what came back.</summary>
    Task<AiTestResult> TestAsync(AiConnection connection, CancellationToken ct);

    /// <summary>The endpoint's model list. <see cref="AiConnection.Model"/> is ignored.</summary>
    Task<AiModelList> ListModelsAsync(AiConnection connection, CancellationToken ct);
}
