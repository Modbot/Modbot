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
/// M8 §2 applies to everything built on this: a human decides. The one exception is an AutoMod
/// rule -- a term list or an AI topic -- that the group's own operator has set to act, after its
/// trial: it may delete a Discord message, time its author out, or ban or remove a VRChat profile's
/// owner from the managed group (AutoMod design §5). Every action is recorded as a fact, and it
/// never acts on a member of the team or on anyone a rule's "Never act on" list names.
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
