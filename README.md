# BlockRdpBruteForce

A free, native Windows service that detects RDP brute-force attempts in real time
and blocks the offending source IPs with a Windows Firewall rule. Ships with a
system-tray app for visibility and a same-binary CLI for scripting.

Built as an open-source alternative to RDPGuard.

## How it works

1. Subscribes to the Windows Security log via `EventLogWatcher` (push, no polling)
   for failed-logon event **4625** with `LogonType=10` (RemoteInteractive).
2. Falls back to event **140** on
   `Microsoft-Windows-RemoteDesktopServices-RdpCoreTS/Operational` for cases where
   NLA suppresses 4625.
3. Tracks failures per source IP in a sliding window. When an IP exceeds the
   threshold, it is added to a consolidated Windows Firewall block rule.
4. Bans expire after a configurable duration (default 24 h). A periodic scheduler
   removes expired entries from both the rule and the persisted state file.
5. State is persisted under `%ProgramData%\BlockRdpBruteForce\` so bans survive
   service restarts. An event-log bookmark is kept alongside it so the service
   replays from the last processed event after a restart.

## Requirements

- Windows 10 / 11 or Windows Server 2016+
- Administrator rights to install (the service runs as `LocalSystem`)
- "Audit Logon" failures enabled (the installer can enable this for you)
- .NET 10 SDK — only required if building from source. The published binaries are
  self-contained and have no runtime dependency.

## Install

Two options: an MSI for production deployment, and a PowerShell script for
in-tree iteration.

### Option 1: MSI installer (recommended)

Build the MSI once, then install from any elevated context (or push it via
Group Policy / Intune):

```powershell
.\installer\build-installer.ps1
msiexec /i installer\bin\Release\BlockRdpBruteForce.msi
```

The installer UI offers three features: service (required), tray app, and
"start tray at logon" (HKLM `Run`, covers all users). For unattended installs:

```powershell
msiexec /i installer\bin\Release\BlockRdpBruteForce.msi /qn
msiexec /i installer\bin\Release\BlockRdpBruteForce.msi /qn ENABLEAUDITPOLICY=0
```

| Property | Default | Purpose |
|---|---|---|
| `ENABLEAUDITPOLICY` | `1` | Run `auditpol /set` to enable Logon-failure auditing |

Major upgrades reuse the existing service and preserve `%ProgramData%\BlockRdpBruteForce\`.
Building the MSI requires the WiX 7 SDK, which is restored from NuGet on first
build (`AcceptEula=wix7` is set in the wixproj — see [WiX OSMF](https://docs.firegiant.com/wix/osmf/)).

### Option 2: PowerShell installer (dev workflow)

From an elevated PowerShell prompt:

```powershell
cd install
.\Install.ps1 -Build -EnableAuditPolicy -RegisterTrayAutostart
```

Common flags:

| Flag | Purpose |
|---|---|
| `-Build` | Run `dotnet publish` for both projects before installing |
| `-SourcePath <dir>` | Use already-published binaries instead of building |
| `-InstallPath <dir>` | Override deploy location (default `C:\Program Files\BlockRdpBruteForce`) |
| `-EnableAuditPolicy` | Enable failure auditing for the Logon subcategory |
| `-RegisterTrayAutostart` | Add the tray app to HKCU `Run` for the current user |
| `-DryRun` | Print actions without making changes |
| `-Force` | Bypass the self-lockout guard (see below) |

The script is safe to re-run: it stops the existing service, redeploys
binaries, and restarts.

### Self-lockout guard

`Install.ps1` refuses to install if `Whitelist` is empty **and**
`FailureThreshold < 3` — a single mistyped password could otherwise ban your
management subnet. Add at least one whitelist entry, raise the threshold, or
pass `-Force` to override. **The MSI does not enforce this guard**; ship the
MSI only with safe defaults baked into the cabbed `appsettings.json`.

## Configuration

Default config lives next to the service exe at `appsettings.json`. Override it
without redeploying by dropping a copy at
`%ProgramData%\BlockRdpBruteForce\appsettings.json` — both files are loaded and
the override wins.

```json
{
  "BlockRdp": {
    "FailureThreshold": 5,
    "SlidingWindowMinutes": 10,
    "BlockDurationMinutes": 1440,
    "Whitelist": [ "127.0.0.1", "::1", "10.0.0.0/8" ],
    "FirewallRuleName": "BlockRDPBruteForce",
    "FirewallScope": "AllPorts",
    "StateFilePath": "%ProgramData%\\BlockRdpBruteForce\\state.json",
    "LogPath":       "%ProgramData%\\BlockRdpBruteForce\\logs\\service-.log",
    "MaxRemoteAddressesPerRule": 1000,
    "EvaluateNlaFallback": true,
    "PipeName": "BlockRdpBruteForce"
  }
}
```

| Setting | Notes |
|---|---|
| `FailureThreshold` | Failures within the window before an IP is blocked |
| `SlidingWindowMinutes` | Window size used for the per-IP failure count |
| `BlockDurationMinutes` | Ban duration; `0` = permanent |
| `Whitelist` | IPv4/IPv6 single addresses **and** CIDR ranges |
| `FirewallScope` | `AllPorts` (block IP entirely) or `RdpOnly` (port 3389) |
| `MaxRemoteAddressesPerRule` | Past this size the rule is sharded into `-2`, `-3`, … siblings |
| `EvaluateNlaFallback` | Enables the RdpCoreTS event-140 subscription |

After editing the override, restart the service:

```powershell
Restart-Service BlockRdpBruteForce
```

## CLI

The service exe doubles as the CLI. Run it from `C:\Program Files\BlockRdpBruteForce\`
(or wherever you installed):

```powershell
BlockRdpBruteForce.exe status              # service health, blocked count, threshold
BlockRdpBruteForce.exe list                # blocked IPs with first/last seen + TTL
BlockRdpBruteForce.exe unblock 1.2.3.4     # remove an IP (admin only)
BlockRdpBruteForce.exe pause [minutes]     # pause blocking, default 60 min (admin only)
BlockRdpBruteForce.exe resume              # resume blocking (admin only)
```

The CLI talks to the service over a named pipe (`\\.\pipe\BlockRdpBruteForce`).
Read commands (`status`, `list`) are available to interactive users; mutating
commands require Administrators-group membership.

## Tray app

`BlockRdpBruteForce.Tray.exe` is a WinForms `NotifyIcon` that polls service
status every few seconds and offers:

- "Show blocked IPs…" — sortable dialog with manual unblock
- "Pause for 1 hour" / "Resume"
- "Open log folder"

Install with `-RegisterTrayAutostart` to launch it at logon, or run it manually
from the install directory.

## Logs

- Rolling daily files: `%ProgramData%\BlockRdpBruteForce\logs\service-*.log`
- Key state changes are also written to the Windows Application event log under
  source `BlockRdpBruteForce`.

Tail the live log:

```powershell
Get-Content "$env:ProgramData\BlockRdpBruteForce\logs\service-*.log" -Tail 30 -Wait
```

## Build from source

```powershell
dotnet build BlockRdpBruteForce.slnx
dotnet test  test\BlockRdpBruteForce.Tests
dotnet publish src\BlockRdpBruteForce      -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
dotnet publish src\BlockRdpBruteForce.Tray -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Or let the installer do it: `.\Install.ps1 -Build`.

## Project layout

```
BlockRdpBruteForce.slnx
src\
  BlockRdpBruteForce\          # Worker Service + CLI dispatcher (net10.0)
  BlockRdpBruteForce.Tray\     # WinForms tray app (net10.0-windows)
test\
  BlockRdpBruteForce.Tests\    # xUnit unit tests
install\
  Install.ps1                  # PS-based installer (dev workflow)
  Uninstall.ps1
installer\
  BlockRdpBruteForce.Installer.wixproj   # WiX 7 MSI project
  Package.wxs                            # MSI definition
  build-installer.ps1                    # publish + build wrapper
```

## Uninstall

If installed via MSI: remove from **Settings → Apps**, or:

```powershell
msiexec /x installer\bin\Release\BlockRdpBruteForce.msi /qn
```

The MSI removes the service, install directory, event-log source, tray
autostart, and the runtime-created `BlockRDPBruteForce-*` firewall rules.
State under `%ProgramData%\BlockRdpBruteForce\` is preserved.

If installed via `Install.ps1`:

```powershell
cd install
.\Uninstall.ps1
```

Removes the service, the install directory, the event-log source, and the tray
autostart entry. State under `%ProgramData%\BlockRdpBruteForce\` is **removed
by default** — pass `-KeepState` to preserve it for a later reinstall.

## Troubleshooting

- **No blocks happening.** Check that failure auditing is on:
  `auditpol /get /subcategory:"Logon"`. If RDP uses NLA and 4625 isn't firing,
  confirm `EvaluateNlaFallback: true` and watch for events on
  `Microsoft-Windows-RemoteDesktopServices-RdpCoreTS/Operational`.
- **You banned yourself.** Add the source IP to `Whitelist`, run
  `BlockRdpBruteForce.exe unblock <ip>` from another admin session, or remove
  the address from the `BlockRDPBruteForce` firewall rule via
  `Get-NetFirewallRule`.
- **Service starts but does nothing.** The startup log will warn if the
  audit-policy probe says failure auditing is disabled, or if the persisted
  event bookmark was invalidated (e.g. the Security log was cleared).
- **IPv6 traffic.** v4 and v6 are kept in separate sibling rules
  (`BlockRDPBruteForce-v4`, `-v6`) because the firewall's `RemoteAddresses`
  CSV doesn't mix families cleanly.

## License

MIT — see [LICENSE](LICENSE).
