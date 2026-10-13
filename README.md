# PDU Manager

Manage network-switchable PDUs from your desktop or your browser: see every outlet and its state, switch outlets
on/off, watch the devices behind them with ping, and edit all PDU settings in one place.

Built for the **Inter-Tech ARGUS SW-0816** ("IP PDU", 8 outlets, firmware 1.0.x), which has no API, no SNMP and
only a basic-auth web page. The protocol is documented in [docs/PROTOCOL.md](docs/PROTOCOL.md), so support for
similar Chinese "IP PDU" firmwares should be a small driver change.

Two front-ends share the same driver and storage code:

| | Project | Output |
|---|---|---|
| **Desktop** (WPF) | `src/PduManager` | `PduManager.exe` |
| **Web** (ASP.NET Core, localhost) | `src/PduManager.Web` | `PduManagerWeb.exe` (opens `http://localhost:5080`) |

Both publish as a **single self-contained exe** (no .NET install needed). Windows only (credential encryption uses DPAPI).

## Download

Ready-to-run exes (64-bit Windows 10/11, nothing to install) are attached to the
[latest release](https://github.com/luukisbouts/Argus-PDU-Manager/releases/latest):

- `PduManager-Desktop-win-x64.exe` — desktop app
- `PduManager-Web-win-x64.exe` — web app (opens http://localhost:5080)

Windows SmartScreen may warn about an unknown publisher because the exes are not code-signed:
choose *More info → Run anyway*. Put the exe in a normal, writable folder; it saves `config.json` next to itself.

## Features

- Several PDUs in one list; per PDU: total load (A), temperature, humidity and the PDU's own alarm status
- All 8 outlets with name and **green ON / red OFF** state, refreshed every few seconds
- Switch an outlet **ON / OFF / power-cycle**, all outlets, or ticked outlets (always with a confirmation)
- **Device monitoring**: bind an IP/host to each outlet; it is pinged and shown as UP / DOWN
- **Auto Power-Off** per outlet: switches the outlet off when the bound device goes down (see below)
- Everything from the PDU's web interface, in a menu bar:
  - rename outlets, ON/OFF delays
  - threshold values (current, voltage, temperature, humidity)
  - network (IP, mask, gateway, DHCP, DNS), system name / administrator / location
  - change the PDU's own login
- **Edit PDU**: change name, address, port or re-enter the login (needed when the config is copied to another PC)
- PDU logins are stored **encrypted with Windows DPAPI**

### Auto Power-Off

Tick the box on an outlet that has a monitor IP. The outlet is switched off when the device **has answered pings and then fails
6 polls in a row (about 30 s)**. Safety rules:

- A device that was never seen up (e.g. still booting after power-on) is never cut.
- Failed pings do not count while the PDU itself is unreachable.
- Switching the outlet off by hand, or changing/clearing its monitor IP, resets the state.
- If the switch command fails it is retried every 30 s until the PDU reports the outlet OFF.
- It only works while the app is running. A device that merely loses its network connection is also cut.

## Build

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows.

```powershell
dotnet build

# single-file, self-contained exes
$opts = "-c","Release","-r","win-x64","--self-contained","true","-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true","-p:EnableCompressionInSingleFile=true","-p:DebugType=none"
dotnet publish src/PduManager/PduManager.csproj     @opts -o publish
dotnet publish src/PduManager.Web/PduManager.Web.csproj @opts -o publish-web
```

If NuGet has no package source configured, the included `nuget.config` points at nuget.org.

## Run

**Desktop:** start `PduManager.exe`, then *File → Add PDU…* (host, port 80, the PDU's username/password, *Test connection*).

**Web:** start `PduManagerWeb.exe`; it opens your browser. Options:

```
PduManagerWeb.exe --port 5080 --config C:\path\config.json --no-browser
```

The web server listens on **127.0.0.1 only and has no login of its own**. It also rejects requests from other
websites and unexpected `Host` headers (CSRF / DNS-rebinding protection). Do not expose it to a network without adding authentication first.

## Data and security

- Settings are saved in `config.json` **next to the exe** (PDU names, addresses, outlet → IP bindings, Auto Power-Off flags and the
  encrypted logins). The desktop and web app use separate files unless you point both at the same one with `--config`.
- Logins are encrypted with DPAPI (`CurrentUser`): only your Windows account on that PC can decrypt them. If you copy the app + `config.json`
  to another PC/user, the PDUs show *"stored login cannot be read"* — use **Edit PDU…** to enter the login again.
- The PDU speaks plain HTTP with Basic auth, so credentials cross your network unencrypted. Keep the PDUs on a trusted management network.
- `config.json` is git-ignored. Don't commit it.

## Project layout

```
src/PduManager/          WPF desktop app (MVVM, CommunityToolkit.Mvvm)
  Drivers/               IPduDriver, ArgusHttpDriver  (shared with the web project)
  Models/ Services/      PduConfig, CredentialStore (DPAPI), ConfigStore, AutoPowerOffMonitor (shared)
  ViewModels/ Views/     UI
src/PduManager.Web/      ASP.NET Core minimal API + embedded single-page UI (wwwroot/index.html)
docs/PROTOCOL.md        how the SW-0816 web interface works
```

## Limitations

- Only the ARGUS SW-0816 protocol is implemented (8 outlets).
- The PDU's HTTP server handles one request at a time, so the app serialises its requests per PDU.
- The "ON/OFF" operation (cycle) uses the PDU's own implementation and delays; test it on a non-critical outlet first.

## License

[MIT](LICENSE)

## Disclaimer

This software switches mains power. Use it at your own risk and test on non-critical equipment first.
