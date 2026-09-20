param([string]$GameDir = $env:STATIONEERS_DIR)
$ErrorActionPreference = "Stop"
if (-not $GameDir) { $GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Stationeers" }
$root = Split-Path (Split-Path $PSScriptRoot)
dotnet build (Join-Path $root "src\TerraformingReloaded.csproj") -c Release -p:GameDir="$GameDir" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Mod build failed." }
dotnet build (Join-Path $PSScriptRoot "PatchCheck.csproj") -c Release -p:GameDir="$GameDir" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "PatchCheck build failed." }
& (Join-Path $PSScriptRoot "bin\Release\PatchCheck.exe") $GameDir (Join-Path $root "srcin\Release")
exit $LASTEXITCODE
