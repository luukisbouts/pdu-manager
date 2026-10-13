using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace PduManager.Views;

public enum RowKind { Text, Password, Check, ReadOnly }

public class FormRow
{
    /// <summary>Form field name on the PDU; empty for display-only rows.</summary>
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public RowKind Kind { get; init; } = RowKind.Text;
    public string Value { get; set; } = "";
    public bool Checked { get; set; }
    public int MaxLength { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    /// <summary>Row is disabled while the check row with this Name is ticked (e.g. static IP fields under DHCP).</summary>
    public string? DisabledWhenChecked { get; init; }
}

/// <summary>Generic settings dialog built from a list of rows. <c>apply</c> returns an error message, or null on success.</summary>
public class FormWindow : Window
{
    private readonly List<FormRow> _rows;
    private readonly Func<List<FormRow>, Task<string?>> _apply;
    private readonly Dictionary<FormRow, Control> _controls = new();
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _applyBtn = new() { Content = "Apply", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };

    public FormWindow(string title, string? intro, List<FormRow> rows, Func<List<FormRow>, Task<string?>> apply)
    {
        _rows = rows;
        _apply = apply;
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current.MainWindow;

        var panel = new StackPanel { Margin = new Thickness(16) };
        if (intro is not null)
            panel.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Foreground = System.Windows.Media.Brushes.Gray });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var row in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var r = grid.RowDefinitions.Count - 1;
            Control c;
            switch (row.Kind)
            {
                case RowKind.Password:
                    c = new PasswordBox { Password = row.Value, MaxLength = row.MaxLength };
                    break;
                case RowKind.Check:
                    c = new CheckBox { Content = row.Label, IsChecked = row.Checked, VerticalAlignment = VerticalAlignment.Center };
                    break;
                default:
                    c = new TextBox { Text = row.Value, MaxLength = row.MaxLength, IsReadOnly = row.Kind == RowKind.ReadOnly };
                    if (row.Kind == RowKind.ReadOnly) c.Foreground = System.Windows.Media.Brushes.DimGray;
                    break;
            }
            c.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(c, r);
            Grid.SetColumn(c, 1);
            if (row.Kind != RowKind.Check)
            {
                var label = new TextBlock { Text = row.Label, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(label, r);
                grid.Children.Add(label);
            }
            grid.Children.Add(c);
            _controls[row] = c;
        }
        panel.Children.Add(grid);
        panel.Children.Add(_message);

        // Enable/disable dependent rows when a check row changes.
        foreach (var dep in rows.Where(x => x.DisabledWhenChecked is not null))
        {
            var check = rows.First(x => x.Name == dep.DisabledWhenChecked);
            var box = (CheckBox)_controls[check];
            void Update(object? s, RoutedEventArgs? e) => _controls[dep].IsEnabled = box.IsChecked != true;
            box.Checked += Update;
            box.Unchecked += Update;
            Update(null, null);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _applyBtn.Click += Apply_Click;
        buttons.Children.Add(_applyBtn);
        buttons.Children.Add(new Button { Content = "Cancel", Padding = new Thickness(10, 4, 10, 4), IsCancel = true });
        panel.Children.Add(buttons);
        Content = panel;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows.Where(r => r.Kind != RowKind.ReadOnly))
        {
            var c = _controls[row];
            switch (c)
            {
                case TextBox t: row.Value = t.Text.Trim(); break;
                case PasswordBox p: row.Value = p.Password; break;
                case CheckBox cb: row.Checked = cb.IsChecked == true; break;
            }
            if (row.Min is null && row.Max is null) continue;
            if (!double.TryParse(row.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                || v < (row.Min ?? double.MinValue) || v > (row.Max ?? double.MaxValue))
            {
                _message.Text = $"{row.Label}: enter a number" +
                    (row.Min is not null && row.Max is not null ? $" between {row.Min} and {row.Max}." : row.Min is not null ? $" of at least {row.Min}." : ".");
                return;
            }
        }

        _applyBtn.IsEnabled = false;
        _message.Foreground = System.Windows.Media.Brushes.Gray;
        _message.Text = "Applying…";
        string? err;
        try { err = await _apply(_rows); }
        catch (Exception ex) { err = ex.Message; }

        if (err is null) { DialogResult = true; return; }
        _message.Foreground = System.Windows.Media.Brushes.Firebrick;
        _message.Text = err;
        _applyBtn.IsEnabled = true;
    }
}
