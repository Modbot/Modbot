using Modbot.Companion.Instances;

namespace Modbot.Companion.Routing;

/// <summary>Something that can be told about an observation: one paired Modbot server.</summary>
public interface IIngestTarget
{
    /// <summary>Which server this is, for the moderator's benefit. Not sent anywhere.</summary>
    string ServerId { get; }

    /// <summary>
    /// The one group this server declared it manages, handed over at pairing. It is held locally so
    /// the client never has to ask anybody "do you own this instance?" — asking is itself the leak
    /// that routing exists to prevent.
    /// </summary>
    string ManagedGroupId { get; }

    void Accept(ObservedPresence observation);
}

/// <summary>
/// Decides which servers, if any, are entitled to hear about an observation.
/// </summary>
/// <remarks>
/// <para><strong>This is a privacy boundary, not a dispatch optimisation.</strong> A moderator may
/// staff two unrelated communities from one PC. Neither community's operator may gain visibility
/// into the other's instances, and "we discard it on receipt" is not good enough — that requires
/// trusting the other operator. Events for one group are never <em>sent</em> to another group's
/// server. M3 5.5.1.</para>
/// <para><strong>It runs entirely on this machine.</strong> VRChat puts the owning group inside the
/// instance id, so the decision is local string comparison: no API call, no server round-trip,
/// nobody asked. Had ownership needed a lookup, the client would have had to ask <em>some</em>
/// server which group an instance belonged to, and asking the wrong one is exactly the leak.</para>
/// <para><strong>What it drops.</strong> Anything whose instance has no <c>~group(…)</c> qualifier
/// — which is every public, friends-only and private instance. A moderator's own VRChat use
/// outside their group's instances is excluded by the structure of the id and never transmitted
/// anywhere. And anything belonging to a group no paired server manages.</para>
/// <para>The failure mode here is silent: everything would appear to work while one community's
/// data quietly accumulated in another's database. Hence the tests.</para>
/// </remarks>
public sealed class EventRouter
{
    private readonly List<IIngestTarget> _targets = [];

    public EventRouter(IEnumerable<IIngestTarget>? targets = null)
    {
        if (targets is not null)
            _targets.AddRange(targets);
    }

    public IReadOnlyList<IIngestTarget> Targets => _targets;

    public void Add(IIngestTarget target) => _targets.Add(target);

    public void Remove(IIngestTarget target) => _targets.Remove(target);

    /// <summary>
    /// Hands one observation to every server that manages its owning group — usually one, sometimes
    /// none, and two only in the unusual case where two paired servers manage the same group.
    /// Returns how many were told, which is zero for everything Modbot has no business reporting.
    /// </summary>
    public int Dispatch(ObservedPresence observation)
    {
        // Not a group instance: the moderator's own private, friends-only or public VRChat use.
        // Dropped here, before anything is built, so there is no object holding it to leak.
        if (observation.Instance.GroupId is not { } groupId)
            return 0;

        var told = 0;
        foreach (var target in _targets)
        {
            // Ordinal comparison on an opaque id. No normalisation, no case folding: ids are
            // compared exactly as VRChat wrote them. Foundation 3.1.1.
            if (!string.Equals(target.ManagedGroupId, groupId, StringComparison.Ordinal))
                continue;

            target.Accept(observation);
            told++;
        }

        return told;
    }

    /// <summary>Dispatches a whole poll's worth and reports how many observations went nowhere.</summary>
    public int DispatchAll(IEnumerable<ObservedPresence> observations)
    {
        var dropped = 0;
        foreach (var observation in observations)
        {
            if (Dispatch(observation) == 0)
                dropped++;
        }

        return dropped;
    }
}
