namespace Modbot.Companion.Dictation;

/// <summary>
/// Lets Dictate and Feedback share one microphone: whichever opened it holds it until it lets go,
/// and the other is told so instead of opening it as well.
/// </summary>
/// <remarks>
/// Each user gets its own recorder from <see cref="Wrap"/>. A recorder refuses to start while the
/// other holds the microphone, with a sentence the controller shows as it shows any microphone that
/// would not open. A recorder that does not hold the microphone cannot stop it, and the recording's
/// limit is told only to the one that holds it.
/// </remarks>
public sealed class MicrophoneTurns
{
    private readonly Lock _gate = new();
    private string? _holder;

    /// <summary>A recorder for <paramref name="name"/> that takes its turn on <paramref name="inner"/>.</summary>
    public IDictationRecorder Wrap(IDictationRecorder inner, string name)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new Turn(this, inner, name);
    }

    private sealed class Turn : IDictationRecorder
    {
        private readonly MicrophoneTurns _turns;
        private readonly IDictationRecorder _inner;
        private readonly string _name;

        public Turn(MicrophoneTurns turns, IDictationRecorder inner, string name)
        {
            _turns = turns;
            _inner = inner;
            _name = name;
            _inner.LimitReached += () =>
            {
                if (Holds())
                    LimitReached?.Invoke();
            };
        }

        public event Action? LimitReached;

        public float Level => _inner.Level;

        public string? Start()
        {
            lock (_turns._gate)
            {
                if (_turns._holder is { } holder && holder != _name)
                    return $"{holder} is listening.";

                var why = _inner.Start();
                if (why is null)
                    _turns._holder = _name;

                return why;
            }
        }

        public float[] Stop()
        {
            lock (_turns._gate)
            {
                if (_turns._holder != _name)
                    return [];

                _turns._holder = null;
                return _inner.Stop();
            }
        }

        private bool Holds()
        {
            lock (_turns._gate)
                return _turns._holder == _name;
        }
    }
}
