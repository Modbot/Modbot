using Modbot.Core.Email;

namespace Modbot.Api.Tests.Fakes;

/// <summary>
/// The SMTP relay, answered from a script. Sits under the real <see cref="EmailSender"/>, so a
/// test sees exactly what got past the daily email limit.
/// </summary>
public sealed class FakeMailRelay : IMailRelay
{
    public List<EmailMessage> Sent { get; } = [];

    /// <summary>Every message handed over, refused ones included.</summary>
    public List<EmailMessage> Tried { get; } = [];

    public bool Configured { get; set; } = true;

    /// <summary>When set, every message is refused with this sentence.</summary>
    public string? RefuseWith { get; set; }

    public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(Configured);

    public Task<SendOutcome> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        Tried.Add(message);

        if (RefuseWith is { } error)
            return Task.FromResult(SendOutcome.Failed(error));

        Sent.Add(message);
        return Task.FromResult(SendOutcome.Ok);
    }
}
