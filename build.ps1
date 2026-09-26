<#
.SYNOPSIS
  Build Terraforming Reloaded, check its patches against the installed game, and stage the
  Steam Workshop package.

.DESCRIPTION
  Produces .\package\ containing the About folder and the built DLL. With -Deploy, also
  copies it into the local mods folder so the in-game mod list picks it up.

  The patch check applies the real patch set to the installed Assembly-CSharp outside the
  game. A failure there means the game changed under the mod; the build stops rather than
  stage something that would load and then switch itself off.

.EXAMPLE
  .\build.ps1 -Deploy
#>
[CmdletBinding()]
param(
    [string]$GameDir = $env:STATIONEERS_DIR,
    [switch]$Deploy,
    [switch]$Force,
    [switch]$SkipPatchCheck,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not $GameDir) {
    $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Stationeers'
}
if (-not (Test-Path (Join-Path $GameDir 'rocketstation_Data\Managed\Assembly-CSharp.dll'))) {
    throw "Stationeers not found at '$GameDir'. Pass -GameDir or set STATIONEERS_DIR."
}

# The version is written in three places and nothing else keeps them in step. A published
# build and a local build sharing a version number cannot be told apart afterwards.
$csproj = Join-Path $root 'src\TerraformingReloaded.csproj'
$plugin = Join-Path $root 'src\Plugin.cs'
$about = Join-Path $root 'About\About.xml'

$versions = [ordered]@{
    'csproj'    = ([regex]::Match((Get-Content $csproj -Raw), '<Version>([^<]+)</Version>')).Groups[1].Value
    'plugin'    = ([regex]::Match((Get-Content $plugin -Raw), 'PluginVersion\s*=\s*"([^"]+)"')).Groups[1].Value
    'About.xml' = ([regex]::Match((Get-Content $about -Raw), '<Version>([^<]+)</Version>')).Groups[1].Value
}
# @() so a single shared version stays an array; indexing a bare string yields one char.
$distinct = @($versions.Values | Sort-Object -Unique)
if ($distinct.Count -ne 1 -or [string]::IsNullOrWhiteSpace($distinct[0])) {
    $detail = ($versions.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '
    throw "Version mismatch: $detail. Bump all three to the same value."
}
$version = $distinct[0]

Write-Host "Building $version against $GameDir"
dotnet build $csproj -c $Configuration -p:GameDir="$GameDir" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$dll = Join-Path $root "src\bin\$Configuration\TerraformingReloaded.dll"
if (-not (Test-Path $dll)) { throw "Build produced no DLL at $dll" }

if (-not $SkipPatchCheck) {
    $check = Join-Path $root 'tools\PatchCheck\PatchCheck.csproj'
    dotnet build $check -c Release -p:GameDir="$GameDir" --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Patch check failed to build.' }
    & (Join-Path $root 'tools\PatchCheck\bin\Release\PatchCheck.exe') $GameDir (Split-Path $dll)
    if ($LASTEXITCODE -ne 0) { throw 'Patch check failed: the game no longer matches what the mod patches. See the FAILED lines above.' }
}

$package = Join-Path $root 'package'
if (Test-Path $package) { Remove-Item $package -Recurse -Force }
New-Item -ItemType Directory -Path $package | Out-Null
Copy-Item (Join-Path $root 'About') $package -Recurse
Copy-Item $dll $package
# Player documentation the Workshop description points at.
Copy-Item (Join-Path $root 'CURVES.md') $package
Copy-Item (Join-Path $root 'WORLDS.md') $package

# Steam rejects workshop previews over 1 MB.
$thumb = Get-Item (Join-Path $package 'About\thumb.png')
if ($thumb.Length -gt 1MB) {
    Write-Warning "About\thumb.png is $([math]::Round($thumb.Length/1MB,2)) MB. Steam caps previews at 1 MB and the game will silently fall back to a blank image."
}

Write-Host "Staged $package"

if ($Deploy) {
    # A running game holds the old DLL and keeps running it; LU may be playing.
    # A brand-new mod the game has never loaded is the one safe exception: pass -Force.
    if (-not $Force -and (Get-Process -Name 'rocketstation' -ErrorAction SilentlyContinue)) {
        throw 'Stationeers is running: close the game before deploying (or -Force for a mod it has never loaded).'
    }
    $mods = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\Stationeers\mods\TerraformingReloaded'
    if (Test-Path $mods) { Remove-Item $mods -Recurse -Force }
    New-Item -ItemType Directory -Path $mods -Force | Out-Null
    Copy-Item "$package\*" $mods -Recurse
    Write-Host "Deployed to $mods"
    Write-Host 'Launch Stationeers; the mod appears in the in-game mods list as a local mod.'
}
