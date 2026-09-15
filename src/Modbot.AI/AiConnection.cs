using OpenAI;
using OpenAI.Chat;

namespace Modbot.AI;

/// <summary>Everything needed to reach one model: where, with which key, and which model.</summary>
/// <param name="ApiKey">Null for a server that wants none, such as a local Ollama.</param>
public sealed record AiConnection(string Provider, Uri Endpoint, string? ApiKey, string Model);

/// <summary>
/// A ready-to-use client for the current AI settings.
/// </summary>
/// <remarks>
/// The OpenAI SDK's own types, deliberately. Features built on this need tools, streaming and
/// structured output, and a Modbot-shaped wrapper over each of those would be a second, worse copy
/// of the SDK. What stays behind <see cref="IAiClients"/> is the part that is Modbot's business:
/// reading the settings, decrypting the key, and choosing the HTTP client.
/// </remarks>
/// <param name="Chat">A chat client for <paramref name="Model"/>.</param>
/// <param name="Client">The underlying client, for a feature that needs a different model or another API.</param>
public sealed record AiChat(ChatClient Chat, OpenAIClient Client, string Model, string Provider);

/// <summary>The Test button's answer.</summary>
/// <param name="Message">What the model said on success, or the provider's error in its own words.</param>
/// <param name="Usage">The provider's token counts, when it answered with any, for the usage ledger.</param>
public sealed record AiTestResult(bool Worked, string Message, ChatTokenUsage? Usage = null);

/// <summary>The model ids the endpoint listed, or why it did not.</summary>
public sealed record AiModelList(IReadOnlyList<string> Models, string? Error);
