using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace PduManager.Web;

/// <summary>Which PDU configuration pages can be edited, which fields they accept, and how those are validated.</summary>
public static partial class FormRules
{
    public static readonly Dictionary<string, (string Page, HashSet<string> Allowed)> Pages = new()
    {
        ["system"] = ("info_system.htm", new() { "sysnm", "admin", "loc" }),
        ["thresholds"] = ("config_threshold.htm", new() { "wrncur", "ovrcur", "wrnvol", "ovrvol", "wrntp1", "wrntp2", "wrnhum" }),
        ["network"] = ("config_network.htm", new() { "host", "dhcp", "ip", "mask", "gate", "dns1", "dns2" }),
        ["outlets"] = ("config_PDU.htm", Enumerable.Range(0, 8).SelectMany(i => new[] { $"otlt{i}", $"ondly{i}", $"ofdly{i}" }).ToHashSet()),
    };

    public static readonly string[] StaticIpFields = { "ip", "mask", "gate", "dns1", "dns2" };

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,253}$")]
    private static partial Regex HostRegex();

    public static bool IsValidHostOrIp(string s) => HostRegex().IsMatch(s);

    /// <summary>Returns an error message, or null if <paramref name="value"/> is acceptable for <paramref name="name"/>.</summary>
    public static string? Validate(string name, string value)
    {
        switch (name)
        {
            case "wrncur" or "ovrcur": return Range(name, value, 0, 100);
            case "wrnvol" or "ovrvol": return Range(name, value, 0, 1000);
            case "wrntp1" or "wrntp2": return Range(name, value, 0, 50);
            case "wrnhum": return Range(name, value, 20, 90);
            case "host": return value.Length is >= 1 and <= 14 ? null : "Host name must be 1–14 characters.";
            case "sysnm" or "admin" or "loc": return value.Length <= 15 && Ascii(value) ? null : $"'{name}' must be at most 15 plain ASCII characters.";
            case "ip" or "mask" or "gate" or "dns1" or "dns2":
                return IPAddress.TryParse(value, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && value.Count(c => c == '.') == 3
                    ? null : $"'{name}' must be a valid IPv4 address.";
        }
        if (name.StartsWith("otlt"))
            return value.Length is >= 1 and <= 11 && Ascii(value) ? null : "Outlet names must be 1–11 plain ASCII characters.";
        if (name.StartsWith("ondly") || name.StartsWith("ofdly"))
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d <= 999 ? null : "Delays must be whole seconds, 0–999.";
        return null;
    }

    private static bool Ascii(string s) => s.All(c => c is >= ' ' and <= '~');

    private static string? Range(string name, string value, double min, double max) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max
            ? null : $"'{name}' must be a number between {min} and {max}.";
}
