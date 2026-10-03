using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Core.Users;

namespace Modbot.Discord.Commands;

/// <summary>How a <c>/verify</c> went, for the reply and the command fact.</summary>
public enum VerifyOutcome
{
    /// <summary>The caller's Discord account is now the account's, or already was.</summary>
    Connected,

    /// <summary>No live code is that one: wrong, expired, used, or tried too often.</summary>
    WrongCode,

    /// <summary>The code was right, but another Modbot account proved this Discord account.</summary>
    Taken,

    /// <summary>Over the per-person limit. Not recorded.</summary>
    TooFast,
}

/// <summary>What <see cref="VerifyCommand.RunAsync"/> did.</summary>
/// <param name="Account">The Modbot account the caller's Discord account is now, when connected.</param>
public sealed record VerifyAnswer(VerifyOutcome Outcome, ModbotUser? Account)
{
    /// <summary>What the caller is told. Every outcome is one sentence, and none names an account.</summary>
    public string Message => Outcome switch
    {
        VerifyOutcome.Connected => VerifyCommand.ConnectedMessage,
        VerifyOutcome.Taken => VerifyCommand.TakenMessage,
        VerifyOutcome.TooFast => MeCommand.TooFastMessage,
        _ => VerifyCommand.WrongMessage,
    };
}

/// <summary>
/// <c>/verify code:</c>: a staff member proves which Discord account is theirs with the code their
/// account page showed them (Discord account linking design §14).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Discord says who ran it.</strong> A slash command carries the account that ran it, and
/// nobody can run one as somebody else; the code says which Modbot account asked. So a right code
/// puts the caller's Discord id on that account as proven, exactly as Connect Discord does, through
/// <see cref="StaffDiscordProof"/>: a Discord account another Modbot account proved is refused and
/// never moved, and one that others only typed in comes off them.
/// </para>
/// <para>
/// <strong>Guessing gets nowhere.</strong> Each Discord account has <see cref="MemberCommandLimits"/>
/// of its own for this command, separate from <c>/me</c>'s; a wrong code counts against the live
/// code it began like (<see cref="StaffDiscordCodes"/>); and a code lasts fifteen minutes and works
/// once. Wrong, expired, used up and never issued all get the same sentence.
/// </para>
/// <para>
/// <strong>The same facts as Connect Discord</strong>, written the way the API's account facts are:
/// <c>modbot.user.discord.link</c> on the account with <c>via: command</c>, and
/// <c>modbot.user.discord.unlink</c> on each account the id came off.
/// </para>
/// </remarks>
public sealed class VerifyCommand
{
    /// <summary>The key its own <see cref="MemberCommandLimits"/> is registered under.</summary>
    public const string LimitsKey = "verify";

    public const string ConnectedMessage = "Your Discord account is now connected to Modbot.";
    public const string WrongMessage = "That code isn't right or has expired.";
    public const string TakenMessage = "This Discord account is connected to another Modbot account.";

    /// <summary>What the fact says the account was proven by.</summary>
    public const string Via = "command";

    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly IModbotClock _clock;
    private readonly MemberCommandLimits _limits;

    public VerifyCommand(
        ModbotContext db,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        IModbotClock clock,
        [FromKeyedServices(LimitsKey)] MemberCommandLimits limits)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(limits);

        _db = db;
        _facts = facts;
        _partitions = partitions;
        _clock = clock;
        _limits = limits;
    }

    /// <summary>Runs one <c>/verify</c> for the Discord account that ran it.</summary>
    public async Task<VerifyAnswer> RunAsync(string discordUserId, string discordUsername, string? typed, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(discordUserId);
        ArgumentNullException.ThrowIfNull(discordUsername);

        var now = _clock.UtcNow;

        if (!_limits.TryUse(discordUserId, now))
            return new VerifyAnswer(VerifyOutcome.TooFast, null);

        var code = await StaffDiscordCodes.FindAsync(_db, typed, now, ct).ConfigureAwait(false);
        if (code is null)
            return new VerifyAnswer(VerifyOutcome.WrongCode, null);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == code.UserId, ct).ConfigureAwait(false);
        if (user is null || user.IsDisabled || user.IsDeleted)
        {
            // Asked for by an account that can no longer sign in: it proves nothing for anybody.
            _db.StaffDiscordCodes.Remove(code);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new VerifyAnswer(VerifyOutcome.WrongCode, null);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var proof = await StaffDiscordProof
            .ProveAsync(_db, user, discordUserId, discordUsername, now, ct)
            .ConfigureAwait(false);

        if (proof.Outcome == StaffDiscordProofOutcome.Taken)
        {
            // The code stays: run from the right Discord account, it still works.
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
            return new VerifyAnswer(VerifyOutcome.Taken, null);
        }

        _db.StaffDiscordCodes.Remove(code);

        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The same code, run twice at once: the other run used it.
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
            return new VerifyAnswer(VerifyOutcome.WrongCode, null);
        }

        if (proof.Outcome == StaffDiscordProofOutcome.Proven)
            await WriteFactsAsync(user, discordUserId, discordUsername, proof, now, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return new VerifyAnswer(VerifyOutcome.Connected, user);
    }

    /// <summary>
    /// The facts Connect Discord writes, in the shape the API's account facts give them: about the
    /// account, by the account, with <c>username</c> and <c>actorDisplayName</c> in the payload.
    /// </summary>
    private async Task WriteFactsAsync(
        ModbotUser user,
        string discordUserId,
        string discordUsername,
        StaffDiscordProofResult proof,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

        foreach (var other in proof.TypedElsewhere)
        {
            await WriteAsync(
                FactType.DiscordDisconnected,
                other,
                user,
                new JsonObject
                {
                    ["discordUserId"] = discordUserId,
                    ["proven"] = false,
                    ["why"] = "proven-by-another-account",
                },
                now,
                ct).ConfigureAwait(false);
        }

        await WriteAsync(
            FactType.DiscordConnected,
            user,
            user,
            new JsonObject
            {
                ["discordUserId"] = discordUserId,
                ["discordUsername"] = discordUsername,
                ["replaced"] = proof.Replaced,
                ["via"] = Via,
            },
            now,
            ct).ConfigureAwait(false);
    }

    private Task WriteAsync(string type, ModbotUser subject, ModbotUser actor, JsonObject data, DateTimeOffset now, CancellationToken ct)
    {
        data["actorDisplayName"] = actor.Username;
        data.TryAdd("username", subject.Username);

        return _facts.WriteAsync(
            new FactRecord
            {
                Type = type,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = subject.Id.ToString(),
                ActorPlatform = FactPlatform.Modbot,
                ActorId = actor.Id.ToString(),
                Source = FactSource.Modbot,
                Data = data,
            },
            ct);
    }
}
