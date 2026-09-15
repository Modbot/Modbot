using System.Net;
using Modbot.VRChat.Session;
using NSubstitute;
using VRChat.API.Api;
using VRChat.API.Client;
using VRChat.API.Model;

namespace Modbot.VRChat.Tests.Fakes;

/// <summary>
/// A scripted <see cref="IVRChat"/>, built as a substitute rather than by hand.
/// </summary>
/// <remarks>
/// <c>IAuthenticationApi</c> alone has over a hundred members — sync, async and
/// <c>...WithHttpInfo</c> variants of every operation — and the gate uses two of them. Writing the
/// other hundred as <c>throw new NotSupportedException()</c> would bury the two that matter.
/// </remarks>
public sealed class FakeVRChat
{
    private readonly Queue<ApiResponse<CurrentUser>> _currentUser = new();
    private readonly Queue<ApiResponse<Verify2FAResult>> _verify = new();

    /// <summary>Answered whenever the queue runs dry. See <see cref="AlwaysSignedInAs"/>.</summary>
    private ApiResponse<CurrentUser>? _standingCurrentUser;

    public FakeVRChat()
    {
        var authentication = Substitute.For<IAuthenticationApi>();

        authentication
            .GetCurrentUserWithHttpInfoAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                GetCurrentUserCalls++;

                // A transport failure does not come back as a response: VRChat.API only catches
                // ApiException, so a DNS failure, a refused connection and its own HTTP timeout
                // all propagate as live exceptions and the gate has to deal with them.
                if (ThrowOnGetCurrentUser is { } failure)
                    return Task.FromException<ApiResponse<CurrentUser>>(failure);

                if (_currentUser.Count == 0 && _standingCurrentUser is { } standing)
                    return Task.FromResult(standing);

                return Task.FromResult(Next(_currentUser, "GetCurrentUser"));
            });

        authentication
            .Verify2FAWithHttpInfoAsync(Arg.Any<TwoFactorAuthCode>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Verify2FACalls++;
                SubmittedCodes.Add(call.Arg<TwoFactorAuthCode>().Code);
                return Task.FromResult(Next(_verify, "Verify2FA"));
            });

        authentication
            .VerifyAuthTokenWithHttpInfoAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                VerifyAuthTokenCalls++;

                if (VerifyAuthTokenGate is { } gate)
                    return gate.Task.ContinueWith(_ => AuthTokenAnswer(), TaskScheduler.Default);

                return Task.FromResult(AuthTokenAnswer());
            });

        // Built before the Returns call, not inside it: configuring one substitute while another
        // is mid-configuration makes NSubstitute lose track of which call it is answering, and it
        // fails at the Returns with a message about the wrong substitute entirely.
        var groups = Groups.Build();
        var users = Users.Build();
        var instances = Instances.Build();
        var calendar = Calendar.Build();

        Client = Substitute.For<IVRChat>();
        Client.Authentication.Returns(authentication);
        Client.Groups.Returns(groups);
        Client.Users.Returns(users);
        Client.Instances.Returns(instances);
        Client.Calendar.Returns(calendar);
        Client.GetCookies().Returns(_ => Cookies);
    }

    public IVRChat Client { get; }

    /// <summary>The group endpoints: the audit log and the group object.</summary>
    public FakeGroups Groups { get; } = new();

    /// <summary>The user endpoint: one profile per request.</summary>
    public FakeUsers Users { get; } = new();

    /// <summary>Rooms' own pages, for their head counts.</summary>
    public FakeInstances Instances { get; } = new();

    /// <summary>The group calendar's writes.</summary>
    public FakeCalendar Calendar { get; } = new();

    /// <summary>When set, every GetCurrentUser fails with this instead of answering.</summary>
    public Exception? ThrowOnGetCurrentUser { get; set; }

    public List<Cookie> Cookies { get; } = [];

    public List<string> SubmittedCodes { get; } = [];

    public int GetCurrentUserCalls { get; private set; }

    public int Verify2FACalls { get; private set; }

    /// <summary><c>GET /auth</c> calls: the session check that does not sign in.</summary>
    public int VerifyAuthTokenCalls { get; private set; }

    /// <summary>
    /// What <c>GET /auth</c> answers: a status and a raw body. A working session by default --
    /// <c>{"ok":true}</c> -- because that is what a stored cookie usually gets.
    /// </summary>
    public (HttpStatusCode Status, string Body) AuthToken { get; set; } =
        (HttpStatusCode.OK, """{"ok":true,"token":"authcookie_secret"}""");

    /// <summary>When set, <c>GET /auth</c> waits for it, so a test can hold the check open.</summary>
    public TaskCompletionSource? VerifyAuthTokenGate { get; set; }

    private ApiResponse<VerifyAuthTokenResult> AuthTokenAnswer()
    {
        var (status, body) = AuthToken;
        var ok = status == HttpStatusCode.OK && body.Contains("\"ok\":true", StringComparison.Ordinal);

        return new ApiResponse<VerifyAuthTokenResult>(
            status, new Multimap<string, string>(),
            status == HttpStatusCode.OK ? new VerifyAuthTokenResult(ok, "authcookie_secret") : null!,
            body);
    }

    public FakeVRChat RespondsWith(params ApiResponse<CurrentUser>[] responses)
    {
        foreach (var response in responses)
            _currentUser.Enqueue(response);

        return this;
    }

    public FakeVRChat VerifiesWith(params ApiResponse<Verify2FAResult>[] responses)
    {
        foreach (var response in responses)
            _verify.Enqueue(response);

        return this;
    }

    /// <summary>A signed-in user, with the <c>auth</c> cookie a real login would have set.</summary>
    public FakeVRChat SignedInAs(string displayName = "Modbot", string id = "usr_fake")
    {
        Cookies.Add(new Cookie("auth", "authCookieValue", "/", "api.vrchat.cloud"));

        return RespondsWith(Ok(new CurrentUser { DisplayName = displayName, Id = id }));
    }

    /// <summary>
    /// A signed-in user that answers however many times the gate asks.
    /// </summary>
    /// <remarks>
    /// The gate re-establishes the session whenever its state is not Healthy, and a 429 makes it
    /// RateLimited -- so every call made while a bucket is cold-stopped re-authenticates first.
    /// A one-shot script would therefore turn "the producer was refused by its own limiter" into
    /// "the test ran out of scripted logins", which is a confusing way to fail.
    /// </remarks>
    public FakeVRChat AlwaysSignedInAs(string displayName = "Modbot", string id = "usr_fake")
    {
        _standingCurrentUser = Ok(new CurrentUser { DisplayName = displayName, Id = id });
        return SignedInAs(displayName, id);
    }

    /// <summary>A login that VRChat answers with a two-factor challenge, then accepts.</summary>
    public FakeVRChat ChallengesWithTotp()
    {
        Cookies.Add(new Cookie("auth", "authCookieValue", "/", "api.vrchat.cloud"));
        Cookies.Add(new Cookie("twoFactorAuth", "twoFactorCookieValue", "/", "api.vrchat.cloud"));

        return RespondsWith(
                Ok(new CurrentUser { RequiresTwoFactorAuth = ["totp"] }),
                Ok(new CurrentUser { DisplayName = "Modbot", Id = "usr_fake" }))
            .VerifiesWith(new ApiResponse<Verify2FAResult>(
                HttpStatusCode.OK, new Multimap<string, string>(), new Verify2FAResult(verified: true)));
    }

    public static ApiResponse<CurrentUser> Ok(CurrentUser user) =>
        new(HttpStatusCode.OK, new Multimap<string, string>(), user, "{}");

    public static ApiResponse<CurrentUser> Status(HttpStatusCode status, string body = "") =>
        new(status, new Multimap<string, string>(), null!, body);

    private static ApiResponse<T> Next<T>(Queue<ApiResponse<T>> responses, string operation) =>
        responses.Count > 0
            ? responses.Dequeue()
            : throw new InvalidOperationException($"The test did not script a response for {operation}.");
}

/// <summary>Hands out scripted clients and records what each one was built from.</summary>
public sealed class FakeClientFactory(Func<VRChatConnection, IVRChat> create) : IVRChatClientFactory
{
    public FakeClientFactory(IVRChat client) : this(_ => client) { }

    /// <summary>Every connection a client was built from, in order.</summary>
    public List<VRChatConnection> Built { get; } = [];

    public IVRChat Create(VRChatConnection connection)
    {
        Built.Add(connection);
        return create(connection);
    }
}

/// <summary>The settings row, without the database.</summary>
public sealed class FakeConnectionStore(VRChatConnection? connection = null) : IVRChatConnectionStore
{
    public VRChatConnection Connection { get; set; } =
        connection ?? new VRChatConnection("modbot@example.com", "hunter2", "JBSWY3DPEHPK3PXP");

    public int SessionSaves { get; private set; }

    public string? SavedAuthCookie { get; private set; }

    public string? SavedTwoFactorAuthCookie { get; private set; }

    public Task<VRChatConnection> ReadAsync(CancellationToken ct = default) =>
        Task.FromResult(Connection);

    public Task SaveSessionAsync(
        string? authCookie, string? twoFactorAuthCookie, CancellationToken ct = default)
    {
        SessionSaves++;
        SavedAuthCookie = authCookie;
        SavedTwoFactorAuthCookie = twoFactorAuthCookie;
        Connection = Connection with { AuthCookie = authCookie, TwoFactorAuthCookie = twoFactorAuthCookie };

        return Task.CompletedTask;
    }

    public int SignInSaves { get; private set; }

    public Task SaveSignInAsync(
        string authCookie, string? twoFactorAuthCookie, VRChatSignedInAccount account, CancellationToken ct = default)
    {
        SignInSaves++;
        SessionSaves++;
        SavedAuthCookie = authCookie;
        SavedTwoFactorAuthCookie = twoFactorAuthCookie;
        Connection = Connection with
        {
            AuthCookie = authCookie,
            TwoFactorAuthCookie = twoFactorAuthCookie,
            SessionAccount = account.Account,
            SessionUserId = account.UserId,
            DisplayName = account.DisplayName ?? Connection.DisplayName,
        };

        return Task.CompletedTask;
    }
}
