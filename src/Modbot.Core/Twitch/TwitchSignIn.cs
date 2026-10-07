using System.Security.Cryptography;
using System.Text;
using Modbot.Core.Time;

namespace Modbot.Core.Twitch;

/// <summary>
/// Keeps the app access token Modbot asks Twitch for, in memory (Twitch design). One per process: a
/// singleton.
/// </summary>
/// <remarks>
/// <para>
/// The client credentials grant gives a token that lasts about two months and has no refresh token,
/// so a token in its last hour is simply asked for again, and one Twitch refused (401) is dropped and
/// asked for again once. Nothing about it is stored.
/// </para>
/// <para>
/// At most one token request is made a minute for a client id and secret: inside that minute the last
/// answer, a token or a refusal, is handed back again, so a refused secret cannot become a loop of
/// refused requests. A request that got no answer, or a 5xx, is not kept: it says nothing about the
/// secret.
/// </para>
/// </remarks>
public sealed class TwitchSignIn(TwitchClient twitch, IModbotClock clock)
{
    /// <summary>How long before Twitch's end a kept token is given up and a new one asked for.</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromHours(1);

    /// <summary>The fewest minutes between two token requests for the same client id and secret.</summary>
    public static readonly TimeSpan RequestFloor = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Kept? _kept;

    /// <summary>
    /// An app access token for the client id and secret: the kept one while it has more than
    /// <see cref="RenewBefore"/> left, otherwise a new one.
    /// </summary>
    /// <param name="fresh">
    /// Ask Twitch again even when a token is kept, as Check does to prove the secret still works.
    /// Still never more than one request a minute.
    /// </param>
    public async Task<TwitchResult<string>> TokenAsync(string clientId, string clientSecret, bool fresh, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);

        try
        {
            var now = clock.UtcNow;
            var who = Who(clientId, clientSecret);
            var kept = _kept is { } k && k.Who == who ? k : null;

            if (!fresh && kept?.Token is { } token && now < kept.ExpiresAt - RenewBefore)
                return TwitchResult<string>.Ok(token);

            if (kept is not null && now >= kept.RequestedAt && now - kept.RequestedAt < RequestFloor)
            {
                if (kept.Token is { } recent && now < kept.ExpiresAt - RenewBefore)
                    return TwitchResult<string>.Ok(recent);

                if (kept.Failure is { } refused)
                    return TwitchResult<string>.Failed(refused);
            }

            var answer = await twitch.TokenAsync(clientId, clientSecret, ct);

            if (answer.Failure is null && answer.Value is { } value)
            {
                _kept = new Kept(who, now, value.Value, now + value.LastsFor, null);
                return TwitchResult<string>.Ok(value.Value);
            }

            // No answer, or Twitch's own trouble, is not kept: the next ask may go out at once
            // rather than a whole minute later. A refused secret and a limit are kept, so neither
            // can turn into a loop of requests.
            if (answer.Failure!.Problem != TwitchProblem.Unavailable)
                _kept = new Kept(who, now, null, now, answer.Failure);

            return TwitchResult<string>.Failed(answer.Failure);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets the kept token, for a client id or secret that was removed or replaced.</summary>
    public void Forget() => Volatile.Write(ref _kept, null);

    /// <summary>
    /// Twitch answered 401 to <paramref name="accessToken"/>: it is not handed out again, so the next
    /// ask makes a new token request.
    /// </summary>
    public void Refused(string accessToken)
    {
        var kept = Volatile.Read(ref _kept);

        if (kept is { Token: { } token } && token == accessToken)
            Volatile.Write(ref _kept, kept with { Token = null, ExpiresAt = kept.RequestedAt });
    }

    /// <summary>The client id and a hash of the secret: never the secret itself.</summary>
    private static string Who(string clientId, string clientSecret) =>
        clientId + "\n" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientSecret)));

    /// <param name="Who">The client id and secret the entry is for.</param>
    /// <param name="RequestedAt">When the token request was made.</param>
    /// <param name="Token">The access token, when the request got one.</param>
    /// <param name="ExpiresAt">When Twitch said it ends.</param>
    /// <param name="Failure">Why the request got none.</param>
    private sealed record Kept(string Who, DateTimeOffset RequestedAt, string? Token, DateTimeOffset ExpiresAt, TwitchFailure? Failure)
    {
        // The token is a secret; this record is never logged, but keep it out of any ToString.
        public override string ToString() => $"Kept({RequestedAt:O})";
    }
}
