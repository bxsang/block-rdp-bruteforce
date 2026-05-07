#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs BlockRdpBruteForce as a Windows service and (optionally) registers
    the tray app for autostart.

.DESCRIPTION
    Verifies prerequisites, deploys the service + tray binaries, registers the
    Windows service, configures the Application event-log source, sets ACLs on
    the ProgramData state directory, and (optionally) registers the tray app
    for the current interactive user's HKCU\Run.

    Safe to re-run: existing service is stopped + replaced, existing event-log
    source is reused, existing ProgramData ACLs are re-applied.

.PARAMETER InstallPath
    Where the service + tray exes will be deployed. Default
    C:\Program Files\BlockRdpBruteForce.

.PARAMETER SourcePath
    Folder containing the already-published service exe (and optionally the
    tray exe). If omitted and -Build is given, the script publishes the repo
    to .\publish\ next to this script.

.PARAMETER Build
    Run `dotnet publish` for both projects before installing. Requires the
    .NET SDK on PATH.

.PARAMETER Configuration
    Build configuration when -Build is set. Default Release.

.PARAMETER Runtime
    .NET RID when -Build is set. Default win-x64.

.PARAMETER FrameworkDependent
    Publish framework-dependent instead of self-contained when -Build is set.
    Produces much smaller binaries but requires the .NET 10 Desktop Runtime
    on the target machine (Desktop because the tray uses WinForms). Without
    the runtime the service won't start.

.PARAMETER EnableAuditPolicy
    If "Audit Logon" failures are not enabled, enable them automatically.
    Without this switch the installer warns and continues; the service
    will start but receive no 4625 events until auditing is enabled.

.PARAMETER RegisterTrayAutostart
    Add an HKCU\...\Run entry for the tray app under the user that ran this
    script. The tray exe will start at next interactive logon for that user.

.PARAMETER DryRun
    Print what would happen without making changes. Useful for review on a
    dev workstation before running on the target box.

.PARAMETER Force
    Override the self-lockout guard (Whitelist empty AND FailureThreshold < 3
    is normally refused).

.EXAMPLE
    .\Install.ps1 -Build -EnableAuditPolicy -RegisterTrayAutostart

.EXAMPLE
    .\Install.ps1 -SourcePath 'D:\releases\brbf\1.0.0' -DryRun
#>
[CmdletBinding()]
param(
    [string] $InstallPath = 'C:\Program Files\BlockRdpBruteForce',
    [string] $SourcePath,
    [switch] $Build,
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [switch] $FrameworkDependent,
    [switch] $EnableAuditPolicy,
    [switch] $RegisterTrayAutostart,
    [switch] $DryRun,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ServiceName    = 'BlockRdpBruteForce'
$ServiceExe     = 'BlockRdpBruteForce.exe'
$TrayExe        = 'BlockRdpBruteForce.Tray.exe'
$UpdaterExe     = 'BlockRdpBruteForce.Updater.exe'
$EventLogName   = 'Application'
$EventLogSource = 'BlockRdpBruteForce'
$ProgramDataDir = Join-Path $env:ProgramData 'BlockRdpBruteForce'
$LogsDir        = Join-Path $ProgramDataDir 'logs'
$RepoRoot       = Split-Path -Parent $PSScriptRoot
$PublishRoot    = Join-Path $PSScriptRoot 'publish'

function Write-Section([string] $Text) {
    Write-Host ''
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Invoke-Step {
    param(
        [string] $Description,
        [scriptblock] $Action
    )
    if ($DryRun) {
        Write-Host "  [dry-run] $Description" -ForegroundColor Yellow
        return
    }
    Write-Host "  $Description"
    & $Action
}

function Test-AuditLogonFailure {
    # auditpol writes "Error 0x00000522 occurred" (privilege not held) to STDOUT
    # not stderr when run unprivileged; combined with $ErrorActionPreference='Stop'
    # PowerShell can turn native exit codes into terminating exceptions. Lower
    # EAP locally and let LASTEXITCODE drive the result.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & auditpol.exe /get /subcategory:"Logon" 2>$null
        $exit = $LASTEXITCODE
        $global:LASTEXITCODE = 0  # don't leak auditpol's exit code to callers
        if ($exit -ne 0) { return $false }
        foreach ($line in $output) {
            # Localized; match the row that ends in "Failure" or "Success and Failure".
            if ($line -is [string] -and $line -match 'Logon\s+(.*)') {
                $setting = $Matches[1].Trim()
                return ($setting -match 'Failure')
            }
        }
        return $false
    } catch {
        return $false
    } finally {
        $ErrorActionPreference = $prev
    }
}

function Find-LatestPublish([string] $Project) {
    $candidates = @(
        (Join-Path $PublishRoot $Project),
        (Join-Path (Join-Path $RepoRoot "src\$Project") "bin\$Configuration\net10.0\$Runtime\publish"),
        (Join-Path (Join-Path $RepoRoot "src\$Project") "bin\$Configuration\net10.0-windows\$Runtime\publish")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return (Resolve-Path $candidate).Path }
    }
    return $null
}

# ---------------------------------------------------------------------------
# 1. Build (optional)
# ---------------------------------------------------------------------------
if ($Build) {
    Write-Section 'Build'
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { throw "dotnet SDK not found on PATH; cannot -Build." }

    $selfContainedArg = if ($FrameworkDependent) { '--no-self-contained' } else { '--self-contained' }

    Invoke-Step "Publishing service to $PublishRoot\BlockRdpBruteForce" {
        $svcPub = Join-Path $PublishRoot 'BlockRdpBruteForce'
        & dotnet publish (Join-Path $RepoRoot 'src\BlockRdpBruteForce') `
            -c $Configuration -r $Runtime $selfContainedArg `
            -p:PublishSingleFile=true -o $svcPub | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish (service) failed." }
    }
    Invoke-Step "Publishing tray to $PublishRoot\BlockRdpBruteForce.Tray" {
        $trayPub = Join-Path $PublishRoot 'BlockRdpBruteForce.Tray'
        & dotnet publish (Join-Path $RepoRoot 'src\BlockRdpBruteForce.Tray') `
            -c $Configuration -r $Runtime $selfContainedArg `
            -p:PublishSingleFile=true -o $trayPub | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish (tray) failed." }
    }
    Invoke-Step "Publishing updater to $PublishRoot\BlockRdpBruteForce.Updater" {
        $updaterPub = Join-Path $PublishRoot 'BlockRdpBruteForce.Updater'
        & dotnet publish (Join-Path $RepoRoot 'src\BlockRdpBruteForce.Updater') `
            -c $Configuration -r $Runtime $selfContainedArg `
            -p:PublishSingleFile=true -o $updaterPub | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish (updater) failed." }
    }
}

# ---------------------------------------------------------------------------
# 2. Resolve source binaries
# ---------------------------------------------------------------------------
Write-Section 'Source binaries'

if (-not $SourcePath) {
    $SourcePath = Find-LatestPublish 'BlockRdpBruteForce'
}
if (-not $SourcePath -or -not (Test-Path $SourcePath)) {
    throw "Could not locate published service binaries. " +
          "Pass -Build, or -SourcePath pointing to a folder containing $ServiceExe."
}
$svcSourceExe = Join-Path $SourcePath $ServiceExe
if (-not (Test-Path $svcSourceExe)) {
    throw "Service exe not found at $svcSourceExe."
}
Write-Host "  Service source:    $SourcePath"

$traySource = Find-LatestPublish 'BlockRdpBruteForce.Tray'
if ($traySource) {
    Write-Host "  Tray source:       $traySource"
} else {
    Write-Warning "  Tray source not found; service will install but tray will not be deployed."
}

$updaterSource = Find-LatestPublish 'BlockRdpBruteForce.Updater'
if ($updaterSource) {
    Write-Host "  Updater source:    $updaterSource"
} else {
    Write-Warning "  Updater source not found; auto-updates will fall back to the silent (no-UI) path."
}

# ---------------------------------------------------------------------------
# 3. Self-lockout guard
# ---------------------------------------------------------------------------
Write-Section 'Self-lockout guard'

$publishedAppSettings = Join-Path $SourcePath 'appsettings.json'
$repoAppSettings      = Join-Path $RepoRoot 'src\BlockRdpBruteForce\appsettings.json'
if (Test-Path $publishedAppSettings) {
    $guardAppSettings = $publishedAppSettings
} elseif (Test-Path $repoAppSettings) {
    $guardAppSettings = $repoAppSettings
} else {
    $guardAppSettings = $null
}

if ($guardAppSettings) {
    Write-Host "  Reading config from $guardAppSettings"
    $cfg = (Get-Content $guardAppSettings -Raw | ConvertFrom-Json).BlockRdp
    $whitelistCount = if ($null -eq $cfg.Whitelist) { 0 } else { @($cfg.Whitelist).Count }
    $threshold      = [int] $cfg.FailureThreshold
    Write-Host ("  Whitelist entries: {0}; FailureThreshold: {1}" -f $whitelistCount, $threshold)
    if ($whitelistCount -eq 0 -and $threshold -lt 3 -and -not $Force) {
        throw "Refusing to install: Whitelist is empty and FailureThreshold is < 3 ($threshold). " +
              "This is a self-lockout footgun -- a single mistyped password could ban your management subnet. " +
              "Either add at least one Whitelist entry to appsettings.json, raise FailureThreshold, or pass -Force."
    }
} else {
    Write-Warning "  appsettings.json not found at $publishedAppSettings or $repoAppSettings; cannot run self-lockout guard."
}

# ---------------------------------------------------------------------------
# 4. Audit policy check
# ---------------------------------------------------------------------------
Write-Section 'Audit policy'

$auditOk = Test-AuditLogonFailure
if ($auditOk) {
    Write-Host '  Logon failure auditing: enabled.' -ForegroundColor Green
} elseif ($EnableAuditPolicy) {
    Invoke-Step 'Enabling audit policy: Logon failures' {
        & auditpol.exe /set /subcategory:"Logon" /failure:enable | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "auditpol /set failed (exit $LASTEXITCODE)." }
    }
} else {
    Write-Warning ("  'Audit Logon' failures are NOT enabled. The service will run but " +
                  "will receive no Security/4625 events. Re-run with -EnableAuditPolicy " +
                  "or run: auditpol /set /subcategory:`"Logon`" /failure:enable")
}

# ---------------------------------------------------------------------------
# 5. ProgramData directory + ACL
# ---------------------------------------------------------------------------
Write-Section 'ProgramData directory'

Invoke-Step "Creating $ProgramDataDir" {
    if (-not (Test-Path $ProgramDataDir)) { New-Item -ItemType Directory -Path $ProgramDataDir -Force | Out-Null }
    if (-not (Test-Path $LogsDir))        { New-Item -ItemType Directory -Path $LogsDir -Force        | Out-Null }
}

Invoke-Step "Applying ACL: SYSTEM/Administrators full, Users read" {
    $acl = Get-Acl -Path $ProgramDataDir
    $acl.SetAccessRuleProtection($true, $false)

    $systemSid = [System.Security.Principal.SecurityIdentifier]::new(
        [System.Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
    $adminsSid = [System.Security.Principal.SecurityIdentifier]::new(
        [System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
    $usersSid  = [System.Security.Principal.SecurityIdentifier]::new(
        [System.Security.Principal.WellKnownSidType]::BuiltinUsersSid, $null)

    $inherit   = [System.Security.AccessControl.InheritanceFlags] 'ContainerInherit, ObjectInherit'
    $propagate = [System.Security.AccessControl.PropagationFlags]::None
    $allow     = [System.Security.AccessControl.AccessControlType]::Allow
    $full      = [System.Security.AccessControl.FileSystemRights]::FullControl
    $read      = [System.Security.AccessControl.FileSystemRights]::ReadAndExecute

    $systemRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        $systemSid, $full, $inherit, $propagate, $allow)
    $adminsRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
        $adminsSid, $full, $inherit, $propagate, $allow)
    $usersRule  = [System.Security.AccessControl.FileSystemAccessRule]::new(
        $usersSid, $read, $inherit, $propagate, $allow)

    $acl.AddAccessRule($systemRule)
    $acl.AddAccessRule($adminsRule)
    $acl.AddAccessRule($usersRule)
    Set-Acl -Path $ProgramDataDir -AclObject $acl
}

# ---------------------------------------------------------------------------
# 6. Event log source
# ---------------------------------------------------------------------------
Write-Section 'Event log source'

function Test-EventLogSource([string] $Source, [string] $LogName) {
    # EventLog::SourceExists enumerates every channel including Security, which
    # is unreadable to non-admin and throws. Probe the registry directly: the
    # source key is HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\<Log>\<Src>.
    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\$LogName\$Source"
    return Test-Path -LiteralPath $key
}

if (Test-EventLogSource -Source $EventLogSource -LogName $EventLogName) {
    Write-Host "  Source '$EventLogSource' already registered under $EventLogName."
} else {
    Invoke-Step "Registering $EventLogName event-log source '$EventLogSource'" {
        New-EventLog -LogName $EventLogName -Source $EventLogSource
    }
}

# ---------------------------------------------------------------------------
# 7. Stop existing service
# ---------------------------------------------------------------------------
Write-Section 'Stop existing service (if any)'

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') {
        Invoke-Step "Stopping $ServiceName" {
            Stop-Service -Name $ServiceName -Force -ErrorAction Stop
            (Get-Service -Name $ServiceName).WaitForStatus('Stopped', '00:00:30')
        }
    } else {
        Write-Host "  Service is already stopped."
    }
} else {
    Write-Host "  No existing service."
}

# ---------------------------------------------------------------------------
# 8. Deploy binaries
# ---------------------------------------------------------------------------
Write-Section 'Deploy binaries'

Invoke-Step "Creating $InstallPath" {
    if (-not (Test-Path $InstallPath)) { New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null }
}

Invoke-Step "Copying service files from $SourcePath" {
    Copy-Item -Path (Join-Path $SourcePath '*') -Destination $InstallPath -Recurse -Force
}

if ($traySource) {
    Invoke-Step "Copying tray files from $traySource" {
        Copy-Item -Path (Join-Path $traySource '*') -Destination $InstallPath -Recurse -Force
    }
}

if ($updaterSource) {
    Invoke-Step "Copying updater files from $updaterSource" {
        Copy-Item -Path (Join-Path $updaterSource '*') -Destination $InstallPath -Recurse -Force
    }
}

# ---------------------------------------------------------------------------
# 9. Create / update service
# ---------------------------------------------------------------------------
Write-Section 'Service registration'

$installedSvcExe = Join-Path $InstallPath $ServiceExe
if (-not (Test-Path $installedSvcExe)) {
    if (-not $DryRun) { throw "Expected service exe at $installedSvcExe after deploy, but it isn't there." }
}

if ($existing) {
    Invoke-Step "Updating existing service (binPath = $installedSvcExe)" {
        & sc.exe config $ServiceName binPath= "`"$installedSvcExe`"" start= auto obj= LocalSystem | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "sc.exe config failed (exit $LASTEXITCODE)." }
    }
} else {
    Invoke-Step "Creating $ServiceName service" {
        New-Service -Name $ServiceName `
                    -BinaryPathName "`"$installedSvcExe`"" `
                    -DisplayName 'BlockRdpBruteForce' `
                    -Description 'Real-time RDP brute-force detection and blocking.' `
                    -StartupType Automatic | Out-Null
        & sc.exe failure $ServiceName reset= 86400 actions= restart/30000/restart/60000/restart/120000 | Out-Null
    }
}

Invoke-Step "Starting $ServiceName" {
    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', '00:00:30')
}

# ---------------------------------------------------------------------------
# 10. Tray autostart (optional)
# ---------------------------------------------------------------------------
if ($RegisterTrayAutostart) {
    Write-Section 'Tray autostart'

    if (-not $traySource) {
        Write-Warning '  -RegisterTrayAutostart given but tray source not found; skipping.'
    } else {
        $trayExePath = Join-Path $InstallPath $TrayExe
        if (-not $DryRun -and -not (Test-Path $trayExePath)) {
            throw "Tray exe not found at $trayExePath after deploy."
        }
        Invoke-Step "Adding HKCU\...\Run entry for current user" {
            $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
            if (-not (Test-Path $runKey)) { New-Item -Path $runKey -Force | Out-Null }
            Set-ItemProperty -Path $runKey -Name 'BlockRdpBruteForceTray' -Value "`"$trayExePath`""
        }
        Invoke-Step 'Launching tray app for current session' {
            Start-Process -FilePath $trayExePath
        }
    }
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Write-Section 'Summary'
if ($DryRun) {
    Write-Host '  Dry-run complete. No changes were made.' -ForegroundColor Yellow
} else {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc) {
        Write-Host ("  Service status:    {0}" -f $svc.Status) -ForegroundColor Green
    }
    Write-Host  "  Install path:      $InstallPath"
    Write-Host  "  State directory:   $ProgramDataDir"
    Write-Host  "  Logs directory:    $LogsDir"
    Write-Host ''
    Write-Host  '  Verify:'
    Write-Host  '    Get-Service BlockRdpBruteForce'
    Write-Host ('    "{0}" status' -f (Join-Path $InstallPath $ServiceExe))
    Write-Host ('    Get-Content "{0}\service-*.log" -Tail 30 -Wait' -f $LogsDir)
}
