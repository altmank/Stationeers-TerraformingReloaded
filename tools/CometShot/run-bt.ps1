<#
.SYNOPSIS
  Runs the comet screenshot rig on bt and brings the pictures back.

.DESCRIPTION
  Builds the mod and the driver here, copies them with run.ps1 to bt over ssh (host alias 'bt'),
  runs run.ps1 there against bt's Steam copy of the game, and copies the PNGs and logs back to
  -Out. Every run.ps1 rule applies on bt: it refuses while bt's game is open, and removes what it
  made in bt's game folder afterwards.

.EXAMPLE
  .\run-bt.ps1 -Comet '1e6 CarbonDioxide:60 Water:25 NitrousOxide:15 preview'
#>
[CmdletBinding()]
param(
    [string]$Comet = '1e6 CarbonDioxide:60 Water:25 NitrousOxide:15 preview',
    [string]$World = 'Mars2',
    [string]$Times = '2,8,12,14,15,15.5,15.8,16.05,16.2,16.5,17,18,19.5,21,23,26,30,36,45,60,80',
    [int]$Width = 1600,
    [int]$Height = 900,
    [double]$Pitch = -6,
    [string]$Yaw = 'centre',
    [double]$Settle = 20,
    [string]$Spot = 'start',
    [string]$Sun = '0.35',
    [double]$TerrainWait = 90,
    [double]$Fov = 0,
    [double]$Back = 0,
    [string]$Out = (Join-Path $env:TEMP ('tr-cometshot\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [string]$RemoteGameDir = 'G:\SteamLibrary\steamapps\common\Stationeers',
    [string]$RemoteDir = 'C:\Users\LU\AppData\Local\Temp\tr-cometshot-rig'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot)

dotnet build (Join-Path $root 'src\TerraformingReloaded.csproj') -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Mod build failed.' }
dotnet build (Join-Path $PSScriptRoot 'CometShot.csproj') -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'CometShot build failed.' }

# Staged locally as the tree bt gets, so one scp carries everything.
$stage = Join-Path $env:TEMP 'tr-cometshot-stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $stage 'rig\About'), (Join-Path $stage 'mod\About') -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'run.ps1') (Join-Path $stage 'rig')
Copy-Item (Join-Path $PSScriptRoot 'About\*') (Join-Path $stage 'rig\About')
Copy-Item (Join-Path $PSScriptRoot 'bin\Release\TRCometShot.dll') (Join-Path $stage 'rig')
Copy-Item (Join-Path $root 'About\*') (Join-Path $stage 'mod\About')
Copy-Item (Join-Path $root 'src\bin\Release\TerraformingReloaded.dll') (Join-Path $stage 'mod')

# The arguments go in a script rather than through ssh's quoting, so a comet's colons and spaces arrive intact.
$c = [cultureinfo]::InvariantCulture
$go = @"
`$ErrorActionPreference = 'Stop'
& '$RemoteDir\rig\run.ps1' -GameDir '$RemoteGameDir' -ModPackage '$RemoteDir\mod' -ShotDll '$RemoteDir\rig\TRCometShot.dll' ``
    -Out '$RemoteDir\out' -Comet '$($Comet -replace "'", "''")' -World '$World' -Times '$Times' ``
    -Width $Width -Height $Height -Pitch $($Pitch.ToString($c)) -Yaw '$Yaw' -Settle $($Settle.ToString($c)) ``
    -Spot '$Spot' -Sun '$Sun' -TerrainWait $($TerrainWait.ToString($c)) -Fov $($Fov.ToString($c)) -Back $($Back.ToString($c))
"@
Set-Content -Path (Join-Path $stage 'go.ps1') -Value $go -Encoding UTF8

# bt's ssh shell is cmd, which would take a pipe for its own; PowerShell gets its script encoded instead.
function Invoke-Bt([string]$script) {
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    & ssh -o BatchMode=yes bt "powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded"
}
Invoke-Bt "if (Test-Path '$RemoteDir') { Remove-Item '$RemoteDir' -Recurse -Force }; New-Item -ItemType Directory -Path '$RemoteDir' -Force | Out-Null"
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the folder on bt.' }
& scp -o BatchMode=yes -r "$stage\rig" "$stage\mod" "$stage\go.ps1" "bt:$($RemoteDir -replace '\\', '/')/"
if ($LASTEXITCODE -ne 0) { throw 'Could not copy the rig to bt.' }

Invoke-Bt "& '$RemoteDir\go.ps1'; exit `$LASTEXITCODE"
$code = $LASTEXITCODE

New-Item -ItemType Directory -Path $Out -Force | Out-Null
& scp -o BatchMode=yes -r "bt:$($RemoteDir -replace '\\', '/')/out/*" $Out
Write-Host "Pictures and logs: $Out"
if ($code -ne 0) { throw "The rig failed on bt (exit $code); see the logs in $Out." }
