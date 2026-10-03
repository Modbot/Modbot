using System.Buffers.Text;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Core.Time;

namespace Modbot.Core.Google;

/// <summary>
/// Signs in to Google as the service account and keeps the access token in memory (Google Calendar
/// design §3.1). One per process: a singleton.
/// </summary>
/// <remarks>
/// <para>
/// The sign-in is OAuth's JWT bearer grant: a JWT signed with the key's RS256 private key is
/// exchanged at <see cref="TokenAddress"/> for an access token that lasts an hour. It is written with
/// <see cref="RSA"/> alone, with no Google package, and its times come from <see cref="IModbotClock"/>.
/// </para>
/// <para>
/// The token is kept until 5 minutes before Google says it ends, and nothing about it is stored.
/// At most one token request is made a minute for a key: inside that minute the last answer, a token
/// or a refusal, is handed back again, so a refused key cannot become a loop of refused requests.
/// </para>
/// </remarks>
public sealed class GoogleSignIn(IHttpClientFactory http, IModbotClock clock)
{
    /// <summary>
    /// Where every token request goes. Fixed: the key file's own <c>token_uri</c> is never read.
    /// </summary>
    public const string TokenAddress = "https://oauth2.googleapis.com/token";

    /// <summary>
    /// What Modbot asks Google for: changing events, and reading who a calendar is shared with
    /// (for Check's Public line). Not the broad <c>calendar</c> scope (design decision 2).
    /// </summary>
    public const string Scopes =
        "https://www.googleapis.com/auth/calendar.events https://www.googleapis.com/auth/calendar.acls.readonly";

    /// <summary>How long before Google's end a kept token is given up and a new one asked for.</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);

    /// <summary>The fewest minutes between two token requests for the same key.</summary>
    public static readonly TimeSpan RequestFloor = TimeSpan.FromMinutes(1);

    /// <summary>How long the signed JWT says it lasts. Google refuses more than an hour.</summary>
    public static readonly TimeSpan AssertionLifetime = TimeSpan.FromMinutes(55);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Kept? _kept;

    /// <summary>
    /// An access token for <paramref name="key"/>: the kept one while it has more than
    /// <see cref="RenewBefore"/> left, otherwise a new one.
    /// </summary>
    /// <param name="fresh">
    /// Ask Google again even when a token is kept, as Check does to prove the key still works.
    /// Still never more than one request a minute.
    /// </param>
    public async Task<GoogleResult<string>> TokenAsync(GoogleCredentials key, bool fresh, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        await _gate.WaitAsync(ct);

        try
        {
            var now = clock.UtcNow;
            var who = Who(key);
            var kept = _kept is { } k && k.Who == who ? k : null;

            if (!fresh && kept?.Token is { } token && now < kept.ExpiresAt - RenewBefore)
                return GoogleResult<string>.Ok(token);

            if (kept is not null && now >= kept.RequestedAt && now - kept.RequestedAt < RequestFloor)
            {
                if (kept.Token is { } recent && now < kept.ExpiresAt - RenewBefore)
                    return GoogleResult<string>.Ok(recent);

                if (kept.Failure is { } refused)
                    return GoogleResult<string>.Failed(refused);
            }

            var answer = await RequestAsync(key, now, ct);

            if (answer.Value is { AccessToken: { } got } value)
            {
                _kept = new Kept(who, now, got, now.AddSeconds(value.ExpiresIn), null);
                return GoogleResult<string>.Ok(got);
            }

            _kept = new Kept(who, now, null, now, answer.Failure);
            return GoogleResult<string>.Failed(answer.Failure!);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets the kept token, for a key that was removed or replaced.</summary>
    public void Forget() => Volatile.Write(ref _kept, null);

    /// <summary>
    /// The signed JWT for the token request (RFC 7523): RS256, the account as <c>iss</c>,
    /// <see cref="Scopes"/>, Google's token address as <c>aud</c>, and <c>iat</c>/<c>exp</c> from
    /// <paramref name="now"/>. The decrypted key is used for this call only.
    /// </summary>
    public static string Assertion(GoogleCredentials key, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(key);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(key.PrivateKeyPem);

        var head = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(
            new JwtHead("RS256", "JWT", key.KeyId), GoogleJson.Default.JwtHead));

        var body = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(
            new JwtBody(
                key.ClientEmail,
                Scopes,
                TokenAddress,
                now.ToUnixTimeSeconds(),
                now.Add(AssertionLifetime).ToUnixTimeSeconds()),
            GoogleJson.Default.JwtBody));

        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes($"{head}.{body}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{head}.{body}.{Base64Url.EncodeToString(signature)}";
    }

    private async Task<GoogleResult<TokenAnswer>> RequestAsync(GoogleCredentials key, DateTimeOffset now, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(
        [
            new("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
            new("assertion", Assertion(key, now)),
        ]);

        try
        {
            using var response = await http.CreateClient(GoogleCalendarClient.HttpClientName)
                .PostAsync(new Uri(TokenAddress), content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                return GoogleResult<TokenAnswer>.Failed(
                    GoogleErrors.FromToken(response.StatusCode, text, response.Headers.RetryAfter));
            }

            var answer = await response.Content.ReadFromJsonAsync(GoogleJson.Default.TokenAnswer, ct);

            if (answer is null || string.IsNullOrEmpty(answer.AccessToken) || answer.ExpiresIn <= 0)
                return GoogleResult<TokenAnswer>.Failed(new GoogleFailure(GoogleProblem.Other, (int)response.StatusCode));

            return GoogleResult<TokenAnswer>.Ok(answer);
        }
        catch (HttpRequestException)
        {
            return GoogleResult<TokenAnswer>.Failed(GoogleErrors.NoAnswer());
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return GoogleResult<TokenAnswer>.Failed(GoogleErrors.NoAnswer());
        }
        catch (JsonException)
        {
            return GoogleResult<TokenAnswer>.Failed(GoogleErrors.NoAnswer());
        }
    }

    private static string Who(GoogleCredentials key) => key.ClientEmail + "\n" + key.KeyId;

    /// <param name="Who">The account and key the entry is for.</param>
    /// <param name="RequestedAt">When the token request was made.</param>
    /// <param name="Token">The access token, when the request got one.</param>
    /// <param name="ExpiresAt">When Google said it ends.</param>
    /// <param name="Failure">Why the request got none.</param>
    private sealed record Kept(string Who, DateTimeOffset RequestedAt, string? Token, DateTimeOffset ExpiresAt, GoogleFailure? Failure)
    {
        // The token is a secret; this record is never logged, but keep it out of any ToString.
        public override string ToString() => $"Kept({RequestedAt:O})";
    }
}

/// <summary>The JWT's header.</summary>
internal sealed record JwtHead(
    [property: JsonPropertyName("alg")] string Alg,
    [property: JsonPropertyName("typ")] string Typ,
    [property: JsonPropertyName("kid")] string Kid);

/// <summary>The JWT's claims (Google's service account sign-in).</summary>
internal sealed record JwtBody(
    [property: JsonPropertyName("iss")] string Iss,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("aud")] string Aud,
    [property: JsonPropertyName("iat")] long Iat,
    [property: JsonPropertyName("exp")] long Exp);

/// <summary>The token endpoint's answer.</summary>
internal sealed record TokenAnswer(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("token_type")] string? TokenType)
{
    public override string ToString() => $"TokenAnswer({ExpiresIn}s)";
}
