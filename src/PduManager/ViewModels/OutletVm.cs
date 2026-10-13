using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PduManager.Models;

namespace PduManager.ViewModels;

public partial class OutletVm : ObservableObject
{
    private readonly PduVm _pdu;

    public OutletVm(PduVm pdu, int index) { _pdu = pdu; Index = index; }

    public int Index { get; }

    [ObservableProperty] private string _name = "";

    [NotifyPropertyChangedFor(nameof(StateText), nameof(ToggleText))]
    [ObservableProperty] private bool? _isOn;

    [ObservableProperty] private string _boundIp = "";

    /// <summary>Ticked in the grid; used by the Outlets menu "Selected" actions.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>null = not monitored / unknown, true = ping ok, false = no reply.</summary>
    [NotifyPropertyChangedFor(nameof(DeviceText))]
    [ObservableProperty] private bool? _deviceUp;

    public string StateText => IsOn switch { true => "ON", false => "OFF", _ => "?" };
    public string ToggleText => IsOn == true ? "Turn OFF" : "Turn ON";
    public string DeviceText => string.IsNullOrWhiteSpace(BoundIp) ? "" : DeviceUp switch { true => "UP", false => "DOWN", _ => "…" };

    /// <summary>"Auto Power-Off": switch this outlet off when the bound device goes down.</summary>
    [NotifyPropertyChangedFor(nameof(AutoText))]
    [ObservableProperty] private bool _autoPowerOff;

    /// <summary>Set while the PDU changes the value itself (loading, revert), so no confirmation is asked.</summary>
    internal bool SuppressAutoPrompt { get; set; }

    public string AutoText => !AutoPowerOff || !_pdu.Online ? "" :
        IsOn != true ? "waiting for power" : _pdu.AutoOff.IsArmed(Index) ? "armed" : "waiting for device";

    public void RefreshAutoText() => OnPropertyChanged(nameof(AutoText));

    partial void OnBoundIpChanged(string value)
    {
        DeviceUp = null;
        OnPropertyChanged(nameof(DeviceText));
        _pdu.SaveBinding(Index, value);
    }

    partial void OnAutoPowerOffChanged(bool value)
    {
        if (!SuppressAutoPrompt) _pdu.OnAutoPowerOffToggled(this, value);
    }

    [RelayCommand]
    private Task Toggle() => _pdu.ControlAsync(new[] { this }, IsOn == true ? OutletOp.Off : OutletOp.On);

    [RelayCommand]
    private Task Cycle() => _pdu.ControlAsync(new[] { this }, OutletOp.Cycle);
}
