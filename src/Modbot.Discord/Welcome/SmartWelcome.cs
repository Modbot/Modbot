using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Npgsql;

namespace Modbot.Discord.Welcome;

/// <summary>
/// Sends a new Discord member up to three next steps, picked from the roles they hold by the rules
/// the admins set (smart welcome design). Separate from the join gate's own welcome message.
/// </summary>
/// <remarks>
/// <para>
/// Three events lead here: the member finished Discord's onboarding, the member joined a server that
/// has none, and the join gate let a member in whose welcome was waiting for it (design §3). They end
/// in one send that <em>claims</em> the member first (design §5.1), so however the events race a
/// member is welcomed once.
/// </para>
/// <para>
/// Nothing here throws for a refused message. A closed DM, a missing channel and Discord being down
/// are written into the fact and the welcome ends.
/// </para>
/// </remarks>
public sealed class SmartWelcome
{
    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly IModbotClock _clock;

    public SmartWelcome(
        ModbotContext db,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _facts = facts;
        _partitions = partitions;
        _clock = clock;
    }

    /// <summary>
    /// A member joined. Returns how the welcome went out (<c>dm</c>, <c>channel</c>, <c>both</c> or
    /// <c>none</c>), or null when none was sent now.
    /// </summary>
    /// <param name="gateHolds">The join gate took the member: the welcome waits until it lets them in.</param>
    public async Task<string?> JoinedAsync(IDiscordGateway gateway, DiscordMemberJoin member, bool gateHolds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(member);

        if (member.IsBot)
            return null;

        var settings = await ReadSettingsAsync(ct).ConfigureAwait(false);
        if (settings is not { DiscordWelcomeEnabled: true })
            return null;

        // A server with onboarding is welcomed when the member finishes it. A read that failed counts
        // as no onboarding (design §3.2): a welcome that is early beats one that never comes.
        if (await gateway.ReadOnboardingOnAsync(member.GuildId, ct).ConfigureAwait(false) == true)
            return null;

        if (gateHolds)
        {
            await WaitAsync(member.UserId, ct).ConfigureAwait(false);
            return null;
        }

        return await SendAsync(gateway, settings, member.GuildId, member.UserId, member.Member?.RoleIds ?? [], ct)
            .ConfigureAwait(false);
    }

    /// <summary>The member finished Discord's onboarding (design §3).</summary>
    public async Task<string?> FinishedOnboardingAsync(
        IDiscordGateway gateway, string guildId, DiscordMemberSnapshot member, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(member);

        if (member.IsBot)
            return null;

        var settings = await ReadSettingsAsync(ct).ConfigureAwait(false);
        if (settings is not { DiscordWelcomeEnabled: true })
            return null;

        // Only members who joined after the switch went on (design §3.1).
        if (settings.DiscordWelcomeStartedAt is not { } started || member.JoinedAt is not { } joined || joined < started)
            return null;

        return await SendAsync(gateway, settings, guildId, member.UserId, member.RoleIds, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A member changed. Sends the welcome that waits for them when the join gate is done with them
    /// (design §3.3). Returns null when nothing was waiting or the gate still holds them.
    /// </summary>
    public async Task<string?> UpdatedAsync(
        IDiscordGateway gateway, string guildId, DiscordMemberSnapshot member, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(member);

        var settings = await ReadSettingsAsync(ct).ConfigureAwait(false);
        if (settings is not { DiscordWelcomeEnabled: true })
            return null;

        var waiting = await _db.DiscordWelcomes.AsNoTracking()
            .AnyAsync(w => w.DiscordUserId == member.UserId && w.State == DiscordWelcomeStates.Waiting, ct)
            .ConfigureAwait(false);

        if (!waiting)
            return null;

        var gateRole = settings.DiscordGateMemberRoleId;
        var gateDone = settings.DiscordGateMode != DiscordGateModes.On
                       || gateRole is null
                       || member.RoleIds.Contains(gateRole, StringComparer.Ordinal);

        if (!gateDone)
            return null;

        return await SendAsync(gateway, settings, guildId, member.UserId, member.RoleIds, ct).ConfigureAwait(false);
    }

    /// <summary>Up to three steps for a member who holds these roles: matching rules, then defaults (design §4).</summary>
    public static IReadOnlyList<DiscordWelcomeStep> PickSteps(
        IReadOnlyList<DiscordWelcomeStep> rules, IReadOnlyList<DiscordWelcomeStep> defaults, IReadOnlyCollection<string> roleIds)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(roleIds);

        var picked = new List<DiscordWelcomeStep>();

        foreach (var rule in rules.OrderBy(r => r.Position))
        {
            if (picked.Count == DiscordWelcomeLimits.MostSteps)
                break;

            if (rule.RoleId is { } role && roleIds.Contains(role, StringComparer.Ordinal))
                picked.Add(rule);
        }

        foreach (var step in defaults.OrderBy(d => d.Position))
        {
            if (picked.Count == DiscordWelcomeLimits.MostSteps)
                break;

            picked.Add(step);
        }

        return picked;
    }

    /// <summary>The message, numbered one step a line.</summary>
    public static string MessageText(string? serverName, IReadOnlyList<DiscordWelcomeStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var text = new StringBuilder(string.IsNullOrWhiteSpace(serverName)
            ? "Welcome! Your next steps:"
            : $"Welcome to **{CardText.Fit(CardText.EscapeName(serverName), CardText.MaxNameLength)}**! Your next steps:");

        for (var i = 0; i < steps.Count; i++)
            text.Append('\n').Append(i + 1).Append(". ").Append(steps[i].Text);

        return text.ToString();
    }

    /// <summary>A link button for each step that has a link, named by the step's number.</summary>
    public static IReadOnlyList<DiscordLinkButton> Buttons(IReadOnlyList<DiscordWelcomeStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var buttons = new List<DiscordLinkButton>();

        for (var i = 0; i < steps.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(steps[i].Link))
                buttons.Add(new DiscordLinkButton($"Step {i + 1}", steps[i].Link!));
        }

        return buttons;
    }

    private async Task<Settings?> ReadSettingsAsync(CancellationToken ct)
        => await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);

    private async Task<string?> SendAsync(
        IDiscordGateway gateway, Settings settings, string guildId, string userId, IReadOnlyList<string> roleIds, CancellationToken ct)
    {
        var steps = await _db.DiscordWelcomeSteps.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var picked = PickSteps(
            [.. steps.Where(s => s.Kind == DiscordWelcomeStepKinds.Rule)],
            [.. steps.Where(s => s.Kind == DiscordWelcomeStepKinds.Default)],
            roleIds);

        // Nobody is claimed for a welcome with nothing in it.
        if (picked.Count == 0)
            return null;

        if (!await ClaimAsync(userId, ct).ConfigureAwait(false))
            return null;

        var serverName = await _db.DiscordServers.AsNoTracking()
            .Where(s => s.GuildId == guildId)
            .Select(s => s.Name)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var text = MessageText(serverName, picked);
        var buttons = Buttons(picked);
        var errors = new List<string>();
        var viaDm = false;
        var viaChannel = false;

        if (DiscordWelcomeWhere.UsesDirectMessage(settings.DiscordWelcomeVia))
        {
            var dm = await gateway.SendDirectMessageAsync(userId, text, buttons, ct).ConfigureAwait(false);
            viaDm = dm.Sent;

            if (!dm.Sent && dm.Error is not null)
                errors.Add(dm.Error);
        }

        if (DiscordWelcomeWhere.UsesChannel(settings.DiscordWelcomeVia))
        {
            var channel = settings.DiscordWelcomeChannelId?.Trim();

            if (string.IsNullOrEmpty(channel))
            {
                errors.Add("No welcome channel is set.");
            }
            else
            {
                var post = await gateway.MentionAsync(channel, userId, text, buttons, ct).ConfigureAwait(false);
                viaChannel = post.Sent;

                if (!post.Sent && post.Error is not null)
                    errors.Add(post.Error);
            }
        }

        var via = (viaDm, viaChannel) switch
        {
            (true, true) => DiscordWelcomeWhere.Both,
            (true, false) => DiscordWelcomeWhere.DirectMessage,
            (false, true) => DiscordWelcomeWhere.Channel,
            _ => "none",
        };

        await RecordAsync(userId, via, picked.Count, errors, ct).ConfigureAwait(false);
        return via;
    }

    /// <summary>
    /// Makes the member this welcome's: a waiting row becomes sent, or a sent row is made. False when
    /// somebody else got there first, or the member was welcomed before.
    /// </summary>
    private async Task<bool> ClaimAsync(string userId, CancellationToken ct)
    {
        var now = _clock.UtcNow;

        var changed = await _db.DiscordWelcomes
            .Where(w => w.DiscordUserId == userId && w.State == DiscordWelcomeStates.Waiting)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.State, DiscordWelcomeStates.Sent)
                .SetProperty(w => w.SentAt, (DateTimeOffset?)now), ct)
            .ConfigureAwait(false);

        if (changed > 0)
            return true;

        try
        {
            _db.DiscordWelcomes.Add(new DiscordWelcome
            {
                DiscordUserId = userId,
                State = DiscordWelcomeStates.Sent,
                CreatedAt = now,
                SentAt = now,
            });

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    /// <summary>Writes down that the member's welcome waits for the join gate. A member with a row already keeps it.</summary>
    private async Task WaitAsync(string userId, CancellationToken ct)
    {
        try
        {
            _db.DiscordWelcomes.Add(new DiscordWelcome
            {
                DiscordUserId = userId,
                State = DiscordWelcomeStates.Waiting,
                CreatedAt = _clock.UtcNow,
            });

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            _db.ChangeTracker.Clear();
        }
    }

    private async Task RecordAsync(string userId, string via, int steps, IReadOnlyList<string> errors, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

        var data = new JsonObject
        {
            ["via"] = via,
            ["steps"] = steps,
        };

        if (errors.Count > 0)
            data["error"] = string.Join(" ", errors);

        await _facts.WriteAsync(new FactRecord
            {
                Type = FactType.DiscordWelcomeSent,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Discord,
                SubjectId = userId,
                Source = FactSource.Modbot,
                Data = data,
            }, ct)
            .ConfigureAwait(false);
    }
}
