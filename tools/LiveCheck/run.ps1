<#
.SYNOPSIS
  Live tests: run the mod in a headless throwaway Mars world and check that gas is conserved.

.DESCRIPTION
  Scenarios
    (default)  Put 100000 mol of CO2 into one outdoor cell. Tank plus outdoor cells must stay
               level while the gas spreads over thousands of cells and drains into the tank, and
               the tank must end up holding what was injected.
    -SaveLoad  Same injection, then save while the gas is spread over thousands of outdoor cells,
               stop, load that save in a fresh instance. The total after the load must equal the
               total at the save. The game's loader rebuilds every saved outdoor cell by cloning
               the planet's air, which would debit the tank once per cell if the mod let it.
    -Reset     The way out of the mod. Terraform (the injection drains into the planet), then put
               every other part of the saved planet state off stock too: ice caps, clouds, both
               heat stores. Run 'terraform reset confirm', save, stop. Then load that save in a
               fresh instance WITHOUT the mod: the unmodded game must find a stock planet.
    -Dump <file>  No test. The unmodded game writes its own evaluated greenhouse and temperature
               curves, every world's starting air and volume, and per-gas constants to a JSON
               file, which tools/Balance models from.
    -Vanilla   The default scenario without the mod, as a control. Not judged: in the unmodded
               game the tank never moves and the injected gas disappears.

  A headless instance uses the game's install folder as its save root, not Documents\My Games,
  so no real save, mod list or setting is read or written. Everything this script creates there
  is removed at the end, and it refuses to start if any of it already exists.

  Things learned the hard way, all checked here rather than assumed:
    - LaunchPad lists <game>\mods before the game creates that folder, so it must exist first.
    - With -logFile, plugin output goes to BepInEx\LogOutput.log, not the Unity log.
    - A headless world pauses itself until a player acts; the driver unpauses it.
    - Launch arguments that change settings are overwritten when the settings file loads.
    - The instance opens a LAN host on port 27016 for as long as it runs.
    - Samples must be taken on the simulation thread. Off it, a mole reports a quantity cached
      at the start of the tick, so totals read from Update swing by thousands of moles that are
      not there. The game's own save reads that same cache; see Guards.SavePrefix.

  Close Stationeers first: the script refuses to run beside another instance.

.EXAMPLE
  .\tools\LiveCheck\run.ps1
  .\tools\LiveCheck\run.ps1 -SaveLoad
#>
[CmdletBinding()]
param(
    [string]$GameDir = $env:STATIONEERS_DIR,
    [switch]$Vanilla,
    [switch]$SaveLoad,
    [switch]$Reset,
    [string]$Dump,
    [switch]$Observe,
    [switch]$Model,
    [double]$HeatK = 0,
    [double]$Tolerance = 0.5,
    [string]$World = 'Mars2',
    [double]$DaySpeed = 10,
    [int]$Ticks = 260,
    [string]$SetAir = '',
    [string]$SetAir2 = '',
    [int]$SetAir2Tick = 0,
    [switch]$Keep,
    [int]$TimeoutSeconds = 360     # raised automatically for long -Observe runs
)

$ErrorActionPreference = 'Stop'
if (-not $GameDir) { $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Stationeers' }
$root = Split-Path (Split-Path $PSScriptRoot)
$exe = Join-Path $GameDir 'rocketstation.exe'
if (-not (Test-Path $exe)) { throw "Stationeers not found at '$GameDir'." }
if (Get-Process rocketstation -ErrorAction SilentlyContinue) { throw 'Stationeers is running. Close it first.' }
if (@(($Vanilla -and -not ($Observe -or $Model)), $SaveLoad, $Reset, [bool]$Dump | Where-Object { $_ }).Count -gt 1) { throw 'Pick one of -Vanilla, -SaveLoad, -Reset and -Dump.' }

dotnet build (Join-Path $root 'src\TerraformingReloaded.csproj') -c Release -p:GameDir="$GameDir" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Mod build failed.' }
dotnet build (Join-Path $PSScriptRoot 'LiveCheck.csproj') -c Release -p:GameDir="$GameDir" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'LiveCheck build failed.' }

# Only what this run creates is removed afterwards.
$names = 'mods', 'saves', 'scripts', 'thumbnails', 'modconfig.xml', 'modrepos.xml', 'setting.xml'
$preexisting = @($names | Where-Object { Test-Path (Join-Path $GameDir $_) })
if ($preexisting.Count -gt 0) {
    throw "Already present in the game folder: $($preexisting -join ', '). A headless run would mix with them; move them aside first."
}

$bepLog = Join-Path $GameDir 'BepInEx\LogOutput.log'
$curvesFile = Join-Path $GameDir 'BepInEx\config\TerraformingReloaded.curves.xml'
$curvesExisted = Test-Path $curvesFile
$unityLog = Join-Path $env:TEMP 'tr-livecheck-unity.log'
$script:game = $null

function Stop-Game {
    if ($script:game -and -not $script:game.HasExited) {
        Stop-Process -Id $script:game.Id -Force
        $script:game.WaitForExit(15000) | Out-Null
    }
    $script:game = $null
}

# Starts an instance and returns the log once $until is true of it, or throws at the timeout.
function Invoke-Game([string[]]$arguments, [hashtable]$environment, [scriptblock]$until, [string]$what) {
    Remove-Item $bepLog, $unityLog -ErrorAction SilentlyContinue
    foreach ($key in 'TR_LIVECHECK_INJECT', 'TR_LIVECHECK_SAVE_AFTER_TICKS', 'TR_LIVECHECK_RESET', 'TR_LIVECHECK_DUMP', 'TR_LIVECHECK_OBSERVE', 'TR_LIVECHECK_DAYSPEED', 'TR_LIVECHECK_SETAIR', 'TR_LIVECHECK_HEATK', 'TR_LIVECHECK_SETAIR2', 'TR_LIVECHECK_SETAIR2_TICK') { Remove-Item "Env:$key" -ErrorAction SilentlyContinue }
    foreach ($key in $environment.Keys) { Set-Item "Env:$key" $environment[$key] }
    $script:game = Start-Process $exe -WorkingDirectory $GameDir -PassThru -ArgumentList (@('-batchmode', '-nographics', '-logFile', "`"$unityLog`"") + $arguments)
    Write-Host "Started headless instance, PID $($script:game.Id): waiting for $what"
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        if ($script:game.HasExited) { throw "The instance exited early (code $($script:game.ExitCode)). See $unityLog" }
        if (Test-Path $bepLog) {
            $log = @(Get-Content $bepLog)
            if (& $until $log) { return $log }
        }
    }
    throw "Timed out after $TimeoutSeconds s waiting for $what. See $bepLog"
}

function Get-Rows($log) {
    foreach ($line in $log) {
        if ($line -match 'LiveCheck: tick\s+(\d+) \| tank ([\d.]+) \| tank CO2 ([\d.]+) \| outdoor cells\s+(\d+) holding ([\d.]+) \| SUM ([\d.]+)') {
            [pscustomobject]@{ Tick = [int]$Matches[1]; Tank = [double]$Matches[2]; Co2 = [double]$Matches[3]; Cells = [int]$Matches[4]; Held = [double]$Matches[5]; Sum = [double]$Matches[6] }
        }
    }
}

function Get-States($log) {
    foreach ($line in $log) {
        if ($line -match 'LiveCheck: state tick (\d+) \| caps ([-\d.E+]+) \| clouds ([-\d.E+]+) \| latentJ ([-\d.E+]+) \| externalJ ([-\d.E+]+)') {
            [pscustomobject]@{ Tick = [int]$Matches[1]; Caps = [double]$Matches[2]; Clouds = [double]$Matches[3]; LatentJ = [double]$Matches[4]; ExternalJ = [double]$Matches[5] }
        }
    }
}

function Assert-ModLive($log) {
    if (-not ($log -match 'Terraforming Reloaded\] Active:')) { throw 'The mod never reported Active. It did not load or did not arm.' }
    if (-not ($log -match 'Planet tick is running\. live')) { throw 'The planet tick never ran with the mod live.' }
    if (-not ($log -match 'Self-test passed: take and give')) { throw 'The in-game self-test did not pass.' }
    if ($log -match '\[(Error|Warning)\s*:\s*Terraforming Reloaded\]') { throw 'The mod logged a warning or error.' }
}

function Get-Spread($values) {
    $m = $values | Measure-Object -Maximum -Minimum
    return $m.Maximum - $m.Minimum
}

try {
    $mods = Join-Path $GameDir 'mods'
    New-Item -ItemType Directory -Path (Join-Path $mods 'TRLiveCheck\About') -Force | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'About\*') (Join-Path $mods 'TRLiveCheck\About')
    Copy-Item (Join-Path $PSScriptRoot 'bin\Release\TRLiveCheck.dll') (Join-Path $mods 'TRLiveCheck')
    if (-not $Vanilla -and -not $Dump) {
        New-Item -ItemType Directory -Path (Join-Path $mods 'TerraformingReloaded\About') -Force | Out-Null
        Copy-Item (Join-Path $root 'About\*') (Join-Path $mods 'TerraformingReloaded\About')
        Copy-Item (Join-Path $root 'src\bin\Release\TerraformingReloaded.dll') (Join-Path $mods 'TerraformingReloaded')
    }

    if ($Model) { $Observe = $true }
    if ($Observe -and $TimeoutSeconds -lt ($Ticks * 0.6 + 240)) { $TimeoutSeconds = [int]($Ticks * 0.6 + 240) }
    if ($Observe) {
        # Not judged: prints the planet through a fast day, for checking tools/Balance against the game.
        $environment = @{ TR_LIVECHECK_OBSERVE = '1'; TR_LIVECHECK_INJECT = '0'; TR_LIVECHECK_DAYSPEED = $DaySpeed.ToString([cultureinfo]::InvariantCulture) }
        if ($SetAir) { $environment.TR_LIVECHECK_SETAIR = $SetAir }
        if ($SetAir2) { $environment.TR_LIVECHECK_SETAIR2 = $SetAir2; $environment.TR_LIVECHECK_SETAIR2_TICK = "$SetAir2Tick" }
        if ($HeatK -ne 0) { $environment.TR_LIVECHECK_HEATK = $HeatK.ToString([cultureinfo]::InvariantCulture) }
        $log = Invoke-Game @('-new', $World) $environment `
            { param($l) $o = @($l -match 'LiveCheck: obs tick (\d+)'); $o.Count -gt 0 -and [int](($o[-1] -replace '.*obs tick (\d+).*', '$1')) -ge $Ticks } "a fast day on $World"
        if (-not $Vanilla) { Assert-ModLive $log }
        if ($Model) {
            # Judged: the simulator rebuilt from what the game reported has to give the game's temperature.
            # Samples before tick 15 are skipped: the air is set at tick 6 and the readings settle after it.
            $obs = @($log -match 'LiveCheck: obs tick') | ForEach-Object { $_ -replace '.*LiveCheck: obs ', '' }
            $obs | python (Join-Path $root 'tools\Balance\compare.py') $World $Tolerance 15 $SetAir2Tick | Write-Host
            if ($LASTEXITCODE -ne 0) { throw 'LiveCheck FAILED: the simulator and the game disagree.' }
            Write-Host 'LiveCheck OK: the simulator matches the game.'
            return
        }
        @($log -match 'LiveCheck: (obs tick|planet air|sky running)|Self-test|temperature response') | ForEach-Object { ($_ -replace '^\[[^\]]*\]\s*', '') -replace 'LiveCheck: obs ', '' } | Write-Host
        return
    }

    if ($Dump) {
        # The unmodded game evaluates its own curves and world data into a JSON file for tools/Balance.
        $target = [System.IO.Path]::GetFullPath($Dump)
        Remove-Item $target -ErrorAction SilentlyContinue
        $log = Invoke-Game @('-new', 'Mars2') @{ TR_LIVECHECK_DUMP = $target; TR_LIVECHECK_INJECT = '0' } `
            { param($l) @($l -match 'LiveCheck: (dumped game data|stopped)').Count -gt 0 } 'the game data dump'
        if ($log -match 'LiveCheck stopped') { throw ('The dump failed: ' + (@($log -match 'LiveCheck stopped')[0])) }
        if (-not (Test-Path $target)) { throw "No dump at $target" }
        Write-Host ("Dumped {0:N0} bytes to {1}" -f (Get-Item $target).Length, $target)
        return
    }

    if ($Reset) {
        $station = 'trlivecheck'
        $log = Invoke-Game @('-file', 'start', $station, 'Mars2') @{ TR_LIVECHECK_RESET = '1' } `
            { param($l) @($l -match 'LiveCheck: saving at tick').Count -gt 0 } 'terraform, reset and save'
        Assert-ModLive $log
        Start-Sleep -Seconds 20
        $log = @(Get-Content $bepLog)
        Stop-Game
        $resetLine = @($log -match 'running terraform reset confirm at tick')
        if ($resetLine.Count -eq 0) { throw 'The driver never ran the reset.' }
        $resetTick = [int]($resetLine[0] -replace '.*at tick (\d+).*', '$1')
        $rows = @(Get-Rows $log); $states = @(Get-States $log)
        $stock = $rows[0]
        $dirtyRow = @($rows | Where-Object { $_.Tick -lt $resetTick })[-1]
        $dirtyState = @($states | Where-Object { $_.Tick -lt $resetTick })[-1]
        $cleanRow = $rows[-1]; $cleanState = $states[-1]
        $fmt = '{0,-16} planet air {1:0.000} ({2:+0.000;-0.000} vs stock), ice caps {3:0.000}, clouds {4:0.000}, latent {5:0.###e0} J, external {6:0.###e0} J'
        Write-Host ('stock            planet air {0:0.000}' -f $stock.Tank)
        Write-Host ($fmt -f 'before reset:', $dirtyRow.Tank, ($dirtyRow.Tank - $stock.Tank), $dirtyState.Caps, $dirtyState.Clouds, $dirtyState.LatentJ, $dirtyState.ExternalJ)
        Write-Host ($fmt -f 'after reset:', $cleanRow.Tank, ($cleanRow.Tank - $stock.Tank), $cleanState.Caps, $cleanState.Clouds, $cleanState.LatentJ, $cleanState.ExternalJ)
        if (($dirtyRow.Tank - $stock.Tank) -lt 50000) { throw 'The planet was not terraformed before the reset, so the reset proves nothing.' }
        if (($dirtyState.Caps + $dirtyState.Clouds) -le 0 -and $dirtyState.LatentJ -eq 0) { throw 'Ice caps, clouds and heat were not off stock before the reset, so the reset proves nothing.' }

        # Now the actual question: the unmodded game loads this save. What planet does it find?
        Remove-Item (Join-Path $mods 'TerraformingReloaded') -Recurse -Force
        $log = Invoke-Game @('-file', 'start', $station, 'Mars2') @{ TR_LIVECHECK_INJECT = '0' } `
            { param($l) @(Get-Rows $l).Count -ge 4 } 'the unmodded game to load the save'
        if ($log -match 'Terraforming Reloaded') { throw 'The mod loaded in the unmodded phase.' }
        $rows = @(Get-Rows $log); $states = @(Get-States $log)
        $v = $rows[-1]; $vs = $states[-1]
        Write-Host ($fmt -f 'mod removed:', $v.Tank, ($v.Tank - $stock.Tank), $vs.Caps, $vs.Clouds, $vs.LatentJ, $vs.ExternalJ)
        $problems = @()
        if ([math]::Abs($v.Tank - $stock.Tank) -gt 1) { $problems += "planet air is off stock by $([math]::Round($v.Tank - $stock.Tank, 3)) mol" }
        if (($vs.Caps + $vs.Clouds) -gt 0.001) { $problems += 'ice caps or clouds are not empty' }
        # 1.27e9 J shifts Mars by 1 K. Allow a hundredth of a kelvin: outdoor cells go on trading a
        # few hundred joules with the planet in the ticks between the reset and the save, and the
        # game's own phase change moves latent heat. Measured after a 3 GJ load: 350 J, 3e-7 K.
        if ([math]::Abs($vs.ExternalJ) -gt 1.27e7) { $problems += 'external heat is not near zero' }
        if ([math]::Abs($vs.LatentJ) -gt 1.27e7) { $problems += 'latent heat is not near zero' }
        if ($problems.Count -gt 0) { throw ('LiveCheck FAILED: after reset and mod removal, ' + ($problems -join '; ')) }
        Write-Host 'LiveCheck OK: the unmodded game finds a stock planet.'
        return
    }

    if ($SaveLoad) {
        $station = 'trlivecheck'
        $log = Invoke-Game @('-file', 'start', $station, 'Mars2') @{ TR_LIVECHECK_SAVE_AFTER_TICKS = '20' } `
            { param($l) @($l -match 'LiveCheck: saving at tick').Count -gt 0 } `
            'the world to be saved mid-spread'
        Assert-ModLive $log
        # Give the save time to finish writing, then take the last sample as the state that was saved.
        Start-Sleep -Seconds 20
        $log = @(Get-Content $bepLog)
        $saved = @(Get-Rows $log)[-1]
        Stop-Game
        $files = @(Get-ChildItem (Join-Path $GameDir "saves\$station") -Recurse -File -ErrorAction SilentlyContinue)
        if ($files.Count -eq 0) { throw "No save was written under saves\$station." }
        Write-Host ("saved:  tick {0}, {1} outdoor cells holding {2:0.000}, tank {3:0.000}, SUM {4:0.000}" -f $saved.Tick, $saved.Cells, $saved.Held, $saved.Tank, $saved.Sum)
        if ($saved.Cells -lt 500) { throw "Only $($saved.Cells) outdoor cells at the save; the scenario needs the gas still spread out." }

        $log = Invoke-Game @('-file', 'start', $station, 'Mars2') @{ TR_LIVECHECK_INJECT = '0' } `
            { param($l) @(Get-Rows $l).Count -ge 4 } 'the save to load and tick'
        Assert-ModLive $log
        $rows = @(Get-Rows $log)
        $rows | Format-Table -AutoSize | Out-String | Write-Host
        $loaded = $rows[0]
        Write-Host ("loaded: tick {0}, {1} outdoor cells holding {2:0.000}, tank {3:0.000}, SUM {4:0.000}" -f $loaded.Tick, $loaded.Cells, $loaded.Held, $loaded.Tank, $loaded.Sum)
        if ($loaded.Cells -lt 500) { throw "Only $($loaded.Cells) outdoor cells after the load; the save did not carry the spread-out state, so this proves nothing." }
        $lost = $saved.Sum - $loaded.Sum
        Write-Host ("total changed by {0:0.000} mol across the save and load of {1} outdoor cells" -f (-$lost), $loaded.Cells)
        if ([math]::Abs($lost) -gt 1) { throw "LiveCheck FAILED: the load changed the planet's total by $([math]::Round(-$lost, 3)) mol." }
        if ((Get-Spread $rows.Sum) -gt 1) { throw 'LiveCheck FAILED: the total drifts after the load.' }
        Write-Host 'LiveCheck OK'
        return
    }

    $log = Invoke-Game @('-new', 'Mars2') @{} { param($l) $r = @(Get-Rows $l); $r.Count -ge 20 -and $r[-1].Cells -le 5 -and $r[-1].Sum -gt ($r[0].Sum + 50000) } 'the injected gas to drain into the tank'
    $rows = @(Get-Rows $log)
    if ($Vanilla) {
        $rows | Format-Table -AutoSize | Out-String | Write-Host
        Write-Host 'Vanilla control run: not judged.'
        return
    }
    Assert-ModLive $log
    $rows | Format-Table -AutoSize | Out-String | Write-Host
    $before = @($rows | Where-Object { $_.Sum -lt ($rows[0].Sum + 50000) })
    $after = @($rows | Where-Object { $_.Sum -ge ($rows[0].Sum + 50000) })
    if ($before.Count -lt 2 -or $after.Count -lt 5) { throw 'Not enough samples either side of the injection.' }
    $co2Gain = $after[-1].Co2 - $before[-1].Co2
    Write-Host ("sum before injection varies by {0:0.000} mol" -f (Get-Spread $before.Sum))
    Write-Host ("sum after injection varies by  {0:0.000} mol across up to {1} outdoor cells" -f (Get-Spread $after.Sum), ($after.Cells | Measure-Object -Maximum).Maximum)
    Write-Host ("planet CO2 gained {0:0.000} mol of 100000 injected" -f $co2Gain)
    $problems = @()
    if ((Get-Spread $before.Sum) -gt 1) { $problems += 'sum drifts before injection' }
    if ((Get-Spread $after.Sum) -gt 1) { $problems += 'sum drifts after injection: gas is being created or lost' }
    if ([math]::Abs($co2Gain - 100000) -gt 5) { $problems += 'the planet did not receive what was injected' }

    # The console command, run by the driver the way the console would run it.
    $answers = @($log -match 'LiveCheck: cmd ') | ForEach-Object { $_ -replace '.*LiveCheck: cmd ', '' }
    $answers | ForEach-Object { Write-Host ("cmd " + $_.Substring(0, [math]::Min(150, $_.Length))) }
    if ($answers -match 'THREW') { $problems += 'a console command threw' }
    if (-not ($answers -match '^curves export -> (Wrote the built-in curves|Already there)')) { $problems += 'curves export did not answer as expected' }
    if (-not ($answers -match '^curves reload -> Loaded ')) { $problems += 'curves reload did not load the exported file' }
    if (-not ($answers -match '^status -> .*planet: live')) { $problems += 'status did not report the planet as live' }
    if (-not $curvesExisted -and -not (Test-Path $curvesFile)) { $problems += 'curves export wrote no file' }

    # Planet size: whatever the setting is, the planet must be exactly that share of the shipped
    # Mars, 45,594,999.269 mol (the -Vanilla run's tank, which never moves), with the same air per cell.
    $sizeLine = @($answers -match 'planet size: ([\d.]+) of shipped')
    if ($sizeLine.Count -eq 0) { $problems += 'status did not report the planet size' }
    else {
        $size = [double]($sizeLine[0] -replace '.*planet size: ([\d.]+) of shipped.*', '$1')
        $expected = 45594999.269 * $size
        Write-Host ("planet size {0}: tank started at {1:0.000} mol, expected {2:0.000}" -f $size, $rows[0].Tank, $expected)
        if ([math]::Abs($rows[0].Tank - $expected) -gt [math]::Max(1.0, $expected * 1e-6)) { $problems += 'the planet is not the size the setting says' }
        # Numeric, not textual: by the time status runs an outdoor cell has drawn its share, which
        # shows in the fifth decimal on a small planet.
        $co2Line = @($answers -match 'CarbonDioxide\s+([\d.]+)')
        $co2 = if ($co2Line.Count) { [double]($co2Line[0] -replace '.*CarbonDioxide\s+([\d.]+).*', '$1') } else { -1 }
        Write-Host ("CO2 per outdoor cell {0} mol (Mars ships 8.66)" -f $co2)
        if ([math]::Abs($co2 - 8.66) -gt 0.001) { $problems += 'the air per outdoor cell changed with the planet size' }
    }

    if ($problems.Count -gt 0) { throw ("LiveCheck FAILED: " + ($problems -join '; ')) }
    Write-Host 'LiveCheck OK'
}
finally {
    Stop-Game
    foreach ($key in 'TR_LIVECHECK_INJECT', 'TR_LIVECHECK_SAVE_AFTER_TICKS', 'TR_LIVECHECK_RESET', 'TR_LIVECHECK_DUMP', 'TR_LIVECHECK_OBSERVE', 'TR_LIVECHECK_DAYSPEED', 'TR_LIVECHECK_SETAIR', 'TR_LIVECHECK_HEATK', 'TR_LIVECHECK_SETAIR2', 'TR_LIVECHECK_SETAIR2_TICK') { Remove-Item "Env:$key" -ErrorAction SilentlyContinue }
    # BepInEx\config is the real one, shared with normal play. Only remove a curves file this run made.
    if (-not $curvesExisted -and (Test-Path $curvesFile)) { Remove-Item $curvesFile -Force }
    if ($Keep) {
        Write-Host "Kept for inspection in ${GameDir}: $($names -join ', '). Delete them before the next run."
    }
    else {
        foreach ($name in $names) {
            $path = Join-Path $GameDir $name
            if (Test-Path $path) { Remove-Item $path -Recurse -Force }
        }
    }
}
