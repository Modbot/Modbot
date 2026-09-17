using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Modbot.Companion.Journal;
using Modbot.Companion.Presentation;

namespace Modbot.Companion.App;

/// <summary>
/// The Events page: every event this client processed, newest first, behind a filter bar, with
/// a selection the keyboard can move and a row that opens into everything it holds.
/// </summary>
/// <remarks>
/// <para>The filters are applied to the rows the snapshot already carries; nothing is read for
/// them. The chips are remembered in <c>settings.json</c> through the same path as every other
/// setting, and taken from the snapshot once, when the page is first drawn.</para>
/// <para>The picker and the JSON box are built once and re-attached on every render, like the
/// paste box: a text box rebuilt every second would lose what was typed into it.</para>
/// </remarks>
public sealed partial class MainWindow
{
    private EventFilterSet _filters = EventFilterSet.Empty;

    private bool _filtersLoaded;

    private JournalRow? _selectedRow;

    private JournalRow? _openedRow;

    private bool _scrollToSelected;

    /// <summary>The rows the page showed on its last draw, in order, so the keys move over what is on screen.</summary>
    private IReadOnlyList<JournalRow> _shownRows = [];

    private IReadOnlyDictionary<string, string> _groups = new Dictionary<string, string>(StringComparer.Ordinal);

    // The picker: one box to type into, one list under it. Step one lists properties; step two
    // lists a property's values, or takes a word for the text filter.
    private readonly Border _picker = new()
    {
        Background = Ui.T.SurfaceBrush,
        BorderBrush = Ui.T.Border2Brush,
        BorderThickness = new Thickness(Ui.T.Density.Hairline),
        CornerRadius = new CornerRadius(Ui.T.Density.Radius),
        Width = 300,
        HorizontalAlignment = HorizontalAlignment.Left,
        ClipToBounds = true,
    };

    private readonly TextBox _pickerBox = Ui.Input();

    private readonly StackPanel _pickerOperators = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(4) };

    private readonly StackPanel _pickerList = new() { Margin = new Thickness(4) };

    private bool _picking;

    private EventFilterProperty? _pickerProperty;

    private int _pickerCursor;

    // The opened row's JSON, in a box a person can select text in; built once so a selection
    // survives the timer.
    private readonly TextBox _jsonBox = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = Ui.Mono,
        FontSize = Ui.T.Density.TextTiny,
        Foreground = Ui.T.TextBrush,
        Background = Ui.T.BackgroundBrush,
        BorderBrush = Ui.T.BorderBrush,
        BorderThickness = new Thickness(Ui.T.Density.Hairline),
        CornerRadius = new CornerRadius(Ui.T.Density.Radius),
        Padding = new Thickness(10, 8),
        MaxHeight = 300,
    };

    private JournalRow? _jsonFor;

    private void SetUpEvents()
    {
        _pickerBox.BorderThickness = new Thickness(0);
        _pickerBox.Background = Brushes.Transparent;
        _pickerBox.CornerRadius = new CornerRadius(0);
        _pickerBox.TextChanged += (_, _) =>
        {
            _pickerCursor = 0;
            RefreshPicker();
        };
        _pickerBox.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Down:
                    _pickerCursor++;
                    RefreshPicker();
                    e.Handled = true;
                    break;
                case Key.Up:
                    _pickerCursor = Math.Max(0, _pickerCursor - 1);
                    RefreshPicker();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    PickerEnter();
                    e.Handled = true;
                    break;
            }
        };

        _picker.Child = new StackPanel
        {
            Children =
            {
                _pickerBox,
                Ui.Hairline(),
                _pickerOperators,
                new ScrollViewer { MaxHeight = 280, Content = _pickerList },
            },
        };
    }

    /// <summary>The keys that work on this page, listed on the sheet while it is open.</summary>
    private IEnumerable<Shortcut> EventsPageKeys() =>
    [
        new("j", "Next row", ShortcutGroup.Lists, () => MoveSelection(1), Page: true),
        new("arrowdown", "Next row", ShortcutGroup.Lists, () => MoveSelection(1), Page: true, Hidden: true),
        new("k", "Previous row", ShortcutGroup.Lists, () => MoveSelection(-1), Page: true),
        new("arrowup", "Previous row", ShortcutGroup.Lists, () => MoveSelection(-1), Page: true, Hidden: true),
        new("enter", "Open the selected row", ShortcutGroup.Lists, OpenSelected, Page: true),
        new("f", "Add a filter", ShortcutGroup.Filters, () => BeginPick(null), Page: true),
        new("/", "Filter by text", ShortcutGroup.Filters, () => BeginPick(EventFilters.PropertyFor(EventFilters.Text)), Page: true),
        new("shift+f", "Remove the last filter", ShortcutGroup.Filters, () => SetFilters(_filters.WithoutLast()), Page: true, Hidden: true),
    ];

    /// <summary><c>Esc</c> on this page: the picker, then the open row, then the selection.</summary>
    private void EscapeEvents()
    {
        if (_picking)
        {
            EndPick();
            return;
        }

        if (_openedRow is not null)
        {
            _openedRow = null;
            ClearFocus();
            RenderPage();
            return;
        }

        if (_selectedRow is not null)
        {
            _selectedRow = null;
            RenderPage();
        }
    }

    private void RenderEvents()
    {
        if (!_filtersLoaded)
        {
            _filters = _snapshot.EventsFiltersOrNone;
            _filtersLoaded = true;
        }

        _groups = _snapshot.Servers.ToDictionary(s => s.ServerId, s => s.GroupName, StringComparer.Ordinal);
        _shownRows = EventFilters.Apply(_snapshot.Events, _filters, _groups);

        if (_selectedRow is not null && !_shownRows.Contains(_selectedRow))
            _selectedRow = null;
        if (_openedRow is not null && !_shownRows.Contains(_openedRow))
            _openedRow = null;

        _body.Children.Add(FilterBar());

        if (_picking)
        {
            DetachFromParent(_picker);
            if (!_pickerList.IsPointerOver)
                RefreshPicker();
            _body.Children.Add(_picker);
        }

        if (_snapshot.Events.Count == 0)
        {
            _body.Children.Add(Ui.Card(Ui.Dim("Nothing yet.")));
            return;
        }

        if (_shownRows.Count == 0)
        {
            _body.Children.Add(Ui.Card(Ui.Dim("Nothing matches.")));
            return;
        }

        var rows = new StackPanel { Spacing = 0 };
        var first = true;
        Border? selectedControl = null;

        foreach (var row in _shownRows)
        {
            var control = EventRow(row, first, _groups);
            first = false;

            var selected = row.Equals(_selectedRow);
            var opened = row.Equals(_openedRow);
            control.Background = opened ? Ui.T.Surface2Brush : selected ? Ui.T.Surface2Brush : Brushes.Transparent;
            if (selected)
                selectedControl = control;

            var thisRow = row;
            control.PointerPressed += (_, _) =>
            {
                _selectedRow = thisRow;
                _openedRow = thisRow.Equals(_openedRow) ? null : thisRow;
                RenderPage();
            };

            rows.Children.Add(control);

            if (opened)
                rows.Children.Add(DetailPanel(row));
        }

        _body.Children.Add(new Border
        {
            Background = Ui.T.SurfaceBrush,
            BorderBrush = Ui.T.BorderBrush,
            BorderThickness = new Thickness(Ui.T.Density.Hairline),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Child = rows,
        });

        if (_scrollToSelected && selectedControl is { } target)
        {
            _scrollToSelected = false;
            Dispatcher.UIThread.Post(target.BringIntoView, DispatcherPriority.Loaded);
        }
    }

    // ── Selection ─────────────────────────────────────────────────────────────────────────────

    private void MoveSelection(int by)
    {
        if (_shownRows.Count == 0)
            return;

        var at = _selectedRow is null ? -1 : IndexOf(_selectedRow);
        var next = at < 0
            ? by > 0 ? 0 : _shownRows.Count - 1
            : Math.Clamp(at + by, 0, _shownRows.Count - 1);

        _selectedRow = _shownRows[next];
        _scrollToSelected = true;
        RenderPage();
    }

    private int IndexOf(JournalRow row)
    {
        for (var index = 0; index < _shownRows.Count; index++)
        {
            if (_shownRows[index].Equals(row))
                return index;
        }

        return -1;
    }

    private void OpenSelected()
    {
        if (_selectedRow is null)
            return;

        _openedRow = _selectedRow.Equals(_openedRow) ? null : _selectedRow;
        _scrollToSelected = true;
        RenderPage();
    }

    // ── The filter bar ────────────────────────────────────────────────────────────────────────

    private void SetFilters(EventFilterSet next)
    {
        _filters = next;
        _actions.SetEventsFilters(next);
        RenderPage();
    }

    /// <summary>
    /// One chip per property, an add control, a clear control, and the count at the end.
    /// </summary>
    private Control FilterBar()
    {
        var bar = new WrapPanel { Orientation = Orientation.Horizontal };

        foreach (var chip in _filters.Chips)
        {
            var control = Chip(chip);
            control.Margin = new Thickness(0, 0, 8, 6);
            bar.Children.Add(control);
        }

        var add = Ui.Button("Filter");
        add.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                Ui.Text("Filter", Ui.T.Density.TextSmall, Ui.T.TextBrush, FontWeight.Medium, wrap: false),
                Ui.Kbd("f"),
            },
        };
        add.Margin = new Thickness(0, 0, 8, 6);
        add.Click += (_, _) => BeginPick(null);
        bar.Children.Add(add);

        if (!_filters.IsEmpty)
        {
            var clear = Ui.Button("Clear");
            clear.Background = Brushes.Transparent;
            clear.BorderThickness = new Thickness(0);
            clear.Margin = new Thickness(0, 0, 8, 6);
            clear.Click += (_, _) => SetFilters(EventFilterSet.Empty);
            bar.Children.Add(clear);
        }

        var count = Ui.Faint(_filters.IsEmpty
            ? $"{_snapshot.Events.Count:N0}"
            : $"{_shownRows.Count:N0} of {_snapshot.Events.Count:N0}");
        count.VerticalAlignment = VerticalAlignment.Center;
        count.Margin = new Thickness(0, 0, 0, 6);
        count.Height = Ui.T.Density.ControlHeight;
        bar.Children.Add(count);

        return bar;
    }

    /// <summary>
    /// A chip reads <c>property · operator · values</c>. The operator turns over when pressed,
    /// the values open the picker on that property, and × takes the chip away.
    /// </summary>
    private Control Chip(EventFilterChip chip)
    {
        var property = EventFilters.PropertyFor(chip.Property);

        var parts = new StackPanel { Orientation = Orientation.Horizontal };

        var name = Ui.Dim(property.Label);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(8, 0);
        parts.Children.Add(name);

        var words = EventFilterChip.OperatorWords(chip.Operator, chip.Values.Count);
        if (property.Operators.Count > 1)
        {
            var flip = ChipButton(Ui.Dim(words));
            flip.Click += (_, _) =>
            {
                var operators = property.Operators.ToList();
                var next = operators[(operators.IndexOf(chip.Operator) + 1) % operators.Count];
                SetFilters(_filters.Replace(chip.With(next)));
            };
            parts.Children.Add(flip);
        }
        else
        {
            var fixedWords = Ui.Dim(words);
            fixedWords.VerticalAlignment = VerticalAlignment.Center;
            fixedWords.Margin = new Thickness(8, 0);
            parts.Children.Add(Edged(fixedWords));
        }

        var labels = chip.Values.Select(v => EventFilters.LabelOf(chip.Property, v)).ToList();
        var shown = labels.Count > 3
            ? $"{string.Join(", ", labels.Take(3))} +{labels.Count - 3}"
            : labels.Count == 0 ? "…" : string.Join(", ", labels);

        var values = ChipButton(Ui.Text(shown, Ui.T.Density.TextSmall, Ui.T.TextBrush, FontWeight.Medium, wrap: false));
        values.MaxWidth = 320;
        values.Click += (_, _) => BeginPick(property);
        parts.Children.Add(values);

        var remove = ChipButton(Ui.Dim("×"));
        remove.Click += (_, _) => SetFilters(_filters.Without(chip.Property));
        parts.Children.Add(remove);

        return new Border
        {
            Background = Ui.T.SurfaceBrush,
            BorderBrush = Ui.T.BorderBrush,
            BorderThickness = new Thickness(Ui.T.Density.Hairline),
            CornerRadius = new CornerRadius(Ui.T.Density.Radius),
            Height = Ui.T.Density.ControlHeight,
            ClipToBounds = true,
            Child = parts,
        };
    }

    private static Button ChipButton(Control content)
    {
        content.VerticalAlignment = VerticalAlignment.Center;
        return new Button
        {
            Content = content,
            Padding = new Thickness(8, 0),
            Background = Brushes.Transparent,
            BorderBrush = Ui.T.BorderBrush,
            BorderThickness = new Thickness(Ui.T.Density.Hairline, 0, 0, 0),
            CornerRadius = new CornerRadius(0),
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
    }

    private static Control Edged(Control content) => new Border
    {
        BorderBrush = Ui.T.BorderBrush,
        BorderThickness = new Thickness(Ui.T.Density.Hairline, 0, 0, 0),
        Child = content,
    };

    // ── The picker ────────────────────────────────────────────────────────────────────────────

    /// <summary>Opens the picker: at the property list, or straight at one property's values.</summary>
    private void BeginPick(EventFilterProperty? property)
    {
        _picking = true;
        _pickerProperty = property;
        _pickerCursor = 0;
        _pickerBox.Text = property?.IsText == true ? (_filters.For(property.Id)?.Values.FirstOrDefault() ?? "") : "";
        RefreshPicker();
        RenderPage();

        Dispatcher.UIThread.Post(() =>
        {
            _pickerBox.Focus();
            _pickerBox.SelectAll();
        });
    }

    private void EndPick()
    {
        _picking = false;
        _pickerProperty = null;
        ClearFocus();
        RenderPage();
    }

    /// <summary>The properties the picker offers: those without a chip yet, narrowed by what was typed.</summary>
    private List<EventFilterProperty> PickableProperties()
    {
        var typed = (_pickerBox.Text ?? "").Trim();
        return EventFilters.Properties
            .Where(p => _filters.For(p.Id) is null)
            .Where(p => p.Label.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>One property's values, counted over the rows the other chips let through, narrowed by what was typed.</summary>
    private List<EventFilterOption> PickableOptions(EventFilterProperty property)
    {
        var typed = (_pickerBox.Text ?? "").Trim();
        var others = EventFilters.Apply(_snapshot.Events, _filters.Without(property.Id), _groups);
        return EventFilters.Options(property.Id, others, _groups)
            .Where(o => o.Label.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary><c>Enter</c> in the picker's box: pick the property, tick the value, or apply the word.</summary>
    private void PickerEnter()
    {
        if (_pickerProperty is not { } property)
        {
            var properties = PickableProperties();
            if (properties.Count == 0)
                return;

            var chosen = properties[Math.Clamp(_pickerCursor, 0, properties.Count - 1)];
            _pickerProperty = chosen;
            _pickerCursor = 0;
            _pickerBox.Text = "";
            RefreshPicker();
            return;
        }

        if (property.IsText)
        {
            var word = (_pickerBox.Text ?? "").Trim();
            if (word.Length == 0)
                return;

            SetFilters(_filters.Replace(new EventFilterChip(property.Id, EventFilterOperator.Contains, [word])));
            EndPick();
            return;
        }

        var options = PickableOptions(property);
        if (options.Count == 0)
            return;

        ToggleValue(property, options[Math.Clamp(_pickerCursor, 0, options.Count - 1)].Value);
    }

    /// <summary>Ticks or unticks one value. A chip with nothing left ticked is taken away.</summary>
    private void ToggleValue(EventFilterProperty property, string value)
    {
        var chip = _filters.For(property.Id) ?? new EventFilterChip(property.Id, EventFilterOperator.Is, []);
        var values = chip.Values.Contains(value, StringComparer.Ordinal)
            ? chip.Values.Where(v => v != value).ToList()
            : [.. chip.Values, value];

        SetFilters(values.Count == 0 ? _filters.Without(property.Id) : _filters.Replace(chip.With(values)));
    }

    /// <summary>Redraws the picker's list from its step, what was typed and where the cursor is.</summary>
    private void RefreshPicker()
    {
        _pickerList.Children.Clear();
        _pickerOperators.Children.Clear();
        _pickerOperators.IsVisible = false;

        if (_pickerProperty is not { } property)
        {
            _pickerBox.Watermark = "Filter by";
            var properties = PickableProperties();
            _pickerCursor = Math.Clamp(_pickerCursor, 0, Math.Max(0, properties.Count - 1));

            if (properties.Count == 0)
                _pickerList.Children.Add(NothingMatches());

            for (var index = 0; index < properties.Count; index++)
            {
                var candidate = properties[index];
                var label = Ui.Text(candidate.Label, Ui.T.Density.TextSmall, Ui.T.TextBrush, wrap: false);
                label.VerticalAlignment = VerticalAlignment.Center;
                _pickerList.Children.Add(PickerRow(label, index, () =>
                {
                    _pickerProperty = candidate;
                    _pickerCursor = 0;
                    _pickerBox.Text = "";
                    RefreshPicker();
                    _pickerBox.Focus();
                }));
            }

            return;
        }

        _pickerBox.Watermark = property.Label;

        if (property.IsText)
        {
            var apply = Ui.Button("Apply", primary: true);
            apply.Margin = new Thickness(6);
            apply.HorizontalAlignment = HorizontalAlignment.Left;
            apply.Click += (_, _) => PickerEnter();
            _pickerList.Children.Add(apply);
            return;
        }

        var chip = _filters.For(property.Id);
        var current = chip?.Operator ?? EventFilterOperator.Is;

        _pickerOperators.IsVisible = true;
        foreach (var @operator in property.Operators)
        {
            var chosen = @operator;
            var button = Ui.Button(EventFilterChip.OperatorWords(@operator, 2), primary: current == @operator);
            button.Height = 24;
            button.Click += (_, _) =>
            {
                if (chip is not null)
                    SetFilters(_filters.Replace(chip.With(chosen)));
            };
            _pickerOperators.Children.Add(button);
        }

        var options = PickableOptions(property);
        _pickerCursor = Math.Clamp(_pickerCursor, 0, Math.Max(0, options.Count - 1));

        if (options.Count == 0)
            _pickerList.Children.Add(NothingMatches());

        for (var index = 0; index < options.Count; index++)
        {
            var option = options[index];
            var on = chip?.Values.Contains(option.Value, StringComparer.Ordinal) == true;

            var tick = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(3),
                BorderBrush = on ? Ui.T.AccentBrush : Ui.T.Border2Brush,
                BorderThickness = new Thickness(Ui.T.Density.Hairline),
                Background = on ? Ui.T.AccentBrush : Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Child = on
                    ? new TextBlock
                    {
                        Text = "✓",
                        FontSize = 10,
                        Foreground = Ui.T.AccentForegroundBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    }
                    : null,
            };

            var count = Ui.Text($"{option.Count:N0}", Ui.T.Density.TextSmall, Ui.T.TextFaintBrush, wrap: false, mono: true);
            count.VerticalAlignment = VerticalAlignment.Center;

            var label = Ui.Text(option.Label, Ui.T.Density.TextSmall, Ui.T.TextBrush, wrap: false);
            label.VerticalAlignment = VerticalAlignment.Center;

            var content = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(tick, Avalonia.Controls.Dock.Left);
            DockPanel.SetDock(count, Avalonia.Controls.Dock.Right);
            content.Children.Add(tick);
            content.Children.Add(count);
            content.Children.Add(label);

            var value = option.Value;
            _pickerList.Children.Add(PickerRow(content, index, () => ToggleValue(property, value)));
        }
    }

    private Button PickerRow(Control content, int index, Action press)
    {
        var row = Ui.ListRow(content, index == _pickerCursor, press);
        row.Height = 30;
        row.PointerEntered += (_, _) =>
        {
            if (_pickerCursor != index)
            {
                _pickerCursor = index;
                RefreshPicker();
            }
        };
        return row;
    }

    private static Control NothingMatches()
    {
        var none = Ui.Dim("Nothing matches");
        none.Margin = new Thickness(10, 8);
        return none;
    }

    // ── The opened row ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Everything the row holds, under it: each field with its label, and the row as JSON in a
    /// box the text can be selected from.
    /// </summary>
    private Control DetailPanel(JournalRow row)
    {
        var fields = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 14,
            RowSpacing = 4,
        };

        var at = 0;
        foreach (var (label, value) in EventRowDetail.Fields(row, _groups))
        {
            fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var caption = Ui.Dim(label);
            var text = Ui.Text(value, Ui.T.Density.TextSmall, Ui.T.TextBrush);
            Grid.SetRow(caption, at);
            Grid.SetRow(text, at);
            Grid.SetColumn(text, 1);
            fields.Children.Add(caption);
            fields.Children.Add(text);
            at++;
        }

        if (!row.Equals(_jsonFor))
        {
            _jsonFor = row;
            _jsonBox.Text = EventRowDetail.ToJson(row);
        }

        DetachFromParent(_jsonBox);

        var json = new StackPanel
        {
            Spacing = 6,
            Children = { Ui.Label("JSON"), _jsonBox },
        };

        var columns = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 20,
        };
        Grid.SetColumn(json, 1);
        columns.Children.Add(fields);
        columns.Children.Add(json);

        return new Border
        {
            Background = Ui.T.BackgroundBrush,
            BorderBrush = Ui.T.BorderBrush,
            BorderThickness = new Thickness(0, Ui.T.Density.Hairline, 0, 0),
            Padding = new Thickness(14, 12),
            Child = columns,
        };
    }
}
