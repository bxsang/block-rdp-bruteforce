#Requires -Version 5.1
<#
.SYNOPSIS
    Publish service + tray, then build the MSI.

.DESCRIPTION
    Wraps the three commands that produce installer\bin\<Configuration>\BlockRdpBruteForce.msi:
      1. dotnet publish src\BlockRdpBruteForce       (single-file, self-contained)
      2. dotnet publish src\BlockRdpBruteForce.Tray   (single-file, self-contained)
      3. dotnet build installer\BlockRdpBruteForce.Installer.wixproj

    No admin rights required to BUILD the MSI. Installing it does require admin.

.PARAMETER Configuration
    MSBuild configuration. Default Release.

.PARAMETER Runtime
    .NET RID for the publish step. Default win-x64.

.PARAMETER SkipPublish
    Skip the dotnet publish steps and build straight from existing publish output.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime       = 'win-x64',
    [switch] $SkipPublish
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
    Invoke-Native dotnet @(
        'publish', (Join-Path $repoRoot 'src\BlockRdpBruteForce'),
        '-c', $Configuration, '-r', $Runtime,
        '--self-contained', '-p:PublishSingleFile=true'
    ) 'Publishing service'

    Invoke-Native dotnet @(
        'publish', (Join-Path $repoRoot 'src\BlockRdpBruteForce.Tray'),
        '-c', $Configuration, '-r', $Runtime,
        '--self-contained', '-p:PublishSingleFile=true'
    ) 'Publishing tray'
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
