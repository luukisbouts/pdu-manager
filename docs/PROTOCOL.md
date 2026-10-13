# ARGUS SW-0816 web interface protocol

Reverse-engineered from the PDU's own web pages (product model "IP PDU", firmware 1.0.6).
There is no documented API; the device serves a frameset and a few forms. All requests use
**HTTP Basic authentication** on port 80 (realm `IP PDU`). Pages are GB2312-encoded; names are plain ASCII in practice.
The server dislikes keep-alive and parallel requests (`Connection: close`, one request at a time).

## Pages

| Page | Purpose |
|---|---|
| `index.html` | frameset |
| `status.xml` | live readings and outlet states (polled by the pages with AJAX) |
| `control_outlet.htm` | outlet names + switching |
| `config_PDU.htm` | outlet names and ON/OFF delays |
| `config_threshold.htm` | warning / overload thresholds |
| `config_network.htm` | IP, mask, gateway, DHCP, DNS, host name |
| `config_user.htm` | change the administrator login |
| `info_system.htm` | model, firmware, MAC, system name, administrator, location |

## status.xml

A flat XML document; the pages read values with `getXMLValue(xml, 'tag')`. Tags used:

| Tag | Meaning |
|---|---|
| `outletStat0` … `outletStat7` | `on` / `off` |
| `curBan` | total load, A |
| `tempBan`, `humBan` | temperature (°C), humidity (%) |
| `statBan` | `normal`, `curwarning`, `curoverload`, `volwarning`, `voloverload`, `tempwarning`, `humwarning` |
| `userVerifyRes` | result of the last login change: `0` none, `1` changed, `2` wrong current login |

## Switching outlets

`GET /control_outlet.htm?outlet<N>=1[&outlet<M>=1…]&op=<op>&submit=Apply`

`op`: `0` ON, `1` OFF, `2` ON/OFF (power cycle, uses the configured delays).
Outlet names appear in the same page, one table row per outlet, next to `<span id="outletStat<N>">`.

## Configuration forms

Every configuration page is a plain HTML form that **POSTs (`application/x-www-form-urlencoded`) back to its own URL**.
Read the page, change the fields you want, and post **all** fields again.

| Page | Fields |
|---|---|
| `config_PDU.htm` | `otlt0..7` (name, max 11), `ondly0..7`, `ofdly0..7` (0–999 s) |
| `config_threshold.htm` | `wrncur`, `ovrcur` (A, 0–100), `wrnvol`, `ovrvol` (V), `wrntp1`, `wrntp2` (°C, 0–50), `wrnhum` (%, 20–90) |
| `config_network.htm` | `host` (max 14), `ip`, `mask`, `gate`, `dns1`, `dns2`, `dhcp` (checkbox, value `1`) |
| `info_system.htm` | `mac` (read-only), `sysnm`, `admin`, `loc` (max 15) |
| `config_user.htm` | `oldnm`, `oldpas`, `newnm`, `newpas`, `confirm` (user max 15, password max 11); check `userVerifyRes` afterwards |

Notes:

- With DHCP ticked, the web page disables the static address fields in JavaScript, so a browser does not submit them. The app does the same.
- Changing the IP address takes effect immediately and drops the connection; the next request must use the new address.
- After a successful login change the PDU wants the new credentials at once, even for the status request that verifies the change.
- The threshold form's inputs are named `wrntp1`/`wrntp2` but have ids `wrntmp1`/`wrntmp2`; the form submits the names.
