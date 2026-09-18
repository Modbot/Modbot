using Modbot.Core.Cloud;

namespace Modbot.Api.Tests.Fakes;

/// <summary>
/// Stands in for Modbot Cloud's subscriber list: records what it was asked, and can be told to
/// fail the way an unreachable Cloud does.
/// </summary>
public sealed class FakeUpdatesSubscriber : IUpdatesSubscriber
{
    /// <summary>Set false to stand for a deployment with Cloud switched off or unconfigured.</summary>
    public bool Available { get; set; } = true;

    /// <summary>Set true to stand for a Cloud that refuses or cannot be reached.</summary>
    public bool Throws { get; set; }

    public List<string> Asked { get; } = [];

    public Task SubscribeAsync(string email, CancellationToken ct)
    {
        Asked.Add(email);

        // The real one swallows everything itself; this one throws so a test can prove the caller
        // is not relying on that -- an account must survive a subscriber that is not so careful.
        return Throws
            ? Task.FromException(new HttpRequestException("Modbot Cloud is not there."))
            : Task.CompletedTask;
    }
}
