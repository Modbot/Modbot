using Modbot.Core.Time;

namespace Modbot.Companion.Overlay;

/// <summary>
/// The pop-up that asks, in the headset or over the game, whether to install a newer version that
/// has finished downloading, so nobody has to leave VRChat to do it (update checking design §10).
/// </summary>
/// <remarks>
/// <para><strong>Once per version.</strong> A version is put to the moderator once. Later, or an
/// install that could not start, does not bring it back; a newer version downloaded after that is
/// a new question. Modbot starting again forgets, and a version that is still waiting is asked
/// about again then, which is also when it would install anyway.</para>
/// <para><strong>Update asks twice, like the sidebar's button.</strong> The first press turns it
/// into <b>Restart now</b> for five seconds; the second does the restart that installs the update,
/// through the same function the sidebar's button calls, so there is one way to restart into an
/// update. Later takes the card away, and the update installs the next time Modbot starts, as it
/// always has.</para>
/// <para>Nothing is sent and nothing is read: the card is made from the version the updater already
/// holds.</para>
/// </remarks>
public sealed class UpdatePrompt
{
    /// <summary>The card's id: there is only ever one.</summary>
    public const string CardId = "update";

    public const string UpdateWords = "Update";
    public const string LaterWords = "Later";
    public const string RestartNowWords = "Restart now";

    /// <summary>How long Restart now waits for the second press before it asks again.</summary>
    public static readonly TimeSpan ArmedFor = TimeSpan.FromSeconds(5);

    private readonly PopUps _popUps;
    private readonly IModbotClock _clock;
    private readonly Func<Task<bool>> _restart;

    private string? _asked;
    private DateTimeOffset? _armedAt;
    private bool _restarting;

    /// <param name="restart">
    /// Starts a fresh copy and stops this one, true once it has; the Settings page's and the
    /// sidebar's own restart.
    /// </param>
    public UpdatePrompt(PopUps popUps, IModbotClock clock, Func<Task<bool>> restart)
    {
        ArgumentNullException.ThrowIfNull(popUps);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(restart);

        _popUps = popUps;
        _clock = clock;
        _restart = restart;
    }

    /// <summary>
    /// Called often with the version that has finished downloading, or null: puts the card up for
    /// a version it has not asked about, and takes Restart now back to Update when five seconds go.
    /// </summary>
    public void Tick(string? ready)
    {
        if (ready is { Length: > 0 } version && !string.Equals(version, _asked, StringComparison.Ordinal))
        {
            _asked = version;
            _armedAt = null;
            Put(null);
            return;
        }

        if (_armedAt is { } armed && Math.Abs((_clock.UtcNow - armed).Ticks) >= ArmedFor.Ticks)
        {
            _armedAt = null;
            Put(null);
        }
    }

    /// <summary>One of the card's buttons was pressed.</summary>
    /// <param name="yes">True for Update (or Restart now), false for Later.</param>
    public async Task AnsweredAsync(string id, bool yes)
    {
        if (!string.Equals(id, CardId, StringComparison.Ordinal) || _asked is null || _restarting)
            return;

        if (!yes)
        {
            _armedAt = null;
            _popUps.Clear(CardId);
            return;
        }

        if (_armedAt is null)
        {
            _armedAt = _clock.UtcNow;
            Put(null);
            return;
        }

        _armedAt = null;
        _restarting = true;
        try
        {
            if (await _restart())
                return;
        }
        finally
        {
            _restarting = false;
        }

        // Nothing was stopped, so the card stays, and says why the restart did not happen.
        Put("Could not start another copy of Modbot.");
    }

    private void Put(string? problem)
        => _popUps.Show(new PopUp(
            CardId,
            "Modbot",
            $"Update {_asked} is ready",
            problem,
            problem is null ? PopUpTone.Plain : PopUpTone.Problem,
            Ask: new PopUpAsk(_armedAt is null ? UpdateWords : RestartNowWords, LaterWords)));
}
