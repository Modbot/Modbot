namespace Modbot.Companion.Presentation;

/// <summary>The three sections of the Cloud Server page that fold.</summary>
public enum CloudFold
{
    GroupInstances,
    NonGroupInstances,
    AlwaysSent,
}

/// <summary>
/// Which sections of the Cloud Server page are open, and what switching Modbot Cloud off and on does
/// to them.
/// </summary>
/// <remarks>
/// <para>Every section folds and unfolds by clicking its header, whether Modbot Cloud is on or off, so
/// somebody can always read what each one holds. Switching Modbot Cloud off folds all of them; switching
/// it on again opens the ones that were open before. This is only what the window is showing: nothing
/// here is saved or sent.</para>
/// <para>A client that starts with Modbot Cloud off starts with every section folded, and opens them
/// all when it is switched on.</para>
/// </remarks>
public sealed class CloudFolds
{
    private static readonly CloudFold[] Sections = [.. Enum.GetValues<CloudFold>()];

    private readonly Dictionary<CloudFold, bool> _open = [];
    private Dictionary<CloudFold, bool>? _before;
    private bool _on;

    public CloudFolds(bool cloudOn)
    {
        _on = cloudOn;

        foreach (var section in Sections)
            _open[section] = cloudOn;

        if (!cloudOn)
            _before = Sections.ToDictionary(section => section, _ => true);
    }

    public bool IsOpen(CloudFold section) => _open[section];

    /// <summary>A click on a section's header.</summary>
    public void Toggle(CloudFold section) => _open[section] = !_open[section];

    /// <summary>
    /// Modbot Cloud was switched on or off. Off folds every section and remembers which were open; on
    /// brings those back. Told the state it already has, nothing changes.
    /// </summary>
    public void CloudChanged(bool cloudOn)
    {
        if (cloudOn == _on)
            return;

        _on = cloudOn;

        if (!cloudOn)
        {
            _before = new Dictionary<CloudFold, bool>(_open);
            foreach (var section in Sections)
                _open[section] = false;

            return;
        }

        if (_before is not null)
        {
            foreach (var section in Sections)
                _open[section] = _before[section];

            _before = null;
        }
    }
}
