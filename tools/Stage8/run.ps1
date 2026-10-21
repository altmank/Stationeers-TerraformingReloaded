<#
.SYNOPSIS
  Stage 8 of docs/COMET-ROCKETS.md: a dedicated server and a rendered client on one machine (bt) see the same
  comets, icons and impacts. Judges every line and prints 'Stage8 OK' only if all of them pass.

.DESCRIPTION
  Runs everything under -Root (G:\TRStage8 by default), which tools/Stage8/setup.ps1 made: our own server
  install, our own client install, and their own data folders. Nothing outside -Root is written, except the
  game's per-user PlayerCookie, which the client shares with the machine's own game: it is copied aside first
  and put back byte for byte at the end.

  The client draws frames, so this must run in the interactive desktop session of a logged-on user, not in
  session 0 (ssh, a service, a herdr pane started as a service). tools/Stage8/task.ps1 starts it that way
  through a one-off scheduled task and waits for it; or run it from a PowerShell window on that desktop.

  The run, in order (each a separate launch, every process started here and stopped here by its PID):
    server make   a new Creative Mars2 world, station trstage8, the server driver in phase make
    client first  joins; the planet sync, the comet list, the map icons, the sky body, comet A by rocket with
                  its warning, impact, push, suit damage, structures and darker sky, then console comet B
    client late   joins again while console comet C, called with nobody online, is playing
    (the server brings comet D down and saves 8 s into its fall; both are stopped)
    server load   the save, held paused until the client is back
    client rejoin joins the loaded world during D's fall, sees it land at the saved clock
  Every judged line, the server's, the client's and this script's own, goes to verdict.txt in the run's
  folder with the logs, the handshake files and the pictures (shots\).

.EXAMPLE
  .\run.ps1 -Root G:\TRStage8
#>
[CmdletBinding()]
param(
    [string]$Root = 'G:\TRStage8',
    # The run's own folder; a new one under <Root>\runs by default.
    [string]$Out = '',
    [string]$World = 'Mars2',
    # 0 picks a free one (see Select-Port); the client joins 127.0.0.1 on it.
    [int]$GamePort = 0,
    [int]$Width = 1600,
    [int]$Height = 900,
    # Already-built mod package (About + TerraformingReloaded.dll) and driver (About + TRStage8.dll); built from
    # this checkout against <Root>\client when left out.
    [string]$ModPackage = '',
    [string]$DriverPackage = '',
    [double]$MinFreeGB = 20,
    # Other servers on this machine whose setting.xml are read (never written) so the port chosen is not theirs.
    [string[]]$OtherServerRoots = @('G:\StationeersDedicated'),
    [int]$JoinMinutes = 20,
    [int]$PhaseMinutes = 120,
    # For a first probe of whether a client draws at all over ssh; the judged run needs the desktop.
    [switch]$AllowSessionZero
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot)
$serverDir = Join-Path $Root 'server'
$serverData = Join-Path $Root 'server-data'
$clientDir = Join-Path $Root 'client'
$clientData = Join-Path $Root 'client-data'
$serverExe = Join-Path $serverDir 'rocketstation_DedicatedServer.exe'
$clientExe = Join-Path $clientDir 'rocketstation.exe'
$lock = Join-Path $Root 'stage8.lock'
$station = 'trstage8'
if (-not $Out) { $Out = Join-Path $Root ('runs\' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$verdictFile = Join-Path $Out 'verdict.txt'
$finished = Join-Path $Out 'finished.txt'
Start-Transcript -Path (Join-Path $Out 'run-transcript.txt') | Out-Null

$script:server = $null
$script:client = $null
$script:locked = $false
$script:cookies = @()
$script:judged = New-Object System.Collections.Generic.List[string]
$cookieDir = Join-Path $env:USERPROFILE 'AppData\LocalLow\Rocketwerkz\rocketstation'
$cookieNames = 'PlayerCookie-v2.xml', 'PlayerCookie.xml'
$envKeys = 'TR_STAGE8_ROLE', 'TR_STAGE8_PHASE', 'TR_STAGE8_OUT', 'TR_STAGE8_ADDRESS'

function Say([string]$text) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $text) }

function Under-Root([string]$path) {
    return $path -and ([IO.Path]::GetFullPath($path)).StartsWith(([IO.Path]::GetFullPath($Root)).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Version-Of([string]$ini) {
    if (-not (Test-Path $ini)) { return $null }
    return ((Get-Content $ini | Select-String '^UPDATEVERSION=').Line -replace '^UPDATEVERSION=', '')
}

function Hashes([string]$folder) {
    if (-not (Test-Path $folder)) { return '' }
    return (Get-ChildItem $folder -Recurse -File -Filter *.dll | Sort-Object Name | ForEach-Object { $_.Name + '=' + (Get-FileHash $_.FullName -Algorithm SHA256).Hash }) -join ';'
}

# ---- preflight ---------------------------------------------------------------------------------------------

function Assert-Preflight {
    $session = (Get-Process -Id $PID).SessionId
    if ($session -eq 0 -and -not $AllowSessionZero) {
        throw 'This runs in session 0 (ssh or a service), where the client draws nothing. Start it in the desktop session with tools\Stage8\task.ps1.'
    }
    foreach ($exe in $serverExe, $clientExe) { if (-not (Test-Path $exe)) { throw "Missing $exe. Run tools\Stage8\setup.ps1 first." } }
    if (Test-Path $lock) {
        $held = 0
        [int]::TryParse(((Get-Content $lock -ErrorAction SilentlyContinue) | Select-Object -First 1), [ref]$held) | Out-Null
        if ($held -and (Get-Process -Id $held -ErrorAction SilentlyContinue)) { throw "Another Stage 8 run holds $lock (PID $held)." }
        Remove-Item $lock -Force
    }
    Set-Content -Path $lock -Value $PID
    $script:locked = $true
    $serverBuild = Version-Of (Join-Path $serverDir 'rocketstation_DedicatedServer_Data\StreamingAssets\version.ini')
    $clientBuild = Version-Of (Join-Path $clientDir 'rocketstation_Data\StreamingAssets\version.ini')
    if (-not $serverBuild -or $serverBuild -ne $clientBuild) { throw "The server's build ($serverBuild) is not the client's ($clientBuild): the game refuses the join. Run setup.ps1 again." }
    $slpServer = Hashes (Join-Path $serverDir 'BepInEx\plugins\StationeersLaunchPad')
    $slpClient = Hashes (Join-Path $clientDir 'BepInEx\plugins\StationeersLaunchPad')
    if (-not $slpServer -or $slpServer -ne $slpClient) { throw 'StationeersLaunchPad differs between the server and the client: LaunchPadBooster refuses the join. Run setup.ps1 again.' }
    if ((Hashes (Join-Path $serverDir 'BepInEx\core')) -ne (Hashes (Join-Path $clientDir 'BepInEx\core'))) { throw 'BepInEx differs between the server and the client. Run setup.ps1 again.' }
    foreach ($p in @(Get-Process -Name rocketstation, rocketstation_DedicatedServer -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $p.Path } catch { $path = $null }
        if ($path -and (Under-Root $path)) { throw "A Stage 8 instance is still running (PID $($p.Id), $path). Run tools\Stage8\stop.ps1 first." }
        if ($p.ProcessName -eq 'rocketstation') {
            throw "This machine's own game is running (PID $($p.Id)). It shares the client's PlayerCookie; close it first. Nothing here stops it."
        }
    }
    $free = [math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB, 1)
    if ($free -lt $MinFreeGB) { throw "Only $free GB of memory free; the server and the client need $MinFreeGB." }
    Say "preflight: session $session, build $serverBuild on both, the same LaunchPad on both, $free GB free"
    return $serverBuild
}

function Select-Port {
    if ($GamePort -gt 0) { return $GamePort }
    $used = New-Object 'System.Collections.Generic.HashSet[int]'
    foreach ($r in $OtherServerRoots) {
        if (-not (Test-Path $r)) { continue }
        foreach ($f in @(Get-ChildItem $r -Recurse -Depth 3 -Filter setting.xml -File -ErrorAction SilentlyContinue)) {
            try {
                $x = [xml](Get-Content $f.FullName -Raw)
                foreach ($n in 'GamePort', 'UpdatePort') {
                    $v = 0
                    if ([int]::TryParse([string]$x.SettingData.$n, [ref]$v) -and $v -gt 0) { [void]$used.Add($v); [void]$used.Add($v + 1) }
                }
            } catch { Say "could not read $($f.FullName): $($_.Exception.Message)" }
        }
    }
    foreach ($e in @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue)) { [void]$used.Add([int]$e.LocalPort) }
    foreach ($c in @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue)) { [void]$used.Add([int]$c.LocalPort) }
    for ($p = 27716; $p -lt 28700; $p += 3) {
        if (-not ($used.Contains($p) -or $used.Contains($p + 1) -or $used.Contains($p + 2))) {
            Say "ports: game $p, update $($p + 2) (in use here or by the other servers' settings: $(($used | Sort-Object | Where-Object { $_ -ge 27000 -and $_ -lt 28000 }) -join ', '))"
            return $p
        }
    }
    throw 'No free port pair found from 27716.'
}

# ---- setting files, mods, the cookie -------------------------------------------------------------------------

function Write-Xml([string]$path, [string]$version, [System.Collections.Specialized.OrderedDictionary]$values) {
    $lines = @('<?xml version="1.0" encoding="utf-8"?>',
        '<SettingData xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">',
        "  <SettingsVersion>$version</SettingsVersion>")
    foreach ($k in $values.Keys) { $lines += "  <$k>$([Security.SecurityElement]::Escape([string]$values[$k]))</$k>" }
    $lines += '</SettingData>'
    Set-Content -Path $path -Value $lines -Encoding UTF8
}

function Install-Mods([string]$data, [string]$modPackage, [string]$driverPackage) {
    $mods = Join-Path $data 'mods'
    if (Test-Path $mods) { Remove-Item $mods -Recurse -Force }
    foreach ($pair in @(@('TerraformingReloaded', $modPackage), @('TRStage8', $driverPackage))) {
        $to = Join-Path $mods $pair[0]
        New-Item -ItemType Directory -Force -Path $to | Out-Null
        Copy-Item (Join-Path $pair[1] '*') $to -Recurse -Force
    }
    $xml = @('<?xml version="1.0" encoding="utf-8"?>',
        '<ModConfig xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">',
        '  <Core Enabled="true">', '    <Path />', '  </Core>')
    foreach ($name in 'TerraformingReloaded', 'TRStage8') { $xml += @('  <Local Enabled="true">', "    <Path Value=`"$(Join-Path $mods $name)`" />", '  </Local>') }
    $xml += '</ModConfig>'
    Set-Content -Path (Join-Path $data 'modconfig.xml') -Value $xml -Encoding UTF8
}

function Save-Cookies {
    $aside = Join-Path $Out 'cookie-aside'
    New-Item -ItemType Directory -Force -Path $aside | Out-Null
    foreach ($n in $cookieNames) {
        $path = Join-Path $cookieDir $n
        $had = Test-Path $path
        if ($had) { Copy-Item $path (Join-Path $aside $n) -Force }
        $script:cookies += [pscustomobject]@{ Name = $n; Path = $path; Had = $had; Aside = (Join-Path $aside $n) }
    }
    # For tools\Stage8\stop.ps1, should this run be stopped before it puts them back itself.
    Set-Content -Path (Join-Path $aside 'manifest.txt') -Value ($script:cookies | ForEach-Object { "$($_.Name)=$(if ($_.Had) { 'present' } else { 'absent' })" })
    Say "PlayerCookie copied aside: $(($script:cookies | ForEach-Object { $_.Name + ' ' + $(if ($_.Had) { 'present' } else { 'absent' }) }) -join ', ')"
}

function Restore-Cookies {
    foreach ($c in $script:cookies) {
        if ($c.Had) { Copy-Item $c.Aside $c.Path -Force }
        elseif (Test-Path $c.Path) { Remove-Item $c.Path -Force }
    }
    if ($script:cookies.Count -gt 0) {
        Set-Content -Path (Join-Path (Join-Path $Out 'cookie-aside') 'restored.txt') -Value (Get-Date -Format o)
        Say 'PlayerCookie put back as it was'
    }
}

# ---- processes, by PID only ----------------------------------------------------------------------------------

function Clear-Env { foreach ($k in $envKeys) { Remove-Item "Env:$k" -ErrorAction SilentlyContinue } }

function Start-Server([string]$phase) {
    Remove-Item (Join-Path $serverDir 'BepInEx\LogOutput.log') -ErrorAction SilentlyContinue
    Clear-Env
    $env:TR_STAGE8_ROLE = 'server'; $env:TR_STAGE8_PHASE = $phase; $env:TR_STAGE8_OUT = $Out
    $log = Join-Path $Out "server-$phase-unity.log"
    $script:server = Start-Process -FilePath $serverExe -WorkingDirectory $serverDir -PassThru -WindowStyle Hidden `
        -ArgumentList @('-batchmode', '-nographics', '-logFile', "`"$log`"", '-file', 'start', $station, $World, 'Creative')
    Set-Content -Path (Join-Path $Root 'server.pid') -Value $script:server.Id
    Clear-Env
    Say "server ($phase) started, PID $($script:server.Id)"
}

function Start-Client([string]$phase) {
    Remove-Item (Join-Path $clientDir 'BepInEx\LogOutput.log') -ErrorAction SilentlyContinue
    Clear-Env
    $env:TR_STAGE8_ROLE = 'client'; $env:TR_STAGE8_PHASE = $phase; $env:TR_STAGE8_OUT = $Out; $env:TR_STAGE8_ADDRESS = "127.0.0.1:$script:port"
    $log = Join-Path $Out "client-$phase-unity.log"
    $script:client = Start-Process -FilePath $clientExe -WorkingDirectory $clientDir -PassThru `
        -ArgumentList @('-screen-fullscreen', '0', '-screen-width', "$Width", '-screen-height', "$Height", '-logFile', "`"$log`"")
    Set-Content -Path (Join-Path $Root 'client.pid') -Value $script:client.Id
    Clear-Env
    Say "client ($phase) started, PID $($script:client.Id)"
}

function Stop-Mine($proc, [string]$label, [string]$installDir, [string]$phase) {
    if ($proc -and -not $proc.HasExited) {
        $path = $null
        try { $path = $proc.Path } catch { $path = $null }
        if ($path -and -not (Under-Root $path)) { throw "PID $($proc.Id) is not ours ($path); not stopping it." }
        Stop-Process -Id $proc.Id -Force
        $proc.WaitForExit(20000) | Out-Null
        Say "$label ($phase) stopped, PID $($proc.Id)"
    }
    $bep = Join-Path $installDir 'BepInEx\LogOutput.log'
    if ($phase -and (Test-Path $bep)) { Copy-Item $bep (Join-Path $Out "$label-$phase-LogOutput.log") -Force }
}

function Wait-For([string[]]$files, [int]$seconds, [string]$what, [object[]]$alive) {
    $deadline = (Get-Date).AddSeconds($seconds)
    $last = Get-Date
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        foreach ($role in 'server', 'client') {
            $failed = Join-Path $Out "$role.failed"
            if (Test-Path $failed) { throw "The $role driver stopped: $((Get-Content $failed) -join ' | ')" }
        }
        foreach ($p in $alive) { if ($p -and $p.HasExited) { throw "PID $($p.Id) exited (code $($p.ExitCode)) while waiting for $what." } }
        if (@($files | Where-Object { -not (Test-Path (Join-Path $Out $_)) }).Count -eq 0) { return }
        if (((Get-Date) - $last).TotalSeconds -ge 60) {
            $last = Get-Date
            $judgedNow = @(Get-ChildItem $Out -Filter '*-judged.txt' -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName }).Count
            Say "waiting for $what ($judgedNow judged lines so far)"
        }
    }
    throw "Timed out after $seconds s waiting for $what."
}

function Remove-Handshake([string[]]$names) { foreach ($n in $names) { Remove-Item (Join-Path $Out $n) -ErrorAction SilentlyContinue } }

# ---- judging the logs ------------------------------------------------------------------------------------------

function Log-Lines([string]$label, [string]$phase) {
    $lines = @()
    foreach ($f in "$label-$phase-unity.log", "$label-$phase-LogOutput.log") {
        $path = Join-Path $Out $f
        if (Test-Path $path) { $lines += @(Get-Content $path) }
    }
    return $lines
}

function Add-Judged([string]$name, [bool]$pass, [string]$details) {
    $line = "Stage8: run judged $name $(if ($pass) { 'PASS' } else { 'FAIL' }) | $details"
    $script:judged.Add($line)
    Write-Host $line
}

function Read-File([string]$name) {
    $values = @{}
    $path = Join-Path $Out $name
    if (Test-Path $path) { foreach ($l in Get-Content $path) { $i = $l.IndexOf('='); if ($i -gt 0) { $values[$l.Substring(0, $i)] = $l.Substring($i + 1) } } }
    return $values
}

function Judge-Logs {
    $inv = [Globalization.CultureInfo]::InvariantCulture
    # @() each: a log of one line would come back as a string, on which -match answers true or false.
    $sMake = @(Log-Lines 'server' 'make'); $sLoad = @(Log-Lines 'server' 'load')
    $cFirst = @(Log-Lines 'client' 'first'); $cLate = @(Log-Lines 'client' 'late'); $cRejoin = @(Log-Lines 'client' 'rejoin')
    $ready = Read-File 'client.ready'; $name = $ready['name']
    $flying = Read-File 'server.flying'; $a = $flying['a']
    $fall = Read-File 'server.fall'
    $late = Read-File 'server.late-fired'
    $saving = Read-File 'server.saving'

    Add-Judged 'server mod live' ([bool]($sMake -match 'Terraforming Reloaded\] Active:') -and [bool]($sMake -match 'Planet tick is running\. live')) 'the server log reports the mod Active and its planet tick live'
    Add-Judged 'client mod live' ([bool]($cFirst -match 'Terraforming Reloaded\] Active:') -and [bool]($cFirst -match 'Planet tick is running\. off: client')) 'the client log reports the mod Active and its planet coming from the host'

    $warnS = @($sMake -match "\[TR comet land\] warning \| comet $a \|")
    Add-Judged 'server warning A' ($warnS.Count -ge 1 -and $warnS[0] -match "this machine's player: none \|" -and $warnS[0] -match [regex]::Escape("players: ") -and $warnS[0] -match [regex]::Escape($name)) "the server's warning for comet ${a}: $($warnS | Select-Object -First 1)"
    $expects = @($cFirst -match "warned-at \| comet $a \|") | Select-Object -First 1
    $want = if ($expects -match "expects '([^']+)'") { $Matches[1] } else { '?' }
    $warnC = @($cFirst -match "\[TR comet land\] warning \| comet $a \|")
    Add-Judged 'client warning A' ($warnC.Count -ge 1 -and $warnC[0] -match [regex]::Escape("this machine's player: $name |") -and $warnC[0].Contains($want)) "the client's own warning, expected to read '$want': $($warnC | Select-Object -First 1)"

    $seq = $fall['seq']
    $show = @($cFirst -match "\[TR comet\] show of event $seq\b")
    Add-Judged 'client show drawn A' ($show.Count -ge 1) "the client drew event ${seq}: $($show | Select-Object -First 1)"

    $thrown = @($cFirst -match '\[TR comet\] the wave threw this player at ([0-9.]+) kPa' | ForEach-Object { if ($_ -match 'at ([0-9.]+) kPa') { [double]::Parse($Matches[1], $inv) } })
    $expA = @($cFirst -match 'expects the wave at ([0-9.]+) kPa' | ForEach-Object { if ($_ -match 'at ([0-9.]+) kPa') { [double]::Parse($Matches[1], $inv) } }) | Select-Object -First 1
    $expB = @($cFirst -match 'expects the console wave at ([0-9.]+) kPa' | ForEach-Object { if ($_ -match 'at ([0-9.]+) kPa') { [double]::Parse($Matches[1], $inv) } }) | Select-Object -First 1
    $nearA = @($thrown | Where-Object { $expA -and [math]::Abs($_ - $expA) -le 0.1 * $expA })
    $nearB = @($thrown | Where-Object { $expB -and [math]::Abs($_ - $expB) -le 0.1 * $expB })
    Add-Judged 'client thrown A' ($nearA.Count -ge 1 -and $expA -gt 200) "the mod's own line on the client, thrown at $($thrown -join ', ') kPa; comet A expected about $expA kPa"
    Add-Judged 'client thrown B' ($nearB.Count -ge 1 -and $expB -lt 200) "comet B expected about $expB kPa"

    $suitUnity = @(Get-Content (Join-Path $Out 'server-make-unity.log') -ErrorAction SilentlyContinue | Where-Object { $_ -match '\[TR comet\] the wave damaged the suit of ' })
    Add-Judged 'server suit line A' (@($suitUnity | Where-Object { $_.Contains($name) }).Count -ge 1) "the host's own line: $($suitUnity | Select-Object -First 1)"
    Add-Judged 'server suit line once' ($suitUnity.Count -eq 1) "suit damage lines in the server's log: $($suitUnity.Count) (comet A at 300 kPa, none for comet B under 200)"

    $site = @($sMake -match 'Site: looking from ')
    $consoleSite = @($site | Where-Object { $_ -match [regex]::Escape("(player $name, who ran the command)") })
    Add-Judged 'server console site line' ($consoleSite.Count -ge 1) "the console comet the player called was aimed from them: $($consoleSite | Select-Object -First 1)"
    $origin = @($site | Where-Object { $_ -match '\(the world origin\)' })
    Add-Judged 'server late site line' ($origin.Count -ge 1) "the console comet called with nobody online, from the world origin and not the server's own scene camera: $($origin | Select-Object -First 1)"

    $lseq = $late['seq']
    $from = @($cLate -match "\[TR comet\] event $lseq from the host, ([0-9.]+) s after it began") | Select-Object -First 1
    $joined = @($cLate -match "\[TR comet\] show of event ${lseq}:.*joined at t=([0-9.]+) s") | Select-Object -First 1
    $x = if ($from -match 'host, ([0-9.]+) s after') { [double]::Parse($Matches[1], $inv) } else { [double]::NaN }
    $y = if ($joined -match 'joined at t=([0-9.]+) s') { [double]::Parse($Matches[1], $inv) } else { [double]::NaN }
    Add-Judged 'client late show line' ($x -gt 0 -and [math]::Abs($x - $y) -lt 1.5) "event $lseq reached the client $x s after it began; its show joined at t=$y s"

    $saved = $saving['clock']
    $landed = @($sLoad -match "\[TR comet land\] comet $($saving['comet']) landed at clock (\S+) ") | Select-Object -First 1
    $lc = if ($landed -match 'landed at clock (\S+) ') { [double]::Parse($Matches[1], $inv) } else { [double]::NaN }
    $sc = if ($saved) { [double]::Parse($saved, $inv) } else { [double]::NaN }
    Add-Judged 'server landed line' ($lc -ge $sc - 1e-6 -and ($lc - $sc) -lt 1.0) "the mod's own landing line after the load: $landed (saved to land at $saved)"

    $all = @($sMake) + @($sLoad) + @($cFirst) + @($cLate) + @($cRejoin)
    $frames = @($all | Where-Object { $_ -match 'TerraformingReloaded(\.[A-Za-z_][A-Za-z0-9_]*){2,}\s?\(' -or $_ -match 'IndexOutOfRangeException|Collection was modified' -or $_ -match '\[(Error|Fatal)\s*:\s*(Terraforming Reloaded|TR Stage8)\]' })
    Add-Judged 'no errors' ($frames.Count -eq 0) "stack frames in the mod or the driver, errors they logged, an index out of range or a list changed under a reader: $($frames.Count) $(($frames | Select-Object -First 3) -join ' / ')"
}

$expected = @(
    'server up', 'server no falling rocks', 'server sees the client', 'server set up', 'server sky body', 'server burn A', 'server suit damage A',
    'server saw the push A', 'server zone pass A', 'server console via serverrun', 'server console site', 'server console no suit damage', 'server saw the push B',
    'server fall saved', 'server fall back after load', 'server landed at the saved clock',
    'client joined', 'client planet sync', 'client host settings', 'client shared clock', 'client comet list', 'client rocket map icon', 'client sky body',
    'client map motherboard icons', 'client fall A', 'client impact event A', 'client pushed A', 'client suit damage A', 'client structures', 'client sky dim',
    'client console event', 'client console straight ahead', 'client pushed B', 'client joined late', 'client late join', 'client joined again',
    'client fall after rejoin', 'client impact after rejoin',
    'server mod live', 'client mod live', 'server warning A', 'client warning A', 'client show drawn A', 'client thrown A', 'client thrown B',
    'server suit line A', 'server suit line once', 'server console site line', 'server late site line', 'client late show line', 'server landed line', 'no errors'
)

function Write-Verdict([string]$problem) {
    $lines = @()
    foreach ($f in Get-ChildItem $Out -Filter '*-LogOutput.log' -ErrorAction SilentlyContinue) {
        $lines += @(Get-Content $f.FullName | Where-Object { $_ -match 'Stage8: (server|client) judged ' } | ForEach-Object { $_ -replace '^.*?Stage8: ', 'Stage8: ' })
    }
    $lines += @($script:judged)
    $names = @{}
    $failed = @()
    foreach ($l in $lines) {
        if ($l -match 'judged (.+?) (PASS|FAIL) \|') {
            $n = $Matches[1]
            if (-not $names.ContainsKey($n)) { $names[$n] = $true }
            if ($Matches[2] -eq 'FAIL') { $failed += $n }
        }
    }
    $missing = @($expected | Where-Object { -not $names.ContainsKey($_) })
    $ok = (-not $problem) -and $failed.Count -eq 0 -and $missing.Count -eq 0
    $out = @($lines)
    if ($problem) { $out += "Stage8: run stopped: $problem" }
    if ($missing.Count -gt 0) { $out += "Stage8: missing judged lines: $($missing -join ', ')" }
    if ($failed.Count -gt 0) { $out += "Stage8: failed: $(($failed | Select-Object -Unique) -join ', ')" }
    $out += $(if ($ok) { 'Stage8 OK' } else { 'Stage8 FAILED' })
    Set-Content -Path $verdictFile -Value $out -Encoding UTF8
    $out | Select-Object -Last 4 | Write-Host
    return $ok
}

# ---- the run ------------------------------------------------------------------------------------------------

$problem = $null
$ok = $false
try {
    $build = Assert-Preflight
    $script:port = Select-Port
    $secret = [guid]::NewGuid().ToString('N')

    if (-not $ModPackage -or -not $DriverPackage) {
        dotnet build (Join-Path $repo 'src\TerraformingReloaded.csproj') -c Release -p:GameDir="$clientDir" --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'Mod build failed.' }
        dotnet build (Join-Path $PSScriptRoot 'Stage8.csproj') -c Release -p:GameDir="$clientDir" --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'Stage8 driver build failed.' }
        $kit = Join-Path $Out 'kit'
        $ModPackage = Join-Path $kit 'mod'; $DriverPackage = Join-Path $kit 'driver'
        New-Item -ItemType Directory -Force -Path (Join-Path $ModPackage 'About'), (Join-Path $DriverPackage 'About') | Out-Null
        Copy-Item (Join-Path $repo 'About\*') (Join-Path $ModPackage 'About') -Recurse
        Copy-Item (Join-Path $repo 'src\bin\Release\TerraformingReloaded.dll') $ModPackage
        Copy-Item (Join-Path $PSScriptRoot 'About\*') (Join-Path $DriverPackage 'About')
        Copy-Item (Join-Path $PSScriptRoot 'bin\Release\TRStage8.dll') $DriverPackage
    }
    foreach ($pkg in $ModPackage, $DriverPackage) { if (-not (Test-Path (Join-Path $pkg 'About\About.xml'))) { throw "No About\About.xml in $pkg." } }

    # Our own data folders: their settings, their mods, and no save but the one this run makes.
    New-Item -ItemType Directory -Force -Path $serverData, $clientData | Out-Null
    $saves = Join-Path $serverData "saves\$station"
    if (Test-Path $saves) { Remove-Item $saves -Recurse -Force }
    $serverSettings = [ordered]@{
        AutoSave = 'false'; SavePath = $serverData; ServerName = 'TR-Stage8'; StartLocalHost = 'true'; ServerVisible = 'false'; ServerPassword = '';
        AdminPassword = ''; ServerAuthSecret = $secret; ServerMaxPlayers = '2'; UpdatePort = "$($script:port + 2)"; GamePort = "$script:port";
        UPNPEnabled = 'false'; LocalIpAddress = '127.0.0.1'; AutoPauseServer = 'false'
    }
    Write-Xml (Join-Path $serverData 'setting.xml') $build $serverSettings
    $clientSettings = [ordered]@{ AutoSave = 'false'; SavePath = $clientData; FullScreen = 'false'; ScreenWidth = "$Width"; ScreenHeight = "$Height"; ServerAuthSecret = $secret }
    Write-Xml (Join-Path $clientData 'setting.xml') $build $clientSettings
    Install-Mods $serverData $ModPackage $DriverPackage
    Install-Mods $clientData $ModPackage $DriverPackage
    Save-Cookies
    Say "run folder $Out; mod $((([xml](Get-Content (Join-Path $ModPackage 'About\About.xml') -Raw)).ModMetadata.Version))"

    # -- phase make, with the client's first and late joins
    Start-Server 'make'
    Wait-For @('server.ready') ($JoinMinutes * 60) 'the server to come up' @($script:server)
    Start-Client 'first'
    Wait-For @('client.first-done') ($PhaseMinutes * 60) 'the client''s first visit: comet A by rocket and console comet B' @($script:server, $script:client)
    Stop-Mine $script:client 'client' $clientDir 'first'
    Wait-For @('server.late-fired') 600 'the late comet, called with nobody online' @($script:server)
    Remove-Handshake @('client.ready', 'client.pos')
    Start-Client 'late'
    Wait-For @('client.late-done') ($JoinMinutes * 60) 'the client joining during the late comet' @($script:server, $script:client)
    Stop-Mine $script:client 'client' $clientDir 'late'
    Wait-For @('server.make-done') ($PhaseMinutes * 60) 'comet D brought down and the world saved in its fall' @($script:server)
    Start-Sleep -Seconds 3
    Stop-Mine $script:server 'server' $serverDir 'make'

    # -- phase load, with the client's rejoin
    Remove-Handshake @('server.ready', 'client.ready', 'client.pos', 'server.clock', 'client.clock')
    Start-Server 'load'
    Wait-For @('server.ready') ($JoinMinutes * 60) 'the server to load the save' @($script:server)
    Start-Client 'rejoin'
    Wait-For @('client.rejoin-done', 'server.load-done') ($JoinMinutes * 60) 'the client joining the loaded world and comet D landing' @($script:server, $script:client)
    Stop-Mine $script:client 'client' $clientDir 'rejoin'
    Stop-Mine $script:server 'server' $serverDir 'load'
    Judge-Logs
}
catch {
    $problem = $_.Exception.Message
    Say "STOPPED: $problem"
}
finally {
    foreach ($pair in @(@($script:client, 'client', $clientDir), @($script:server, 'server', $serverDir))) {
        try { Stop-Mine $pair[0] $pair[1] $pair[2] '' } catch { Say $_.Exception.Message }
    }
    foreach ($pair in @(@('client', $clientDir), @('server', $serverDir))) {
        $bep = Join-Path $pair[1] 'BepInEx\LogOutput.log'
        if ((Test-Path $bep) -and $problem) { Copy-Item $bep (Join-Path $Out "$($pair[0])-stopped-LogOutput.log") -Force }
    }
    Remove-Item (Join-Path $Root 'server.pid'), (Join-Path $Root 'client.pid') -ErrorAction SilentlyContinue
    Restore-Cookies
    Clear-Env
    try { $ok = Write-Verdict $problem } catch { Say "verdict: $($_.Exception.Message)" }
    if ($script:locked) { Remove-Item $lock -ErrorAction SilentlyContinue }
    Set-Content -Path $finished -Value $(if ($ok) { 'OK' } else { 'FAILED' })
    Stop-Transcript | Out-Null
}
if (-not $ok) { exit 1 }
exit 0
