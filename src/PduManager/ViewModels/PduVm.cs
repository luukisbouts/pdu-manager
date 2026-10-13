using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using PduManager.Drivers;
using PduManager.Models;
using PduManager.Services;

namespace PduManager.ViewModels;

public partial class PduVm : ObservableObject
{
    private readonly Action _save;
    private ArgusHttpDriver? _driver;

    public PduVm(PduConfig config, Action save)
    {
        Config = config;
        _save = save;
        for (var i = 0; i < ArgusHttpDriver.OutletCount; i++)
        {
            var o = new OutletVm(this, i) { Name = $"Outlet {i + 1}" };
            if (config.Bindings.TryGetValue(i, out var ip)) o.BoundIp = ip;
            o.SuppressAutoPrompt = true;
            o.AutoPowerOff = config.AutoPowerOff.GetValueOrDefault(i) && config.Bindings.ContainsKey(i);
            o.SuppressAutoPrompt = false;
            Outlets.Add(o);
        }
    }

    public AutoPowerOffMonitor AutoOff { get; } = new();

    [ObservableProperty] private string _lastEvent = "";

    /// <summary>Called when the user ticks/unticks "Auto Power-Off" for an outlet.</summary>
    public void OnAutoPowerOffToggled(OutletVm outlet, bool enabled)
    {
        void Revert()
        {
            outlet.SuppressAutoPrompt = true;
            outlet.AutoPowerOff = !enabled;
            outlet.SuppressAutoPrompt = false;
        }

        if (enabled)
        {
            if (string.IsNullOrWhiteSpace(outlet.BoundIp))
            {
                MessageBox.Show("Set a monitor IP for this outlet first.", "Auto Power-Off");
                Revert();
                return;
            }
            var ask = $"Auto Power-Off for '{outlet.Name}':\n\nWhen {outlet.BoundIp} has answered pings and then stops answering " +
                      "for about 30 seconds, this outlet is switched OFF automatically.\n\n" +
                      "A device that merely loses network connectivity will also be switched off. Enable?";
            if (MessageBox.Show(ask, "Auto Power-Off", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                Revert();
                return;
            }
            Config.AutoPowerOff[outlet.Index] = true;
        }
        else Config.AutoPowerOff.Remove(outlet.Index);
        _save();
    }

    /// <summary>Switches off outlets whose Auto Power-Off device has stopped answering pings.</summary>
    public async Task AutoPowerOffAsync()
    {
        foreach (var o in Outlets)
        {
            var fire = AutoOff.Evaluate(o.Index, o.AutoPowerOff, !string.IsNullOrWhiteSpace(o.BoundIp), o.IsOn, Online, o.DeviceUp);
            o.RefreshAutoText();
            if (!fire) continue;
            try
            {
                await Driver.ControlAsync(new[] { o.Index }, OutletOp.Off);
                LastEvent = $"{DateTime.Now:HH:mm:ss}  Auto power-off: '{o.Name}' switched off — {o.BoundIp} stopped responding.";
                await Task.Delay(500);
                await RefreshAsync();
            }
            catch (Exception ex) { LastEvent = $"{DateTime.Now:HH:mm:ss}  Auto power-off of '{o.Name}' FAILED: {ex.Message}"; }
        }
    }

    public PduConfig Config { get; }
    public string Name => Config.Name;
    public ObservableCollection<OutletVm> Outlets { get; } = new();

    [ObservableProperty] private string _status = "Connecting…";
    [ObservableProperty] private bool _online;
    [ObservableProperty] private string _readings = "";
    [ObservableProperty] private bool _alarm;

    public ArgusHttpDriver Driver => _driver ??= new ArgusHttpDriver(
        Config.Host, Config.Port,
        CredentialStore.Unprotect(Config.EncryptedUser),
        CredentialStore.Unprotect(Config.EncryptedPassword));

    private void ResetDriver() { _driver?.Dispose(); _driver = null; }

    /// <summary>Call after the PDU's own login was changed, so the stored (encrypted) copy stays valid.</summary>
    public void UpdateCredentials(string user, string password)
    {
        Config.EncryptedUser = CredentialStore.Protect(user);
        Config.EncryptedPassword = CredentialStore.Protect(password);
        _save();
        ResetDriver();
    }

    /// <summary>Call after the PDU's IP address was changed.</summary>
    public void UpdateHost(string host)
    {
        Config.Host = host;
        _save();
        ResetDriver();
    }

    /// <summary>Applies the result of the Edit PDU dialog (name, address, port, login).</summary>
    public void ApplyEdit(PduConfig edited)
    {
        Config.Name = edited.Name;
        Config.Host = edited.Host;
        Config.Port = edited.Port;
        Config.EncryptedUser = edited.EncryptedUser;
        Config.EncryptedPassword = edited.EncryptedPassword;
        _save();
        ResetDriver();
        OnPropertyChanged(nameof(Name));
    }

    public void SaveBinding(int index, string ip)
    {
        AutoOff.Reset(index); // what was learned about the old address no longer applies
        if (string.IsNullOrWhiteSpace(ip))
        {
            Config.Bindings.Remove(index);
            if (Config.AutoPowerOff.Remove(index))     // auto power-off needs a monitored device
            {
                var o = Outlets[index];
                o.SuppressAutoPrompt = true;
                o.AutoPowerOff = false;
                o.SuppressAutoPrompt = false;
            }
        }
        else Config.Bindings[index] = ip.Trim();
        _save();
    }

    public async Task RefreshAsync()
    {
        try
        {
            var snap = await Driver.GetSnapshotAsync();
            foreach (var info in snap.Outlets)
            {
                var o = Outlets[info.Index];
                o.Name = info.Name;
                o.IsOn = info.IsOn;
            }
            var s = snap.Status;
            Readings = $"Total load: {s.TotalCurrent} A    Temperature: {s.Temperature} °C    Humidity: {s.Humidity} %    PDU status: {s.StatusText}";
            Alarm = !s.IsNormal;
            Online = true;
            Status = $"Online — {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            Online = false;
            Status = CredentialStore.UnreadableHelp;
        }
        catch (Exception ex)
        {
            Online = false;
            Status = "Error: " + ex.Message;
        }
    }

    public async Task ControlAsync(IReadOnlyCollection<OutletVm> targets, OutletOp op)
    {
        if (targets.Count == 0)
        {
            MessageBox.Show("No outlets selected.", "PDU Manager");
            return;
        }
        var verb = op switch { OutletOp.On => "Turn ON", OutletOp.Off => "Turn OFF", _ => "Power-cycle (ON/OFF)" };
        var list = string.Join(", ", targets.Select(t => t.Name));
        var msg = $"{verb} on {Name}:\n{list}?";
        var bound = targets.Where(t => op != OutletOp.On && !string.IsNullOrWhiteSpace(t.BoundIp)).Select(t => t.BoundIp).ToList();
        if (bound.Count > 0) msg += $"\n\nThis cuts power to: {string.Join(", ", bound)}";
        if (MessageBox.Show(msg, "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            await Driver.ControlAsync(targets.Select(t => t.Index), op);
            await Task.Delay(500); // let the PDU apply the change
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
            return;
        }
        await RefreshAsync();
    }

    public async Task PingAllAsync()
    {
        await Task.WhenAll(Outlets.Select(async o =>
        {
            var ip = o.BoundIp?.Trim();
            if (string.IsNullOrEmpty(ip)) { o.DeviceUp = null; return; }
            bool ok;
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(ip, 1500);
                ok = reply.Status == IPStatus.Success;
            }
            catch { ok = false; }
            // Ignore the result if the outlet was re-bound while the ping was in flight.
            if (o.BoundIp?.Trim() == ip) o.DeviceUp = ok;
        }));
    }
}
