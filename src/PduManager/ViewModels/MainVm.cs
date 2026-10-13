using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PduManager.Models;
using PduManager.Services;
using PduManager.Views;

namespace PduManager.ViewModels;

public partial class MainVm : ObservableObject
{
    private readonly ConfigStore _store = new();
    private readonly AppConfig _config;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _ticking;

    public MainVm()
    {
        _config = _store.Load();
        foreach (var p in _config.Pdus) Pdus.Add(new PduVm(p, Save));
        Selected = Pdus.FirstOrDefault();
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
        _ = TickAsync();
    }

    public ObservableCollection<PduVm> Pdus { get; } = new();

    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [ObservableProperty] private PduVm? _selected;

    public bool HasSelection => Selected is not null;

    private void Save() => _store.Save(_config);

    private async Task TickAsync()
    {
        if (_ticking) return;
        _ticking = true;
        try
        {
            await Task.WhenAll(Pdus.Select(async p =>
            {
                await p.RefreshAsync();
                await p.PingAllAsync();
                await p.AutoPowerOffAsync();
            }));
        }
        finally { _ticking = false; }
    }

    [RelayCommand]
    private async Task AddPdu()
    {
        var dlg = new AddPduWindow { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        var cfg = dlg.Result!;
        _config.Pdus.Add(cfg);
        Save();
        var vm = new PduVm(cfg, Save);
        Pdus.Add(vm);
        Selected = vm;
        await vm.RefreshAsync();
    }

    [RelayCommand]
    private async Task EditPdu()
    {
        if (Selected is null) return;
        var dlg = new AddPduWindow(Selected.Config) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        Selected.ApplyEdit(dlg.Result!);
        await Selected.RefreshAsync();
    }

    [RelayCommand]
    private void RemovePdu()
    {
        if (Selected is null) return;
        if (MessageBox.Show($"Remove '{Selected.Name}' and its stored credentials?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _config.Pdus.Remove(Selected.Config);
        Pdus.Remove(Selected);
        Selected = Pdus.FirstOrDefault();
        Save();
    }

    [RelayCommand] private Task Refresh() => TickAsync();
    [RelayCommand] private void Exit() => Application.Current.Shutdown();

    [RelayCommand]
    private void OpenWebInterface()
    {
        if (Selected is null) return;
        Process.Start(new ProcessStartInfo($"http://{Selected.Config.Host}:{Selected.Config.Port}/") { UseShellExecute = true });
    }

    [RelayCommand] private Task OutletSettings() => Selected is null ? Task.CompletedTask : SettingsDialogs.OutletsAsync(Selected);
    [RelayCommand] private Task Thresholds() => Selected is null ? Task.CompletedTask : SettingsDialogs.ThresholdsAsync(Selected);
    [RelayCommand] private Task SystemInfo() => Selected is null ? Task.CompletedTask : SettingsDialogs.SystemAsync(Selected);
    [RelayCommand] private Task Network() => Selected is null ? Task.CompletedTask : SettingsDialogs.NetworkAsync(Selected);
    [RelayCommand] private Task ChangeLogin() => Selected is null ? Task.CompletedTask : SettingsDialogs.UserAsync(Selected);

    [RelayCommand] private Task AllOn() => Selected is null ? Task.CompletedTask : Selected.ControlAsync(Selected.Outlets, OutletOp.On);
    [RelayCommand] private Task AllOff() => Selected is null ? Task.CompletedTask : Selected.ControlAsync(Selected.Outlets, OutletOp.Off);
    [RelayCommand] private Task SelectedOn() => RunSelected(OutletOp.On);
    [RelayCommand] private Task SelectedOff() => RunSelected(OutletOp.Off);
    [RelayCommand] private Task SelectedCycle() => RunSelected(OutletOp.Cycle);

    private Task RunSelected(OutletOp op) =>
        Selected is null ? Task.CompletedTask
            : Selected.ControlAsync(Selected.Outlets.Where(o => o.IsSelected).ToList(), op);

    [RelayCommand]
    private void About() => MessageBox.Show(
        "PDU Manager\nControls Inter-Tech ARGUS SW-0816 PDUs through their web interface.\n" +
        "Credentials are stored encrypted with Windows DPAPI.", "About", MessageBoxButton.OK, MessageBoxImage.Information);
}
