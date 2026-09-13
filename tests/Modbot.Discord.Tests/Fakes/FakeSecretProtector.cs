using Modbot.Core.Security;

namespace Modbot.Discord.Tests.Fakes;

/// <summary>Reversible and obviously not encryption. What matters here is that the bot reads through it.</summary>
public sealed class FakeSecretProtector : ISecretProtector
{
    private const string Prefix = "protected:";

    public string Protect(string plaintext) => Prefix + plaintext;

    public string? Unprotect(string? ciphertext)
        => ciphertext is not null && ciphertext.StartsWith(Prefix, StringComparison.Ordinal)
            ? ciphertext[Prefix.Length..]
            : null;
}
