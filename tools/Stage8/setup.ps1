<#
.SYNOPSIS
  Makes or refreshes the folders tools/Stage8/run.ps1 runs in: our own dedicated server, our own client, and
  their data folders, all under -Root (G:\TRStage8). Safe to run again after a game or LaunchPad update.

.DESCRIPTION
  Reads, never writes, the two installs it copies from:
    -ClientSource  the machine's Steam copy of the game (bt: G:\SteamLibrary\steamapps\common\Stationeers)
    -ServerSource  an installed dedicated server's folder, of which only the game's own files are copied:
                   rocketstation_DedicatedServer.exe, rocketstation_DedicatedServer_Data, MonoBleedingEdge and
                   the other top-level .exe and .dll files. Never its BepInEx, config, data, mods, saves, logs
                   or steamapps.
    or -SteamCmd   steamcmd.exe, which installs the dedicated server (Steam app 600760) into <Root>\server
                   anonymously instead.
  Then:
    <Root>\server       the dedicated server, with winhttp.dll, doorstop_config.ini, BepInEx\core and
                        BepInEx\plugins\StationeersLaunchPad copied from the client source, so the server and the
                        client run the same LaunchPadBooster (a different one refuses the join)
    <Root>\client       a mirror of the client source without its mods, saves, scripts, thumbnails, settings,
                        mod list, BepInEx config, BepInEx plugins (but StationeersLaunchPad) and logs
    <Root>\server-data  and <Root>\client-data: where each one's LaunchPad points the game's save path
                        (SavePathOverride), so neither reads or writes the machine's own settings or saves
  The LaunchPad config of each: SavePathOverride, Steam off, no update, news, repo or server pack checks, no
  countdowns.

.EXAMPLE
  .\setup.ps1 -Root G:\TRStage8 -ServerSource G:\StationeersDedicated\server
.EXAMPLE
  .\setup.ps1 -Root G:\TRStage8 -SteamCmd C:\steamcmd\steamcmd.exe
#>
[CmdletBinding()]
param(
    [string]$Root = 'G:\TRStage8',
    [string]$ClientSource = 'G:\SteamLibrary\steamapps\common\Stationeers',
    [string]$ServerSource = '',
    [string]$SteamCmd = '',
    [switch]$SkipClient,
    [switch]$SkipServer
)

$ErrorActionPreference = 'Stop'
$serverDir = Join-Path $Root 'server'
$clientDir = Join-Path $Root 'client'
$serverData = Join-Path $Root 'server-data'
$clientData = Join-Path $Root 'client-data'

function Say([string]$text) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $text) }

function Copy-Tree([string]$from, [string]$to, [string[]]$more) {
    if (-not (Test-Path $from)) { throw "missing $from" }
    $arguments = @($from, $to, '/MIR', '/NFL', '/NDL', '/NJH', '/NJS', '/NP') + $more
    & robocopy @arguments | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy $from -> $to failed ($LASTEXITCODE)" }
}

function Set-Cfg([string]$path, [string]$section, [string]$key, [string]$value) {
    $lines = New-Object 'System.Collections.Generic.List[string]'
    if (Test-Path $path) { $lines.AddRange([string[]]@(Get-Content $path)) }
    $start = $lines.IndexOf("[$section]")
    if ($start -lt 0) {
        $lines.Add(''); $lines.Add("[$section]"); $lines.Add("$key = $value")
    } else {
        $end = $lines.Count
        for ($i = $start + 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^\[') { $end = $i; break } }
        $found = $false
        for ($i = $start + 1; $i -lt $end; $i++) { if ($lines[$i] -match "^\s*$([regex]::Escape($key))\s*=") { $lines[$i] = "$key = $value"; $found = $true } }
        if (-not $found) { $lines.Insert($end, "$key = $value") }
    }
    Set-Content -Path $path -Value $lines -Encoding UTF8
}

function Set-LaunchPad([string]$install, [string]$data, [bool]$server) {
    $config = Join-Path $install 'BepInEx\config'
    New-Item -ItemType Directory -Force -Path $config | Out-Null
    $cfg = Join-Path $config 'stationeers.launchpad.cfg'
    Set-Cfg $cfg 'Mod Loading' 'SavePathOverride' $data
    Set-Cfg $cfg 'Startup' 'AutoLoadOnStart' 'true'
    Set-Cfg $cfg 'Startup' 'DisableSteam' 'true'
    Set-Cfg $cfg 'Startup' 'CheckForUpdate' 'false'
    Set-Cfg $cfg 'Startup' 'AutoUpdateOnStart' 'false'
    Set-Cfg $cfg 'Startup' 'AutoLoadCountdown' '0'
    Set-Cfg $cfg 'Startup' 'AutoStartCountdown' '0'
    Set-Cfg $cfg 'Startup' 'DevMode' 'true'
    Set-Cfg $cfg 'Mod Repos' 'RepoCheckUpdates' 'false'
    Set-Cfg $cfg 'News' 'NewsCheckOnStart' 'false'
    Set-Cfg $cfg 'Server Packs' 'Enabled' 'false'
    Set-Cfg $cfg 'Server Packs' 'AutoConnect' 'false'
    if ($server) { Set-Cfg $cfg 'Hosting' 'ShareModList' 'false' }
    New-Item -ItemType Directory -Force -Path $data, (Join-Path $data 'mods'), (Join-Path $data 'saves') | Out-Null
    Say "LaunchPad config $cfg -> save path $data"
}

function Version-Of([string]$ini) {
    if (Test-Path $ini) { return ((Get-Content $ini | Select-String '^UPDATEVERSION=').Line -replace '^UPDATEVERSION=', '') }
    return $null
}

if (-not (Test-Path (Join-Path $ClientSource 'rocketstation.exe'))) { throw "No game at $ClientSource." }
if (-not (Test-Path (Join-Path $ClientSource 'BepInEx\plugins\StationeersLaunchPad'))) { throw "No StationeersLaunchPad in $ClientSource." }
New-Item -ItemType Directory -Force -Path $Root | Out-Null
foreach ($p in @(Get-Process -Name rocketstation, rocketstation_DedicatedServer -ErrorAction SilentlyContinue)) {
    $path = $null
    try { $path = $p.Path } catch { $path = $null }
    if ($path -and ([IO.Path]::GetFullPath($path)).StartsWith(([IO.Path]::GetFullPath($Root)).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "A Stage 8 instance is running (PID $($p.Id)); run stop.ps1 first."
    }
}

if (-not $SkipServer) {
    if ($SteamCmd) {
        Say "steamcmd: app 600760 into $serverDir"
        & $SteamCmd +force_install_dir $serverDir +login anonymous +app_update 600760 validate +quit | Tee-Object (Join-Path $Root 'steamcmd.log') | Out-Null
    } elseif ($ServerSource) {
        if (-not (Test-Path (Join-Path $ServerSource 'rocketstation_DedicatedServer.exe'))) { throw "No dedicated server at $ServerSource." }
        Say "copying the dedicated server's game files from $ServerSource (read only)"
        New-Item -ItemType Directory -Force -Path $serverDir | Out-Null
        foreach ($dir in 'rocketstation_DedicatedServer_Data', 'MonoBleedingEdge') {
            Copy-Tree (Join-Path $ServerSource $dir) (Join-Path $serverDir $dir) @()
        }
        foreach ($f in @(Get-ChildItem $ServerSource -File | Where-Object { ($_.Extension -in '.exe', '.dll') -and $_.Name -ne 'winhttp.dll' })) {
            Copy-Item $f.FullName $serverDir -Force
        }
    } elseif (-not (Test-Path (Join-Path $serverDir 'rocketstation_DedicatedServer.exe'))) {
        throw 'Give -ServerSource (an installed dedicated server, read only) or -SteamCmd.'
    }
    if (-not (Test-Path (Join-Path $serverDir 'rocketstation_DedicatedServer.exe'))) { throw "No dedicated server in $serverDir after the copy." }
    foreach ($f in 'winhttp.dll', 'doorstop_config.ini') { Copy-Item (Join-Path $ClientSource $f) $serverDir -Force }
    Copy-Tree (Join-Path $ClientSource 'BepInEx\core') (Join-Path $serverDir 'BepInEx\core') @()
    Copy-Tree (Join-Path $ClientSource 'BepInEx\plugins\StationeersLaunchPad') (Join-Path $serverDir 'BepInEx\plugins\StationeersLaunchPad') @()
    Set-LaunchPad $serverDir $serverData $true
}

if (-not $SkipClient) {
    Say "mirroring the client from $ClientSource (read only) into $clientDir"
    $skipDirs = @('mods', 'saves', 'scripts', 'thumbnails', 'BepInEx\config', 'BepInEx\plugins', 'BepInEx\cache') | ForEach-Object { Join-Path $ClientSource $_ }
    Copy-Tree $ClientSource $clientDir (@('/XD') + $skipDirs + @('/XF', 'setting.xml', 'setting.xml.tr-aside', 'modconfig.xml', 'modrepos.xml', 'LogOutput.log', '*.log'))
    Copy-Tree (Join-Path $ClientSource 'BepInEx\plugins\StationeersLaunchPad') (Join-Path $clientDir 'BepInEx\plugins\StationeersLaunchPad') @()
    Set-LaunchPad $clientDir $clientData $false
}

$s = Version-Of (Join-Path $serverDir 'rocketstation_DedicatedServer_Data\StreamingAssets\version.ini')
$c = Version-Of (Join-Path $clientDir 'rocketstation_Data\StreamingAssets\version.ini')
Say "server build $s, client build $c$(if ($s -ne $c) { ': THEY DIFFER, the game refuses the join' })"
if ($s -ne $c) { exit 1 }
Say 'setup done'
exit 0
