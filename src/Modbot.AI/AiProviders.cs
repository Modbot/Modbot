namespace Modbot.AI;

/// <summary>One preset on the provider list.</summary>
/// <param name="Id">What is stored in <c>settings.ai_provider</c>.</param>
/// <param name="Endpoint">The OpenAI-compatible base address. Empty for <see cref="AiProviders.Custom"/>.</param>
/// <param name="AllowsHttp">
/// Whether a plain-http address is accepted. Only for a custom endpoint: a local model server such
/// as Ollama listens on <c>http://localhost:11434/v1</c>, and M8 4.2 makes that a first-class
/// setup. A hosted provider reached over plain http would be sending the key in the clear.
/// </param>
public sealed record AiProvider(string Id, string Label, string Endpoint, bool AllowsHttp);

/// <summary>
/// The providers the settings page offers, each an OpenAI-compatible endpoint the official
/// OpenAI SDK can talk to without changes.
/// </summary>
/// <remarks>
/// The addresses were checked against each provider's own documentation in September 2026:
/// OpenRouter's quickstart, xAI's REST reference, Anthropic's "OpenAI SDK compatibility" page
/// (which gives the address with its trailing slash), and OpenAI's API reference. They are only
/// what the endpoint field is filled with; the operator can edit any of them.
/// </remarks>
public static class AiProviders
{
    public static readonly AiProvider OpenRouter = new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", false);

    public static readonly AiProvider XAi = new("xai", "xAI", "https://api.x.ai/v1", false);

    public static readonly AiProvider Anthropic = new("anthropic", "Anthropic", "https://api.anthropic.com/v1/", false);

    public static readonly AiProvider OpenAI = new("openai", "OpenAI", "https://api.openai.com/v1", false);

    public static readonly AiProvider Custom = new("custom", "Custom", "", true);

    /// <summary>In the order the settings page shows them.</summary>
    public static IReadOnlyList<AiProvider> All { get; } = [OpenRouter, XAi, Anthropic, OpenAI, Custom];

    /// <summary>The preset a new deployment starts on.</summary>
    public static AiProvider Default => OpenRouter;

    public static AiProvider? Find(string? id) =>
        id is null
            ? null
            : All.FirstOrDefault(p => string.Equals(p.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
}
