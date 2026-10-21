<#
.SYNOPSIS
  Screenshots of a comet impact, taken by the game itself in an off-screen instance.

.DESCRIPTION
  Starts Stationeers in batch mode with graphics on (no window, but cameras still render), on a
  new throwaway world, with this build of the mod and the TR CometShot driver. The driver stands a
  camera on the ground, runs 'terraform comet <Comet>', and saves a PNG at each of -Times seconds
  after it. Like tools/LiveCheck, the instance uses the game's install folder as its save root, so
  no real save or mod list is read or written; what it creates there is removed after, and a
  settings file already there is put back byte for byte.

  Meant to run on bt (run-bt.ps1 copies the builds over and runs this there), not beside the
  owner's game.

.EXAMPLE
  .\run.ps1 -Comet '1e6 CarbonDioxide:60 Water:25 NitrousOxide:15 preview'
#>
[CmdletBinding()]
param(
    [string]$Comet = '1e6 CarbonDioxide:60 Water:25 NitrousOxide:15',
    [string]$World = 'Mars2',
    [string]$Times = '2,8,12,14,15,15.5,15.8,16.05,16.2,16.5,17,18,19.5,21,23,26,30,36,45,60,80',
    [string]$Out = (Join-Path $env:TEMP 'tr-cometshot'),
    [int]$Width = 1600,
    [int]$Height = 900,
    [double]$Pitch = -6,
    [double]$Yaw = 0,
    [double]$Settle = 20,
    [string]$GameDir = $env:STATIONEERS_DIR,
    [int]$TimeoutSeconds = 420,
    # Already-built mod package (About + DLL) and driver DLL, for a machine with the game but not the SDK.
    [string]$ModPackage,
    [string]$ShotDll
)

$ErrorActionPreference = 'Stop'
if (-not $GameDir) { $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Stationeers' }
$root = Split-Path (Split-Path $PSScriptRoot)
$exe = Join-Path $GameDir 'rocketstation.exe'
if (-not (Test-Path $exe)) { throw "Stationeers not found at '$GameDir'." }
if (Get-Process rocketstation -ErrorAction SilentlyContinue) { throw 'Stationeers is running. Close it first.' }

# What a run creates in the game folder and removes afterwards.
$names = 'mods', 'saves', 'scripts', 'thumbnails', 'modconfig.xml', 'modrepos.xml'
$preexisting = @($names | Where-Object { Test-Path (Join-Path $GameDir $_) })
if ($preexisting.Count -gt 0) {
    throw "Already present in the game folder: $($preexisting -join ', '). A previous run was interrupted; remove them first."
}
# A settings file already there is the machine's own: the game rewrites it, so it is kept and put back.
$settingFile = Join-Path $GameDir 'setting.xml'
$settingSaved = if (Test-Path $settingFile) { [System.IO.File]::ReadAllBytes($settingFile) } else { $null }

if ($ModPackage) {
    $modDll = Join-Path $ModPackage 'TerraformingReloaded.dll'
    $modAbout = Join-Path $ModPackage 'About'
    if (-not (Test-Path $modDll) -or -not (Test-Path $ShotDll)) { throw "No prebuilt DLLs at '$ModPackage' / '$ShotDll'." }
} else {
    dotnet build (Join-Path $root 'src\TerraformingReloaded.csproj') -c Release -p:GameDir="$GameDir" --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Mod build failed.' }
    dotnet build (Join-Path $PSScriptRoot 'CometShot.csproj') -c Release -p:GameDir="$GameDir" --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'CometShot build failed.' }
    $modDll = Join-Path $root 'src\bin\Release\TerraformingReloaded.dll'
    $modAbout = Join-Path $root 'About'
    $ShotDll = Join-Path $PSScriptRoot 'bin\Release\TRCometShot.dll'
}

if (Test-Path $Out) { Remove-Item (Join-Path $Out '*') -Recurse -Force }
New-Item -ItemType Directory -Path $Out -Force | Out-Null

$bepLog = Join-Path $GameDir 'BepInEx\LogOutput.log'
$unityLog = Join-Path $Out 'unity.log'
$envKeys = 'TR_COMETSHOT_ARGS', 'TR_COMETSHOT_DIR', 'TR_COMETSHOT_TIMES', 'TR_COMETSHOT_W', 'TR_COMETSHOT_H', 'TR_COMETSHOT_PITCH', 'TR_COMETSHOT_YAW', 'TR_COMETSHOT_SETTLE'
$game = $null
try {
    $mods = Join-Path $GameDir 'mods'
    New-Item -ItemType Directory -Path (Join-Path $mods 'TRCometShot\About') -Force | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'About\*') (Join-Path $mods 'TRCometShot\About')
    Copy-Item $ShotDll (Join-Path $mods 'TRCometShot')
    New-Item -ItemType Directory -Path (Join-Path $mods 'TerraformingReloaded\About') -Force | Out-Null
    Copy-Item (Join-Path $modAbout '*') (Join-Path $mods 'TerraformingReloaded\About')
    Copy-Item $modDll (Join-Path $mods 'TerraformingReloaded')

    $env:TR_COMETSHOT_ARGS = $Comet
    $env:TR_COMETSHOT_DIR = $Out
    $env:TR_COMETSHOT_TIMES = $Times
    $env:TR_COMETSHOT_W = "$Width"
    $env:TR_COMETSHOT_H = "$Height"
    $env:TR_COMETSHOT_PITCH = $Pitch.ToString([cultureinfo]::InvariantCulture)
    $env:TR_COMETSHOT_YAW = $Yaw.ToString([cultureinfo]::InvariantCulture)
    $env:TR_COMETSHOT_SETTLE = $Settle.ToString([cultureinfo]::InvariantCulture)
    Remove-Item $bepLog -ErrorAction SilentlyContinue
    $game = Start-Process $exe -WorkingDirectory $GameDir -PassThru -ArgumentList @('-batchmode', '-logFile', "`"$unityLog`"", '-new', $World)
    Write-Host "Started off-screen instance, PID $($game.Id), on a new $World"
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        if ($game.HasExited) { throw "The instance exited early (code $($game.ExitCode)). See $unityLog" }
        if (Test-Path (Join-Path $Out 'done.txt')) { break }
    }
    if (-not (Test-Path (Join-Path $Out 'done.txt'))) { throw "Timed out after $TimeoutSeconds s. See $bepLog" }
    Get-Content $bepLog | Where-Object { $_ -match 'CometShot|TR comet|Terraforming' } | Write-Host
    Get-ChildItem $Out -Filter *.png | ForEach-Object { Write-Host $_.FullName }
}
finally {
    if ($game -and -not $game.HasExited) {
        Stop-Process -Id $game.Id -Force
        $game.WaitForExit(15000) | Out-Null
    }
    foreach ($key in $envKeys) { Remove-Item "Env:$key" -ErrorAction SilentlyContinue }
    if (Test-Path $bepLog) { Copy-Item $bepLog (Join-Path $Out 'LogOutput.log') -ErrorAction SilentlyContinue }
    foreach ($name in $names) {
        $path = Join-Path $GameDir $name
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }
    if ($settingSaved) { [System.IO.File]::WriteAllBytes($settingFile, $settingSaved) }
    elseif (Test-Path $settingFile) { Remove-Item $settingFile -Force }
}
