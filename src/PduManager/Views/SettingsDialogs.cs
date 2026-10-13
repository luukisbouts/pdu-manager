using System.Net.Http;
using System.Windows;
using PduManager.Models;
using PduManager.ViewModels;

namespace PduManager.Views;

/// <summary>The PDU web interface's configuration pages, as dialogs.</summary>
public static class SettingsDialogs
{
    private static FormField Get(List<FormField> fields, string name)
    {
        var f = fields.FirstOrDefault(x => x.Name == name);
        if (f is null) fields.Add(f = new FormField(name));
        return f;
    }

    private static FormRow Text(List<FormField> fields, string name, string label, int maxLength = 0, double? min = null, double? max = null) =>
        new() { Name = name, Label = label, Value = Get(fields, name).Value, MaxLength = maxLength, Min = min, Max = max };

    private static void Sync(List<FormRow> rows, List<FormField> fields)
    {
        foreach (var r in rows.Where(r => r.Name.Length > 0 && r.Kind != RowKind.ReadOnly))
        {
            var f = Get(fields, r.Name);
            if (r.Kind == RowKind.Check) f.Checked = r.Checked; else f.Value = r.Value;
        }
    }

    private static void Error(Exception ex) =>
        MessageBox.Show(ex.Message, "PDU Manager", MessageBoxButton.OK, MessageBoxImage.Error);

    /// <summary>info_system.htm: system name, administrator, location (+ read-only model/firmware/MAC).</summary>
    public static async Task SystemAsync(PduVm pdu)
    {
        try
        {
            var fields = await pdu.Driver.GetFormAsync("info_system.htm");
            var info = await pdu.Driver.GetDeviceInfoAsync();
            var rows = new List<FormRow>
            {
                new() { Label = "Product model", Kind = RowKind.ReadOnly, Value = info.Model },
                new() { Label = "Firmware version", Kind = RowKind.ReadOnly, Value = info.Firmware },
                new() { Label = "MAC address", Kind = RowKind.ReadOnly, Value = Get(fields, "mac").Value },
                Text(fields, "sysnm", "System name", 15),
                Text(fields, "admin", "Administrator", 15),
                Text(fields, "loc", "System location", 15),
            };
            var dlg = new FormWindow($"System information — {pdu.Name}", null, rows, async r =>
            {
                Sync(r, fields);
                await pdu.Driver.PostFormAsync("info_system.htm", fields);
                return null;
            });
            dlg.ShowDialog();
        }
        catch (Exception ex) { Error(ex); }
    }

    /// <summary>config_threshold.htm: current/voltage/temperature/humidity warning levels.</summary>
    public static async Task ThresholdsAsync(PduVm pdu)
    {
        try
        {
            var fields = await pdu.Driver.GetFormAsync("config_threshold.htm");
            var rows = new List<FormRow>
            {
                Text(fields, "wrncur", "Current warning (A)", 6, 0, 100),
                Text(fields, "ovrcur", "Current overload (A)", 6, 0, 100),
                Text(fields, "wrnvol", "Voltage warning (V)", 6, 0),
                Text(fields, "ovrvol", "Voltage overload (V)", 6, 0),
                Text(fields, "wrntp1", "Temperature under (°C)", 3, 0, 50),
                Text(fields, "wrntp2", "Temperature above (°C)", 3, 0, 50),
                Text(fields, "wrnhum", "Humidity warning (%)", 3, 20, 90),
            };
            var dlg = new FormWindow($"Threshold values — {pdu.Name}", null, rows, async r =>
            {
                Sync(r, fields);
                await pdu.Driver.PostFormAsync("config_threshold.htm", fields);
                return null;
            });
            if (dlg.ShowDialog() == true) await pdu.RefreshAsync();
        }
        catch (Exception ex) { Error(ex); }
    }

    /// <summary>config_network.htm: host name, IP, mask, gateway, DHCP, DNS.</summary>
    public static async Task NetworkAsync(PduVm pdu)
    {
        try
        {
            var fields = await pdu.Driver.GetFormAsync("config_network.htm");
            var rows = new List<FormRow>
            {
                Text(fields, "host", "Host name", 14),
                new() { Name = "dhcp", Label = "Enable DHCP", Kind = RowKind.Check, Checked = Get(fields, "dhcp").Checked },
                Text(fields, "ip", "IP address"),
                Text(fields, "mask", "Subnet mask"),
                Text(fields, "gate", "Gateway"),
                Text(fields, "dns1", "Primary DNS"),
                Text(fields, "dns2", "Secondary DNS"),
            };
            foreach (var n in new[] { "ip", "mask", "gate", "dns1", "dns2" })
            {
                var i = rows.FindIndex(r => r.Name == n);
                rows[i] = new FormRow { Name = n, Label = rows[i].Label, Value = rows[i].Value, DisabledWhenChecked = "dhcp" };
            }

            var oldIp = Get(fields, "ip").Value;
            var dlg = new FormWindow($"Network settings — {pdu.Name}",
                "Wrong values can make the PDU unreachable. A changed IP address takes effect on the PDU immediately.",
                rows, async r =>
                {
                    Sync(r, fields);
                    var dhcp = Get(fields, "dhcp").Checked;
                    // The web UI disables (and so does not submit) the static fields while DHCP is on.
                    var post = dhcp
                        ? fields.Where(f => f.Name is not ("ip" or "mask" or "gate" or "dns1" or "dns2")).ToList()
                        : fields;
                    try { await pdu.Driver.PostFormAsync("config_network.htm", post); }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                    {
                        // The PDU may drop the connection while re-applying its network settings.
                    }
                    var newIp = Get(fields, "ip").Value;
                    if (!dhcp && newIp.Length > 0 && newIp != oldIp) pdu.UpdateHost(newIp);
                    return null;
                });
            if (dlg.ShowDialog() == true) await pdu.RefreshAsync();
        }
        catch (Exception ex) { Error(ex); }
    }

    /// <summary>config_user.htm: change the PDU's login (also updates the encrypted copy stored here).</summary>
    public static async Task UserAsync(PduVm pdu)
    {
        var rows = new List<FormRow>
        {
            new() { Name = "oldnm", Label = "Current user name", MaxLength = 15 },
            new() { Name = "oldpas", Label = "Current password", Kind = RowKind.Password, MaxLength = 11 },
            new() { Name = "newnm", Label = "New user name", MaxLength = 15 },
            new() { Name = "newpas", Label = "New password", Kind = RowKind.Password, MaxLength = 11 },
            new() { Name = "confirm", Label = "Confirm password", Kind = RowKind.Password, MaxLength = 11 },
        };
        var dlg = new FormWindow($"Change PDU login — {pdu.Name}",
            "Changes the user name and password of the PDU's web interface (password max 11 characters). The stored credentials in this app are updated automatically.",
            rows, async r =>
            {
                string V(string n) => r.First(x => x.Name == n).Value;
                if (V("oldnm").Length == 0 || V("newnm").Length == 0) return "Enter the current and the new user name.";
                if (V("newpas") != V("confirm")) return "The new passwords do not match.";

                var fields = r.Select(x => new FormField(x.Name, x.Kind == RowKind.Password ? "password" : "text", x.Value)).ToList();
                var oldUser = Services.CredentialStore.Unprotect(pdu.Config.EncryptedUser);
                var oldPass = Services.CredentialStore.Unprotect(pdu.Config.EncryptedPassword);
                await pdu.Driver.PostFormAsync("config_user.htm", fields);

                // On success the PDU immediately wants the new login; on failure the old one still works.
                pdu.UpdateCredentials(V("newnm"), V("newpas"));
                string res;
                try { res = await pdu.Driver.GetStatusTagAsync("userVerifyRes"); }
                catch (UnauthorizedAccessException)
                {
                    pdu.UpdateCredentials(oldUser, oldPass);
                    res = await pdu.Driver.GetStatusTagAsync("userVerifyRes");
                }
                if (res == "1") return null;
                if (res == "2") return "The PDU rejected the current user name or password.";
                return "The PDU did not confirm the change. Check Test connection with the new login.";
            });
        if (dlg.ShowDialog() == true) await pdu.RefreshAsync();
    }

    /// <summary>config_PDU.htm: outlet names and ON/OFF delays.</summary>
    public static async Task OutletsAsync(PduVm pdu)
    {
        try
        {
            var fields = await pdu.Driver.GetFormAsync("config_PDU.htm");
            var dlg = new OutletSettingsWindow(pdu, fields);
            if (dlg.ShowDialog() == true) await pdu.RefreshAsync();
        }
        catch (Exception ex) { Error(ex); }
    }
}
