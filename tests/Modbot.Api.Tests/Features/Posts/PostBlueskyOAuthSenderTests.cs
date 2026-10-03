using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Posts;
using Modbot.Core.Bluesky;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Posts;
using Modbot.Core.Security;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Posts;

/// <summary>
/// Posting on a sign-in with Bluesky (posts design §4.2c, step 3b): every call carries a DPoP proof
/// and is made once more with the account server's nonce; a token near its end is renewed at the
/// sign-in server and the new pair written before it is used; a renewal Bluesky refuses ends the
/// sign-in, and nothing signs in with a password in its place.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PostBlueskyOAuthSenderTests
{
    private const string Public = "https://modbot.example.com";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PostgresFixture _db;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeBluesky _bluesky;
    private readonly BlueskyOAuth _oauth;
    private readonly BlueskySession _session;

    public PostBlueskyOAuthSenderTests(PostgresFixture db)
    {
        _db = db;
        _bluesky = new FakeBluesky(() => _clock.UtcNow);
        _oauth = new BlueskyOAuth(new OneHandlerClients(_bluesky), new PlainProtector(), _clock);
        _session = new BlueskySession(Client(), new PlainProtector(), _clock, _oauth);
    }

    private BlueskyClient Client() => new(new OneHandlerClients(_bluesky), _clock);

    /// <summary>Bluesky signed in with Bluesky, checked and posting, with no posts.</summary>
    private async Task SignedInAsync()
    {
        await using var context = _db.NewContext();
        await context.Posts.ExecuteDeleteAsync(Ct);

        var settings = await context.GetSettingsAsync(Ct);
        settings.PublicAddress = Public;
        settings.PostsPaused = false;
        settings.BlueskyHandle = FakeBluesky.Handle;
        settings.BlueskyDid = FakeBluesky.Did;
        settings.BlueskyServer = FakeBluesky.Server;
        settings.BlueskyAppPasswordEncrypted = null;
        settings.BlueskySessionEncrypted = null;
        settings.BlueskyOAuthSignedIn = false;
        settings.BlueskyOAuthKeyEncrypted = null;
        settings.BlueskyOAuthPendingEncrypted = null;
        settings.BlueskyCheckedAt = _clock.UtcNow.AddHours(-1);
        settings.BlueskyProblem = null;
        settings.BlueskyPostingOn = true;
        settings.BlueskyStoppedUntil = null;
        settings.BlueskySignInRefused = false;
        settings.BlueskySignedInAt = null;
        settings.BlueskySignInsDay = null;
        settings.BlueskySignInsUsed = 0;
        await context.SaveChangesAsync(Ct);

        var person = Guid.CreateVersion7();
        var account = new BlueskyAccount(FakeBluesky.Did, FakeBluesky.Handle, new Uri(FakeBluesky.Server));

        var started = await _oauth.StartAsync(context, Public, account, person, Ct);
        Assert.NotNull(started.Value);

        var (code, state) = _bluesky.Approve();
        var finish = await _oauth.FinishAsync(context, code, state, FakeBluesky.AuthServer, null, person, Ct);
        Assert.Equal(BlueskyOAuthEnd.SignedIn, finish.End);

        await _session.SignedInWithBlueskyAsync(context, finish.Tokens!, FakeBluesky.Handle, new Uri(FakeBluesky.Server), Ct);
    }

    private async Task<PostBlueskyPass> PassAsync()
    {
        await using var context = _db.NewContext();
        var sender = new PostBlueskySender(
            context,
            _clock,
            new FactWriter(context, _clock),
            new EventPartitionMaintainer(context, _clock),
            new PostClaim(context, _clock),
            _session,
            Client(),
            new BlueskyIdentity(new OneHandlerClients(_bluesky)));

        return await sender.RunOnceAsync(Ct);
    }

    private async Task<Guid> AddPostAsync(string text = "Friday at eight. Bring snacks.")
    {
        await using var context = _db.NewContext();

        var post = new Post
        {
            Id = Guid.CreateVersion7(),
            Title = "Movie night",
            Text = text,
            Status = PostStatuses.Scheduled,
            SendAt = _clock.UtcNow,
            TimeZone = "UTC",
            CreatedAt = _clock.UtcNow,
            UpdatedAt = _clock.UtcNow,
        };

        var destination = new PostDestination
        {
            Id = Guid.CreateVersion7(),
            PostId = post.Id,
            Network = PostNetworks.Bluesky,
            Target = FakeBluesky.Did,
            Options = PostTexts.WriteBlueskyOptions(new BlueskyPostOptions()),
            State = PostDestinationStates.Waiting,
            UpdatedAt = _clock.UtcNow,
        };

        post.Destinations.Add(destination);
        context.Posts.Add(post);
        await context.SaveChangesAsync(Ct);

        return destination.Id;
    }

    private async Task<PostDestination> DestinationAsync(Guid id)
    {
        await using var context = _db.NewContext();
        return await context.PostDestinations.AsNoTracking().SingleAsync(d => d.Id == id, Ct);
    }

    private async Task<Core.Data.Entities.Settings> SettingsAsync()
    {
        await using var context = _db.NewContext();
        return await context.Settings.AsNoTracking().SingleAsync(s => s.Id == 1, Ct);
    }

    [Fact]
    public async Task APostGoesWithADpopProof_SentOnceMoreWithTheServersNonce()
    {
        await SignedInAsync();
        var destinationId = await AddPostAsync();

        var pass = await PassAsync();

        Assert.Equal(1, pass.Sent);
        Assert.Equal(1, _bluesky.Puts);
        Assert.Equal(PostDestinationStates.Posted, (await DestinationAsync(destinationId)).State);

        var puts = _bluesky.Requests.Where(r => r.Method == "com.atproto.repo.putRecord").ToList();
        Assert.Equal(2, puts.Count);
        Assert.All(puts, p => Assert.Equal("DPoP", p.Scheme));
        Assert.Null(FakeBluesky.ClaimsOf(puts[0].Dpop)!["nonce"]);
        Assert.Equal(_bluesky.ServerNonce, FakeBluesky.ClaimsOf(puts[1].Dpop)!["nonce"]!.GetValue<string>());

        // No password sign-in, ever.
        Assert.Equal(0, _bluesky.SignIns);
    }

    [Fact]
    public async Task ATokenNearItsEndIsRenewed_AndTheNewPairIsWrittenBeforeItIsUsed()
    {
        await SignedInAsync();
        var spent = _bluesky.OAuthRefresh;
        await AddPostAsync();

        _clock.Advance(TimeSpan.FromMinutes(4.5));
        var pass = await PassAsync();

        Assert.Equal(1, pass.Sent);
        Assert.Equal(1, _bluesky.OAuthRefreshes);
        Assert.NotEqual(spent, _bluesky.OAuthRefresh);

        var renewal = _bluesky.TokenRequests.Last();
        Assert.Equal("refresh_token", renewal["grant_type"]);
        Assert.Equal(spent, renewal["refresh_token"]);

        // The pair Bluesky handed out last is the one stored, and the post went with its token.
        var stored = _session.Read((await SettingsAsync()).BlueskySessionEncrypted)!;
        Assert.Equal(_bluesky.OAuthRefresh, stored.RefreshJwt);
        Assert.Equal(_bluesky.AccessJwt, stored.AccessJwt);
        Assert.Equal(_bluesky.AccessJwt, _bluesky.Requests.Last(r => r.Method == "com.atproto.repo.putRecord").Bearer);
    }

    [Fact]
    public async Task ARenewalBlueskyRefusesEndsTheSignIn_AndNothingSignsInWithAPassword()
    {
        await SignedInAsync();
        var destinationId = await AddPostAsync();

        // The refresh token Modbot holds is no longer Bluesky's: revoked, say.
        await using (var context = _db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            var tokens = _session.Read(settings.BlueskySessionEncrypted)!;
            settings.BlueskySessionEncrypted = _session.Protect(tokens with { RefreshJwt = "revoked" });
            await context.SaveChangesAsync(Ct);
        }

        _clock.Advance(TimeSpan.FromMinutes(10));
        var pass = await PassAsync();

        Assert.Equal(0, pass.Sent);
        Assert.Equal(0, _bluesky.Puts);
        Assert.Equal(0, _bluesky.SignIns);

        var settingsAfter = await SettingsAsync();
        Assert.True(settingsAfter.BlueskySignInRefused);
        Assert.Null(settingsAfter.BlueskySessionEncrypted);
        Assert.Equal(BlueskyErrors.SignInEnded, settingsAfter.BlueskyProblem);
        Assert.False(PostSites.BlueskyReady(settingsAfter));

        Assert.Equal(PostDestinationStates.Waiting, (await DestinationAsync(destinationId)).State);

        // And it stays ended: the next pass asks Bluesky nothing.
        var asked = _bluesky.Requests.Count;
        _clock.Advance(TimeSpan.FromMinutes(5));
        await PassAsync();
        Assert.Equal(asked, _bluesky.Requests.Count);
    }

    /// <summary>The tokens and keys are stored as they are: the protector's own tests cover the encryption.</summary>
    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string? Unprotect(string? ciphertext) => ciphertext;
    }
}
