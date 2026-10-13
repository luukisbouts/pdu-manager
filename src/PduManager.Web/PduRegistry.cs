using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using PduManager.Drivers;
using PduManager.Models;
using PduManager.Services;

namespace PduManager.Web;

public class ApiException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>One configured PDU plus its live state. All PDU HTTP calls go through <see cref="Gate"/> (the PDU copes badly with parallel requests).</summary>
public class PduRuntime(PduConfig config, Action save)
{
    private ArgusHttpDriver? _driver;

    public PduConfig Config { get; } = config;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public PduSnapshot? Snapshot { get; private set; }
    public string? Error { get; private set; }
    public DateTime? LastOk { get; private set; }
    public ConcurrentDictionary<int, bool> Up { get; } = new();

    public ArgusHttpDriver Driver => _driver ??= new ArgusHttpDriver(
        Config.Host, Config.Port,
        CredentialStore.Unprotect(Config.EncryptedUser),
        CredentialStore.Unprotect(Config.EncryptedPassword));

    public void ResetDriver() { _driver?.Dispose(); _driver = null; }

    public void UpdateCredentials(string user, string password)
    {
        Config.EncryptedUser = CredentialStore.Protect(user);
        Config.EncryptedPassword = CredentialStore.Protect(password);
        save();
        ResetDriver();
    }

    /// <summary>True when the stored login was saved by another Windows user/computer and can't be decrypted here.</summary>
    public bool NeedsLogin { get; private set; }

    public bool CanReadStoredUser => CredentialStore.TryUnprotect(Config.EncryptedUser, out _);
    public bool CanReadStoredPassword => CredentialStore.TryUnprotect(Config.EncryptedPassword, out _);

    public string StoredUser => CredentialStore.TryUnprotect(Config.EncryptedUser, out var u) ? u : "";
    public string StoredPassword => CredentialStore.TryUnprotect(Config.EncryptedPassword, out var p) ? p : "";

    /// <summary>Edit PDU: name, address, port and (optionally) the login.</summary>
    public async Task UpdateSettingsAsync(string name, string host, int port, string user, string password)
    {
        await Gate.WaitAsync();
        try
        {
            Config.Name = name;
            Config.Host = host;
            Config.Port = port;
            Config.EncryptedUser = CredentialStore.Protect(user);
            Config.EncryptedPassword = CredentialStore.Protect(password);
            save();
            ResetDriver();
            NeedsLogin = false;
        }
        finally { Gate.Release(); }
    }

    public void UpdateHost(string host)
    {
        Config.Host = host;
        save();
        ResetDriver();
    }

    public AutoPowerOffMonitor AutoOff { get; } = new();
    private readonly List<(DateTime Time, string Text)> _events = new();

    /// <summary>Most recent automatic actions, newest first.</summary>
    public IReadOnlyList<(DateTime Time, string Text)> Events
    {
        get { lock (_events) return _events.AsEnumerable().Reverse().Take(5).ToList(); }
    }

    private void AddEvent(string text)
    {
        lock (_events) { _events.Add((DateTime.Now, text)); if (_events.Count > 50) _events.RemoveAt(0); }
    }

    public void SetBinding(int index, string? ip)
    {
        var trimmed = ip?.Trim() ?? "";
        if (Config.Bindings.GetValueOrDefault(index, "") == trimmed) return;
        if (trimmed.Length == 0)
        {
            Config.Bindings.Remove(index);
            Config.AutoPowerOff.Remove(index); // auto power-off needs a monitored device
        }
        else Config.Bindings[index] = trimmed;
        Up.TryRemove(index, out _);   // old ping result belongs to the old address
        AutoOff.Reset(index);
        save();
    }

    public void SetAutoPowerOff(int index, bool enabled)
    {
        if (enabled && !Config.Bindings.ContainsKey(index))
            throw new ApiException("Set a monitor IP for this outlet first.");
        if (enabled) Config.AutoPowerOff[index] = true; else Config.AutoPowerOff.Remove(index);
        save();
    }

    /// <summary>Switches off outlets whose Auto Power-Off device has stopped answering pings.</summary>
    public async Task AutoPowerOffAsync()
    {
        var snap = Snapshot;
        for (var i = 0; i < ArgusHttpDriver.OutletCount; i++)
        {
            bool? on = snap?.Outlets[i].IsOn;
            bool? up = Up.TryGetValue(i, out var u) ? u : null;
            if (!AutoOff.Evaluate(i, Config.AutoPowerOff.GetValueOrDefault(i), Config.Bindings.ContainsKey(i),
                    on, Error is null && snap is not null, up))
                continue;

            var name = snap!.Outlets[i].Name;
            try
            {
                await WithDriverAsync(async d => { await d.ControlAsync(new[] { i }, OutletOp.Off); return 0; });
                AddEvent($"Auto power-off: '{name}' switched off — {Config.Bindings[i]} stopped responding.");
                await Task.Delay(500);
                await RefreshAsync();
            }
            catch (Exception ex) { AddEvent($"Auto power-off of '{name}' FAILED: {ex.Message}"); }
        }
    }

    /// <summary>Runs <paramref name="work"/> with exclusive access to the PDU.</summary>
    public async Task<T> WithDriverAsync<T>(Func<ArgusHttpDriver, Task<T>> work)
    {
        await Gate.WaitAsync();
        try { return await work(Driver); }
        finally { Gate.Release(); }
    }

    public async Task RefreshAsync()
    {
        await Gate.WaitAsync();
        try
        {
            Snapshot = await Driver.GetSnapshotAsync();
            Error = null;
            NeedsLogin = false;
            LastOk = DateTime.Now;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            Error = CredentialStore.UnreadableHelp;
            NeedsLogin = true;
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Gate.Release(); }
    }

    public async Task PingAllAsync()
    {
        var bindings = Config.Bindings.ToArray();
        await Task.WhenAll(bindings.Select(async b =>
        {
            bool ok;
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(b.Value, 1500);
                ok = reply.Status == IPStatus.Success;
            }
            catch { ok = false; }
            // Ignore the result if the outlet was re-bound while the ping was in flight.
            if (Config.Bindings.GetValueOrDefault(b.Key) == b.Value) Up[b.Key] = ok;
        }));
    }
}

public class PduRegistry
{
    private readonly ConfigStore _store;
    private readonly AppConfig _config;
    private readonly object _sync = new();
    private readonly List<PduRuntime> _pdus;

    public PduRegistry(ConfigStore store)
    {
        _store = store;
        _config = store.Load();
        _pdus = _config.Pdus.Select(c => new PduRuntime(c, Save)).ToList();
    }

    public IReadOnlyList<PduRuntime> All() { lock (_sync) return _pdus.ToList(); }

    public PduRuntime Get(Guid id) =>
        All().FirstOrDefault(p => p.Config.Id == id) ?? throw new ApiException("PDU not found.", 404);

    public PduRuntime Add(PduConfig cfg)
    {
        lock (_sync)
        {
            _config.Pdus.Add(cfg);
            var p = new PduRuntime(cfg, Save);
            _pdus.Add(p);
            _store.Save(_config);
            return p;
        }
    }

    public void Remove(Guid id)
    {
        lock (_sync)
        {
            var p = Get(id);
            _pdus.Remove(p);
            _config.Pdus.Remove(p.Config);
            _store.Save(_config);
        }
    }

    public void Save() { lock (_sync) _store.Save(_config); }
}

/// <summary>Refreshes every PDU and pings all bound IPs every 5 seconds.</summary>
public class PollingService(PduRegistry registry) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                await Task.WhenAll(registry.All().Select(async p =>
                {
                    await Task.WhenAll(p.RefreshAsync(), p.PingAllAsync());
                    await p.AutoPowerOffAsync();
                }));
            }
            catch { /* individual failures are recorded per PDU */ }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
