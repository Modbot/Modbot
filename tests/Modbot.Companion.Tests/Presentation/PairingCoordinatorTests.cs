using Modbot.Companion.Ingest;
using Modbot.Companion.Pairing;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.Tests.Presentation;

/// <summary>
/// What a moderator is told when pairing fails, and whether anything was stored when it did.
/// </summary>
/// <remarks>
/// Pairing is the moment somebody decides whether to trust this program at all, and the four ways
/// it can fail need four different actions from them: get a fresh link, update something, check
/// where the link came from, or simply wait. Collapsing those into "that did not work" makes three
/// of the four unfixable, so the wording is tested rather than assumed.
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

    private static readonly PairingToken Token = new(new Uri("https://modbot.example/"), "AB12-CD34");

    /// <summary>The pasted form of <see cref="Token"/>: what "Copy pairing token" produces.</summary>
    private static string Pasted => Token.Encode();

    /// <summary>The link form: what "Open in Modbot" hands to Windows.</summary>
    private static string Link => Token.ToLink();

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

        var result = await coordinator.PairAsync(Pasted, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("dev_token", Assert.Single(store.Saved).DeviceToken);

        // The sentence states the boundary, at the moment consent is given: this group's instances
        // and nothing else.
        Assert.Contains("that group's instances and nothing else", result.Message);
    }

    [Fact]
    public async Task ALinkAndAPastedTokenAreTheSamePath()
    {
        // "Open in Modbot" and "Copy pairing token" carry the same bytes and must end in the same
        // place, or a moderator whose browser refused the link would get a different product.
        var (coordinator, client, _) = Build(new PairingResult(PairingOutcome.Paired, Pairing()));

        await coordinator.PairAsync(Link, TestContext.Current.CancellationToken);
        await coordinator.PairAsync(Pasted, TestContext.Current.CancellationToken);

        Assert.Equal(2, client.Attempts.Count);
        Assert.Equal(client.Attempts[0], client.Attempts[1]);
        Assert.Equal(new Uri("https://modbot.example/"), client.Attempts[0].BaseUri);
        Assert.Equal("AB12-CD34", client.Attempts[0].Code);
    }

    [Fact]
    public async Task TheServerLabelIsTheHostAndNeverAnythingTheModeratorHasToInvent()
    {
        var (coordinator, client, _) = Build(new PairingResult(PairingOutcome.Paired, Pairing()));

        await coordinator.PairAsync(Pasted, TestContext.Current.CancellationToken);
        await coordinator.PairAsync(
            new PairingToken(new Uri("https://modbot.example:8443/"), "AB12-CD34").Encode(),
            TestContext.Current.CancellationToken);

        Assert.Equal("modbot.example", client.Attempts[0].ServerId);
        Assert.Equal("modbot.example:8443", client.Attempts[1].ServerId);
    }

    [Fact]
    public async Task NothingIsStoredWhenPairingFails()
    {
        // A half-written pairing would leave the client reporting to a server it never joined.
        var (coordinator, _, store) = Build(PairingResult.Failed(PairingOutcome.CodeRejected, "no"));

        var result = await coordinator.PairAsync(Pasted, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task ARejectedCodeSaysTheLinkHasExpiredAndWhereToGetAnother()
    {
        // The server answers the same way for expired, already used and never existed, so the
        // client says the two things a moderator can actually have done and names the fix.
        var (coordinator, _, _) = Build(PairingResult.Failed(PairingOutcome.CodeRejected, "nope"));

        var result = await coordinator.PairAsync(Pasted, TestContext.Current.CancellationToken);

        Assert.Contains("expired or was already used", result.Message);
        Assert.Contains("open the pairing page again", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A401SaysBeingRemovedFromStaffIsAnExpectedReason()
    {
        // Terminal for this pairing, and frequently not a fault at all. Somebody who has just been
        // taken off a moderation team should not spend an evening debugging their client.
        var (coordinator, _, _) = Build(PairingResult.Failed(PairingOutcome.Unauthorised, "revoked"));

        var result = await coordinator.PairAsync(Pasted, TestContext.Current.CancellationToken);

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

        var result = await coordinator.PairAsync(Pasted, TestContext.Current.CancellationToken);

        Assert.Equal(detail, result.Message);
    }

    [Fact]
    public async Task AnAddressThatIsNotAModbotServerSaysToCheckItRatherThanLookingLikeAnOutage()
    {
        var (coordinator, _, _) = Build(PairingResult.Failed(PairingOutcome.NotAModbotServer, "html"));

        var result = await coordinator.PairAsync(Pasted, TestContext.Current.CancellationToken);

        Assert.Contains("Check the address", result.Message);
        Assert.Contains("open Modbot in a browser", result.Message);
    }

    [Fact]
    public async Task BeingOfflineSaysNothingHasBeenChanged()
    {
        var (coordinator, _, _) = Build(PairingResult.Failed(PairingOutcome.NetworkFailure, "dns"));

        var result = await coordinator.PairAsync(Pasted, TestContext.Current.CancellationToken);

        Assert.Contains("nothing has been changed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a token")]
    [InlineData("modbot-companion://pair")]
    public async Task ABadTokenIsCaughtBeforeAnyRequestIsMade(string bad)
    {
        // A code is single-use and a request costs a round trip. Nothing goes on the wire until
        // the text has been shown to be a pairing token naming an address the client would use.
        var (coordinator, client, store) = Build(new PairingResult(PairingOutcome.Paired, Pairing()));

        var result = await coordinator.PairAsync(bad, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Empty(client.Attempts);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task ATokenPointingAtPlainHttpNeverReachesTheNetwork()
    {
        // A token is whatever the browser handed over. One that names an insecure address is
        // refused here, with the address in the sentence, and nothing is sent anywhere.
        var (coordinator, client, store) = Build(new PairingResult(PairingOutcome.Paired, Pairing()));

        var result = await coordinator.PairAsync(
            new PairingToken(new Uri("http://modbot.example/"), "AB12-CD34").Encode(),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("http://modbot.example", result.Message);
        Assert.Empty(client.Attempts);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task TheRequestCarriesTheCodeAndTheAddressAndNothingAboutThisMachine()
    {
        // No device name, no machine name, no account name: the attempt is the token's two fields
        // and a local label the moderator will recognise. Anything added here later is a change to
        // what the client tells a server about the machine it runs on.
        var (coordinator, client, _) = Build(new PairingResult(PairingOutcome.Paired, Pairing()));

        await coordinator.PairAsync(Pasted, TestContext.Current.CancellationToken);

        var attempt = Assert.Single(client.Attempts);
        Assert.Equal(new PairingAttempt(new Uri("https://modbot.example/"), "AB12-CD34", "modbot.example"), attempt);
    }

    [Fact]
    public void UnpairingRemovesTheStoredCredential()
    {
        var (coordinator, _, store) = Build(new PairingResult(PairingOutcome.Paired, Pairing()));

        coordinator.Unpair("modbot.example");

        Assert.Equal("modbot.example", Assert.Single(store.Removed));
    }

    [Fact]
    public async Task OneTokenIsOneAttempt()
    {
        // Codes are single-use, so a background retry would burn the moderator's code against a
        // server that already refused it.
        var (coordinator, client, _) = Build(PairingResult.Failed(PairingOutcome.NetworkFailure, "no"));

        await coordinator.PairAsync(Pasted, TestContext.Current.CancellationToken);

        Assert.Single(client.Attempts);
    }
}
