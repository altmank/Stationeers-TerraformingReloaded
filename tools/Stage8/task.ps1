<#
.SYNOPSIS
  Starts tools/Stage8/run.ps1 in a logged-on user's desktop session through a one-off scheduled task, waits for
  it, prints its verdict and removes the task. For an operator on ssh (session 0), where a client draws nothing.

.DESCRIPTION
  Creates the task TR-Stage8 (schtasks /Create /IT /RU <User>), which runs only while that user is logged on and
  runs in their session; starts it; follows the run's transcript; and when the run writes finished.txt, prints
  the end of verdict.txt and deletes the task. The task runs <Root>\task-go.ps1, written here, which calls
  run.ps1 with the run's folder and any -RunArgs. If the ssh session ends, the task and the run go on, and the
  run cleans up after itself; tools/Stage8/stop.ps1 stops one that has to be stopped.

  <User> must be logged on at the machine's desktop (an RDP session left connected or disconnected), and the
  scheduled task is created as the user running this script, which needs no password for an interactive task.

.EXAMPLE
  .\task.ps1 -Root G:\TRStage8 -User lu
#>
[CmdletBinding()]
param(
    [string]$Root = 'G:\TRStage8',
    [string]$User = $env:USERNAME,
    # Further arguments for run.ps1, as one string, e.g. "-World Mars2 -MinFreeGB 16".
    [string]$RunArgs = '',
    [int]$TimeoutMinutes = 480,
    [string]$TaskName = 'TR-Stage8'
)

$ErrorActionPreference = 'Stop'
# schtasks /IT /RU on bt refuses a bare user name ("The task XML contains a value which is incorrectly
# formatted"); the machine-qualified name works, so a bare name is qualified with this machine's.
if ($User -notlike '*\*') { $User = "$env:COMPUTERNAME\$User" }
$run = Join-Path $PSScriptRoot 'run.ps1'
$out = Join-Path $Root ('runs\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $out | Out-Null
$go = Join-Path $Root 'task-go.ps1'

# schtasks writes to stderr when the task is absent; under Windows PowerShell 5.1 with Stop that is a
# terminating NativeCommandError, so the query runs with Continue and only its exit code is read.
$saved = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& schtasks /Query /TN $TaskName 2>&1 | Out-Null
$queried = $LASTEXITCODE
$ErrorActionPreference = $saved
if ($queried -eq 0) { throw "A scheduled task $TaskName already exists. If no run is going, remove it: schtasks /Delete /TN $TaskName /F" }

Set-Content -Path $go -Encoding UTF8 -Value @(
    '$ErrorActionPreference = ''Stop''',
    "& '$($run -replace "'", "''")' -Root '$($Root -replace "'", "''")' -Out '$($out -replace "'", "''")' $RunArgs",
    'exit $LASTEXITCODE'
)
$action = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$go`""
& schtasks /Create /TN $TaskName /TR $action /SC ONCE /ST 00:00 /IT /RU $User /F | Write-Host
if ($LASTEXITCODE -ne 0) { throw "schtasks /Create failed ($LASTEXITCODE)." }
& schtasks /Run /TN $TaskName | Write-Host
if ($LASTEXITCODE -ne 0) { throw "schtasks /Run failed ($LASTEXITCODE)." }
Write-Host "Started $TaskName in $User's session; the run's folder is $out"

$transcript = Join-Path $out 'run-transcript.txt'
$finished = Join-Path $out 'finished.txt'
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$shown = 0
$started = Get-Date
try {
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 10
        if (-not (Test-Path $transcript)) {
            if (((Get-Date) - $started).TotalSeconds -gt 120) {
                throw "The task did not start the run within 120 s. Is $User logged on at the desktop? schtasks /Query /TN $TaskName /V /FO LIST says: $((& schtasks /Query /TN $TaskName /V /FO LIST) -join ' ')"
            }
            continue
        }
        $lines = @(Get-Content $transcript -ErrorAction SilentlyContinue)
        if ($lines.Count -gt $shown) {
            $lines[$shown..($lines.Count - 1)] | Where-Object { $_ -match '^\[\d\d:\d\d:\d\d\]|judged|Stage8' } | Write-Host
            $shown = $lines.Count
        }
        if (Test-Path $finished) { break }
    }
    if (-not (Test-Path $finished)) { throw "No finished.txt in $out after $TimeoutMinutes minutes; the run may still be going. tools\Stage8\stop.ps1 -Out $out stops it." }
    Write-Host ''
    Get-Content (Join-Path $out 'verdict.txt') -ErrorAction SilentlyContinue | Where-Object { $_ -match 'FAIL|missing|stopped|Stage8 OK|Stage8 FAILED' } | Write-Host
    Write-Host "Verdict, logs and pictures: $out"
    if ((Get-Content $finished) -ne 'OK') { exit 1 }
}
finally {
    if (Test-Path $finished) { & schtasks /Delete /TN $TaskName /F | Out-Null }
}
exit 0
