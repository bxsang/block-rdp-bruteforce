# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Windows-only RDP brute-force blocker: a .NET 10 Worker Service that subscribes
to Windows event logs, counts failed RDP logons per source IP, and adds offenders
to a Windows Firewall block rule. Ships with a WinForms tray app and a
same-binary CLI. See `README.md` for user-facing docs.

## Common commands

The solution file is `BlockRdpBruteForce.slnx` (the new XML solution format) —
not a `.sln`. Most `dotnet` commands accept it directly.

```powershell
# Build
dotnet build BlockRdpBruteForce.slnx

# Run all tests
dotnet test test\BlockRdpBruteForce.Tests

# Run a single test class or method
dotnet test test\BlockRdpBruteForce.Tests --filter "FullyQualifiedName~FailureTrackerTests"
dotnet test test\BlockRdpBruteForce.Tests --filter "FullyQualifiedName~EventXmlParserTests.Parses_Ipv6_Address"

# Publish self-contained single-file binaries (what Install.ps1 ships by default).
# Both Install.ps1 and build-installer.ps1 also accept -FrameworkDependent, which
# swaps --self-contained for --no-self-contained; the target box must then have
# the .NET 10 Desktop Runtime installed.
dotnet publish src\BlockRdpBruteForce         -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
dotnet publish src\BlockRdpBruteForce.Tray    -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
dotnet publish src\BlockRdpBruteForce.Updater -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

# Build + install + start the service (elevated PowerShell)
.\install\Install.ps1 -Build -EnableAuditPolicy -RegisterTrayAutostart

# Build the MSI installer (WiX 7) -- outputs installer\bin\Release\BlockRdpBruteForce.msi
.\installer\build-installer.ps1
msiexec /i installer\bin\Release\BlockRdpBruteForce.msi

# Iterate without reinstalling: rebuild, then bounce the service
dotnet publish src\BlockRdpBruteForce -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
Stop-Service BlockRdpBruteForce
Copy-Item src\BlockRdpBruteForce\bin\Release\net10.0\win-x64\publish\* "C:\Program Files\BlockRdpBruteForce\" -Force
Start-Service BlockRdpBruteForce
Get-Content "$env:ProgramData\BlockRdpBruteForce\logs\service-*.log" -Tail 30 -Wait
```

Running the service directly (`dotnet run`) requires an elevated prompt — it
needs `HNetCfg.FwPolicy2` COM access and Security-log subscription rights.

## Big-picture architecture

### Single binary, two modes

`src\BlockRdpBruteForce\Program.cs` checks `CliDispatcher.IsCliInvocation(args)`
**first**. If `args[0]` is one of `status | list | unblock | pause | resume`,
the exe runs as a pipe client and exits. Otherwise it boots the Worker Service
host. The same exe is therefore both the service and the CLI — there is no
separate CLI project.

`src\BlockRdpBruteForce.Tray\` is a separate WinForms exe (`net10.0-windows`)
that talks to the running service over the same named pipe via a project
reference to the service assembly (it reuses `Ipc\PipeProtocol.cs`).

`src\BlockRdpBruteForce.Updater\` is a third WinForms exe (`net10.0-windows`)
used only during auto-update. The service stages a copy of it into
`%ProgramData%\BlockRdpBruteForce\updates\stage\` and launches it in the
user's session with the linked elevated token; the updater downloads the MSI
with progress UI, runs `msiexec /quiet`, and shows the result. It runs out
of ProgramData so the binary swap during MSI install doesn't kill it. No
project reference back to the service: it's standalone, talks via CLI args
and writes `update-applying.json` to coordinate.

### Event flow (push-based, no polling)

```
EventLogWatcher  ─┐
  Security/4625   │
                  ├──►  Channel<FailedLogon>  ──►  single consumer  ──►  FailureTracker
EventLogWatcher  ─┘     (bounded 1024,                                       │ threshold
  RdpCoreTS/140         Wait policy)                                         ▼ breached
                                                                          BlockAsync
                                                                       (under _gate)
                                                                             │
                                                                  StateStore + FirewallManager
```

Two `EventLogWatcher` subscribers (`SecurityEventSubscriber`,
`RdpCoreTsSubscriber`) write to a shared bounded `Channel<FailedLogon>`. A
single consumer task in `Worker.ConsumeAsync` reads it and routes through
`FailureTracker` (sliding window per IP). The RdpCoreTS subscriber is the NLA
fallback for hosts where 4625 isn't generated; toggle with
`BlockRdp:EvaluateNlaFallback`.

### The single semaphore is load-bearing

A `SemaphoreSlim(1, 1)` is registered as a DI **singleton** (see `Program.cs`)
and serializes **all** firewall + state mutations across:

- the event-processing consumer (`Worker.BlockAsync`)
- the unblock scheduler (`UnblockScheduler`)
- pipe-server `unblock` commands (`Worker.UnblockAsync`)

Reason: `HNetCfg.FwPolicy2` / `INetFwRule2` COM is not thread-safe, and the
consolidated-rule pattern is read-modify-write on the rule's `RemoteAddresses`
CSV. Anything new that mutates firewall state must go through the same gate.

### `Worker` doubles as `IPipeOps`

Both registrations point to the same instance:

```csharp
builder.Services.AddSingleton<Worker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Worker>());
builder.Services.AddSingleton<IPipeOps>(sp => sp.GetRequiredService<Worker>());
builder.Services.AddHostedService<PipeServer>();
```

`PipeServer` is a separate hosted service that calls `IPipeOps` methods on the
worker — that's how the pipe server gets at live in-memory state (whitelist
counts, paused-until ticks, tracker) without re-resolving DI per request.

### Persistence layout (under `%ProgramData%\BlockRdpBruteForce\`)

- `state.json` — `BlockState` records (IP, counts, first/last seen, blocked-until)
- `bookmark-security.xml`, `bookmark-rdpcorets.xml` — `EventBookmark`s so the
  service replays from the last processed event after a restart
- `logs\service-*.log` — Serilog rolling daily files
- `geo\ipinfo_lite.mmdb` — optional IPinfo Lite database (~40 MB, downloaded
  on demand by `GeoRefreshService` when `GeoLookupEnabled=true`)
- `geo\geo-meta.json` — last-refresh timestamp + last-error tracking, so
  `geo-status` doesn't have to stat the .mmdb on every call

`FirewallRuleSync` runs at startup and reconciles `state.json` against the
actual firewall rule contents — handles the case where the rule was edited
while the service was stopped, or where state was wiped but the rule still
contains entries.

### Configuration layering

Both `Program.cs` and `CliDispatcher.ResolvePipeName` apply the same two-layer
config:

1. `appsettings.json` next to the exe (base, shipped)
2. `%ProgramData%\BlockRdpBruteForce\appsettings.json` (override, optional,
   `reloadOnChange: true`)

Settings live under the `BlockRdp:` section (see `AppOptions.SectionName`).
When adding a new option, add it to `Configuration\AppOptions.cs`, the shipped
`src\BlockRdpBruteForce\appsettings.json`, and the README config table.

Nine settings are also writable at runtime through the pipe —
`FailureThreshold`, `SlidingWindowMinutes`, `BlockDurationMinutes` (JSON
array; single entry = flat, multiple = repeat-offender ladder),
`Whitelist`, `FirewallScope`, `EvaluateNlaFallback`, `GeoLookupEnabled`,
`IpInfoToken`, `GeoRefreshIntervalDays`. The verbs are `config-get`,
`config-set`, `whitelist-add`, `whitelist-remove`;
`Configuration\SettingsWriter.cs` merges into the override JSON atomically
(tmp + `File.Move`) and preserves any unmanaged keys an admin added by hand.
The same self-lockout invariant from `Install.ps1` (empty whitelist +
threshold < 3) is enforced server-side.

`reloadOnChange: true` is still inert for most consumers because they cache
`IOptions.Value` in their constructors. The exceptions:

- **Whitelist** — `Worker._whitelist` is a swappable `WhitelistEvaluator`
  (Volatile.Read in the consumer, Interlocked.Exchange in
  `ApplyWhitelistHot`) so whitelist edits take effect without a restart.
- **Geo settings** — `GeoRefreshService` and `Worker.EnrichGeo` both read
  `IOptionsMonitor<AppOptions>.CurrentValue`, so toggling
  `GeoLookupEnabled`, updating `IpInfoToken`, or changing
  `GeoRefreshIntervalDays` is hot. The on-demand `geo-refresh` verb honors
  the new token immediately.

All other settings still require `Restart-Service BlockRdpBruteForce`.

### Windows-only attribute discipline

Most code is annotated `[SupportedOSPlatform("windows")]`. `Program.cs` exits
early on non-Windows. The service project targets `net10.0`; the tray project
targets `net10.0-windows` because of WinForms. Don't drop the platform
annotations when adding new files that touch firewall, event log, registry,
or pipe-ACL APIs — analyzer warnings are gated on them and `Program.cs`
suppresses CA1416 only inside `RunService`.

### Optional IP geolocation (off by default)

`Geo\GeoRefreshService.cs` is a hosted service that, when
`GeoLookupEnabled=true`, downloads the IPinfo Lite MMDB to
`%ProgramData%\BlockRdpBruteForce\geo\ipinfo_lite.mmdb` once a week
(`GeoRefreshIntervalDays`, range 1–30). It uses `IHttpClientFactory` (named
client `GeoDownloader`) and writes via `.tmp` + `File.Move`, then asks
`GeoLookup` to swap in a new `MaxMind.Db.Reader` (Apache-2.0,
`MemoryMapped` mode, thread-safe). The reader swap is the same hot-swap
pattern as `Worker._whitelist` (Interlocked.Exchange).

`Worker.GetList` enriches every `IpEntry` with `CountryCode`, `Asn`,
`AsName` if `IOptionsMonitor.CurrentValue.GeoLookupEnabled` is true and the
DB is loaded — null fields otherwise (no error). Pipe verbs are
`geo-status` (read-only, anyone) and `geo-refresh` (admin-only). Token is
stored alongside other settings in the override `appsettings.json`; default
ProgramData ACLs apply (Admins read/write, Users read). The .mmdb is **not**
redistributed with the MSI/installer — license is CC BY-SA 4.0; attribution
lives in README and SettingsForm group title.

### Self-lockout guard

`Install.ps1` refuses to install if `Whitelist` is empty **and**
`FailureThreshold < 3` (override with `-Force`). If you change defaults in
`appsettings.json`, keep this invariant in mind — a misconfigured default
could ban a fresh installer's management subnet on first failed-password
typo.

### Two installer paths

`install\Install.ps1` (dev/iteration) and `installer\` (WiX 7 MSI, production)
both deploy the same binaries. Functional differences worth knowing when
touching either:

- **Tray autostart**: `Install.ps1` writes HKCU `Run` for the installing user
  only. The MSI writes HKLM `Run` (covers all users) as an opt-in feature.
- **Self-lockout guard**: enforced by `Install.ps1`, **not** by the MSI. If you
  ever change the default `appsettings.json` shipped with the MSI to unsafe
  values, the MSI will install them silently.
- **State on uninstall**: `Uninstall.ps1` removes `%ProgramData%\BlockRdpBruteForce\`
  by default; the MSI preserves it (matching the Windows convention that an
  installer doesn't manage runtime-created data).
- **Firewall rule cleanup on uninstall**: both sweep `BlockRDPBruteForce-*`
  rules (PS via `Remove-NetFirewallRule`, MSI via a deferred CA running
  PowerShell with the same query).

When changing service installation behavior (recovery actions, ACLs, event-log
source, audit policy), update **both** `install\Install.ps1` and
`installer\Package.wxs` so the two paths don't diverge.

## Gotchas worth knowing

- **IPv4 and IPv6 are kept in separate sibling firewall rules** (`-v4`, `-v6`)
  because `INetFwRule.RemoteAddresses` doesn't mix families cleanly in a
  single CSV. See `FirewallManager` and `FirewallRuleChunker`.
- **`RemoteAddresses` slows past ~1000 entries**, so the rule is sharded into
  `BlockRDPBruteForce-2`, `-3`, … past `MaxRemoteAddressesPerRule`. Tests use
  `InMemoryFirewallManager` (a fake `IFirewallManager`) to verify chunking.
- **Pipe ACL** allows INTERACTIVE read so the tray app in the user session can
  poll `status`/`list`. Mutating verbs (`unblock`, `pause`, `resume`) check
  the connected token's `BuiltinAdministratorsSid` membership in `PipeServer`.
- **Use `DateTime.UtcNow` everywhere.** `EventRecord.TimeCreated` is converted
  to UTC before windowing — don't accidentally introduce local time.
- **Audit policy must be enabled** for the service to see anything: `auditpol
  /set /subcategory:"Logon" /failure:enable`. The installer can do this with
  `-EnableAuditPolicy`.
