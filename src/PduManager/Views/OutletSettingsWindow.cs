using System.Windows;
using System.Windows.Controls;
using PduManager.Models;
using PduManager.ViewModels;

namespace PduManager.Views;

public class OutletRow
{
    public int Index { get; init; }
    public string Name { get; set; } = "";
    public string OnDelay { get; set; } = "0";
    public string OffDelay { get; set; } = "0";
}

/// <summary>Rename outlets and set ON/OFF delays (config_PDU.htm: otlt/ondly/ofdly 0..7).</summary>
public class OutletSettingsWindow : Window
{
    private readonly PduVm _pdu;
    private readonly List<FormField> _fields;
    private readonly List<OutletRow> _rows;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column };
    private readonly TextBlock _message = new() { Margin = new Thickness(0, 8, 0, 8), TextWrapping = TextWrapping.Wrap };
    private readonly Button _applyBtn = new() { Content = "Apply", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };

    public OutletSettingsWindow(PduVm pdu, List<FormField> fields)
    {
        _pdu = pdu;
        _fields = fields;
        string Val(string n) => fields.FirstOrDefault(f => f.Name == n)?.Value ?? "";
        _rows = Enumerable.Range(0, 8).Select(i => new OutletRow
        {
            Index = i, Name = Val($"otlt{i}"), OnDelay = Val($"ondly{i}"), OffDelay = Val($"ofdly{i}"),
        }).ToList();

        Title = $"Outlet names & delays — {pdu.Name}";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current.MainWindow;

        _grid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new System.Windows.Data.Binding("Index") { Mode = System.Windows.Data.BindingMode.OneWay }, IsReadOnly = true, Width = 30 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Outlet name (max 11)", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "ON delay (s)", Binding = new System.Windows.Data.Binding("OnDelay"), Width = 90 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "OFF delay (s)", Binding = new System.Windows.Data.Binding("OffDelay"), Width = 90 });
        _grid.ItemsSource = _rows;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(_grid);
        panel.Children.Add(_message);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _applyBtn.Click += Apply_Click;
        buttons.Children.Add(_applyBtn);
        buttons.Children.Add(new Button { Content = "Cancel", Padding = new Thickness(10, 4, 10, 4), IsCancel = true });
        panel.Children.Add(buttons);
        Content = panel;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (var r in _rows)
        {
            r.Name = r.Name.Trim();
            if (r.Name.Length is 0 or > 11) { Fail($"Outlet {r.Index + 1}: the name must be 1–11 characters."); return; }
            if (r.Name.Any(ch => ch > 126 || ch < 32)) { Fail($"Outlet {r.Index + 1}: use plain ASCII characters only."); return; }
            if (!IsDelay(r.OnDelay) || !IsDelay(r.OffDelay)) { Fail($"Outlet {r.Index + 1}: delays must be whole seconds, 0–999."); return; }
        }

        _applyBtn.IsEnabled = false;
        _message.Foreground = System.Windows.Media.Brushes.Gray;
        _message.Text = "Applying…";
        try
        {
            foreach (var r in _rows)
            {
                Set($"otlt{r.Index}", r.Name);
                Set($"ondly{r.Index}", r.OnDelay);
                Set($"ofdly{r.Index}", r.OffDelay);
            }
            await _pdu.Driver.PostFormAsync("config_PDU.htm", _fields);
            DialogResult = true;
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private static bool IsDelay(string s) => int.TryParse(s, out var v) && v is >= 0 and <= 999;

    private void Set(string name, string value)
    {
        var f = _fields.FirstOrDefault(x => x.Name == name);
        if (f is null) _fields.Add(new FormField(name, "text", value));
        else f.Value = value;
    }

    private void Fail(string msg)
    {
        _message.Foreground = System.Windows.Media.Brushes.Firebrick;
        _message.Text = msg;
        _applyBtn.IsEnabled = true;
    }
}
