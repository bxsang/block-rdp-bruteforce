#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Uninstalls the BlockRdpBruteForce service and removes its firewall rules.

.DESCRIPTION
    Stops and removes the Windows service, removes BlockRDPBruteForce-v4* /
    -v6* firewall rules, removes the tray HKCU\Run entry, removes the
    Application event-log source, and (unless -KeepState) deletes the install
    directory and ProgramData state.

.PARAMETER InstallPath
    Where the service was installed. Default
    C:\Program Files\BlockRdpBruteForce.

.PARAMETER KeepState
    Preserve %ProgramData%\BlockRdpBruteForce\ (state.json, bookmarks, logs).
    Useful when planning to reinstall.

.PARAMETER DryRun
    Print what would happen without making changes.
#>
[CmdletBinding()]
param(
    [string] $InstallPath = 'C:\Program Files\BlockRdpBruteForce',
    [switch] $KeepState,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ServiceName    = 'BlockRdpBruteForce'
$EventLogSource = 'BlockRdpBruteForce'
$FirewallRulePrefix = 'BlockRDPBruteForce-'
$ProgramDataDir = Join-Path $env:ProgramData 'BlockRdpBruteForce'
$RunKey         = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunValueName   = 'BlockRdpBruteForceTray'

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

# ---------------------------------------------------------------------------
# 1. Stop + remove service
# ---------------------------------------------------------------------------
Write-Section 'Service'

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') {
        Invoke-Step "Stopping $ServiceName" {
            Stop-Service -Name $ServiceName -Force -ErrorAction Stop
            (Get-Service -Name $ServiceName).WaitForStatus('Stopped', '00:00:30')
        }
    }
    Invoke-Step "Removing $ServiceName" {
        & sc.exe delete $ServiceName | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed (exit $LASTEXITCODE)." }
    }
} else {
    Write-Host "  No service named $ServiceName."
}

# ---------------------------------------------------------------------------
# 2. Firewall rules
# ---------------------------------------------------------------------------
Write-Section 'Firewall rules'

if (Get-Command Get-NetFirewallRule -ErrorAction SilentlyContinue) {
    $rules = Get-NetFirewallRule -ErrorAction SilentlyContinue |
             Where-Object { $_.DisplayName -like "$FirewallRulePrefix*" }
    if ($rules) {
        foreach ($rule in $rules) {
            Invoke-Step ("Removing rule {0}" -f $rule.DisplayName) {
                Remove-NetFirewallRule -Name $rule.Name -ErrorAction Stop
            }.GetNewClosure()
        }
    } else {
        Write-Host "  No matching rules."
    }
} else {
    Write-Warning "  NetSecurity module not available; falling back to netsh."
    Invoke-Step 'Removing rules via netsh' {
        $names = @()
        $output = & netsh.exe advfirewall firewall show rule name=all 2>$null
        foreach ($line in $output) {
            if ($line -match "^\s*Rule Name:\s*($([regex]::Escape($FirewallRulePrefix))[^\s].*)$") {
                $names += $Matches[1].Trim()
            }
        }
        foreach ($name in ($names | Sort-Object -Unique)) {
            & netsh.exe advfirewall firewall delete rule name="$name" | Out-Null
        }
    }
}

# ---------------------------------------------------------------------------
# 3. Tray HKCU Run entry (current user only)
# ---------------------------------------------------------------------------
Write-Section 'Tray autostart'

if (Test-Path $RunKey) {
    $existing = Get-ItemProperty -Path $RunKey -Name $RunValueName -ErrorAction SilentlyContinue
    if ($existing) {
        Invoke-Step "Removing HKCU\...\Run\$RunValueName for current user" {
            Remove-ItemProperty -Path $RunKey -Name $RunValueName -ErrorAction Stop
        }
    } else {
        Write-Host "  No HKCU\...\Run entry for current user."
    }
} else {
    Write-Host "  HKCU Run key not present."
}
Write-Host "  (Other users' Run entries are not removed; remove them manually if needed.)"

# ---------------------------------------------------------------------------
# 4. Event log source
# ---------------------------------------------------------------------------
Write-Section 'Event log source'

function Test-EventLogSource([string] $Source) {
    # EventLog::SourceExists enumerates the Security log too, which non-admin
    # can't read. Probe the registry directly.
    foreach ($log in 'Application','System') {
        if (Test-Path -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\$log\$Source") {
            return $true
        }
    }
    return $false
}

if (Test-EventLogSource -Source $EventLogSource) {
    Invoke-Step "Removing event-log source $EventLogSource" {
        Remove-EventLog -Source $EventLogSource
    }
} else {
    Write-Host "  Source $EventLogSource not registered."
}

# ---------------------------------------------------------------------------
# 5. Install directory
# ---------------------------------------------------------------------------
Write-Section 'Install directory'

if (Test-Path $InstallPath) {
    Invoke-Step "Removing $InstallPath" {
        Remove-Item -Path $InstallPath -Recurse -Force -ErrorAction Stop
    }
} else {
    Write-Host "  $InstallPath does not exist."
}

# ---------------------------------------------------------------------------
# 6. ProgramData state (unless -KeepState)
# ---------------------------------------------------------------------------
Write-Section 'ProgramData state'

if (Test-Path $ProgramDataDir) {
    if ($KeepState) {
        Write-Host "  Preserving $ProgramDataDir (-KeepState)."
    } else {
        Invoke-Step "Removing $ProgramDataDir" {
            Remove-Item -Path $ProgramDataDir -Recurse -Force -ErrorAction Stop
        }
    }
} else {
    Write-Host "  $ProgramDataDir does not exist."
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Write-Section 'Summary'
if ($DryRun) {
    Write-Host '  Dry-run complete. No changes were made.' -ForegroundColor Yellow
} else {
    Write-Host '  Uninstall complete.' -ForegroundColor Green
    if ($KeepState) {
        Write-Host "  State retained at $ProgramDataDir."
    }
}
