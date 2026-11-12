<#
.SYNOPSIS
  Stops an interrupted Stage 8 run and puts back what it set aside. Touches only what the run made.

.DESCRIPTION
  Stops the server and the client whose PIDs the run wrote to <Root>\server.pid and <Root>\client.pid, and only
  when the process with that PID is our own install under <Root>; stops the run itself when the PID in
  <Root>\stage8.lock is a PowerShell whose command line runs tools\Stage8\run.ps1; puts the machine's PlayerCookie
  back from the run's folder (-Out, else the newest run under <Root>\runs that set one aside and did not put it
  back); removes the lock and the TR-Stage8 scheduled task. Never stops a process by name.

.EXAMPLE
  .\stop.ps1 -Root G:\TRStage8
#>
[CmdletBinding()]
param(
    [string]$Root = 'G:\TRStage8',
    [string]$Out = '',
    [string]$TaskName = 'TR-Stage8'
)

$ErrorActionPreference = 'Stop'

function Under-Root([string]$path) {
    return $path -and ([IO.Path]::GetFullPath($path)).StartsWith(([IO.Path]::GetFullPath($Root)).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}

$lock = Join-Path $Root 'stage8.lock'
if (Test-Path $lock) {
    $held = 0
    if ([int]::TryParse(((Get-Content $lock) | Select-Object -First 1), [ref]$held) -and $held) {
        $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId = $held" -ErrorAction SilentlyContinue).CommandLine
        # run.ps1 runs inside the PowerShell the task starts on task-go.ps1, or inside one started on run.ps1 itself.
        if ($cmd -and $cmd -match 'task-go\.ps1|Stage8\\run\.ps1') {
            Stop-Process -Id $held -Force
            Write-Host "stopped the run, PID $held"
        } elseif ($cmd) {
            Write-Host "PID $held in the lock is not a Stage 8 run ($cmd); left alone"
        }
    }
}

foreach ($name in 'client.pid', 'server.pid') {
    $file = Join-Path $Root $name
    if (-not (Test-Path $file)) { continue }
    $id = 0
    if ([int]::TryParse(((Get-Content $file) | Select-Object -First 1), [ref]$id) -and $id) {
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        $path = $null
        if ($p) { try { $path = $p.Path } catch { $path = $null } }
        if ($p -and (Under-Root $path)) {
            Stop-Process -Id $id -Force
            $p.WaitForExit(20000) | Out-Null
            Write-Host "stopped $name PID $id ($path)"
        } elseif ($p) {
            Write-Host "PID $id from $name is not ours ($path); left alone"
        }
    }
    Remove-Item $file -Force
}

if (-not $Out) {
    $Out = Get-ChildItem (Join-Path $Root 'runs') -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending |
        Where-Object { (Test-Path (Join-Path $_.FullName 'cookie-aside\manifest.txt')) -and -not (Test-Path (Join-Path $_.FullName 'cookie-aside\restored.txt')) } |
        Select-Object -First 1 -ExpandProperty FullName
}
if ($Out -and (Test-Path (Join-Path $Out 'cookie-aside\manifest.txt')) -and -not (Test-Path (Join-Path $Out 'cookie-aside\restored.txt'))) {
    $cookieDir = Join-Path $env:USERPROFILE 'AppData\LocalLow\Rocketwerkz\rocketstation'
    foreach ($line in Get-Content (Join-Path $Out 'cookie-aside\manifest.txt')) {
        $n, $state = $line -split '=', 2
        $path = Join-Path $cookieDir $n
        if ($state -eq 'present') { Copy-Item (Join-Path $Out "cookie-aside\$n") $path -Force }
        elseif (Test-Path $path) { Remove-Item $path -Force }
    }
    Set-Content -Path (Join-Path $Out 'cookie-aside\restored.txt') -Value (Get-Date -Format o)
    Write-Host "PlayerCookie put back from $Out"
}

if (Test-Path $lock) { Remove-Item $lock -Force }
# schtasks writes to stderr when the task is absent; under Windows PowerShell 5.1 with Stop that is a
# terminating NativeCommandError, so the query runs with Continue and only its exit code is read.
$saved = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& schtasks /Query /TN $TaskName 2>&1 | Out-Null
$queried = $LASTEXITCODE
$ErrorActionPreference = $saved
if ($queried -eq 0) { & schtasks /Delete /TN $TaskName /F | Out-Null; Write-Host "removed the scheduled task $TaskName" }
Write-Host 'stopped'
