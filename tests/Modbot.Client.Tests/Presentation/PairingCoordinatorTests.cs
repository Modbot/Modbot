using Modbot.Client.Ingest;
using Modbot.Client.Pairing;
using Modbot.Client.Presentation;

namespace Modbot.Client.Tests.Presentation;

/// <summary>
/// What a moderator is told when pairing fails, and whether anything was stored when it did.
/// </summary>
/// <remarks>
/// Pairing is the moment somebody decides whether to trust this program at all, and the four ways
/// it can fail need four different actions from them: get a fresh code, update something, check
/// the address, or simply wait. Collapsing those into "that did not work" makes three of the four
/// unfixable, so the wording is tested rather than assumed.
/// </remarks>
public class PairingCoordinatorTests
{
    private sealed class ScriptedClient(PairingResult result) : IPairingClient
    {
        public List<PairingAttempt> Attempts { get; } = [];

        public Task<PairingResult> PairAsync(PairingAttempt attempt, CancellationToken cancellationToken)
        {
            Attempts.Add(attempt);
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingStore : IPairingStore
    {
        public List<ServerPairing> Saved { get; } = [];

        public List<string> Removed { get; } = [];

        public IReadOnlyList<LoadedPairing> Load() => [];

        public void Save(ServerPairing pairing) => Saved.Add(pairing);

        public void Remove(string serverId) => Removed.Add(serverId);
    }

    private static ServerPairing Pairing() =>
        new("modbot.example", new Uri("https://modbot.example"), "dev_token", "grp_cats");

    private static (PairingCoordinator Coordinator, ScriptedClient Client, RecordingStore Store) Build(
        PairingResult result)
    {
        var client = new ScriptedClient(result);
        var store = new RecordingStore();

        return (new PairingCoordinator(client, store), client, store);
    }

    [Fact]
    public async Task ASuccessfulPairingIsStoredAndSaysWhatWillBeReported()
    {
        var (coordinator, _, store) = Build(new PairingResult(PairingOutcome.Paired, Pairing()));

        var result = await coordinator.PairAsync(
            "modbot.example", "AB12-CD34", "Rin's desktop", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("dev_token", Assert.Single(store.Saved).DeviceToken);

        // The sentence states the boundary, at the moment consent is given: this group's instances
        // and nothing else.
        Assert.Contains("that group's instances and nothing else", result.Message);
    }

    [Fact]
    public async Task NothingIsStoredWhenPairingFails()
    {
        // A half-written pairing would leave the client reporting to a server it never joined.
        var (coordinator, _, store) = Build(PairingResult.Failed(PairingOutcome.CodeRejected, "no"));

        var result = await coordinator.PairAsync(
            "modbot.example", "AB12-CD34", "Rin's desktop", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task ARejectedCodeSaysCodesAreSingleUseAndWhereToGetAnother()
    {
        var (coordinator, _, _) = Build(PairingResult.Failed(PairingOutcome.CodeRejected, "nope"));

        var result = await coordinator.PairAsync(
            "modbot.example", "AB12-CD34", "Rin's desktop", TestContext.Current.CancellationToken);

        Assert.Contains("single-use", result.Message);
        Assert.Contains("generate a fresh one", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A401SaysBeingRemovedFromStaffIsAnExpectedReason()
    {
        // Terminal for this pairing, and frequently not a fault at all. Somebody who has just been
        // taken off a moderation team should not spend an evening debugging their client.
        var (coordinator, _, _) = Build(PairingResult.Failed(PairingOutcome.Unauthorised, "revoked"));

        var result = await coordinator.PairAsync(
            "modbot.example", "AB12-CD34", "Rin's desktop", TestContext.Current.CancellationToken);

        Assert.Contains("removed from that group's staff", result.Message);
        Assert.Contains("nothing to fix", result.Message);
    }

    [Fact]
    public async Task AVersionMismatchPassesThroughTheMessageThatNamesBothNumbers()
    {
        // The client already builds that sentence, and it is the only one that says which side has
        // to update. Rewording it here would lose the numbers.
        var detail = "This client speaks API v1–v2 and https://modbot.example/ speaks v4–v6. Update the client.";
        var (coordinator, _, _) = Build(PairingResult.Failed(PairingOutcome.VersionUnsupported, detail));

        var result = await coordinator.PairAsync(
            "modbot.example", "AB12-CD34", "Rin's desktop", TestContext.Current.CancellationToken);

        Assert.Equal(detail, result.Message);
    }

    [Fact]
    public async Task AWrongAddressSaysToCheckItRatherThanLookingLikeAnOutage()
    {
        var (coordinator, _, _) = Build(PairingResult.Failed(PairingOutcome.NotAModbotServer, "html"));

        var result = await coordinator.PairAsync(
            "modbot.example", "AB12-CD34", "Rin's desktop", TestContext.Current.CancellationToken);

        Assert.Contains("Check the address", result.Message);
        Assert.Contains("open Modbot in a browser", result.Message);
    }

    [Fact]
    public async Task BeingOfflineSaysNothingHasBeenChanged()
    {
        var (coordinator, _, _) = Build(PairingResult.Failed(PairingOutcome.NetworkFailure, "dns"));

        var result = await coordinator.PairAsync(
            "modbot.example", "AB12-CD34", "Rin's desktop", TestContext.Current.CancellationToken);

        Assert.Contains("nothing has been changed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyFieldIsCaughtBeforeAnyRequestIsMade(string blank)
    {
        // A code is single-use. Sending a blank one would be a wasted round trip; sending a real
        // one to a mistyped address would burn it against a server that cannot use it.
        var (coordinator, client, _) = Build(new PairingResult(PairingOutcome.Paired, Pairing()));

        Assert.False((await coordinator.PairAsync(
            blank, "AB12-CD34", "d", TestContext.Current.CancellationToken)).Succeeded);
        Assert.False((await coordinator.PairAsync(
            "modbot.example", blank, "d", TestContext.Current.CancellationToken)).Succeeded);

        Assert.Empty(client.Attempts);
    }

    [Theory]
    [InlineData("modbot.example", "https://modbot.example/")]
    [InlineData("https://modbot.example", "https://modbot.example/")]
    [InlineData("modbot.example/", "https://modbot.example/")]
    [InlineData("  modbot.example  ", "https://modbot.example/")]
    [InlineData("modbot.example:8443", "https://modbot.example:8443/")]
    public void ABareHostNameGetsHttps(string typed, string expected)
    {
        // What people actually type. Refusing it teaches nobody anything.
        Assert.True(PairingCoordinator.TryNormaliseAddress(typed, out var uri));
        Assert.Equal(expected, uri.ToString());
    }

    [Theory]
    [InlineData("http://modbot.example")]
    [InlineData("ftp://modbot.example")]
    [InlineData("not a host")]
    [InlineData("")]
    public void AnAddressThatIsNotHttpsIsRefusedRatherThanUpgraded(string typed)
    {
        // Plain http typed deliberately is refused, not silently rewritten. Presence data crossing
        // a café network in clear text is not something to fix quietly, and a client that rewrote
        // the scheme would be hiding the one decision worth showing.
        Assert.False(PairingCoordinator.TryNormaliseAddress(typed, out _));
    }

    [Fact]
    public async Task PlainHttpNeverReachesTheNetwork()
    {
        var (coordinator, client, store) = Build(new PairingResult(PairingOutcome.Paired, Pairing()));

        var result = await coordinator.PairAsync(
            "http://modbot.example", "AB12-CD34", "d", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Empty(client.Attempts);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task TheDeviceNameIsWhateverTheModeratorTypedAndNothingElse()
    {
        // The client does not read the machine name and send it unasked: a hostname is frequently
        // somebody's real name, and it is not this program's to disclose.
        var (coordinator, client, _) = Build(new PairingResult(PairingOutcome.Paired, Pairing()));

        await coordinator.PairAsync(
            "modbot.example", "AB12-CD34", "  Rin's desktop  ", TestContext.Current.CancellationToken);

        Assert.Equal("Rin's desktop", Assert.Single(client.Attempts).DeviceName);
    }

    [Fact]
    public async Task AnUnnamedDeviceGetsAPlaceholderRatherThanTheMachineName()
    {
        var (coordinator, client, _) = Build(new PairingResult(PairingOutcome.Paired, Pairing()));

        await coordinator.PairAsync(
            "modbot.example", "AB12-CD34", "   ", TestContext.Current.CancellationToken);

        Assert.Equal("Unnamed device", Assert.Single(client.Attempts).DeviceName);
        Assert.DoesNotContain(Environment.MachineName, Assert.Single(client.Attempts).DeviceName);
    }

    [Fact]
    public void UnpairingRemovesTheStoredCredential()
    {
        var (coordinator, _, store) = Build(new PairingResult(PairingOutcome.Paired, Pairing()));

        coordinator.Unpair("modbot.example");

        Assert.Equal("modbot.example", Assert.Single(store.Removed));
    }

    [Fact]
    public async Task OnePressIsOneAttempt()
    {
        // Codes are single-use, so a background retry would burn the moderator's code against a
        // server that already refused it.
        var (coordinator, client, _) = Build(PairingResult.Failed(PairingOutcome.NetworkFailure, "no"));

        await coordinator.PairAsync(
            "modbot.example", "AB12-CD34", "d", TestContext.Current.CancellationToken);

        Assert.Single(client.Attempts);
    }
}
