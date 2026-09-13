using Modbot.Client.Instances;
using Modbot.Client.LogReading;
using Modbot.Core.Time;

namespace Modbot.Client.Pipeline;

/// <summary>
/// The reading half of the client: log file in, presence facts out.
/// </summary>
/// <remarks>
/// <para><strong>The whole of what this observes.</strong> It pulls new lines from VRChat's own log
/// (<see cref="VRChatLogTail"/>), keeps the <c>[Behaviour]</c> ones, recognises a short list of
/// shapes among those (<see cref="BehaviourEventParser"/>), and works out which of the recognised
/// arrivals and departures actually happened (<see cref="InstanceSessionTracker"/>).</para>
/// <para><strong>Nothing has been sent anywhere at this point.</strong> What comes out of here is
/// still on the moderator's machine, still unfiltered by group, and still in the machine's own
/// local time. Routing decides who is entitled to hear about any of it, and drops everything that
/// is not a group instance.</para>
/// <para><strong>Lines already in the file when Modbot started are read but not reported.</strong>
/// They are needed — they are how the client works out which instance the moderator is sitting in
/// right now — but reporting them would re-submit hours of old observations on every restart.</para>
/// </remarks>
public sealed class PresenceObserver
{
    private readonly VRChatLogTail _tail;
    private readonly InstanceSessionTracker _tracker;
    private readonly IModbotClock _clock;

    private long _behaviourLines;
    private long _recognisedEvents;
    private DateTimeOffset? _lastLineAt;
    private DateTimeOffset? _lastRecognisedAt;

    public PresenceObserver(VRChatLogTail tail, IModbotClock clock, InstanceSessionTracker? tracker = null)
    {
        _tail = tail;
        _clock = clock;
        _tracker = tracker ?? new InstanceSessionTracker();
    }

    /// <summary>The instance the moderator is in, as far as the log has said.</summary>
    public InstanceLocation? CurrentInstance => _tracker.CurrentInstance;

    /// <summary>Who is in it. Used by the overlay, which renders from local state only.</summary>
    public IReadOnlyCollection<string> Roster => _tracker.Roster;

    public LogHealth Health => new(
        _tail.LinesRead,
        _behaviourLines,
        _recognisedEvents,
        _lastLineAt,
        _lastRecognisedAt);

    /// <summary>
    /// Reads whatever VRChat has written since the last call and returns the facts it completes.
    /// Safe to call as often as you like; it does no work when the file has not grown.
    /// </summary>
    public IReadOnlyList<ObservedPresence> Poll()
    {
        var observations = new List<ObservedPresence>();

        foreach (var line in _tail.ReadPending())
        {
            _lastLineAt = _clock.UtcNow;

            if (!VRChatLogLineParser.TryParse(line.Text, out var parsed))
                continue;

            // The tag filter. Every other tag in the file -- tracking, asset downloads, OSC, Steam,
            // VRChat's own HTTP -- is skipped here, unread. That is roughly 96% of the file.
            if (!string.Equals(parsed.Tag, "Behaviour", StringComparison.Ordinal))
                continue;

            _behaviourLines++;

            if (BehaviourEventParser.Parse(parsed) is not { } logEvent)
                continue;

            _recognisedEvents++;
            _lastRecognisedAt = _clock.UtcNow;

            foreach (var observation in _tracker.Observe(logEvent))
            {
                // Replayed history rebuilds the tracker's state and is then discarded. The client
                // reports what it sees happen, not what it finds already written down.
                if (!line.IsReplay)
                    observations.Add(observation);
            }
        }

        return observations;
    }
}
