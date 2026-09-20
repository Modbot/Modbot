using Modbot.Companion.Instances;
using Modbot.Companion.LogReading;
using Modbot.Core.Time;

namespace Modbot.Companion.Pipeline;

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
    /// <summary>
    /// How long the log may go completely silent before the client stops believing it knows where
    /// the moderator is standing.
    /// </summary>
    /// <remarks>
    /// <para><strong>The ordinary way a session ends is that the log simply stops.</strong> Users
    /// close VRChat, VRChat crashes, machines sleep. The observed sample ends with no
    /// <c>OnLeftRoom</c>, no <c>OnPlayerLeft</c> and no disconnect line of any kind — just two
    /// teardown lines and then nothing. A client that waited for a clean marker would go on
    /// believing the moderator was in that instance forever, and the overlay would keep fetching
    /// and showing a roster for an instance nobody is in.</para>
    /// <para><strong>Why a whole-file signal rather than a <c>[Behaviour]</c> one.</strong>
    /// <c>[Behaviour]</c> silence proves nothing: in the same sample there is a forty-five minute
    /// stretch with no <c>[Behaviour]</c> line at all while the moderator was demonstrably still
    /// present. The <em>file</em>, on the other hand, never goes quiet — VRChat writes an
    /// <c>[IK Debug Log]</c> frame-rate line roughly every ten seconds while it runs, and across
    /// the whole post-startup session the largest gap between consecutive lines of any tag is
    /// eleven seconds. So "the file has stopped growing" is a reliable ten-second-granularity
    /// liveness signal for VRChat itself, and it is what this measures.</para>
    /// <para>Two minutes is roughly twelve of those heartbeats: long enough to ride out a hitch, a
    /// long asset load or a suspended VM, short enough that the overlay stops talking about an instance
    /// the moderator walked out of. Erring long is the safer direction — the cost is a stale
    /// roster which says it is stale, not a wrong one.</para>
    /// </remarks>
    public static readonly TimeSpan InstanceStaleAfter = TimeSpan.FromMinutes(2);

    private readonly VRChatLogTail _tail;
    private readonly InstanceSessionTracker _tracker;
    private readonly IModbotClock _clock;

    private long _behaviourLines;
    private long _recognisedEvents;
    private DateTimeOffset? _lastLineAt;
    private DateTimeOffset? _lastRecognisedAt;

    /// <summary>
    /// When a line last arrived in VRChat's own shape, and when a <c>[Behaviour]</c> line last did.
    /// Between them they are how <see cref="LogHealth.Evaluate"/> tells a quiet instance from a
    /// parser that has stopped matching; the counters beside them cannot, because they say how many
    /// and not when.
    /// </summary>
    private DateTimeOffset? _lastTimestampedLineAt;
    private DateTimeOffset? _lastBehaviourLineAt;

    /// <summary>VRChat's own timestamp on the last line read, of any tag. What a stop is dated at.</summary>
    private DateTime? _lastLineWritten;

    /// <summary>
    /// Whether a line written <em>after</em> startup has been read from the current file. Replayed
    /// history is how the client learns where the moderator is, but a log that was already dead
    /// when Modbot started is not a log that stopped while anyone was watching.
    /// </summary>
    private bool _sawLiveLine;

    /// <summary>
    /// Whether this stop has already been reported. A stopped log is reported once, not once per
    /// poll: this is not a heartbeat, and a client that repeated it would be one.
    /// </summary>
    private bool _stopReported;

    public PresenceObserver(VRChatLogTail tail, IModbotClock clock, InstanceSessionTracker? tracker = null)
    {
        _tail = tail;
        _clock = clock;
        _tracker = tracker ?? new InstanceSessionTracker();
    }

    /// <summary>
    /// Whether VRChat is demonstrably still running and writing.
    /// </summary>
    /// <remarks>
    /// Measured from when a line was <em>read</em> rather than from the timestamp inside it,
    /// because the timestamps in the file are a wall clock with no offset and this question is
    /// about the here and now.
    /// </remarks>
    public bool LogIsLive => _lastLineAt is { } last && _clock.UtcNow - last <= InstanceStaleAfter;

    /// <summary>
    /// The instance the moderator is standing in, or <c>null</c> when that is not known.
    /// </summary>
    /// <remarks>
    /// Null covers four different situations that all mean the same thing to a caller: VRChat is
    /// not running, VRChat stopped writing and is presumed gone (see
    /// <see cref="InstanceStaleAfter"/>), the moderator has left an instance and not yet entered
    /// another, or the log named a location this client could not read. The overlay treats every
    /// one of them as "show the idle screen and contact nobody", which is the right answer to all
    /// four.
    /// </remarks>
    public InstanceLocation? CurrentInstance => LogIsLive ? _tracker.CurrentInstance : null;

    /// <summary>
    /// The readable name of the world the moderator is in, or null when it is not known. Used to
    /// name a saved clip and for nothing else; see <see cref="InstanceSessionTracker.WorldName"/>.
    /// </summary>
    public string? CurrentWorldName => LogIsLive ? _tracker.WorldName : null;

    /// <summary>
    /// The moderator's own VRChat id, once the log has said which of the people in it is them, or
    /// null until then. The voice uses it to keep quiet about the moderator's own comings and goings.
    /// </summary>
    public string? ModeratorId => _tracker.LocalUserId;

    /// <summary>Who is in it. Used by the overlay, which renders from local state only.</summary>
    public IReadOnlyCollection<string> Roster => _tracker.Roster;

    public LogHealth Health => new(
        _tail.LinesRead,
        _behaviourLines,
        _recognisedEvents,
        _lastLineAt,
        _lastRecognisedAt,
        _lastTimestampedLineAt,
        _lastBehaviourLineAt);

    /// <summary>
    /// Reads whatever VRChat has written since the last call and returns the facts it completes.
    /// Safe to call as often as you like; it does no work when the file has not grown.
    /// </summary>
    public IReadOnlyList<ObservedPresence> Poll()
    {
        var observations = new List<ObservedPresence>();

        var previousFile = _tail.CurrentFile;
        var pending = _tail.ReadPending();

        // VRChat opens a new log on every launch, so the file changing under us is the one signal
        // that says plainly "the previous session is over". Everything learned from it is dropped
        // rather than carried into the new one -- otherwise the client would spend the minute
        // between VRChat launching and the first world load reporting the moderator as still being
        // wherever they were last night.
        if (previousFile is not null
            && !string.Equals(previousFile, _tail.CurrentFile, StringComparison.OrdinalIgnoreCase))
        {
            _tracker.ForgetSession();

            // A new file is a new session. Whatever the old one's stop was, it is not owed a resume:
            // the people it listed are not in this session's roster, and the new session reports
            // its own arrival burst.
            _sawLiveLine = false;
            _stopReported = false;
        }

        foreach (var line in pending)
        {
            _lastLineAt = _clock.UtcNow;

            if (!VRChatLogLineParser.TryParse(line.Text, out var parsed))
                continue;

            _lastLineWritten = parsed.Timestamp;
            _lastTimestampedLineAt = _clock.UtcNow;

            if (!line.IsReplay)
            {
                _sawLiveLine = true;

                // The log had stopped and this client said so; now it is growing again -- a slept
                // laptop woke, a paused VM resumed. The server ended the watch at the stop, so the
                // instance is restated once, dated at this line. Before this line's own event is
                // applied, so that a leave on this very line still takes effect afterwards.
                if (_stopReported)
                {
                    _stopReported = false;
                    observations.AddRange(_tracker.SeenAgain(parsed.Timestamp));
                }
            }

            // The tag filter. Every other tag in the file -- tracking, asset downloads, OSC, Steam,
            // VRChat's own HTTP -- is skipped here, unread. That is roughly 96% of the file.
            if (!string.Equals(parsed.Tag, "Behaviour", StringComparison.Ordinal))
                continue;

            _behaviourLines++;
            _lastBehaviourLineAt = _clock.UtcNow;

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

        if (StoppedJustNow() is { } stopped)
            observations.Add(stopped);

        return observations;
    }

    /// <summary>
    /// The one report that the log has stopped, or null when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// <para><strong>Once per stop.</strong> The client says the log stopped and then says nothing
    /// until it starts again. It never sends "still here" while the log is growing: a moderator's
    /// whereabouts are a by-product of what they observe, not something reported on a timer
    /// (see <c>DeviceLocations</c> on the server, which records why that was rejected).</para>
    /// <para><strong>Only a stop worth reporting.</strong> The log must have grown while this client
    /// was running, the moderator must be settled in an instance, and their own id must be known --
    /// otherwise there is no watch on the server for this to end, and no subject to name.</para>
    /// <para>Dated at VRChat's own timestamp on the last line, which is the last moment anything
    /// was actually seen, rather than two minutes later when the silence was noticed.</para>
    /// </remarks>
    private ObservedPresence? StoppedJustNow()
    {
        if (_stopReported || !_sawLiveLine || LogIsLive)
            return null;

        if (_lastLineWritten is not { } lastWritten
            || _tracker.CurrentInstance is not { } instance
            || _tracker.LocalUserId is not { } moderator)
        {
            return null;
        }

        _stopReported = true;
        return new ObservedPresence(PresenceKind.LogStopped, lastWritten, moderator, _tracker.LocalDisplayName, instance);
    }
}
