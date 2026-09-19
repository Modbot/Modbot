using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Modbot.Companion.Presentation;
using Modbot.Companion.Sounds;

namespace Modbot.Companion.App;

/// <summary>
/// The Settings page's <strong>Tell me about</strong> card: one row per kind of event, one tick
/// per way of being told.
/// </summary>
/// <remarks>
/// <para>The three ways keep their own on switches and volumes elsewhere — the Notifications card
/// above for the sound, the Voice card below for the voice, the SteamVR page for the notification
/// overlay. This card only says which kinds each one is allowed to raise, and a way that is off
/// tells nobody anything whatever its column says (notification filters design 2026-09-19).</para>
/// <para>Built once, like the cards either side of it: a box rebuilt every second under the
/// window's timer can lose the click on it.</para>
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>One box per kind and way, by the pair it belongs to.</summary>
    private readonly Dictionary<(NotificationWay Way, NotificationKind Kind), CheckBox> _filterBoxes = [];

    /// <summary>Wires the filter card's boxes. Called once, from the constructor.</summary>
    private void SetUpNotificationFilters()
    {
        foreach (var way in NotificationFilters.Ways)
        {
            foreach (var kind in NotificationFilters.Kinds)
            {
                var box = new CheckBox { HorizontalAlignment = HorizontalAlignment.Center };
                box.IsCheckedChanged += (_, _) => NotificationFiltersChanged(way, kind, box.IsChecked == true);
                _filterBoxes[(way, kind)] = box;
            }
        }
    }

    /// <summary>One tick changed. The whole list goes back, as one record.</summary>
    private void NotificationFiltersChanged(NotificationWay way, NotificationKind kind, bool on)
    {
        if (_renderingSwitches)
            return;

        _actions.SetNotificationFilters(_snapshot.NotificationFiltersOrDefault.With(way, kind, on));
    }

    /// <summary>Puts the snapshot into the card's boxes without any of them answering back.</summary>
    private void RefreshNotificationFilterControls(NotificationFilters filters)
    {
        foreach (var ((way, kind), box) in _filterBoxes)
            box.IsChecked = filters.Wants(way, kind);
    }

    /// <summary>The card: a heading row of the three ways, then one row per kind.</summary>
    private Control NotificationFiltersCard()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,72,72,72"),
            RowSpacing = 6,
        };

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        foreach (var _ in NotificationFilters.Kinds)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (var column = 0; column < NotificationFilters.Ways.Count; column++)
        {
            var heading = Ui.Label(NotificationFilters.Label(NotificationFilters.Ways[column]));
            heading.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetRow(heading, 0);
            Grid.SetColumn(heading, column + 1);
            grid.Children.Add(heading);
        }

        for (var row = 0; row < NotificationFilters.Kinds.Count; row++)
        {
            var kind = NotificationFilters.Kinds[row];

            var label = Ui.Text(NotificationFilters.Label(kind), Ui.T.Density.TextSmall, Ui.T.TextBrush, wrap: false);
            label.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(label, row + 1);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            for (var column = 0; column < NotificationFilters.Ways.Count; column++)
            {
                var box = _filterBoxes[(NotificationFilters.Ways[column], kind)];
                DetachFromParent(box);
                Grid.SetRow(box, row + 1);
                Grid.SetColumn(box, column + 1);
                grid.Children.Add(box);
            }
        }

        return grid;
    }
}
