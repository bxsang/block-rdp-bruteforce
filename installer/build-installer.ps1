#Requires -Version 5.1
<#
.SYNOPSIS
    Publish service + tray, then build the MSI.

.DESCRIPTION
    Wraps the commands that produce installer\bin\<Configuration>\BlockRdpBruteForce.msi:
      1. dotnet publish src\BlockRdpBruteForce          (single-file)
      2. dotnet publish src\BlockRdpBruteForce.Tray      (single-file)
      3. dotnet publish src\BlockRdpBruteForce.Updater   (single-file)
      4. dotnet build installer\BlockRdpBruteForce.Installer.wixproj

    By default the publish is self-contained, which makes the MSI ~70-90 MB but
    requires no .NET runtime on the target box. Pass -FrameworkDependent for a
    much smaller MSI (a few MB) that requires the .NET 10 Desktop Runtime to
    be pre-installed on the target.

    No admin rights required to BUILD the MSI. Installing it does require admin.

.PARAMETER Configuration
    MSBuild configuration. Default Release.

.PARAMETER Runtime
    .NET RID for the publish step. Default win-x64.

.PARAMETER SkipPublish
    Skip the dotnet publish steps and build straight from existing publish output.

.PARAMETER FrameworkDependent
    Publish framework-dependent instead of self-contained. Target machine must
    have the .NET 10 Desktop Runtime installed (Desktop because the tray uses
    WinForms). Trades MSI size for a runtime prerequisite.
#>
[CmdletBinding()]
param(
    [string] $Configuration      = 'Release',
    [string] $Runtime            = 'win-x64',
    [switch] $SkipPublish,
    [switch] $FrameworkDependent
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot      = Split-Path -Parent $PSScriptRoot
$installerProj = Join-Path $PSScriptRoot 'BlockRdpBruteForce.Installer.wixproj'

function Invoke-Native {
    param([string] $Tool, [string[]] $Arguments, [string] $Label)
    Write-Host "==> $Label" -ForegroundColor Cyan
    & $Tool @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Label failed (exit $LASTEXITCODE)." }
}

if (-not $SkipPublish) {
    $selfContainedArg = if ($FrameworkDependent) { '--no-self-contained' } else { '--self-contained' }

    Invoke-Native dotnet @(
        'publish', (Join-Path $repoRoot 'src\BlockRdpBruteForce'),
        '-c', $Configuration, '-r', $Runtime,
        $selfContainedArg, '-p:PublishSingleFile=true'
    ) 'Publishing service'

    Invoke-Native dotnet @(
        'publish', (Join-Path $repoRoot 'src\BlockRdpBruteForce.Tray'),
        '-c', $Configuration, '-r', $Runtime,
        $selfContainedArg, '-p:PublishSingleFile=true'
    ) 'Publishing tray'

    Invoke-Native dotnet @(
        'publish', (Join-Path $repoRoot 'src\BlockRdpBruteForce.Updater'),
        '-c', $Configuration, '-r', $Runtime,
        $selfContainedArg, '-p:PublishSingleFile=true'
    ) 'Publishing updater'
}

Invoke-Native dotnet @(
    'build', $installerProj,
    '-c', $Configuration
) 'Building MSI'

$msi = Join-Path $PSScriptRoot "bin\$Configuration\BlockRdpBruteForce.msi"
if (Test-Path $msi) {
    Write-Host ''
    Write-Host "MSI: $msi" -ForegroundColor Green
} else {
    Write-Warning "Build reported success but MSI not found at $msi."
}
