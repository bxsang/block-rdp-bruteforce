# Downloads Twemoji 72x72 country flag PNGs into
# src/BlockRdpBruteForce.Tray/Resources/Flags/, named by the ISO 3166-1
# alpha-2 country code in lowercase (e.g., us.png, vn.png).
#
# Twemoji is Copyright 2020 Twitter, Inc and other contributors. Graphics
# licensed under CC-BY 4.0 (https://creativecommons.org/licenses/by/4.0/).
# Source mirror: https://github.com/jdecked/twemoji
#
# Run from repo root:
#   pwsh -File scripts/download-flags.ps1

[CmdletBinding()]
param(
    [string]$OutputDir = "src/BlockRdpBruteForce.Tray/Resources/Flags",
    [string]$BaseUrl = "https://raw.githubusercontent.com/jdecked/twemoji/main/assets/72x72"
)

$ErrorActionPreference = "Stop"

# ISO 3166-1 alpha-2 country codes that have Twemoji flag glyphs.
$codes = @(
    'ad','ae','af','ag','ai','al','am','ao','aq','ar','as','at','au','aw','ax','az',
    'ba','bb','bd','be','bf','bg','bh','bi','bj','bl','bm','bn','bo','bq','br','bs',
    'bt','bv','bw','by','bz','ca','cc','cd','cf','cg','ch','ci','ck','cl','cm','cn',
    'co','cr','cu','cv','cw','cx','cy','cz','de','dj','dk','dm','do','dz','ec','ee',
    'eg','eh','er','es','et','fi','fj','fk','fm','fo','fr','ga','gb','gd','ge','gf',
    'gg','gh','gi','gl','gm','gn','gp','gq','gr','gs','gt','gu','gw','gy','hk','hm',
    'hn','hr','ht','hu','id','ie','il','im','in','io','iq','ir','is','it','je','jm',
    'jo','jp','ke','kg','kh','ki','km','kn','kp','kr','kw','ky','kz','la','lb','lc',
    'li','lk','lr','ls','lt','lu','lv','ly','ma','mc','md','me','mf','mg','mh','mk',
    'ml','mm','mn','mo','mp','mq','mr','ms','mt','mu','mv','mw','mx','my','mz','na',
    'nc','ne','nf','ng','ni','nl','no','np','nr','nu','nz','om','pa','pe','pf','pg',
    'ph','pk','pl','pm','pn','pr','ps','pt','pw','py','qa','re','ro','rs','ru','rw',
    'sa','sb','sc','sd','se','sg','sh','si','sj','sk','sl','sm','sn','so','sr','ss',
    'st','sv','sx','sy','sz','tc','td','tf','tg','th','tj','tk','tl','tm','tn','to',
    'tr','tt','tv','tw','tz','ua','ug','um','us','uy','uz','va','vc','ve','vg','vi',
    'vn','vu','wf','ws','xk','ye','yt','za','zm','zw'
)

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

function Get-FlagFilename([string]$cc) {
    # Map each ASCII letter to its Regional Indicator codepoint (0x1F1E6 + offset)
    # then format the pair as 'hex-hex' (e.g., 'us' -> '1f1fa-1f1f8').
    $base = 0x1F1E6
    $a = [int][char]$cc[0] - [int][char]'a'
    $b = [int][char]$cc[1] - [int][char]'a'
    return ("{0:x}-{1:x}.png" -f ($base + $a), ($base + $b))
}

$ok = 0
$fail = 0
foreach ($code in $codes) {
    $remote = Get-FlagFilename $code
    $url = "$BaseUrl/$remote"
    $dest = Join-Path $OutputDir "$code.png"
    try {
        Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing -ErrorAction Stop
        $ok++
    }
    catch {
        Write-Warning "Failed to download $code from $url : $($_.Exception.Message)"
        $fail++
    }
}

Write-Host "Done. Downloaded $ok flags to $OutputDir ($fail failures)."
