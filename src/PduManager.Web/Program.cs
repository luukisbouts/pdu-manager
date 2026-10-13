using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using PduManager.Drivers;
using PduManager.Models;
using PduManager.Services;
using PduManager.Web;

// Usage: PduManagerWeb.exe [--port 5080] [--config path\to\config.json] [--no-browser]
string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var port = int.TryParse(Arg("--port"), out var p) ? p : 5080;

var builder = WebApplication.CreateBuilder(args);
// Localhost only: the app has no login of its own.
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddSingleton(new PduRegistry(new ConfigStore(Arg("--config"))));
builder.Services.AddHostedService<PollingService>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNameCaseInsensitive = true);

var app = builder.Build();

// ---- Safety: JSON errors, and protection against other websites / DNS rebinding driving the local server ----
app.Use(async (ctx, next) =>
{
    try
    {
        var host = ctx.Request.Host.Host;
        if (host is not ("localhost" or "127.0.0.1" or "[::1]"))
        {
            ctx.Response.StatusCode = 403;
            return;
        }
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
        {
            var origin = ctx.Request.Headers.Origin.ToString();
            var sameOrigin = origin.Length == 0 ||
                (Uri.TryCreate(origin, UriKind.Absolute, out var o) && o.Host == host && o.Port == ctx.Request.Host.Port);
            // The custom header forces a CORS preflight for foreign sites, which we never answer.
            if (!sameOrigin || !ctx.Request.Headers.ContainsKey("X-PDU-Client"))
            {
                ctx.Response.StatusCode = 403;
                return;
            }
        }
        await next();
    }
    catch (ApiException ex)
    {
        ctx.Response.StatusCode = ex.Status;
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
    {
        ctx.Response.StatusCode = 409;
        await ctx.Response.WriteAsJsonAsync(new { error = CredentialStore.UnreadableHelp });
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UnauthorizedAccessException or IOException)
    {
        ctx.Response.StatusCode = 502;
        await ctx.Response.WriteAsJsonAsync(new { error = ex is TaskCanceledException ? "The PDU did not respond (timeout)." : ex.Message });
    }
});

// ---- UI (embedded index.html) ----
app.MapGet("/", () =>
{
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html")!;
    using var r = new StreamReader(s);
    return Results.Content(r.ReadToEnd(), "text/html; charset=utf-8");
});

// ---- API ----
var api = app.MapGroup("/api");

static object ToDto(PduRuntime p)
{
    var snap = p.Snapshot;
    return new
    {
        id = p.Config.Id,
        name = p.Config.Name,
        host = p.Config.Host,
        port = p.Config.Port,
        online = p.Error is null && snap is not null,
        error = p.Error,
        needsLogin = p.NeedsLogin,
        user = p.StoredUser, // never the password
        lastOk = p.LastOk,
        readings = snap is null ? null : new
        {
            current = snap.Status.TotalCurrent,
            temperature = snap.Status.Temperature,
            humidity = snap.Status.Humidity,
            status = snap.Status.StatusText,
            alarm = !snap.Status.IsNormal,
        },
        events = p.Events.Select(e => new { time = e.Time, text = e.Text }),
        outlets = Enumerable.Range(0, ArgusHttpDriver.OutletCount).Select(i => new
        {
            index = i,
            name = snap?.Outlets[i].Name ?? $"Outlet {i + 1}",
            isOn = snap?.Outlets[i].IsOn,
            boundIp = p.Config.Bindings.GetValueOrDefault(i, ""),
            deviceUp = p.Up.TryGetValue(i, out var up) ? (bool?)up : null,
            autoOff = p.Config.AutoPowerOff.GetValueOrDefault(i),
            armed = p.AutoOff.IsArmed(i),
        }),
    };
}

api.MapGet("/pdus", (PduRegistry reg) => reg.All().Select(ToDto));

api.MapPost("/pdus/test", async (AddReq req, PduRegistry reg) =>
{
    var (host, port) = CheckAddress(req);
    var (user, pass) = ResolveLogin(req, req.Id is { } id ? reg.Get(id) : null);
    using var driver = new ArgusHttpDriver(host, port, user, pass);
    var snap = await driver.GetSnapshotAsync();
    return Results.Ok(new { outlets = snap.Outlets.Count });
});

// Edit PDU: name, address, port, login. Empty username/password keep the stored value (if it is readable here).
api.MapPut("/pdus/{id:guid}", async (Guid id, AddReq req, PduRegistry reg) =>
{
    var p = reg.Get(id);
    var (host, port) = CheckAddress(req);
    if (string.IsNullOrWhiteSpace(req.Name)) throw new ApiException("Enter a name.");
    var (user, pass) = ResolveLogin(req, p);
    await p.UpdateSettingsAsync(req.Name.Trim(), host, port, user, pass);
    await p.RefreshAsync();
    return Results.Ok(ToDto(p));
});

api.MapPost("/pdus", async (AddReq req, PduRegistry reg) =>
{
    var (host, port) = CheckAddress(req);
    if (string.IsNullOrWhiteSpace(req.Name)) throw new ApiException("Enter a name.");
    var (user, pass) = ResolveLogin(req, null);
    var p = reg.Add(new PduConfig
    {
        Name = req.Name.Trim(), Host = host, Port = port,
        EncryptedUser = CredentialStore.Protect(user),
        EncryptedPassword = CredentialStore.Protect(pass),
    });
    await p.RefreshAsync();
    return Results.Ok(ToDto(p));
});

api.MapDelete("/pdus/{id:guid}", (Guid id, PduRegistry reg) => { reg.Remove(id); return Results.NoContent(); });

api.MapPut("/pdus/{id:guid}/bindings/{index:int}", (Guid id, int index, BindReq req, PduRegistry reg) =>
{
    if (index is < 0 or >= ArgusHttpDriver.OutletCount) throw new ApiException("Invalid outlet.");
    var ip = req.Ip?.Trim();
    if (!string.IsNullOrEmpty(ip) && !FormRules.IsValidHostOrIp(ip)) throw new ApiException("Enter a valid IP address or host name.");
    reg.Get(id).SetBinding(index, ip);
    _ = reg.Get(id).PingAllAsync();
    return Results.NoContent();
});

api.MapPut("/pdus/{id:guid}/autooff/{index:int}", (Guid id, int index, AutoOffReq req, PduRegistry reg) =>
{
    if (index is < 0 or >= ArgusHttpDriver.OutletCount) throw new ApiException("Invalid outlet.");
    reg.Get(id).SetAutoPowerOff(index, req.Enabled);
    return Results.NoContent();
});

api.MapPost("/pdus/{id:guid}/control",async (Guid id, ControlReq req, PduRegistry reg) =>
{
    var p = reg.Get(id);
    if (!Enum.TryParse<OutletOp>(req.Op, true, out var op) || !Enum.IsDefined(op)) throw new ApiException("Invalid operation.");
    var outlets = (req.Outlets ?? []).Distinct().ToArray();
    if (outlets.Length == 0 || outlets.Any(i => i is < 0 or >= ArgusHttpDriver.OutletCount)) throw new ApiException("Invalid outlets.");
    await p.WithDriverAsync(async d => { await d.ControlAsync(outlets, op); return 0; });
    await Task.Delay(500); // let the PDU apply the change
    await p.RefreshAsync();
    return Results.Ok(ToDto(p));
});

api.MapGet("/pdus/{id:guid}/forms/{kind}", async (Guid id, string kind, PduRegistry reg) =>
{
    var p = reg.Get(id);
    if (!FormRules.Pages.TryGetValue(kind, out var def)) throw new ApiException("Unknown settings page.", 404);
    return await p.WithDriverAsync(async d =>
    {
        var fields = await d.GetFormAsync(def.Page);
        var values = fields.Where(f => def.Allowed.Contains(f.Name))
            .ToDictionary(f => f.Name, f => f.Type == "checkbox" ? (object)f.Checked : f.Value);
        object? info = null;
        if (kind == "system")
        {
            var di = await d.GetDeviceInfoAsync();
            info = new { model = di.Model, firmware = di.Firmware, mac = fields.FirstOrDefault(f => f.Name == "mac")?.Value ?? "" };
        }
        return Results.Ok(new { values, info });
    });
});

api.MapPost("/pdus/{id:guid}/forms/{kind}", async (Guid id, string kind, FormReq req, PduRegistry reg) =>
{
    var p = reg.Get(id);
    if (!FormRules.Pages.TryGetValue(kind, out var def)) throw new ApiException("Unknown settings page.", 404);
    var input = req.Values ?? new();
    foreach (var name in input.Keys)
        if (!def.Allowed.Contains(name)) throw new ApiException($"Field '{name}' is not allowed.");

    bool dhcp = kind == "network" && input.TryGetValue("dhcp", out var dv) && dv.ValueKind == JsonValueKind.True;
    foreach (var (name, el) in input)
    {
        if (name == "dhcp") continue;
        if (dhcp && FormRules.StaticIpFields.Contains(name)) continue; // not sent under DHCP
        var err = FormRules.Validate(name, Text(el));
        if (err is not null) throw new ApiException(err);
    }

    string? newIp = null;
    await p.WithDriverAsync(async d =>
    {
        var fields = await d.GetFormAsync(def.Page);
        FormField Field(string n)
        {
            var f = fields.FirstOrDefault(x => x.Name == n);
            if (f is null) fields.Add(f = new FormField(n, n == "dhcp" ? "checkbox" : "text", n == "dhcp" ? "1" : ""));
            return f;
        }
        foreach (var (name, el) in input)
        {
            var f = Field(name);
            if (f.Type == "checkbox") f.Checked = el.ValueKind == JsonValueKind.True;
            else f.Value = Text(el);
        }
        if (kind == "network")
        {
            // The PDU's own web page disables (and so does not submit) the static fields while DHCP is on.
            if (dhcp) fields.RemoveAll(f => FormRules.StaticIpFields.Contains(f.Name));
            else newIp = Field("ip").Value;
        }
        try { await d.PostFormAsync(def.Page, fields); }
        catch (Exception ex) when (kind == "network" && ex is HttpRequestException or TaskCanceledException)
        {
            // The PDU can drop the connection while re-applying its network settings.
        }
        return 0;
    });
    if (newIp is { Length: > 0 } && newIp != p.Config.Host) p.UpdateHost(newIp);
    await p.RefreshAsync();
    return Results.NoContent();
});

api.MapPost("/pdus/{id:guid}/login", async (Guid id, LoginReq req, PduRegistry reg) =>
{
    var p = reg.Get(id);
    if (string.IsNullOrEmpty(req.OldUser) || string.IsNullOrEmpty(req.NewUser)) throw new ApiException("Enter the current and the new user name.");
    if (req.NewUser.Length > 15 || (req.NewPass ?? "").Length > 11) throw new ApiException("User name max 15, password max 11 characters.");
    var oldUser = CredentialStore.Unprotect(p.Config.EncryptedUser);
    var oldPass = CredentialStore.Unprotect(p.Config.EncryptedPassword);
    var form = new List<FormField>
    {
        new("oldnm", "text", req.OldUser), new("oldpas", "password", req.OldPass ?? ""),
        new("newnm", "text", req.NewUser), new("newpas", "password", req.NewPass ?? ""),
        new("confirm", "password", req.NewPass ?? ""),
    };
    string res;
    await p.Gate.WaitAsync();
    try
    {
        await p.Driver.PostFormAsync("config_user.htm", form);
        // On success the PDU immediately wants the new login; on failure the old one still works.
        p.UpdateCredentials(req.NewUser, req.NewPass ?? "");
        try { res = await p.Driver.GetStatusTagAsync("userVerifyRes"); }
        catch (UnauthorizedAccessException)
        {
            p.UpdateCredentials(oldUser, oldPass);
            res = await p.Driver.GetStatusTagAsync("userVerifyRes");
        }
    }
    finally { p.Gate.Release(); }
    if (res == "2") throw new ApiException("The PDU rejected the current user name or password.");
    if (res != "1") throw new ApiException("The PDU did not confirm the change. Check the connection.");
    await p.RefreshAsync();
    return Results.NoContent();
});

// ---- start ----
var url = $"http://localhost:{port}/";
Console.WriteLine($"PDU Manager (web) running at {url}\nLocalhost only, no login. Close this window to stop.");
if (!args.Contains("--no-browser"))
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    });
app.Run();

static (string Host, int Port) CheckAddress(AddReq req)
{
    var host = req.Host?.Trim() ?? "";
    if (!FormRules.IsValidHostOrIp(host)) throw new ApiException("Enter a valid host or IP address.");
    if (req.Port is < 1 or > 65535) throw new ApiException("Enter a valid port.");
    return (host, req.Port);
}

/// <summary>Typed login wins; an empty field falls back to the stored value when it can be decrypted here.</summary>
static (string User, string Pass) ResolveLogin(AddReq req, PduRuntime? existing)
{
    var user = req.Username ?? "";
    var pass = req.Password ?? "";
    if (existing is not null)
    {
        if (user.Length == 0) user = existing.StoredUser;
        if (pass.Length == 0) pass = existing.StoredPassword;
    }
    if (user.Length == 0 || pass.Length == 0) throw new ApiException("Enter the username and password.");
    return (user, pass);
}

static string Text(JsonElement el) => el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.GetRawText();

record AddReq(string? Name, string? Host, int Port, string? Username, string? Password, Guid? Id = null);
record ControlReq(int[]? Outlets, string? Op);
record BindReq(string? Ip);
record AutoOffReq(bool Enabled);
record FormReq(Dictionary<string, JsonElement>? Values);
record LoginReq(string? OldUser, string? OldPass, string? NewUser, string? NewPass);
