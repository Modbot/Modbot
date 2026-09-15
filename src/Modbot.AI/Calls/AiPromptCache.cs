using OpenAI.Chat;

namespace Modbot.AI.Calls;

/// <summary>
/// Makes each feature's fixed instructions cacheable by the provider.
/// </summary>
/// <remarks>
/// <para>
/// Providers that cache charge less for the part of a request they have seen before, and they can
/// only do that for a prefix that is byte-for-byte the same as last time. So every feature puts
/// its instructions first, unchanged between calls, and everything that differs -- the text being
/// checked, the figures, the time -- comes after them in later messages.
/// </para>
/// <para>
/// OpenAI and xAI do this on their own for a long enough prefix. Anthropic's models want to be
/// told where the reusable part ends, and OpenRouter passes that marker through, so it is set
/// there and nowhere else: a provider that has never heard of <c>cache_control</c> refuses the
/// whole request rather than ignoring the field.
/// </para>
/// </remarks>
public static class AiPromptCache
{
    private static readonly BinaryData Ephemeral = BinaryData.FromString("""{"type":"ephemeral"}""");

    /// <summary>
    /// A system message holding instructions that do not change between calls, marked so a
    /// provider that caches prefixes can.
    /// </summary>
    public static SystemChatMessage Instructions(string text, string? provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var message = new SystemChatMessage(ChatMessageContentPart.CreateTextPart(text));

        if (provider == AiProviders.OpenRouter.Id)
        {
#pragma warning disable SCME0001 // JsonPatch is marked experimental; it is the SDK's only way to send a field it has no property for.
            message.Patch.Set("$.content[0].cache_control"u8, Ephemeral);
#pragma warning restore SCME0001
        }

        return message;
    }
}
