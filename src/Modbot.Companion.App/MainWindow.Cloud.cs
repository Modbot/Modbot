using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Modbot.Companion.CloudBackup;
using Modbot.Companion.Ingest;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.App;

/// <summary>
/// Modbot Cloud on screen: its box on the Settings page, and the Cloud Server page where the
/// person chooses what the backup sends.
/// </summary>
/// <remarks>
/// <para><strong>This reverses an earlier decision.</strong> The cloud backup design specs of
/// 2026-09-15 and 2026-09-19 kept the backup off every screen. At the person's request on 2026-10-04
/// the backup is named here, with a switch and a choice of what goes — see the cloud settings on
/// screen design spec.</para>
/// <para><strong>One setting, two boxes.</strong> The Settings page's Modbot Cloud box and the one on the
/// Cloud Server page are both <c>cloud.disabled</c> turned the other way round. Both are kept controls
/// put into by the same refresh, so they cannot disagree.</para>
/// <para><strong>What the page can say.</strong> Controls, three short paragraphs in the information
/// icon's tip, the warning when the last event is switched off, and <em>Nothing is sent.</em> A
/// label names a control; nothing explains how it works.</para>
/// <para>Built once, like the other cards: a box rebuilt every second under the window's timer can
/// lose the click on it. The Next batch card is the one thing drawn again, and only when what it
/// shows is different.</para>
/// </remarks>
public sealed partial class MainWindow
{
    /// <summary>The Modbot Cloud boxes, one on each page. Both are one setting.</summary>
    private readonly CheckBox _cloudOnSettings = CloudBox("cloud-on-settings");

    private readonly CheckBox _cloudOnPage = CloudBox("cloud-on-page");

    private readonly Dictionary<(bool InGroup, CloudEventKinds Kind), CheckBox> _cloudKindBoxes = [];
    private readonly Dictionary<(bool InGroup, CloudDetail Detail), CheckBox> _cloudDetailBoxes = [];
    private readonly Dictionary<bool, CheckBox> _cloudSectionBoxes = [];
    private readonly List<CheckBox> _cloudAlwaysBoxes = [];
    private readonly Dictionary<bool, TextBlock> _cloudCounts = [];
    private readonly Dictionary<CloudFold, CloudFoldView> _cloudFoldViews = [];

    private Border? _cloudWarning;
    private bool _cloudWarn;

    private readonly StackPanel _cloudNext = new() { Spacing = 8 };

    /// <summary>What the Next batch card was last drawn from, so it is drawn again only when it would differ.</summary>
    private string? _cloudNextDrawn;

    /// <summary>Which sections are open. Made on the first refresh, from whether Modbot Cloud is on then.</summary>
    private CloudFolds? _cloudFolds;

    /// <summary>The three parts of a section a refresh touches.</summary>
    private sealed record CloudFoldView(Border Box, Control Body, Avalonia.Controls.Shapes.Path Chevron);

    /// <summary>A box with no caption: its name is on the row it sits in. The control's own name is for tests to find it by.</summary>
    private static CheckBox CloudBox(string name) => new()
    {
        Name = name,
        MinWidth = 0,
        MinHeight = 0,
        Padding = new Thickness(0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    /// <summary>Makes every kept control of the two Cloud screens and wires it. Called once, from the constructor.</summary>
    private void SetUpCloud()
    {
        _cloudOnSettings.IsCheckedChanged += (_, _) => CloudOnChanged(_cloudOnSettings);
        _cloudOnPage.IsCheckedChanged += (_, _) => CloudOnChanged(_cloudOnPage);

        foreach (var inGroup in new[] { true, false })
        {
            foreach (var (kind, _, _) in CloudEventKindNames.All)
            {
                var box = CloudBox($"cloud-{Which(inGroup)}-{kind}");
                box.IsCheckedChanged += (_, _) => CloudKindChanged(inGroup, kind, box);
                _cloudKindBoxes[(inGroup, kind)] = box;
            }

            foreach (var detail in CloudDetails(inGroup))
            {
                var box = CloudBox($"cloud-{Which(inGroup)}-{detail}");
                box.IsCheckedChanged += (_, _) => CloudDetailChanged(inGroup, detail, box);
                _cloudDetailBoxes[(inGroup, detail)] = box;
            }

            // A section's own box only reports: ticked while one of its events is on. Nobody ticks it.
            var derived = CloudBox($"cloud-{Which(inGroup)}-section");
            derived.IsEnabled = false;
            _cloudSectionBoxes[inGroup] = derived;
            _cloudCounts[inGroup] = Ui.Faint("");
        }

        for (var i = 0; i < AlwaysSent.Count; i++)
        {
            var box = CloudBox($"cloud-always-{i}");
            box.IsEnabled = false;
            _cloudAlwaysBoxes.Add(box);
        }
    }

    /// <summary>What is always sent, in the order the page lists it. Not a choice.</summary>
    private static readonly IReadOnlyList<string> AlwaysSent =
    [
        "Display name",
        "User ID",
        "App version",
        "Unique ID",
        "Event Type",
        "Time / Offset to Cloud",
    ];

    private static string Which(bool inGroup) => inGroup ? "group" : "other";

    private static IEnumerable<CloudDetail> CloudDetails(bool inGroup)
    {
        yield return CloudDetail.WorldId;
        yield return CloudDetail.InstanceId;

        // A non-group instance has no group, so there is none to leave out.
        if (inGroup)
            yield return CloudDetail.GroupId;

        yield return CloudDetail.AvatarName;
    }

    private static string Label(CloudDetail detail) => detail switch
    {
        CloudDetail.WorldId => "World ID",
        CloudDetail.InstanceId => "Instance ID",
        CloudDetail.GroupId => "Group",
        _ => "Avatar name",
    };

    /// <summary>Either Modbot Cloud box changed. The two are one setting.</summary>
    private void CloudOnChanged(CheckBox box)
    {
        if (Quiet)
            return;

        _cloudWarn = false;
        _actions.SetCloudOn(box.IsChecked == true);
    }

    /// <summary>One kind of event was ticked or unticked. The last one that is on cannot be unticked.</summary>
    private void CloudKindChanged(bool inGroup, CloudEventKinds kind, CheckBox box)
    {
        if (Quiet)
            return;

        var (next, refused) = _snapshot.CloudOrDefault.Choices.WithEvent(inGroup, kind, box.IsChecked == true);

        if (refused)
        {
            // It stays ticked, and the page says why.
            _cloudWarn = true;
            Quietly(() =>
            {
                box.IsChecked = true;
                RefreshCloudWarning();
            });

            return;
        }

        _cloudWarn = false;
        _actions.SetCloudChoices(next);
    }

    private void CloudDetailChanged(bool inGroup, CloudDetail detail, CheckBox box)
    {
        if (Quiet)
            return;

        _cloudWarn = false;
        _actions.SetCloudChoices(_snapshot.CloudOrDefault.Choices.WithDetail(inGroup, detail, box.IsChecked == true));
    }

    /// <summary>The Modbot Cloud card on the Settings page: its title, the information icon and the box, and nothing else.</summary>
    private Control CloudSettingsCard()
    {
        DetachFromParent(_cloudOnSettings);
        return Ui.Card(null, "Modbot Cloud", _cloudOnSettings, beside: CloudInfoIcon());
    }

    /// <summary>The Cloud Server page: the Modbot Cloud card, then the events waiting.</summary>
    private void RenderCloudServer()
    {
        DetachFromParent(_cloudOnPage);

        var sections = new StackPanel { Spacing = 10 };
        sections.Children.Add(CloudFoldBox(
            CloudFold.GroupInstances, "Group instances", true, () => CloudSectionBody(inGroup: true)));
        sections.Children.Add(CloudFoldBox(
            CloudFold.NonGroupInstances, "Non-group instances", true, () => CloudSectionBody(inGroup: false)));

        _cloudWarning ??= CloudWarningLine();
        DetachFromParent(_cloudWarning);

        var instanceData = new StackPanel
        {
            Spacing = 0,
            Children =
            {
                Ui.Text("Instance Data", Ui.T.Density.TextSmall, Ui.T.TextBrush, FontWeight.SemiBold),
                new Border { Margin = new Thickness(0, 6, 0, 0), Child = sections },
                _cloudWarning,
            },
        };

        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                instanceData,
                CloudFoldBox(CloudFold.AlwaysSent, "Always sent", false, CloudAlwaysBody),
            },
        };

        _body.Children.Add(Ui.Card(content, "Modbot Cloud", _cloudOnPage, beside: CloudInfoIcon()));

        DetachFromParent(_cloudNext);
        _cloudNextDrawn = null;
        _body.Children.Add(Ui.Card(new StackPanel { Children = { _cloudNext } }, "Next batch"));
    }

    /// <summary>The line that says why the last event could not be unticked. Amber, and only while it applies.</summary>
    private Border CloudWarningLine()
    {
        var line = new Border
        {
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(12, 8),
            Background = Ui.T.WarnDimBrush,
            BorderBrush = Ui.T.WarnBrush,
            BorderThickness = new Thickness(Ui.T.Density.Hairline),
            CornerRadius = new CornerRadius(8),
            IsVisible = false,
            Child = Ui.Text(
                "At least one event must be on for Modbot Cloud to work.", Ui.T.Density.TextSmall, Ui.T.WarnBrush),
        };

        return line;
    }

    /// <summary>One section's two lists of boxes, in the order the page names them.</summary>
    private Control CloudSectionBody(bool inGroup)
    {
        var body = new StackPanel { Spacing = 0 };

        body.Children.Add(CloudLabel("Events"));
        foreach (var (kind, _, label) in CloudEventKindNames.All)
            body.Children.Add(CloudRow(label, _cloudKindBoxes[(inGroup, kind)]));

        body.Children.Add(CloudLabel("Details"));
        foreach (var detail in CloudDetails(inGroup))
            body.Children.Add(CloudRow(Label(detail), _cloudDetailBoxes[(inGroup, detail)]));

        return body;
    }

    private Control CloudAlwaysBody()
    {
        var body = new StackPanel { Spacing = 0 };

        for (var i = 0; i < AlwaysSent.Count; i++)
            body.Children.Add(CloudRow(AlwaysSent[i], _cloudAlwaysBoxes[i]));

        return body;
    }

    private static Control CloudLabel(string text)
    {
        var label = Ui.Label(text);
        label.Margin = new Thickness(0, 10, 0, 4);
        return label;
    }

    /// <summary>A name on the left and its box at the right end.</summary>
    private static Control CloudRow(string label, CheckBox box)
    {
        DetachFromParent(box);

        var name = Ui.Text(label, Ui.T.Density.TextSmall, Ui.T.TextBrush, wrap: false);
        name.VerticalAlignment = VerticalAlignment.Center;

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 28 };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(box, 1);
        row.Children.Add(name);
        row.Children.Add(box);
        return row;
    }

    /// <summary>
    /// A section that folds: a header that is a button, with an arrow, the name and, for the two
    /// instance sections, how many events are on and a box that only reports it.
    /// </summary>
    private Control CloudFoldBox(CloudFold fold, string title, bool withCount, Func<Control> makeBody)
    {
        // Built once for each section and kept, so which are open survives a page being built again.
        if (!_cloudFoldViews.TryGetValue(fold, out var view))
        {
            var chevron = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M1,3 L5,7 L9,3"),
                Stroke = Ui.T.TextFaintBrush,
                StrokeThickness = 1.5,
                Width = 10,
                Height = 10,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = RelativePoint.Center,
            };

            var name = Ui.Text(title, Ui.T.Density.TextSmall, Ui.T.TextBrush, FontWeight.SemiBold, wrap: false);
            name.VerticalAlignment = VerticalAlignment.Center;

            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { chevron, name },
            };

            if (withCount)
            {
                var count = _cloudCounts[fold is CloudFold.GroupInstances];
                count.VerticalAlignment = VerticalAlignment.Center;
                left.Children.Add(count);
            }

            var header = new Button
            {
                Content = left,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 9),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };

            header.Click += (_, _) =>
            {
                _cloudFolds?.Toggle(fold);
                ApplyCloudFolds();
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(header, 0);
            row.Children.Add(header);

            if (withCount)
            {
                var derived = _cloudSectionBoxes[fold is CloudFold.GroupInstances];
                derived.Margin = new Thickness(0, 0, 12, 0);
                Grid.SetColumn(derived, 1);
                row.Children.Add(derived);
            }

            // Made only this once: its boxes are kept controls, and building the body again would
            // take them out of the one that is showing.
            var inner = new Border { Padding = new Thickness(12, 0, 12, 10), Child = makeBody() };

            var box = new Border
            {
                Background = Ui.T.Surface2Brush,
                BorderBrush = Ui.T.BorderBrush,
                BorderThickness = new Thickness(Ui.T.Density.Hairline),
                CornerRadius = new CornerRadius(8),
                Child = new StackPanel { Children = { row, inner } },
            };

            view = new CloudFoldView(box, inner, chevron);
            _cloudFoldViews[fold] = view;
        }

        if (view.Box.Parent is Panel panel)
            panel.Children.Remove(view.Box);

        return view.Box;
    }

    /// <summary>Opens and folds each section as <see cref="_cloudFolds"/> says.</summary>
    private void ApplyCloudFolds()
    {
        if (_cloudFolds is null)
            return;

        foreach (var (fold, view) in _cloudFoldViews)
        {
            var open = _cloudFolds.IsOpen(fold);
            view.Body.IsVisible = open;
            view.Chevron.RenderTransform = open ? null : new RotateTransform(-90);
        }
    }

    /// <summary>
    /// The small (i) beside the title, whose tip says what Modbot Cloud is: three short paragraphs.
    /// </summary>
    private static Control CloudInfoIcon()
    {
        var tip = new StackPanel { Spacing = 6, MaxWidth = 320 };

        tip.Children.Add(Ui.Text(
            "Modbot Cloud keeps a copy of what your app sees, held apart from your group's server.",
            Ui.T.Density.TextSmall));

        var forYourGroup = Ui.Text("", Ui.T.Density.TextSmall);
        forYourGroup.Inlines =
        [
            new Run("For your group:") { FontWeight = FontWeight.Bold },
            new Run(" if its server is ever lost or reset, a copy of what its moderators saw still exists. "
                + "Cloud keeps events for a year by default."),
        ];
        tip.Children.Add(forYourGroup);

        tip.Children.Add(Ui.Text(
            "Your group's server still only gets your group's instances.",
            Ui.T.Density.TextSmall));

        var icon = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            BorderBrush = Ui.T.TextFaintBrush,
            BorderThickness = new Thickness(Ui.T.Density.Hairline),
            Focusable = true,
            Cursor = new Cursor(StandardCursorType.Help),
            Child = new TextBlock
            {
                Text = "i",
                FontFamily = new FontFamily("Georgia"),
                FontStyle = FontStyle.Italic,
                FontWeight = FontWeight.Bold,
                FontSize = 10,
                Foreground = Ui.T.TextDimBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        ToolTip.SetTip(icon, tip);
        ToolTip.SetShowDelay(icon, 100);
        ToolTip.SetPlacement(icon, PlacementMode.Bottom);
        return icon;
    }

    /// <summary>Puts the snapshot into both Modbot Cloud boxes and everything under the page's card, without any answering back.</summary>
    private void RefreshCloudControls()
    {
        var cloud = _snapshot.CloudOrDefault;
        var on = !cloud.Disabled;
        var locked = cloud.SwitchLocked;

        // Switching it off folds every section and switching it on opens the ones that were. Done
        // here so it happens whichever page the box was ticked on.
        _cloudFolds ??= new CloudFolds(on);
        _cloudFolds.CloudChanged(on);
        ApplyCloudFolds();

        _cloudOnSettings.IsChecked = on;
        _cloudOnSettings.IsEnabled = !locked;
        _cloudOnPage.IsChecked = on;
        _cloudOnPage.IsEnabled = !locked;

        foreach (var inGroup in new[] { true, false })
        {
            var section = cloud.Choices.For(inGroup);

            // Ticked while Modbot Cloud is on and at least one event of the section is.
            _cloudSectionBoxes[inGroup].IsChecked = on && section.EventCount > 0;
            _cloudCounts[inGroup].Text = $"{section.EventCount} of {CloudEventKindNames.All.Count} events";

            foreach (var (kind, _, _) in CloudEventKindNames.All)
            {
                var box = _cloudKindBoxes[(inGroup, kind)];
                box.IsChecked = section.Sends(kind);
                box.IsEnabled = on;
            }

            foreach (var detail in CloudDetails(inGroup))
            {
                var box = _cloudDetailBoxes[(inGroup, detail)];
                box.IsChecked = section.Has(detail);
                box.IsEnabled = on;
            }
        }

        // What is always sent is ticked while Modbot Cloud is on, and not while it is off.
        foreach (var box in _cloudAlwaysBoxes)
            box.IsChecked = on;

        RefreshCloudWarning();
    }

    /// <summary>The amber line shows only while Modbot Cloud is on and the last event was just refused.</summary>
    private void RefreshCloudWarning()
    {
        if (_cloudWarning is not null)
            _cloudWarning.IsVisible = _cloudWarn && !_snapshot.CloudOrDefault.Disabled;
    }

    /// <summary>
    /// The Cloud Server page, put into the controls it keeps. Run on every tick, whether or not the
    /// page was built again.
    /// </summary>
    private void RefreshCloudServer()
    {
        Quietly(RefreshCloudControls);
        RefreshCloudNext();
    }

    /// <summary>
    /// Draws the Next batch card: the newest events waiting, up to five, each with what it will
    /// carry. Drawn again only when it would look different.
    /// </summary>
    private void RefreshCloudNext()
    {
        var on = !_snapshot.CloudOrDefault.Disabled;
        var waiting = on ? _actions.NextCloudBatch(5) : CloudWaiting.None;

        var shown = waiting.Events;

        var key = string.Join(
            "|",
            shown.Select(e => $"{e.EventId}/{e.WorldId}/{e.InstanceId}/{e.GroupId}/{e.AvatarName}/{e.DisplayName}"))
            + $"@{waiting.ClockOffset.Ticks}";

        if (key == _cloudNextDrawn)
            return;

        _cloudNextDrawn = key;
        _cloudNext.Children.Clear();

        if (shown.Count == 0)
        {
            _cloudNext.Children.Add(Ui.Text("Nothing is sent.", Ui.T.Density.TextSmall, Ui.T.TextFaintBrush));
            return;
        }

        foreach (var queued in shown)
            _cloudNext.Children.Add(CloudQueuedCard(queued, waiting.ClockOffset));
    }

    /// <summary>One waiting event, drawn like the mock: its kind and where it happened, then a row for each detail.</summary>
    private static Control CloudQueuedCard(CloudQueuedEvent queued, TimeSpan offset)
    {
        var label = CloudEventKindNames.All.FirstOrDefault(k => k.Kind == queued.Kind).Label ?? queued.Type.ToString();

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var kind = Ui.Text(label, Ui.T.Density.TextSmall, Ui.T.TextBrush, FontWeight.SemiBold, wrap: false);
        var where = Ui.Faint(queued.InGroup ? "Group instance" : "Non-group instance");
        Grid.SetColumn(where, 1);
        head.Children.Add(kind);
        head.Children.Add(where);

        var rows = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*"), RowSpacing = 2 };
        var row = 0;

        void Add(string name, string? value, bool mono = false)
        {
            rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var left = Ui.Text(name, Ui.T.Density.TextSmall, Ui.T.TextFaintBrush, wrap: false);
            Grid.SetRow(left, row);
            rows.Children.Add(left);

            // A detail that is off reads "not sent" in the place its value would be.
            var right = value is null
                ? Ui.Text("not sent", Ui.T.Density.TextSmall, Ui.T.TextFaintBrush, wrap: false)
                : Ui.Text(value, Ui.T.Density.TextSmall, Ui.T.TextBrush, wrap: false, mono: mono);
            if (value is null)
                right.FontStyle = FontStyle.Italic;

            Grid.SetRow(right, row);
            Grid.SetColumn(right, 1);
            rows.Children.Add(right);
            row++;
        }

        Add("Unique ID", ShortId(queued.EventId), mono: true);
        Add("Event Type", queued.Type.ToString(), mono: true);
        Add("Time / Offset to Cloud", $"{queued.OccurredAt.ToLocalTime():t}, {offset.TotalSeconds:+0.0;-0.0;+0.0} s");
        Add("Display name", queued.DisplayName ?? "—");
        Add("User ID", queued.SubjectId, mono: true);
        Add("App version", Modbot.Core.ModbotVersion.Release);
        Add("World ID", queued.WorldId, mono: true);
        Add("Instance ID", queued.InstanceId, mono: true);

        if (queued.InGroup)
            Add("Group", queued.GroupId, mono: true);

        if (queued.Type is CompanionEventType.AvatarChanged)
            Add("Avatar name", queued.AvatarName);

        return new Border
        {
            Background = Ui.T.Surface2Brush,
            BorderBrush = Ui.T.BorderBrush,
            BorderThickness = new Thickness(Ui.T.Density.Hairline),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Child = new StackPanel { Spacing = 4, Children = { head, rows } },
        };
    }

    /// <summary>The start and end of an id, so a card does not carry a whole one.</summary>
    private static string ShortId(string id) => id.Length <= 9 ? id : $"{id[..4]}…{id[^4..]}";
}
