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
    -WallVent  The wall vent fix, called directly: a headless run cannot build a vent, so the driver
               hands the hook body two grids, one with a cell and one without, and checks that a cell
               is built and that tank plus cells does not move.
    -BuildOver Building into an occupied outdoor cell (D2). The driver makes two neighbouring cells,
               waits for the simulation to link them, tops the first one up so the gas at stake is
               unmistakable, and calls what a structure calls when it fills a grid. Tank plus cells
               must not move. Add -Unguarded to run it again with the mod's guard taken off, which
               must show the duplicate: that is what says the check can fail. -Vanilla is no control
               here, because unmodded the planet throws away whatever it is handed, so the defect
               costs nothing until the mod switches the planet simulation on.
    -CustomWorld  A world written here rather than shipped by the game, staged into the test mod's
               GameData folder, which is where the game loads worlds from. It has air and its own
               Temperature curve and leaves out the greenhouse and density curves, so the mod has to
               supply those two and add nothing at all while the air is as the file sets it.
               -ZeroVolume starts the same world declaring a planet of no volume, which the mod must
               refuse. Nothing of the game's is copied anywhere: the world names the terrain, sun and
               sky the game installed.
    -Weather   A cloud bucket filling while other weather is already running (D6). The driver starts
               snow, fills the liquid clouds, and checks that the bucket went back into the air and
               snow is still the running event. With -Vanilla snow must be replaced by rain instead.
               -WeatherEvent picks what is running when the bucket fills; snow is the case that
               reaches the defect, because the game's own guard only steps aside for storm and rain.
    -Strip     The other direction: planet air taken OUT through outdoor cells, which is what a
               player with vents does and what the injection scenario does not cover. A headless run
               cannot build a vent, so the driver does what an inward ActiveVent does (clone the
               outdoor cell at its own grid, remove gas from it) at -StripCells grids every tick for
               -StripTicks ticks, and discards what it takes. Not judged as a conservation test: it
               reports what share of the planet outdoor cells hold while air is leaving, which is the
               number the storm rule's measure turns on. -StripPerCell caps what one draw point may
               take in a tick; the default takes everything above -StripFloor, which is the fastest a
               cell can be emptied and still be there next tick. The floor stands in for the vent
               itself: a cell with a structure on it is kept instantiated, and a cell taken to zero
               without one is cleaned up and its draw point goes dead.
    -WalkCost  Times a walk over every atmosphere from the planet tick, where a per-tick measure of
               the planet's air would sit, and prints microseconds against the cell count. Add it to
               the default scenario (which sweeps from 1 cell to about 4,300 and back) or to -Strip.
    -MenuPressure  The mix the new-game menu builds to describe a world must be the shipped planet,
               not the resized one it is playing.
    -Rescale   'terraform size <share> confirm' on the planet being played, with the ice caps, the
               clouds and both heat stores loaded first. Run from the main thread while the planet
               ticks on its own, which is how a console command reaches it. The air per outdoor cell,
               the pressure and both heat offsets in kelvin must not move, while the volume, the
               moles, the cells and the reservoir contents all move by the same factor; then forty
               more rescales from the main thread with nothing held, to give a rescale that did not
               take the tank lock a chance to tear a tick in half. -RescaleBy is how many times its
               present size to ask for (default 2.5), measured off the planet itself so the share
               asked for is never the share it already is. -RescaleTo asks for an absolute share.

  -Storm <id> -StormTick <n> force a weather event on at tick n during -Observe or -Model, because
  the game only schedules one after a cooldown of days. Ids are in the game's weather data:
  MarsDustStorm, EuropaSnowStorm, VulcanAshStorm, VulcanFireStorm, VenusStorm, Rain, Snow,
  SolarStorm, VulcanSolarStorm.

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

  -Clean removes what a previous run left in the game folder and does nothing else. A run that is
  interrupted never reaches its own cleanup, and every run after it refuses to start; this is the way
  back. It removes only the names this script creates, and only from the game folder.

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
    [switch]$WallVent,
    [switch]$MenuPressure,
    [switch]$Rescale,
    [switch]$BuildOver,
    [switch]$Unguarded,
    [switch]$CustomWorld,
    [switch]$ZeroVolume,
    [switch]$Weather,
    [switch]$Strip,
    [int]$StripTicks = 150,
    [int]$StripCells = 100,
    [double]$StripPerCell = 0,
    [double]$StripFloor = 0,
    [double]$StripShare = 0,
    [switch]$WalkCost,
    [string]$WeatherEvent = 'Snow',
    [double]$RescaleTo = 0,
    [double]$RescaleBy = 2.5,
    [string]$Storm = '',
    [int]$StormTick = 0,
    [double]$HeatK = 0,
    [double]$Tolerance = 0.5,
    [string]$World = 'Mars2',
    [double]$DaySpeed = 10,
    [int]$Ticks = 260,
    [string]$SetAir = '',
    [string]$SetAir2 = '',
    [int]$SetAir2Tick = 0,
    [switch]$Keep,
    [switch]$Clean,
    [int]$TimeoutSeconds = 360     # raised automatically for long -Observe runs
)

$ErrorActionPreference = 'Stop'
if (-not $GameDir) { $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Stationeers' }
$root = Split-Path (Split-Path $PSScriptRoot)
$exe = Join-Path $GameDir 'rocketstation.exe'
if (-not (Test-Path $exe)) { throw "Stationeers not found at '$GameDir'." }
if (Get-Process rocketstation -ErrorAction SilentlyContinue) { throw 'Stationeers is running. Close it first.' }
if (@(($Vanilla -and -not ($Observe -or $Model -or $BuildOver -or $Weather)), $SaveLoad, $Reset, [bool]$Dump, $WallVent, $MenuPressure, $Rescale, $BuildOver, $Weather, $CustomWorld, $Strip | Where-Object { $_ }).Count -gt 1) { throw 'Pick one of -Vanilla, -SaveLoad, -Reset, -Dump, -WallVent, -MenuPressure, -Rescale, -BuildOver, -Weather, -CustomWorld and -Strip.' }
if (($StripTicks -le 0) -or ($StripCells -le 0)) { throw '-StripTicks and -StripCells are how many ticks to draw for and how many draw points to draw from; both must be positive.' }
if ($StripPerCell -lt 0) { throw '-StripPerCell is how many moles one draw point may take in a tick; 0 means take everything.' }
if (($StripShare -lt 0) -or ($StripShare -gt 1)) { throw '-StripShare is the share of what a cell holds that one draw takes, between 0 and 1; 0 leaves the driver default.' }
if ($Rescale -and -not (($RescaleTo -gt 0) -or ($RescaleBy -gt 0))) { throw 'Give -RescaleBy, how many times its present size the planet should end up, or -RescaleTo, an absolute share of the shipped planet.' }
if ($Rescale -and ($RescaleTo -le 0) -and ([math]::Abs($RescaleBy - 1) -lt 1e-9)) { throw '-RescaleBy 1 is not a rescale.' }
if ($Storm -and -not ($Observe -or $Model)) { throw '-Storm only applies to -Observe and -Model.' }
if ($Unguarded -and -not $BuildOver) { throw '-Unguarded only applies to -BuildOver.' }
if ($Unguarded -and $Vanilla) { throw 'There is no guard to take off without the mod.' }
if ($ZeroVolume -and -not $CustomWorld) { throw '-ZeroVolume only applies to -CustomWorld.' }
if ($CustomWorld -and $Vanilla) { throw 'Without the mod there is nothing to say about a custom world.' }
if ($Storm -and $StormTick -le 0) { throw '-Storm needs -StormTick, the tick to force the event on at.' }

# Only what a run creates is removed afterwards.
$names = 'mods', 'saves', 'scripts', 'thumbnails', 'modconfig.xml', 'modrepos.xml', 'setting.xml'

if ($Clean) {
    # A run that is interrupted never reaches its own cleanup, and every run after it then refuses to
    # start. This is that cleanup on its own: the same names, the same folder, nothing else.
    $left = @($names | Where-Object { Test-Path (Join-Path $GameDir $_) })
    if ($left.Count -eq 0) {
        Write-Host "Nothing from a previous run is left in ${GameDir}."
        return
    }
    foreach ($name in $left) { Remove-Item (Join-Path $GameDir $name) -Recurse -Force }
    Write-Host "Removed from ${GameDir}: $($left -join ', ')"
    return
}

dotnet build (Join-Path $root 'src\TerraformingReloaded.csproj') -c Release -p:GameDir="$GameDir" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Mod build failed.' }
dotnet build (Join-Path $PSScriptRoot 'LiveCheck.csproj') -c Release -p:GameDir="$GameDir" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'LiveCheck build failed.' }

$preexisting = @($names | Where-Object { Test-Path (Join-Path $GameDir $_) })
if ($preexisting.Count -gt 0) {
    throw "Already present in the game folder: $($preexisting -join ', '). A headless run would mix with them; move them aside first."
}

$bepLog = Join-Path $GameDir 'BepInEx\LogOutput.log'
$curvesFile = Join-Path $GameDir 'BepInEx\config\TerraformingReloaded.curves.xml'
$curvesExisted = Test-Path $curvesFile
$unityLog = Join-Path $env:TEMP 'tr-livecheck-unity.log'
$script:game = $null
# Every variable the driver reads, cleared before each launch and again at the end, so one scenario
# cannot inherit another's settings.
$envKeys = @(
    'TR_LIVECHECK_INJECT', 'TR_LIVECHECK_SAVE_AFTER_TICKS', 'TR_LIVECHECK_RESET', 'TR_LIVECHECK_DUMP',
    'TR_LIVECHECK_OBSERVE', 'TR_LIVECHECK_DAYSPEED', 'TR_LIVECHECK_SETAIR', 'TR_LIVECHECK_HEATK',
    'TR_LIVECHECK_SETAIR2', 'TR_LIVECHECK_SETAIR2_TICK', 'TR_LIVECHECK_STORM', 'TR_LIVECHECK_STORM_TICK',
    'TR_LIVECHECK_WALLVENT_TICK', 'TR_LIVECHECK_MENUMIX', 'TR_LIVECHECK_RESCALE', 'TR_LIVECHECK_RESCALE_BY',
    'TR_LIVECHECK_RESCALE_TICK', 'TR_LIVECHECK_BUILDOVER_TICK', 'TR_LIVECHECK_BUILDOVER_UNGUARD',
    'TR_LIVECHECK_WEATHER_TICK', 'TR_LIVECHECK_WEATHER_EVENT', 'TR_LIVECHECK_STATUS_TICK',
    'TR_LIVECHECK_STRIP_TICK', 'TR_LIVECHECK_STRIP_TICKS', 'TR_LIVECHECK_STRIP_CELLS',
    'TR_LIVECHECK_STRIP_PER_CELL', 'TR_LIVECHECK_STRIP_FLOOR', 'TR_LIVECHECK_STRIP_SHARE',
    'TR_LIVECHECK_WALKCOST')

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
    foreach ($key in $envKeys) { Remove-Item "Env:$key" -ErrorAction SilentlyContinue }
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
        if ($line -match 'LiveCheck: tick\s+(\d+) \| tank ([\d.]+) \| tank CO2 ([\d.]+) \| outdoor cells\s+(\d+) holding ([\d.]+) \| cell CO2 ([\d.]+) \| SUM ([\d.]+)') {
            [pscustomobject]@{ Tick = [int]$Matches[1]; Tank = [double]$Matches[2]; Co2 = [double]$Matches[3]; Cells = [int]$Matches[4]
                Held = [double]$Matches[5]; CellCo2 = [double]$Matches[6]; Sum = [double]$Matches[7] }
        }
    }
}

# One side of a rescale: everything the command may move and everything it may not.
function Get-Rescale($log, $which) {
    $lines = @($log -match "LiveCheck: rescale $which ")
    if ($lines.Count -eq 0) { throw "LiveCheck FAILED: the driver logged no '$which' figures for the rescale." }
    $pattern = "volume ([\d.E+-]+) \| mol ([\d.E+-]+) \| cells ([\d.E+-]+) \| P ([\d.E+-]+) \| caps ([\d.E+-]+) \| clouds ([\d.E+-]+) \| capsVolume ([\d.E+-]+) \| latentK ([\d.E+-]+) \| extK ([\d.E+-]+) \| gases (.*)$"
    if ($lines[0] -notmatch $pattern) { throw "LiveCheck FAILED: could not read the '$which' figures: $($lines[0])" }
    [pscustomobject]@{
        Volume = [double]$Matches[1]; Mol = [double]$Matches[2]; Cells = [double]$Matches[3]; P = [double]$Matches[4]
        Caps = [double]$Matches[5]; Clouds = [double]$Matches[6]; CapsVolume = [double]$Matches[7]
        LatentK = [double]$Matches[8]; ExtK = [double]$Matches[9]; Gases = $Matches[10].Trim()
    }
}

# The draw scenario's own line: the same four figures as Get-Rows, every tick rather than every five,
# plus what was taken out of the cells on that tick.
function Get-StripRows($log) {
    foreach ($line in $log) {
        if ($line -match 'LiveCheck: strip tick (\d+) \| tank ([\d.]+) \| outdoor cells (\d+) holding ([\d.]+) \| SUM ([\d.]+) \| took ([\d.]+) \| took total ([\d.]+) \| made (\d+) \| drew from (\d+)(?: \| planet per cell ([\d.]+) \| planet grid cells (\d+))?') {
            [pscustomobject]@{ Tick = [int]$Matches[1]; Tank = [double]$Matches[2]; Cells = [int]$Matches[3]
                Held = [double]$Matches[4]; Sum = [double]$Matches[5]; Took = [double]$Matches[6]
                TookTotal = [double]$Matches[7]; Made = [int]$Matches[8]; DrewFrom = [int]$Matches[9]
                PerCell = $(if ($Matches[10]) { [double]$Matches[10] } else { 0 })
                GridCells = $(if ($Matches[11]) { [double]$Matches[11] } else { 0 }) }
        }
    }
}

function Get-Walks($log) {
    foreach ($line in $log) {
        if ($line -match 'LiveCheck: walk tick (\d+) \| pool (\d+) \| cells (\d+) \| sum us ([\d.]+) mean ([\d.]+) \| count us ([\d.]+) mean ([\d.]+) \| tickMs ([\d.]+)') {
            [pscustomobject]@{ Tick = [int]$Matches[1]; Pool = [int]$Matches[2]; Cells = [int]$Matches[3]
                SumUs = [double]$Matches[4]; SumMeanUs = [double]$Matches[5]; CountUs = [double]$Matches[6]
                CountMeanUs = [double]$Matches[7]; TickMs = [double]$Matches[8] }
        }
    }
}

# What a walk costs against how many cells there are, and what that is worth against one tick.
function Show-Walks($log) {
    $walks = @(Get-Walks $log)
    if ($walks.Count -eq 0) {
        Write-Host 'No walk timings were logged.'
        return
    }
    $walks | Sort-Object Cells | Format-Table Tick, Pool, Cells,
        @{ n = 'sum us'; e = { '{0:N2}' -f $_.SumUs } }, @{ n = 'sum mean us'; e = { '{0:N2}' -f $_.SumMeanUs } },
        @{ n = 'count us'; e = { '{0:N2}' -f $_.CountUs } },
        @{ n = 'ns per cell'; e = { if ($_.Cells -gt 0) { '{0:N1}' -f ($_.SumUs * 1000 / $_.Cells) } else { '' } } },
        @{ n = '% of tick'; e = { '{0:N4}' -f ($_.SumUs / 1000 / $_.TickMs * 100) } } -AutoSize | Out-String | Write-Host
    $busiest = ($walks | Sort-Object Cells)[-1]
    $idle = @($walks | Where-Object { $_.Cells -le 2 })
    if ($idle.Count -gt 0) {
        $q = ($idle | Measure-Object SumUs -Minimum).Minimum
        Write-Host ("walk with {0} cells: {1:N2} us" -f $idle[0].Cells, $q)
    }
    Write-Host ("walk with {0} outdoor cells (pool {1}): {2:N2} us best, {3:N2} us mean, {4:N1} ns per cell, {5:N4}% of a {6:N0} ms tick" -f `
        $busiest.Cells, $busiest.Pool, $busiest.SumUs, $busiest.SumMeanUs,
        ($(if ($busiest.Cells -gt 0) { $busiest.SumUs * 1000 / $busiest.Cells } else { 0 })),
        ($busiest.SumUs / 1000 / $busiest.TickMs * 100), $busiest.TickMs)
    # Cost per cell, fitted over every sample with cells to speak of, so a count can be extrapolated.
    $fit = @($walks | Where-Object { $_.Cells -ge 100 })
    if ($fit.Count -ge 2) {
        $perCellNs = (($fit | Measure-Object -Property SumUs -Sum).Sum * 1000) / (($fit | Measure-Object -Property Cells -Sum).Sum)
        $budgetUs = $busiest.TickMs * 1000
        Write-Host ("over {0} samples of 100 cells or more: {1:N1} ns per cell, so 1% of a tick is about {2:N0} cells and a whole tick about {3:N0}" -f `
            $fit.Count, $perCellNs, ($budgetUs * 0.01 * 1000 / $perCellNs), ($budgetUs * 1000 / $perCellNs))
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
    if ($CustomWorld) {
        # The game loads a world from every enabled mod's GameData folder (WorldManager.LoadDataFiles).
        Copy-Item (Join-Path $PSScriptRoot 'GameData') (Join-Path $mods 'TRLiveCheck') -Recurse
    }
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
        if ($Storm) { $environment.TR_LIVECHECK_STORM = $Storm; $environment.TR_LIVECHECK_STORM_TICK = "$StormTick" }
        $log = Invoke-Game @('-new', $World) $environment `
            { param($l) $o = @($l -match 'LiveCheck: obs tick (\d+)'); $o.Count -gt 0 -and [int](($o[-1] -replace '.*obs tick (\d+).*', '$1')) -ge $Ticks } "a fast day on $World"
        if (-not $Vanilla) { Assert-ModLive $log }
        if ($Storm) {
            # A started event is not a running one: check the driver's line and the samples.
            @($log -match 'LiveCheck: storm ') | ForEach-Object { $_ -replace '^\[[^\]]*\]\s*', '' } | Write-Host
            if (-not ($log -match "LiveCheck: storm $Storm asked for.*event $Storm, running True")) { throw "The storm $Storm never started." }
            $stormy = @(@($log -match 'LiveCheck: obs tick') | Where-Object { $_ -notmatch 'stormK 0\s*$' })
            Write-Host ("{0} samples were taken with the storm's offset applied" -f $stormy.Count)
            if ($stormy.Count -lt 5) { throw 'The storm was over before enough samples were taken.' }
        }
        if ($Model) {
            # Judged: the simulator rebuilt from what the game reported has to give the game's temperature.
            # Samples before tick 15 are skipped: the air is set at tick 6 and the readings settle after it.
            $obs = @($log -match 'LiveCheck: obs tick') | ForEach-Object { $_ -replace '.*LiveCheck: obs ', '' }
            $obs | python (Join-Path $root 'tools\Balance\compare.py') $World $Tolerance 15 $SetAir2Tick | Write-Host
            if ($LASTEXITCODE -ne 0) { throw 'LiveCheck FAILED: the simulator and the game disagree.' }
            Write-Host 'LiveCheck OK: the simulator matches the game.'
            return
        }
        @($log -match 'LiveCheck: (obs tick|planet air|sky running|storm )|Self-test|temperature response') | ForEach-Object { ($_ -replace '^\[[^\]]*\]\s*', '') -replace 'LiveCheck: obs ', '' } | Write-Host
        return
    }

    if ($WallVent) {
        # D15. LiveCheck cannot build a wall vent, so the driver calls the hook body with two grids.
        $log = Invoke-Game @('-new', 'Mars2') @{ TR_LIVECHECK_INJECT = '0'; TR_LIVECHECK_WALLVENT_TICK = '30' } `
            { param($l) @($l -match 'LiveCheck: wallvent ').Count -gt 0 } 'the wall vent check'
        Assert-ModLive $log
        $line = @($log -match 'LiveCheck: wallvent ')[0] -replace '.*LiveCheck: ', ''
        Write-Host $line
        if ($line -notmatch '^wallvent PASS') { throw "LiveCheck FAILED: $line" }
        Write-Host 'LiveCheck OK: the wall vent fix builds a cell and the planet total is unchanged.'
        return
    }

    if ($BuildOver) {
        # D2. A structure filling an occupied outdoor cell: the game copies the cell's gas to its open
        # neighbours and then removes the cell, and removing a world cell gives the still-full mixture
        # to the planet as well. Unmodded, the planet gains a whole extra copy of that cell.
        $environment = @{ TR_LIVECHECK_INJECT = '0'; TR_LIVECHECK_BUILDOVER_TICK = '30' }
        if ($Unguarded) { $environment.TR_LIVECHECK_BUILDOVER_UNGUARD = '1' }
        $log = Invoke-Game @('-new', $World) $environment `
            { param($l) @($l -match 'LiveCheck: buildover (PASS|FAIL)').Count -gt 0 } 'the build-over check'
        if (-not $Vanilla) { Assert-ModLive $log }
        $line = @($log -match 'LiveCheck: buildover (PASS|FAIL)')[0] -replace '.*LiveCheck: ', ''
        Write-Host $line
        if ($line -notmatch 'held ([\d.]+) mol with (\d+) open neighbour') {
            throw "LiveCheck FAILED: the check never reached a cell it could build over: $line"
        }
        $held = [double]$Matches[1]
        if ($line -notmatch 'tank plus cells [\d.]+ -> [\d.]+ mol \(([+-][\d.]+)\)') {
            throw "LiveCheck FAILED: could not read the totals: $line"
        }
        $moved = [double]$Matches[1]
        if ($Unguarded) {
            # Far short of what the cell held, and that is expected: the event is queued, and the cell
            # goes on draining into the planet the ordinary way until the atmospherics pass applies it.
            # What lands in the planet twice is whatever is left in the cell at that moment. The bar is
            # only that it is unmistakably more than the tolerance the guarded run is held to.
            if ($moved -lt 100) {
                throw "LiveCheck FAILED: with the guard off, building over a cell holding $held mol should have duplicated what was left of it, but the total moved only $moved mol. The check is not reaching the defect."
            }
            Write-Host ("LiveCheck OK: with the guard off, building over the cell put {0:N3} mol into the planet twice, from a cell that held {1:N3} mol when it was built over. That is what the guard is worth." -f $moved, $held)
            return
        }
        if ($line -notmatch '^buildover PASS') { throw "LiveCheck FAILED: $line" }
        Write-Host ("LiveCheck OK: building over a cell holding {0:N3} mol left the planet total unchanged." -f $held)
        return
    }

    if ($Weather) {
        # D6. When a cloud bucket fills, the planet tick gives it back to the air and then schedules
        # rain without asking what the weather is doing. Snow is the case that reaches it: the tick's
        # own guard only steps aside for a storm or for rain.
        $log = Invoke-Game @('-new', $World) @{ TR_LIVECHECK_INJECT = '0'; TR_LIVECHECK_WEATHER_TICK = '30'; TR_LIVECHECK_WEATHER_EVENT = $WeatherEvent } `
            { param($l) @($l -match 'LiveCheck: weather (PASS|FAIL)').Count -gt 0 } 'the cloud against weather check'
        if (-not $Vanilla) { Assert-ModLive $log }
        $line = @($log -match 'LiveCheck: weather (PASS|FAIL)')[0] -replace '.*LiveCheck: ', ''
        Write-Host $line
        if ($line -notmatch 'bucket emptied (True|False) .* event (\S+) -> (\S+), running (True|False)') {
            throw "LiveCheck FAILED: the check never reached a full bucket: $line"
        }
        $emptied = $Matches[1] -eq 'True'; $was = $Matches[2]; $now = $Matches[3]
        if (-not $emptied) {
            throw "LiveCheck FAILED: the bucket was not emptied, so the tick never reached the code this is about: $line"
        }
        if ($Vanilla) {
            if ($now -eq $was) {
                throw "LiveCheck FAILED: unmodded, a full cloud bucket should have scheduled rain over the running $was, but the event is still $was. The check is not reaching the defect."
            }
            Write-Host ("LiveCheck OK: unmodded, a full cloud bucket replaced the running {0} with {1}. That is the defect." -f $was, $now)
            return
        }
        if ($line -notmatch '^weather PASS') { throw "LiveCheck FAILED: $line" }
        Write-Host ("LiveCheck OK: a full cloud bucket gave its gas back to the air and left {0} running." -f $was)
        return
    }

    if ($Strip) {
        # Air taken OUT through outdoor cells, the direction a player with vents moves it. What is
        # measured is the share of the planet its outdoor cells hold while that is happening, which
        # is what decides whether a measure of how much air a planet has left must count them.
        $environment = @{ TR_LIVECHECK_INJECT = '0'; TR_LIVECHECK_STRIP_TICK = '30'
            TR_LIVECHECK_STRIP_TICKS = "$StripTicks"; TR_LIVECHECK_STRIP_CELLS = "$StripCells" }
        if ($StripPerCell -gt 0) { $environment.TR_LIVECHECK_STRIP_PER_CELL = $StripPerCell.ToString([cultureinfo]::InvariantCulture) }
        if ($StripFloor -gt 0) { $environment.TR_LIVECHECK_STRIP_FLOOR = $StripFloor.ToString([cultureinfo]::InvariantCulture) }
        if ($StripShare -gt 0) { $environment.TR_LIVECHECK_STRIP_SHARE = $StripShare.ToString([cultureinfo]::InvariantCulture) }
        if ($WalkCost) { $environment.TR_LIVECHECK_WALKCOST = '1' }
        if ($TimeoutSeconds -lt ($StripTicks * 0.7 + 240)) { $TimeoutSeconds = [int]($StripTicks * 0.7 + 240) }
        # Waits for samples taken after the draw stops as well, so the cells can be seen settling back.
        $log = Invoke-Game @('-new', $World) $environment `
            { param($l)
              $done = @($l -match 'LiveCheck: strip done ')
              if ($done.Count -eq 0) { return $false }
              $tick = if ($done[0] -match '\| tick (\d+)') { [int]$Matches[1] } else { 0 }
              @(Get-Rows $l | Where-Object { $_.Tick -ge $tick + 40 }).Count -ge 1 } 'the planet to be drawn down through outdoor cells'
        Assert-ModLive $log
        @($log -match 'LiveCheck: strip (starting|done|FAIL)') | ForEach-Object { $_ -replace '^\[[^\]]*\]\s*', '' } | Write-Host
        if ($log -match 'LiveCheck: strip FAIL') { throw 'LiveCheck FAILED: the driver could not draw the planet down.' }
        $stripRows = @(Get-StripRows $log)
        if ($stripRows.Count -eq 0) { throw 'LiveCheck FAILED: the driver never drew any gas out.' }

        $share = { param($r) if (($r.Tank + $r.Held) -gt 0) { $r.Held / ($r.Tank + $r.Held) * 100 } else { 0 } }
        # Every fifth tick of the draw, so a long run still prints as a page.
        $stripRows | Where-Object { $_.Tick % 5 -eq 0 } | Format-Table Tick, Cells,
            @{ n = 'held'; e = { '{0:N3}' -f $_.Held } }, @{ n = 'tank'; e = { '{0:N3}' -f $_.Tank } },
            @{ n = 'sum'; e = { '{0:N3}' -f $_.Sum } }, @{ n = 'took'; e = { '{0:N3}' -f $_.Took } },
            @{ n = 'took total'; e = { '{0:N3}' -f $_.TookTotal } }, @{ n = 'made'; e = { $_.Made } },
            @{ n = 'cell share %'; e = { '{0:N4}' -f (& $share $_) } } -AutoSize | Out-String | Write-Host

        $shares = @($stripRows | ForEach-Object { & $share $_ })
        $peak = ($shares | Measure-Object -Maximum).Maximum
        $mean = ($shares | Measure-Object -Average).Average
        $peakRow = $stripRows[[array]::IndexOf($shares, $peak)]
        $rows = @(Get-Rows $log)
        $startTick = $stripRows[0].Tick
        $endTick = $stripRows[-1].Tick
        $beforeRows = @($rows | Where-Object { $_.Tick -lt $startTick })
        $afterRows = @($rows | Where-Object { $_.Tick -gt $endTick + 10 })
        $beforeShare = if ($beforeRows.Count) { & $share $beforeRows[-1] } else { -1 }
        $afterShare = if ($afterRows.Count) { & $share $afterRows[-1] } else { -1 }
        $took = $stripRows[-1].TookTotal
        # The first row is logged after that tick's draw, so the planet before the draw began is its
        # sum plus what that tick took. Taking the row as it stands hides the first tick's moles.
        $fell = ($stripRows[0].Sum + $stripRows[0].Took) - $stripRows[-1].Sum
        $states = @(Get-States $log)
        $endState = if ($states.Count) { $states[-1] } else { $null }

        Write-Host ("drew {0:N3} mol out of the planet over {1} ticks from {2} draw points; planet plus cells fell {3:N3} mol, so {4:N3} mol is unaccounted for" -f `
            $took, $stripRows.Count, $StripCells, $fell, ($fell - $took))
        if ($endState) { Write-Host ("ice caps {0:N3} mol, clouds {1:N3} mol at the end" -f $endState.Caps, $endState.Clouds) }
        Write-Host ("cell share before the draw {0:N4}%, peak during it {1:N4}% (tick {2}, {3} cells holding {4:N3} of {5:N3}), mean {6:N4}%, after it settles {7:N4}%" -f `
            $beforeShare, $peak, $peakRow.Tick, $peakRow.Cells, $peakRow.Held, ($peakRow.Tank + $peakRow.Held), $mean, $afterShare)
        Write-Host ("most outdoor cells at once: {0}; most held at once: {1:N3} mol" -f `
            ($stripRows.Cells | Measure-Object -Maximum).Maximum, ($stripRows.Held | Measure-Object -Maximum).Maximum)
        # The scale the share is set by: the planet is a number of grid cells, and an outdoor cell
        # resting at the planet's own density is exactly one of them.
        $dense = @($stripRows | Where-Object { $_.PerCell -gt 0 -and $_.Cells -gt 0 })
        if ($dense.Count) {
            $last = $dense[-1]
            Write-Host ("the planet is {0:N0} grid cells of {1:N3} mol, so one outdoor cell at planet density is {2:N6}% of it; at the end of the draw its cells held {3:N3} mol each, {4:N1}% of planet density" -f `
                $last.GridCells, $last.PerCell, (100 / $last.GridCells), ($last.Held / $last.Cells), (($last.Held / $last.Cells) / $last.PerCell * 100))
        }
        if ($WalkCost) { Show-Walks $log }

        $problems = @()
        if ($took -lt 100) { $problems += "the draw only took $took mol, which is too little to say anything about" }
        if ($peakRow.Cells -lt 1) { $problems += 'no outdoor cell was ever there to draw from' }
        if ($problems.Count -gt 0) { throw ('LiveCheck FAILED: ' + ($problems -join '; ')) }
        Write-Host 'LiveCheck OK: the planet was drawn down through its outdoor cells and the share they hold is above.'
        return
    }

    if ($CustomWorld) {
        # The mod keeps no list of worlds; it reads whatever is loaded (WORLDS.md). This starts one
        # written here, so what it proves is about a world the mod has never seen.
        $id = if ($ZeroVolume) { 'TRTestWorldNoVolume' } else { 'TRTestWorld' }
        # The later status is the one to read: the temperature response is worked out the first time
        # it is needed, so the one at tick 6 says it has not been evaluated yet.
        $log = Invoke-Game @('-new', $id) @{ TR_LIVECHECK_INJECT = '0'; TR_LIVECHECK_STATUS_TICK = '40' } `
            { param($l) @($l -match 'LiveCheck: cmd status').Count -ge 2 } "the custom world $id to run"
        $status = @($log -match 'LiveCheck: cmd status')[-1]
        Write-Host (($status -replace '.*LiveCheck: cmd status -> ', '') -replace ' / ', "`n")

        if ($ZeroVolume) {
            if (-not ($log -match 'GlobalAtmosphere has no usable Volume')) {
                throw 'LiveCheck FAILED: a world declaring a planet of no volume was not refused.'
            }
            if ($status -notmatch 'planet: off:') {
                throw "LiveCheck FAILED: the mod did not stand down on a world with no planet volume: $status"
            }
            Write-Host 'LiveCheck OK: a custom world declaring no planet volume is refused, and its planet is left as shipped.'
            return
        }

        Assert-ModLive $log
        $problems = @()
        if ($status -notmatch 'fills greenhouse=True, density=True') { $problems += 'the mod did not supply the two curves this world leaves out' }
        if ($status -notmatch 'adding 0 K now') { $problems += 'the mod moved the temperature of a world whose air is exactly as its author set it' }
        if ($status -notmatch 'CarbonDioxide\s+([\d.]+)') { $problems += 'the status did not report the air per outdoor cell' }
        else {
            $co2 = [double]$Matches[1]
            if ([math]::Abs($co2 - 6) -gt 0.05) { $problems += "the planet is not this world's air: $co2 mol CO2 per outdoor cell, not 6" }
        }
        if ($problems.Count -gt 0) { throw "LiveCheck FAILED: $($problems -join '; ')." }
        Write-Host 'LiveCheck OK: a world written by hand runs, the mod supplies the curves it leaves out, and adds nothing while its air is untouched.'
        return
    }

    if ($MenuPressure) {
        # D16. The new-game menu divides a world's moles by its unscaled volume, so the mix it builds
        # must be the shipped planet even while a resized one is being played.
        $log = Invoke-Game @('-new', 'Mars2') @{ TR_LIVECHECK_INJECT = '0'; TR_LIVECHECK_MENUMIX = '1' } `
            { param($l) @($l -match 'LiveCheck: menumix ').Count -gt 0 -and @($l -match 'LiveCheck: cmd status').Count -gt 0 } 'the new-game menu mix'
        Assert-ModLive $log
        $line = @($log -match 'LiveCheck: menumix ')[0] -replace '.*LiveCheck: ', ''
        Write-Host $line
        if ($line -notmatch 'menu ([\d.]+) mol in ([\d.]+) L \| live ([\d.]+) mol in ([\d.]+) L \| \d+ outdoor cells \| shipped volume ([\d.]+) L') {
            throw "LiveCheck FAILED: the driver could not build the menu's mix: $line"
        }
        $menuMoles = [double]$Matches[1]; $menuLitres = [double]$Matches[2]
        $liveMoles = [double]$Matches[3]; $liveLitres = [double]$Matches[4]; $shippedLitres = [double]$Matches[5]
        $answers = @($log -match 'LiveCheck: cmd ') | ForEach-Object { $_ -replace '.*LiveCheck: cmd ', '' }
        $sizeLine = @($answers -match 'planet size: ([\d.]+) of shipped')
        if ($sizeLine.Count -eq 0) { throw 'LiveCheck FAILED: status did not report the planet size.' }
        $size = [double]($sizeLine[0] -replace '.*planet size: ([\d.]+) of shipped.*', '$1')
        Write-Host ("planet size {0}: menu {1:N3} mol in {2:N0} L, live {3:N3} mol in {4:N0} L" -f $size, $menuMoles, $menuLitres, $liveMoles, $liveLitres)
        $problems = @()
        if ([math]::Abs($menuMoles - 45594999.269) -gt 1.0) { $problems += 'the menu mix is not the shipped planet' }
        if ([math]::Abs($menuLitres - $shippedLitres) -gt 1.0) { $problems += 'the menu mix is not the shipped volume' }
        # The live figure is the planet plus its outdoor cells, so it is the whole share, not the tank.
        if ([math]::Abs($liveMoles - 45594999.269 * $size) -gt 1.0) { $problems += 'the planet being played is not the size the setting says' }
        if ([math]::Abs($liveLitres - $shippedLitres * $size) -gt [math]::Max(1.0, $shippedLitres * $size * 1e-6)) { $problems += 'the played planet is not the volume the setting says' }
        if ($problems.Count -gt 0) { throw ('LiveCheck FAILED: ' + ($problems -join '; ')) }
        Write-Host 'LiveCheck OK: the menu sees the shipped planet while a resized one is played.'
        return
    }

    if ($Rescale) {
        # 'terraform size <share> confirm'. Planet size is what a player picks before they know what
        # it means, so it can be changed on the planet being played. What that must not touch is the
        # air: the mix per outdoor cell, the pressure and both heat offsets have to come out where
        # they were, while the planet behind them scales whole.
        #
        # The share is measured off the planet by the driver (-RescaleBy times its present size), so
        # it can never be the share the planet already is: an absolute default silently became a
        # no-op whenever the config happened to match it, and the run then judged a rescale that had
        # correctly refused to happen.
        $environment = @{ TR_LIVECHECK_INJECT = '0'; TR_LIVECHECK_RESCALE_TICK = '30' }
        if ($RescaleTo -gt 0) { $environment.TR_LIVECHECK_RESCALE = $RescaleTo.ToString([cultureinfo]::InvariantCulture) }
        else { $environment.TR_LIVECHECK_RESCALE_BY = $RescaleBy.ToString([cultureinfo]::InvariantCulture) }
        # Waits for a sample taken after the last rescale as well, so a planet that stopped ticking
        # under them shows up as a timeout here rather than as an empty check below.
        $log = Invoke-Game @('-new', 'Mars2') $environment `
            { param($l)
              $done = @($l -match 'LiveCheck: rescale done ')
              if ($done.Count -eq 0) { return $false }
              $tick = if ($done[0] -match '\| tick (\d+)') { [int]$Matches[1] } else { 0 }
              @(Get-Rows $l | Where-Object { $_.Tick -gt $tick }).Count -ge 1 } 'the planet rescale'
        Assert-ModLive $log
        $askedLine = @($log -match 'LiveCheck: rescale asking for ')
        if ($askedLine.Count -eq 0) { throw 'LiveCheck FAILED: the driver never chose a share to ask for.' }
        if ($askedLine[0] -notmatch 'asking for ([\d.E+-]+) of shipped at tick \d+ \| planet is ([\d.E+-]+) \| factor ([\d.E+-]+)') {
            throw "LiveCheck FAILED: could not read the share asked for: $($askedLine[0])"
        }
        $asked = [double]$Matches[1]; $factor = [double]$Matches[3]
        if ([math]::Abs($factor - 1) -lt 1e-6) { throw "LiveCheck FAILED: the share asked for is the size the planet already is, so this run would prove nothing." }
        $before = Get-Rescale $log 'before'
        $after = Get-Rescale $log 'after'
        $answers = @($log -match 'LiveCheck: cmd ') | ForEach-Object { $_ -replace '.*LiveCheck: cmd ', '' }
        $answers | ForEach-Object { Write-Host ("cmd " + $_.Substring(0, [math]::Min(200, $_.Length))) }
        $sizes = @(@($answers -match 'planet size: ([\d.]+) of shipped') | ForEach-Object { [double]($_ -replace '.*planet size: ([\d.]+) of shipped.*', '$1') })
        if ($sizes.Count -lt 2) { throw 'LiveCheck FAILED: status did not report the planet size both before and after.' }
        Write-Host ("planet size {0} -> {1}, so everything the rescale moves must move by {2}" -f $sizes[0], $sizes[-1], $factor)
        Write-Host ("before: volume {0:N0} L, {1:N3} mol, caps {2:N3}, clouds {3:N3}, P {4:0.00000} kPa, latent {5:0.00000} K, external {6:0.00000} K" -f $before.Volume, $before.Mol, $before.Caps, $before.Clouds, $before.P, $before.LatentK, $before.ExtK)
        Write-Host ("after:  volume {0:N0} L, {1:N3} mol, caps {2:N3}, clouds {3:N3}, P {4:0.00000} kPa, latent {5:0.00000} K, external {6:0.00000} K" -f $after.Volume, $after.Mol, $after.Caps, $after.Clouds, $after.P, $after.LatentK, $after.ExtK)

        $problems = @()
        if ($before.Caps -le 0 -or $before.Clouds -le 0 -or $before.ExtK -eq 0 -or $before.LatentK -eq 0) {
            $problems += 'the ice caps, clouds or heat stores were empty before the rescale, so their scaling proves nothing'
        }
        if ($after.Gases -ne $before.Gases) { $problems += "the air per outdoor cell changed: '$($before.Gases)' became '$($after.Gases)'" }
        foreach ($same in @(@('pressure', $before.P, $after.P), @('the latent heat offset', $before.LatentK, $after.LatentK), @('the external heat offset', $before.ExtK, $after.ExtK))) {
            if ([math]::Abs($same[2] - $same[1]) -gt [math]::Max(1e-9, [math]::Abs($same[1]) * 1e-6)) {
                $problems += ("{0} moved, {1} to {2}" -f $same[0], $same[1], $same[2])
            }
        }
        foreach ($scaled in @(@('volume', $before.Volume, $after.Volume), @('moles', $before.Mol, $after.Mol), @('outdoor cells', $before.Cells, $after.Cells),
                              @('ice caps', $before.Caps, $after.Caps), @('clouds', $before.Clouds, $after.Clouds), @('ice cap volume', $before.CapsVolume, $after.CapsVolume))) {
            $wanted = $scaled[1] * $factor
            # The driver prints these to six decimals, so each side carries up to 5e-7 of rounding and
            # the before figure carries it times the factor. Below that there is nothing to compare.
            if ([math]::Abs($scaled[2] - $wanted) -gt ([math]::Abs($wanted) * 1e-9 + 1e-6 * (1 + $factor))) {
                $problems += ("{0} did not scale by {1}: {2} became {3}, expected {4}" -f $scaled[0], $factor, $scaled[1], $scaled[2], $wanted)
            }
        }
        # Status prints the size to four decimals, so compare it at that. The last status is taken
        # after the hammering, so it also says the planet came back to the share it was asked for.
        if ([math]::Abs($sizes[-1] - $asked) -gt 5e-5) { $problems += "status reports the planet at $($sizes[-1]) of shipped, not $asked" }
        if ($answers -match 'THREW') { $problems += 'a console command threw' }
        if ($log -match 'LiveCheck: rescale FAIL') { $problems += 'the driver could not run the rescale' }
        if (-not ($answers -match "^size 0 confirm -> '0' is not a planet size")) { $problems += 'size 0 confirm was not refused' }
        if (-not ($answers -match "^size banana confirm -> 'banana' is not a planet size")) { $problems += 'a size that is not a number was not refused' }
        if (-not ($answers -match "^size 1000 confirm -> '1000' is not a planet size")) { $problems += 'a size outside the range was not refused' }
        if (-not ($answers -match '^size [\d.]+ -> This rescales the planet you are playing')) { $problems += 'size without confirm did not explain and ask' }
        if (-not ($answers -match '^size [\d.]+ confirm -> Planet rescaled\.')) { $problems += 'the rescale did not report what it did' }
        if (-not ($answers -match 'the setting is [\d.]+ and applies to a new world')) { $problems += 'status no longer says the setting and the planet disagree' }

        # The unlocked path: rescale after rescale from the main thread while the planet ticks on its
        # own. Every one has to answer that it rescaled, and the world has to go on ticking afterwards.
        $doneLine = @($log -match 'LiveCheck: rescale done ')[0]
        $hammered = if ($doneLine -match 'from the main thread (\d+) more times \| tick (\d+)') { [int]$Matches[1] } else { 0 }
        $doneTick = if ($doneLine -match 'from the main thread \d+ more times \| tick (\d+)') { [int]$Matches[1] } else { 0 }
        $rescaled = @($answers -match '^size [\d.]+ confirm -> Planet rescaled\.').Count
        Write-Host ("{0} rescales ran from the main thread while the planet ticked, all answered" -f $rescaled)
        if ($hammered -lt 20) { $problems += "only $hammered rescales crossed threads; the run did not exercise the console path" }
        if ($rescaled -lt $hammered + 1) { $problems += "$rescaled of $($hammered + 1) rescales answered that they rescaled" }
        $ticking = @(@(Get-Rows $log) | Where-Object { $_.Tick -gt $doneTick })
        if ($ticking.Count -lt 1) { $problems += 'the planet stopped ticking after the rescales' }
        if ($problems.Count -gt 0) { throw ('LiveCheck FAILED: ' + ($problems -join '; ')) }
        Write-Host 'LiveCheck OK: the planet scaled whole, its air did not move, and it took the command from another thread.'
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
        # The reset restores the tank; the outdoor cells still standing are not touched by it and go
        # on handing back what they hold over the ticks before the save. They were resting at the
        # terraformed planet's density, so what they hand back above stock is what they held times
        # how much denser than stock the planet was, and that share is larger on a smaller planet: a
        # flat 1 mol bound only passes at the size it was measured at (+1.825 mol at 0.01). Take the
        # bound from the run's own figures instead, with 1 mol for the rest.
        $denser = [math]::Max(0.0, ($dirtyRow.Tank / $stock.Tank) - 1.0)
        $allowed = 1.0 + $dirtyRow.Held * $denser
        Write-Host ("outdoor cells held {0:0.000} mol at the reset on a planet {1:P2} denser than stock, so the planet may come back up to {2:0.000} mol above it" -f $dirtyRow.Held, $denser, $allowed)
        if ([math]::Abs($v.Tank - $stock.Tank) -gt $allowed) { $problems += "planet air is off stock by $([math]::Round($v.Tank - $stock.Tank, 3)) mol, more than the $([math]::Round($allowed, 3)) the cells could hand back" }
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

    $environment = @{}
    if ($WalkCost) { $environment.TR_LIVECHECK_WALKCOST = '1' }
    $log = Invoke-Game @('-new', 'Mars2') $environment { param($l) $r = @(Get-Rows $l); $r.Count -ge 20 -and $r[-1].Cells -le 5 -and $r[-1].Sum -gt ($r[0].Sum + 50000) } 'the injected gas to drain into the tank'
    $rows = @(Get-Rows $log)
    if ($Vanilla) {
        $rows | Format-Table -AutoSize | Out-String | Write-Host
        Write-Host 'Vanilla control run: not judged.'
        return
    }
    Assert-ModLive $log
    $rows | Format-Table -AutoSize | Out-String | Write-Host
    if ($WalkCost) {
        # The same sweep the injection makes, 1 cell to thousands and back, timed at each sample.
        $rows | Format-Table Tick, Cells, @{ n = 'held'; e = { '{0:N3}' -f $_.Held } },
            @{ n = 'cell share %'; e = { '{0:N4}' -f ($_.Held / $_.Sum * 100) } } -AutoSize | Out-String | Write-Host
        Show-Walks $log
    }
    $before = @($rows | Where-Object { $_.Sum -lt ($rows[0].Sum + 50000) })
    $after = @($rows | Where-Object { $_.Sum -ge ($rows[0].Sum + 50000) })
    if ($before.Count -lt 2 -or $after.Count -lt 5) { throw 'Not enough samples either side of the injection.' }
    # Moles, then species. The planet and its outdoor cells together must gain exactly what was put
    # outdoors, at any planet size; almost all of it must be the gas that was injected.
    #
    # Not "the planet's carbon dioxide gained 100,000 within 5": that assertion only passes at the
    # size it was measured at. The game's mixing moves totals, and takes the composition from
    # whichever side it is drawing from, so while thousands of cells hold gas that is nearly pure
    # carbon dioxide and the planet's own mix has shifted under them, the planet trades a little
    # carbon dioxide for a little of everything else. Nothing is lost: measured here at size 0.01,
    # carbon dioxide ended 4.9 mol under the 100,000 while oxygen, nitrogen and pollutant each ended
    # 0.021 % over, which is the same 4.9 mol. The trade is as large as the injection shifts the
    # planet's own composition, so it is five times larger at size 0.01 than at 0.05, which is why a
    # flat 5 mol bound on one gas fails on a small planet and passes on a large one.
    $gain = $after[-1].Sum - $before[-1].Sum
    $co2Gain = ($after[-1].Co2 + $after[-1].CellCo2) - ($before[-1].Co2 + $before[-1].CellCo2)
    Write-Host ("sum before injection varies by {0:0.000} mol" -f (Get-Spread $before.Sum))
    Write-Host ("sum after injection varies by  {0:0.000} mol across up to {1} outdoor cells" -f (Get-Spread $after.Sum), ($after.Cells | Measure-Object -Maximum).Maximum)
    Write-Host ("the planet and its cells gained {0:0.000} mol of 100000 injected, of which {1:0.000} is CO2; the other gases took {2:0.000} mol of it in trade" -f `
        $gain, $co2Gain, ($gain - $co2Gain))
    $problems = @()
    if ((Get-Spread $before.Sum) -gt 1) { $problems += 'sum drifts before injection' }
    if ((Get-Spread $after.Sum) -gt 1) { $problems += 'sum drifts after injection: gas is being created or lost' }
    if ([math]::Abs($gain - 100000) -gt 1) { $problems += 'the planet did not receive what was injected' }
    if ($co2Gain -lt $gain * 0.999) { $problems += 'what reached the planet is not the gas that was injected' }

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
    foreach ($key in $envKeys) { Remove-Item "Env:$key" -ErrorAction SilentlyContinue }
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
