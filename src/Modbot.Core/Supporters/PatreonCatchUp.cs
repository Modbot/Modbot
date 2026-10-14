using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Serilog;

namespace Modbot.Core.Supporters;

/// <summary>What one run of <see cref="PatreonCatchUp"/> did.</summary>
/// <param name="CaughtUp">Whether the daily catch-up ran to the end.</param>
/// <param name="Members">How many members the catch-up read.</param>
/// <param name="Looked">How many patrons were looked up one by one.</param>
/// <param name="Taken">How many of those changed a supporter.</param>
public sealed record PatreonPassResult(bool CaughtUp, int Members, int Looked, int Taken)
{
    public static readonly PatreonPassResult Nothing = new(false, 0, 0, 0);
}

/// <summary>
/// Asks Patreon for what its webhooks leave out: each patron's Discord account, and once a day every
/// current patron (Patreon design, step 2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The Discord lookup.</strong> A patron a webhook brought in has no Discord account. Their
/// member is fetched with its user's connected apps, once, and fed through the same intake as a
/// webhook message, which matches the account by Ko-fi's rules. The webhook has already been answered;
/// this runs apart from it.
/// </para>
/// <para>
/// <strong>The daily catch-up.</strong> Every member of the creator's first campaign, page by page, each
/// fed through the intake as a webhook message would be (same tier, roles, thank-you and roles-off
/// setting). It brings in patrons from before Modbot and anything a webhook that never arrived left out.
/// </para>
/// <para>
/// <strong>Pace.</strong> At most one request a second (<see cref="PatreonLimits.MinGap"/>). A 429 is never
/// retried: the run stops, <see cref="Settings.PatreonStoppedUntil"/> holds every call back (15 minutes at
/// least, longer when Patreon says), and the next run after it probes. Any other trouble is written as a
/// sentence in <see cref="Settings.PatreonCatchUpProblem"/> and the catch-up tries again later; while
/// the sentence stands only the catch-up asks Patreon anything.
/// </para>
/// <para>
/// <strong>Tokens.</strong> The creator's access token is replaced with the refresh token before it runs out
/// (a token pasted in has no known age, so it is replaced the first time), and the new pair is saved at once,
/// since Patreon gives a new refresh token each time. A 401 gets one new token and one more try. No token is logged.
/// </para>
/// </remarks>
public sealed class PatreonCatchUp
{
    /// <summary>How often every patron is read.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromDays(1);

    /// <summary>How soon the catch-up tries again after Patreon did not answer.</summary>
    public static readonly TimeSpan TryAgainAfter = TimeSpan.FromMinutes(15);

    /// <summary>A token with less than this left is replaced.</summary>
    public static readonly TimeSpan RefreshBefore = TimeSpan.FromDays(3);

    private const int MostProblemLength = 300;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly ISecretProtector _protector;
    private readonly PatreonClient _patreon;
    private readonly SupporterIntake _intake;
    private readonly SupporterSignal _signal;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    private int _calls;
    private bool _stopped;
    private bool _refreshed;
    private string? _token;

    public PatreonCatchUp(
        ModbotContext db,
        IModbotClock clock,
        ISecretProtector protector,
        PatreonClient patreon,
        SupporterIntake intake,
        SupporterSignal signal,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(patreon);
        ArgumentNullException.ThrowIfNull(intake);
        ArgumentNullException.ThrowIfNull(signal);

        _db = db;
        _clock = clock;
        _protector = protector;
        _patreon = patreon;
        _intake = intake;
        _signal = signal;
        _delay = delay ?? Task.Delay;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>One run: the waiting lookups, and the catch-up when it is due. Reads the settings and nothing more when there is nothing to do.</summary>
    public async Task<PatreonPassResult> RunOnceAsync(CancellationToken ct)
    {
        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);

        if (settings is not { PatreonOn: true } || !HasKeys(settings))
            return PatreonPassResult.Nothing;

        var now = _clock.UtcNow;

        if (settings.PatreonStoppedUntil is { } until && now < until)
            return PatreonPassResult.Nothing;

        var catchUpDue = settings.PatreonCatchUpDueAt is not { } due || now >= due;
        var lookupsWaiting = settings.PatreonCatchUpProblem is null && await _db.Supporters.AnyAsync(WaitingForLookup, ct).ConfigureAwait(false);

        if (!catchUpDue && !lookupsWaiting)
            return PatreonPassResult.Nothing;

        if (await TokenAsync(ct).ConfigureAwait(false) is null)
            return PatreonPassResult.Nothing;

        var members = 0;
        var looked = 0;
        var taken = 0;
        var caughtUp = false;

        if (catchUpDue)
            (caughtUp, members, taken) = await CatchUpAsync(ct).ConfigureAwait(false);

        if (!_stopped)
        {
            var (lookedUp, lookupTaken) = await LookUpAsync(ct).ConfigureAwait(false);
            looked = lookedUp;
            taken += lookupTaken;
        }

        if (taken > 0)
            _signal.Changed();

        return new PatreonPassResult(caughtUp, members, looked, taken);
    }

    private static readonly System.Linq.Expressions.Expression<Func<Supporter, bool>> WaitingForLookup =
        s => s.Provider == SupporterProviders.Patreon && s.DiscordUserId == null && s.DiscordLookupAt == null && s.PatreonMemberId != null;

    private static bool HasKeys(Settings settings) =>
        !string.IsNullOrEmpty(settings.PatreonClientIdEncrypted)
        && !string.IsNullOrEmpty(settings.PatreonClientSecretEncrypted)
        && !string.IsNullOrEmpty(settings.PatreonAccessTokenEncrypted)
        && !string.IsNullOrEmpty(settings.PatreonRefreshTokenEncrypted);

    // ── The two jobs ─────────────────────────────────────────────────────────────────────

    private async Task<(bool Done, int Members, int Taken)> CatchUpAsync(CancellationToken ct)
    {
        if (await CampaignIdAsync(ct).ConfigureAwait(false) is not { } campaign)
            return (false, 0, 0);

        string? cursor = null;
        var members = 0;
        var taken = 0;

        for (var page = 0; page < PatreonLimits.MostPages; page++)
        {
            var current = cursor;
            var answer = await CallAsync(token => _patreon.MembersAsync(token, campaign, current, ct), notFoundIsTrouble: true, ct).ConfigureAwait(false);

            if (answer is not { Failure: null, Value: { } list })
                return (false, members, taken);

            foreach (var member in list.Data ?? [])
            {
                if (member.Type is not null && member.Type != "member")
                    continue;

                members++;

                if (await TakeAsync(list.MessageFor(member), ct).ConfigureAwait(false))
                    taken++;
            }

            var next = list.NextCursor;

            if (next is null || next == cursor)
            {
                await SaveSettingsAsync(
                    s =>
                    {
                        s.PatreonCatchUpAt = _clock.UtcNow;
                        s.PatreonCatchUpDueAt = _clock.UtcNow + Every;
                        s.PatreonCatchUpProblem = null;
                    },
                    ct).ConfigureAwait(false);

                return (true, members, taken);
            }

            cursor = next;
        }

        await ProblemAsync("Patreon's list of patrons is longer than Modbot reads at once.", Every, ct).ConfigureAwait(false);

        return (false, members, taken);
    }

    private async Task<(int Looked, int Taken)> LookUpAsync(CancellationToken ct)
    {
        var problem = await _db.Settings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.PatreonCatchUpProblem).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (problem is not null)
            return (0, 0);

        var waiting = await _db.Supporters.AsNoTracking()
            .Where(WaitingForLookup)
            .OrderBy(s => s.UpdatedAt)
            .Select(s => s.PatreonMemberId!)
            .Take(PatreonLimits.MostLookups)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var looked = 0;
        var taken = 0;

        foreach (var memberId in waiting)
        {
            var answer = await CallAsync(token => _patreon.MemberAsync(token, memberId, ct), notFoundIsTrouble: false, ct).ConfigureAwait(false);

            if (answer is null)
                break;

            looked++;

            // A member Patreon no longer has, or one the intake does not take, is not asked about again.
            if (answer.Value.Failure is { Problem: PatreonProblem.NotFound })
            {
                await MarkLookedUpAsync(memberId, ct).ConfigureAwait(false);
                continue;
            }

            if (answer.Value.Failure is not null || answer.Value.Value is not { } message)
                break;

            if (await TakeAsync(message, ct).ConfigureAwait(false))
                taken++;

            await MarkLookedUpAsync(memberId, ct).ConfigureAwait(false);
        }

        return (looked, taken);
    }

    /// <summary>Feeds one member through the intake like a webhook message. True when it changed a supporter.</summary>
    private async Task<bool> TakeAsync(PatreonMessage message, CancellationToken ct)
    {
        try
        {
            var outcome = await _intake.TakePatreonAsync(message, null, _clock.UtcNow, ct, discordKnown: true).ConfigureAwait(false);

            return outcome.Result == SupporterIntake.Taken;
        }
        catch (DbUpdateException e)
        {
            // One member the tables will not take must not hold up the rest; the next catch-up reads it again.
            _log.Warning(e, "A Patreon member could not be saved; the catch-up goes on");

            return false;
        }
        finally
        {
            // Hundreds of members share this context: keep it from growing, and from counting old rows as changes.
            _db.ChangeTracker.Clear();
        }
    }

    private async Task MarkLookedUpAsync(string memberId, CancellationToken ct)
    {
        var now = _clock.UtcNow;

        await _db.Supporters
            .Where(s => s.Provider == SupporterProviders.Patreon && s.PatreonMemberId == memberId && s.DiscordLookupAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.DiscordLookupAt, now), ct)
            .ConfigureAwait(false);
    }

    // ── The token and the campaign ───────────────────────────────────────────────────────

    private async Task<string?> TokenAsync(CancellationToken ct)
    {
        var settings = await _db.Settings.AsNoTracking().FirstAsync(s => s.Id == 1, ct).ConfigureAwait(false);
        var access = _protector.Unprotect(settings.PatreonAccessTokenEncrypted);

        if (string.IsNullOrEmpty(access))
        {
            await ProblemAsync(UnreadableKeys, Every, ct).ConfigureAwait(false);
            return null;
        }

        if (settings.PatreonTokenExpiresAt is { } expires && expires - _clock.UtcNow > RefreshBefore)
            return _token = access;

        return await RefreshAsync(ct).ConfigureAwait(false);
    }

    private const string UnreadableKeys = "Modbot cannot read the saved Patreon keys. Save them again.";

    /// <summary>Asks Patreon for a new access token and saves the new pair at once. Null when it could not.</summary>
    private async Task<string?> RefreshAsync(CancellationToken ct)
    {
        var settings = await _db.Settings.AsNoTracking().FirstAsync(s => s.Id == 1, ct).ConfigureAwait(false);
        var clientId = _protector.Unprotect(settings.PatreonClientIdEncrypted);
        var clientSecret = _protector.Unprotect(settings.PatreonClientSecretEncrypted);
        var refresh = _protector.Unprotect(settings.PatreonRefreshTokenEncrypted);

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret) || string.IsNullOrEmpty(refresh))
        {
            await ProblemAsync(UnreadableKeys, Every, ct).ConfigureAwait(false);
            return null;
        }

        await PaceAsync(ct).ConfigureAwait(false);
        var answer = await _patreon.RefreshAsync(clientId, clientSecret, refresh, ct).ConfigureAwait(false);

        if (answer.Failure is { } failure)
        {
            await TroubleAsync(failure, ct).ConfigureAwait(false);
            return null;
        }

        var tokens = answer.Value!;

        await SaveSettingsAsync(
            s =>
            {
                s.PatreonAccessTokenEncrypted = _protector.Protect(tokens.AccessToken);

                if (tokens.RefreshToken is not null)
                    s.PatreonRefreshTokenEncrypted = _protector.Protect(tokens.RefreshToken);

                s.PatreonTokenExpiresAt = _clock.UtcNow + tokens.LastsFor;
            },
            ct).ConfigureAwait(false);

        _refreshed = true;

        return _token = tokens.AccessToken;
    }

    /// <summary>The campaign to read: the one kept, or else the creator's first, found and kept.</summary>
    private async Task<string?> CampaignIdAsync(CancellationToken ct)
    {
        var kept = await _db.Settings.AsNoTracking().Where(s => s.Id == 1).Select(s => s.PatreonCampaignId).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(kept))
            return kept;

        var answer = await CallAsync(token => _patreon.FirstCampaignIdAsync(token, ct), notFoundIsTrouble: true, ct).ConfigureAwait(false);

        if (answer is not { Failure: null })
            return null;

        if (answer.Value.Value is not { } id)
        {
            await ProblemAsync("The Patreon account has no campaign.", Every, ct).ConfigureAwait(false);
            return null;
        }

        await SaveSettingsAsync(s => s.PatreonCampaignId = id, ct).ConfigureAwait(false);

        return id;
    }

    // ── Calls ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One call with the kept token, a second after the one before. A 401 gets one new token and one more
    /// try; a failure is written down and stops the run (a 429 is never retried). Null when no call was made.
    /// </summary>
    private async Task<PatreonResult<T>?> CallAsync<T>(Func<string, Task<PatreonResult<T>>> call, bool notFoundIsTrouble, CancellationToken ct)
    {
        if (_stopped || _token is null)
            return null;

        await PaceAsync(ct).ConfigureAwait(false);
        var answer = await call(_token).ConfigureAwait(false);

        if (answer.Failure is { Problem: PatreonProblem.Unauthorized } && !_refreshed)
        {
            if (await RefreshAsync(ct).ConfigureAwait(false) is null)
                return null;

            await PaceAsync(ct).ConfigureAwait(false);
            answer = await call(_token!).ConfigureAwait(false);
        }

        if (answer.Failure is { } failure && (notFoundIsTrouble || failure.Problem != PatreonProblem.NotFound))
            await TroubleAsync(failure, ct).ConfigureAwait(false);

        return answer;
    }

    private async Task PaceAsync(CancellationToken ct)
    {
        if (_calls++ > 0)
            await _delay(PatreonLimits.MinGap, ct).ConfigureAwait(false);
    }

    // ── Trouble ──────────────────────────────────────────────────────────────────────────

    private async Task TroubleAsync(PatreonFailure failure, CancellationToken ct)
    {
        switch (failure.Problem)
        {
            case PatreonProblem.Limited:
                var until = _clock.UtcNow + PatreonLimits.StopFor(failure);

                await SaveSettingsAsync(
                    s =>
                    {
                        s.PatreonStoppedUntil = until;
                        s.PatreonCatchUpProblem = "Patreon is limiting Modbot's requests. Nothing is sent to it for a while.";

                        // The catch-up is what clears the sentence, so it is the first thing to run after the stop.
                        if (s.PatreonCatchUpDueAt is { } due && due > until)
                            s.PatreonCatchUpDueAt = until;
                    },
                    ct).ConfigureAwait(false);

                _stopped = true;
                _log.Warning("Patreon is limiting Modbot ({Status}); nothing is sent to Patreon until {Until}", failure.Status, until);
                break;

            case PatreonProblem.Unavailable:
                await ProblemAsync("Patreon did not answer.", TryAgainAfter, ct).ConfigureAwait(false);
                break;

            case PatreonProblem.CredentialsRefused:
                await ProblemAsync("Patreon did not accept the client id, client secret or refresh token.", Every, ct).ConfigureAwait(false);
                break;

            case PatreonProblem.Unauthorized:
                await ProblemAsync("Patreon did not accept the creator access token.", Every, ct).ConfigureAwait(false);
                break;

            case PatreonProblem.Forbidden:
                await ProblemAsync("Patreon does not let this access token read the campaign's members.", Every, ct).ConfigureAwait(false);
                break;

            case PatreonProblem.NotFound:
                await ProblemAsync("Patreon does not have the campaign.", Every, ct).ConfigureAwait(false);
                break;

            default:
                await ProblemAsync("Patreon refused the request.", Every, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Writes why the run stopped and when the catch-up tries again, and stops the run.</summary>
    private async Task ProblemAsync(string sentence, TimeSpan tryAgainAfter, CancellationToken ct)
    {
        var clipped = sentence.Length <= MostProblemLength ? sentence : sentence[..MostProblemLength];

        await SaveSettingsAsync(
            s =>
            {
                s.PatreonCatchUpProblem = clipped;
                s.PatreonCatchUpDueAt = _clock.UtcNow + tryAgainAfter;
            },
            ct).ConfigureAwait(false);

        _stopped = true;
        _log.Warning("Patreon catch-up stopped: {Problem}", clipped);
    }

    /// <summary>Changes the settings row and saves at once, so nothing is left pending for the intake to mistake for its own change.</summary>
    private async Task SaveSettingsAsync(Action<Settings> change, CancellationToken ct)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);
        change(settings);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _db.ChangeTracker.Clear();
    }
}
