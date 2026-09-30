using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.VRChat.RateLimiting;
using Npgsql;
using Serilog;

namespace Modbot.VRChat.Sync;

/// <summary>What one name read is for: another group's instance, or another group.</summary>
public enum OtherNameKind
{
    /// <summary>An instance's own page, for the name it was opened with. The id is its location.</summary>
    Instance,

    /// <summary>A group's page, for its name. The id is the group's id.</summary>
    Group,
}

/// <summary>One name to ask VRChat for.</summary>
/// <param name="Id">The instance's location (world id, <c>:</c>, instance id as listed), or the group's id.</param>
public readonly record struct OtherNameRequest(OtherNameKind Kind, string Id);

/// <summary>How one name read ended.</summary>
public enum OtherNameOutcome
{
    /// <summary>VRChat answered, with a name, without one, or with a refusal, and the answer is kept.</summary>
    Saved,

    /// <summary>A kept answer was already there. Nothing was sent.</summary>
    AlreadyKnown,

    /// <summary>Not asked: the location says outsiders cannot join, or has no instance to ask about.</summary>
    NotAsked,

    /// <summary>The bucket is cold-stopped or a sign-in is being waited out. Nothing was kept.</summary>
    Paused,

    /// <summary>VRChat was not reached, or its answer was not about the instance or group. Nothing was kept.</summary>
    NoAnswer,
}

/// <summary>
/// The names the World tab is waiting for: other groups' instances and other groups, asked for when
/// a popup lists them, one at a time.
/// </summary>
/// <remarks>
/// <para>
/// <strong>In memory, on purpose.</strong> What is kept is the answer, in the database
/// (<see cref="OtherInstanceName"/>, <see cref="OtherGroupName"/>); the queue only holds what a
/// popup asked for in the last few seconds. Lost on a restart, it is filled again by the next popup
/// that lists the same instances, which is the only time the names are wanted.
/// </para>
/// <para>
/// An id is waiting at most once, however many popups ask. <see cref="MostWaiting"/> keeps a
/// deployment whose gate is stopped from growing the queue without end.
/// </para>
/// </remarks>
public sealed class OtherNameQueue
{
    /// <summary>The most names waiting at once. A popup asks for at most eight instances and their groups.</summary>
    public const int MostWaiting = 200;

    private readonly Lock _lock = new();
    private readonly HashSet<OtherNameRequest> _waiting = [];
    private readonly HashSet<OtherNameRequest> _reading = [];

    // One line per kind, so group reads spaced a minute apart never hold up an instance's name.
    private readonly Channel<OtherNameRequest> _instances = Channel.CreateUnbounded<OtherNameRequest>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly Channel<OtherNameRequest> _groups = Channel.CreateUnbounded<OtherNameRequest>(
        new UnboundedChannelOptions { SingleReader = true });

    private Channel<OtherNameRequest> Line(OtherNameKind kind) =>
        kind == OtherNameKind.Group ? _groups : _instances;

    /// <summary>How many names are waiting, not counting the one being read.</summary>
    public int Count
    {
        get { lock (_lock) return _waiting.Count; }
    }

    /// <summary>
    /// Adds a name to ask for. True when it is waiting or being read after the call, false when the
    /// queue is full.
    /// </summary>
    public bool Offer(OtherNameRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Id);

        lock (_lock)
        {
            if (_waiting.Contains(request) || _reading.Contains(request))
                return true;

            if (_waiting.Count >= MostWaiting)
                return false;

            _waiting.Add(request);
        }

        // Unbounded, so this never fails while the queue is open.
        Line(request.Kind).Writer.TryWrite(request);
        return true;
    }

    /// <summary>Whether a name is waiting to be asked for or being asked for now.</summary>
    public bool IsWaiting(OtherNameRequest request)
    {
        lock (_lock)
            return _waiting.Contains(request) || _reading.Contains(request);
    }

    /// <summary>
    /// Forgets every waiting name of one kind, without asking for any: the bucket they would be
    /// read from is stopped. The next popup that lists them offers them again.
    /// </summary>
    /// <returns>How many were forgotten.</returns>
    public int DropWaiting(OtherNameKind kind)
    {
        lock (_lock)
            return _waiting.RemoveWhere(r => r.Kind == kind);
    }

    /// <summary>
    /// The next name of this kind to ask for. Waits until there is one. An entry forgotten by
    /// <see cref="DropWaiting"/> is skipped.
    /// </summary>
    public async Task<OtherNameRequest> NextAsync(OtherNameKind kind, CancellationToken ct)
    {
        var line = Line(kind);

        while (true)
        {
            var request = await line.Reader.ReadAsync(ct).ConfigureAwait(false);

            lock (_lock)
            {
                if (_waiting.Remove(request))
                {
                    _reading.Add(request);
                    return request;
                }
            }
        }
    }

    /// <summary>Marks a name taken by <see cref="NextAsync"/> as no longer being read.</summary>
    public void Done(OtherNameRequest request)
    {
        lock (_lock)
            _reading.Remove(request);
    }
}

/// <summary>
/// Asks VRChat for one other instance's name or one other group's name, and keeps the answer.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Asked once, ever.</strong> A kept answer is looked for before anything is sent, and an
/// answer is kept whatever it says: a name, no name, or a refusal (400, 403, 404). Anything else --
/// a cold stop, a wait to sign in, VRChat not reached or answering 5xx or 408, a session VRChat did
/// not accept, Cloudflare's block -- is not kept, because none of those is VRChat saying anything
/// lasting about the instance or the group; a later popup asks again.
/// </para>
/// <para>
/// <strong>Never asked when the location says outsiders cannot join.</strong>
/// <see cref="InstanceLocationParts.ClosedToOutsiders"/>. The popup already leaves those out; this
/// checks again so no other caller can send one.
/// </para>
/// <para>
/// <strong>Budgets.</strong> An instance's page is <c>instances.read</c>, the bucket the head count
/// reads use (one a second, measured); a group's page is <c>groups.read</c>, the bucket the group's own
/// info poll uses (spec 4.2), scoped to the group asked about. No new endpoint, so spec 4.3.4's
/// question was answered when those two were first used. Group reads are spaced out by
/// <see cref="OtherNameService"/>, not here. A 429 is never retried (spec 4.3.1): the read ends,
/// nothing is kept, and a later popup asks again once the stop has lifted.
/// </para>
/// <para>
/// <strong>What a 200 means here.</strong> <c>GET /instances/{location}</c> answers 200 even for an
/// instance that never existed, with <c>active: false</c> (research: vrchat-instance-findings.md
/// section 1). That is why the head count read does not believe such a body. The name is a different
/// question: the world's own list vouched for this instance, and an instance VRChat made up has no
/// <c>displayName</c>, so a body's name is taken whether or not it says <c>active</c>.
/// </para>
/// </remarks>
public sealed class OtherNameReader
{
    private readonly IVRChatGate _gate;
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    public OtherNameReader(IVRChatGate gate, ModbotContext db, IModbotClock clock, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _gate = gate;
        _db = db;
        _clock = clock;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    public Task<OtherNameOutcome> ReadAsync(OtherNameRequest request, CancellationToken ct = default) =>
        request.Kind == OtherNameKind.Group ? GroupAsync(request.Id, ct) : InstanceAsync(request.Id, ct);

    private async Task<OtherNameOutcome> InstanceAsync(string location, CancellationToken ct)
    {
        if (await _db.OtherInstanceNames.AsNoTracking().AnyAsync(n => n.Location == location, ct).ConfigureAwait(false))
            return OtherNameOutcome.AlreadyKnown;

        var parts = InstanceLocationParts.Split(location);
        var colon = location.IndexOf(':');

        if (parts.ClosedToOutsiders || parts.InstanceId is null || colon <= 0)
            return OtherNameOutcome.NotAsked;

        // Passed exactly as the list carried it, qualifiers and all, and never checked for shape
        // (spec 3.1.1) -- the same split the head count read makes.
        var worldId = location[..colon];
        var instanceId = location[(colon + 1)..];

        var result = await _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.InstancesRead, location, "GetInstance (another instance's name)"),
            (client, token) => client.Instances.GetInstanceWithHttpInfoAsync(worldId, instanceId, token),
            VRChatCallPriority.Background,
            ct).ConfigureAwait(false);

        if (Stopped(result))
            return OtherNameOutcome.Paused;

        if (!Answered(result))
            return OtherNameOutcome.NoAnswer;

        var name = result.Success ? VRChatInstance.NameFrom(result.Value?.DisplayName, parts.InstanceId) : null;

        _db.OtherInstanceNames.Add(new OtherInstanceName
        {
            Location = location,
            Name = name,
            AskedAt = _clock.UtcNow,
            Refused = !result.Success,
        });

        return await SaveAsync(OtherNameKind.Instance, location, result.StatusCode, name, ct).ConfigureAwait(false);
    }

    private async Task<OtherNameOutcome> GroupAsync(string groupId, CancellationToken ct)
    {
        if (await _db.OtherGroupNames.AsNoTracking().AnyAsync(g => g.GroupId == groupId, ct).ConfigureAwait(false))
            return OtherNameOutcome.AlreadyKnown;

        var result = await _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.GroupsRead, groupId, "GetGroup (another group's name)"),
            (client, token) => client.Groups.GetGroupWithHttpInfoAsync(groupId, cancellationToken: token),
            VRChatCallPriority.Background,
            ct).ConfigureAwait(false);

        if (Stopped(result))
            return OtherNameOutcome.Paused;

        if (!Answered(result))
            return OtherNameOutcome.NoAnswer;

        var name = result.Success && !string.IsNullOrWhiteSpace(result.Value?.Name) ? result.Value.Name.Trim() : null;

        _db.OtherGroupNames.Add(new OtherGroupName
        {
            GroupId = groupId,
            Name = name,
            AskedAt = _clock.UtcNow,
            Refused = !result.Success,
        });

        return await SaveAsync(OtherNameKind.Group, groupId, result.StatusCode, name, ct).ConfigureAwait(false);
    }

    private async Task<OtherNameOutcome> SaveAsync(
        OtherNameKind kind, string id, int status, string? name, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Somebody else kept an answer between the look and the write -- a second Modbot on the
            // same database. Theirs stands; nothing is asked again either way.
            _db.ChangeTracker.Clear();
            return OtherNameOutcome.AlreadyKnown;
        }

        _log.Information(
            "Asked VRChat for {Kind} {Id}'s name: {Status}, {Answer}",
            kind, id, status, name is null ? "no name" : "named");

        return OtherNameOutcome.Saved;
    }

    /// <summary>A cold stop, a 429, or a wait to sign in. Nothing was learned, and nothing is retried.</summary>
    private static bool Stopped<T>(VRChatResult<T> result) =>
        result.IsRateLimited || result.Kind is VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting;

    /// <summary>
    /// Whether VRChat gave an answer that asking again would only repeat: any 2xx, or a plain "no"
    /// about this instance or group -- 400 (it will not take this id), 403 (not for this account),
    /// 404 (no such thing).
    /// </summary>
    /// <remarks>
    /// Nothing else is kept. A 5xx or a 408 is VRChat having trouble, not an answer, and keeping it
    /// as a refusal would leave the name a number for good after one bad minute. A 401 is about the
    /// session, and Cloudflare's block is not VRChat at all.
    /// </remarks>
    private static bool Answered<T>(VRChatResult<T> result) =>
        result.Success
        || (!result.IsWafBlocked && result.StatusCode is 400 or 403 or 404);
}

/// <summary>
/// Asks for the names the World tab offered, as they arrive: instances one after another, groups one
/// a minute at most.
/// </summary>
/// <remarks>
/// <para>
/// No timer: it waits on <see cref="OtherNameQueue"/> and does nothing until a popup offers a name.
/// When a bucket is stopped, every name waiting on it is let go rather than sent to a gate that
/// would refuse each in turn.
/// </para>
/// <para>
/// <strong>Why groups wait a minute.</strong> <c>groups.read</c> is one request per five seconds for
/// the whole class, and its calls go through the "group" lane with the managed group's member, ban,
/// audit log and instance list polls and the moderators' kicks and bans. A call keeps the lane while
/// it waits for its token, so eight group reads back to back would take the class's tokens for forty
/// seconds and make those polls wait behind them. One a minute leaves the class bucket full nearly
/// every time a read comes, so a read waits for no token and keeps the lane only for the request
/// itself. The one case that still waits is a read landing within five seconds of the group's own
/// info poll, which runs every five minutes: at most five seconds, at most that often. The gate's
/// priorities are not touched; <see cref="VRChatCallPriority.Background"/> is already its lowest.
/// </para>
/// <para>
/// Instances have their own line so a group waiting out its minute never holds up an instance's
/// name. They are one a second at most (<c>instances.read</c>), and the head count reads that share
/// that bucket are thirty seconds apart each.
/// </para>
/// </remarks>
public sealed class OtherNameService : BackgroundService
{
    /// <summary>The least time between two group reads that reached VRChat.</summary>
    public static readonly TimeSpan GroupReadsEvery = TimeSpan.FromMinutes(1);

    private readonly OtherNameQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly IDelayScheduler _delays;
    private readonly ILogger _log;

    public OtherNameService(
        OtherNameQueue queue,
        IServiceScopeFactory scopes,
        IDelayScheduler? delays = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(scopes);

        _queue = queue;
        _scopes = scopes;
        _delays = delays ?? new RealDelayScheduler();
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(
            RunAsync(OtherNameKind.Instance, TimeSpan.Zero, stoppingToken),
            RunAsync(OtherNameKind.Group, GroupReadsEvery, stoppingToken));

    private async Task RunAsync(OtherNameKind kind, TimeSpan spacing, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            OtherNameRequest request;

            try
            {
                request = await _queue.NextAsync(kind, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var sent = false;

            try
            {
                using var scope = _scopes.CreateScope();
                var reader = scope.ServiceProvider.GetRequiredService<OtherNameReader>();
                var outcome = await reader.ReadAsync(request, stoppingToken).ConfigureAwait(false);

                // Kept, or not kept for want of an answer: either way it went to the gate.
                sent = outcome is OtherNameOutcome.Saved or OtherNameOutcome.NoAnswer or OtherNameOutcome.Paused;

                if (outcome == OtherNameOutcome.Paused)
                {
                    var dropped = _queue.DropWaiting(kind);
                    _log.Information(
                        "Asking VRChat for other {Kind} names is paused; {Dropped} more let go until a popup asks again",
                        kind, dropped);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One bad read must not end the service; the name is asked for again by the next
                // popup that lists it.
                sent = true;
                _log.Warning(ex, "Asking VRChat for {Kind} {Id}'s name failed", kind, request.Id);
            }
            finally
            {
                _queue.Done(request);
            }

            if (!sent || spacing <= TimeSpan.Zero)
                continue;

            try
            {
                await _delays.DelayAsync(spacing, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
