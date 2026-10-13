using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using PduManager.Models;

namespace PduManager.Drivers;

/// <summary>
/// Inter-Tech ARGUS SW-0816 ("IP PDU") over HTTP Basic auth.
/// Names: scraped from control_outlet.htm. Readings and states: status.xml.
/// Switching: GET control_outlet.htm?outletN=1&amp;op=0|1|2&amp;submit=Apply
/// Configuration pages (config_*.htm, info_system.htm) are plain forms POSTed back to their own URL.
/// </summary>
public class ArgusHttpDriver : IPduDriver, IDisposable
{
    public const int OutletCount = 8;
    private readonly HttpClient _http;

    public ArgusHttpDriver(string host, int port, string user, string password)
    {
        _http = new HttpClient { BaseAddress = new Uri($"http://{host}:{port}/"), Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.Latin1.GetBytes($"{user}:{password}")));
        _http.DefaultRequestHeaders.ConnectionClose = true; // embedded web servers dislike keep-alive
    }

    private static async Task<string> ReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("PDU rejected the username/password.");
        resp.EnsureSuccessStatusCode();
        return Encoding.Latin1.GetString(await resp.Content.ReadAsByteArrayAsync(ct));
    }

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        return await ReadAsync(resp, ct);
    }

    private static string Tag(string xml, string name)
    {
        var m = Regex.Match(xml, $@"<{name}>\s*([^<]*?)\s*</{name}>", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    public async Task<PduSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var page = await GetAsync("control_outlet.htm", ct);
        var xml = await GetAsync("status.xml", ct);

        var names = new string[OutletCount];
        foreach (Match m in Regex.Matches(page,
                     @"<td[^>]*>\s*([^<]*?)\s*</td>\s*<td>\s*<span\s+id\s*=\s*""outletStat(\d)""", RegexOptions.IgnoreCase))
        {
            var i = int.Parse(m.Groups[2].Value);
            if (i < OutletCount) names[i] = WebUtility.HtmlDecode(m.Groups[1].Value);
        }

        var outlets = new List<OutletInfo>();
        for (var i = 0; i < OutletCount; i++)
        {
            var s = Tag(xml, $"outletStat{i}");
            bool? on = s.Length > 0 ? s.Equals("on", StringComparison.OrdinalIgnoreCase) : null;
            outlets.Add(new OutletInfo(i, string.IsNullOrWhiteSpace(names[i]) ? $"Outlet {i + 1}" : names[i], on));
        }

        var status = new PduStatus(Tag(xml, "curBan"), Tag(xml, "tempBan"), Tag(xml, "humBan"), Tag(xml, "statBan"));
        return new PduSnapshot(outlets, status);
    }

    public async Task ControlAsync(IEnumerable<int> outlets, OutletOp op, CancellationToken ct = default)
    {
        var parts = outlets.Distinct().OrderBy(i => i).Select(i =>
            i is >= 0 and < OutletCount ? $"outlet{i}=1" : throw new ArgumentOutOfRangeException(nameof(outlets))).ToList();
        if (parts.Count == 0) return;
        await GetAsync($"control_outlet.htm?{string.Join('&', parts)}&op={(int)op}&submit=Apply", ct);
    }

    public async Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default)
    {
        var page = await GetAsync("info_system.htm", ct);
        string Cell(string label)
        {
            var m = Regex.Match(page, label + @"\s*</strong></td>\s*<td[^>]*>\s*([^<]*?)\s*</td>", RegexOptions.IgnoreCase);
            return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : "";
        }
        return new DeviceInfo(Cell(@"Product\s+model"), Cell(@"Firmware\s+version"));
    }

    public async Task<List<FormField>> GetFormAsync(string page, CancellationToken ct = default)
    {
        var html = await GetAsync(page, ct);
        var fields = new List<FormField>();
        foreach (Match m in Regex.Matches(html, @"<input\b([^>]*?)/?>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var text = m.Groups[1].Value;
            var attrs = Regex.Matches(text, @"(\w+)\s*=\s*""([^""]*)""")
                .ToDictionary(a => a.Groups[1].Value.ToLowerInvariant(), a => a.Groups[2].Value, StringComparer.OrdinalIgnoreCase);
            if (!attrs.TryGetValue("name", out var name) || name.Length == 0) continue;
            var type = attrs.GetValueOrDefault("type", "text").ToLowerInvariant();
            if (type is "submit" or "button" or "image" or "reset") continue;
            var isChecked = Regex.IsMatch(Regex.Replace(text, @"""[^""]*""", ""), @"\bchecked\b", RegexOptions.IgnoreCase);
            fields.Add(new FormField(name, type, WebUtility.HtmlDecode(attrs.GetValueOrDefault("value", "")), isChecked));
        }
        return fields;
    }

    public async Task PostFormAsync(string page, IEnumerable<FormField> fields, CancellationToken ct = default)
    {
        var pairs = fields
            .Where(f => f.Type != "checkbox" || f.Checked)
            .Select(f => new KeyValuePair<string, string>(f.Name, f.Type == "checkbox" && f.Value.Length == 0 ? "1" : f.Value));
        using var resp = await _http.PostAsync(page, new FormUrlEncodedContent(pairs), ct);
        await ReadAsync(resp, ct);
    }

    /// <summary>Raw status.xml tag, e.g. userVerifyRes after a user/password change.</summary>
    public async Task<string> GetStatusTagAsync(string tag, CancellationToken ct = default) =>
        Tag(await GetAsync("status.xml", ct), tag);

    public void Dispose() => _http.Dispose();
}
