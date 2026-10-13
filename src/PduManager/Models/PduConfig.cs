namespace PduManager.Models;

public class PduConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 80;

    /// <summary>DPAPI-encrypted (CurrentUser) base64 blobs. Never plaintext.</summary>
    public string EncryptedUser { get; set; } = "";
    public string EncryptedPassword { get; set; } = "";

    /// <summary>Outlet index -> IP/hostname to ping.</summary>
    public Dictionary<int, string> Bindings { get; set; } = new();

    /// <summary>Outlet index -> true when "Auto Power-Off" is on (switch the outlet off when its bound device goes down).</summary>
    public Dictionary<int, bool> AutoPowerOff { get; set; } = new();
}

public class AppConfig
{
    public List<PduConfig> Pdus { get; set; } = new();
}

public record OutletInfo(int Index, string Name, bool? IsOn);

/// <summary>Values of the PDU's control form: op=0 ON, op=1 OFF, op=2 ON/OFF (cycle).</summary>
public enum OutletOp { On = 0, Off = 1, Cycle = 2 }

/// <summary>Live readings from status.xml.</summary>
public record PduStatus(string TotalCurrent, string Temperature, string Humidity, string StatusCode)
{
    public string StatusText => StatusCode switch
    {
        "normal" => "Normal",
        "curwarning" => "Current above warning value",
        "curoverload" => "Current above overload value",
        "volwarning" => "Voltage above warning value",
        "voloverload" => "Voltage above overload value",
        "tempwarning" => "Temperature outside warning range",
        "humwarning" => "Humidity above warning value",
        _ => StatusCode,
    };

    public bool IsNormal => StatusCode == "normal";
}

public record PduSnapshot(IReadOnlyList<OutletInfo> Outlets, PduStatus Status);

public record DeviceInfo(string Model, string Firmware);

/// <summary>One &lt;input&gt; of a PDU configuration page, so a form can be loaded, edited and posted back.</summary>
public class FormField
{
    public FormField(string name, string type = "text", string value = "", bool isChecked = false)
    {
        Name = name; Type = type; Value = value; Checked = isChecked;
    }

    public string Name { get; }
    public string Type { get; }
    public string Value { get; set; }
    public bool Checked { get; set; }
}
