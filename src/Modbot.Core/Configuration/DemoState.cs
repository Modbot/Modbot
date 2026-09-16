namespace Modbot.Core.Configuration;

/// <summary>
/// What the demo is doing right now, and the way to ask it to start over.
/// </summary>
/// <remarks>
/// A singleton shared by the seeder, the reset schedule and the API. It holds no data of its own —
/// only the progress line the Health page shows while a seed or a reset is running, and the flag
/// that asks for the next one.
/// </remarks>
public sealed class DemoState
{
    private readonly Lock _gate = new();

    private string _step = string.Empty;
    private int _done;
    private int _total;
    private DateTimeOffset? _seededAt;
    private DateTimeOffset? _resetAskedAt;
    private string? _resetAskedBy;

    /// <summary>What is being filled in, in plain words. Empty when nothing is running.</summary>
    public string Step { get { lock (_gate) return _step; } }

    /// <summary>How much of the current step is done, and how much there is.</summary>
    public (int Done, int Total) Progress { get { lock (_gate) return (_done, _total); } }

    /// <summary>True while a seed or a reset is running.</summary>
    public bool Busy { get { lock (_gate) return _step.Length > 0; } }

    /// <summary>When the data finished being filled in, or null while it never has.</summary>
    public DateTimeOffset? SeededAt { get { lock (_gate) return _seededAt; } }

    /// <summary>Whether somebody has asked for a reset that has not started yet.</summary>
    public bool ResetAsked { get { lock (_gate) return _resetAskedAt is not null; } }

    /// <summary>When the demo puts itself back next, or null when nothing is scheduled.</summary>
    public DateTimeOffset? NextResetAt { get; set; }

    public void Begin(string step, int total = 0)
    {
        lock (_gate)
        {
            _step = step;
            _done = 0;
            _total = total;
        }
    }

    public void Advance(int done)
    {
        lock (_gate) _done = done;
    }

    /// <summary>Nothing is running, and this is when the data was last complete.</summary>
    public void Finish(DateTimeOffset seededAt)
    {
        lock (_gate)
        {
            _step = string.Empty;
            _done = 0;
            _total = 0;
            _seededAt = seededAt;
        }
    }

    /// <summary>Asks for a wipe and re-seed. The demo service picks it up within a few seconds.</summary>
    public void AskForReset(DateTimeOffset at, string by)
    {
        lock (_gate)
        {
            _resetAskedAt = at;
            _resetAskedBy = by;
        }
    }

    /// <summary>Takes the outstanding request, if there is one, and clears it.</summary>
    public string? TakeResetRequest()
    {
        lock (_gate)
        {
            if (_resetAskedAt is null)
                return null;

            var by = _resetAskedBy ?? DemoMode.AdministratorUsername;
            _resetAskedAt = null;
            _resetAskedBy = null;
            return by;
        }
    }
}
