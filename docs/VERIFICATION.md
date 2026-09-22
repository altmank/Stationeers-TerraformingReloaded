# Verification

**A static audit is not enough for this mod.** Three code audits missed D12; the first live
save-load run showed 5,800 mol appearing from nowhere. Run the live tests after any change to
`src/Patching/` and after a game update. Close the game first; each run takes about four minutes, and only one can run at a time.

```powershell
.\build.ps1 -Deploy                         # build, PatchCheck, stage package, copy to local mods
.\tools\LiveCheck\run.ps1                   # conservation, console command, planet size
.\tools\LiveCheck\run.ps1 -SaveLoad         # save mid-spread, load, total unchanged
.\tools\LiveCheck\run.ps1 -Reset            # terraform, reset, save, load WITHOUT the mod: stock
.\tools\LiveCheck\run.ps1 -Vanilla          # control: without the mod the gas vanishes
.\tools\LiveCheck\run.ps1 -WallVent         # the wall vent fix: a cell appears, the planet total does not move
.\tools\LiveCheck\run.ps1 -BuildOver       # building over an occupied outdoor cell: the planet total does not move
.\tools\LiveCheck\run.ps1 -BuildOver -Unguarded   # the same with the guard off: it must duplicate, or the check proves nothing
.\tools\LiveCheck\run.ps1 -Weather         # a cloud bucket filling must not overwrite weather that is already running
.\tools\LiveCheck\run.ps1 -Weather -Vanilla      # control: unmodded it does overwrite it
.\tools\LiveCheck\run.ps1 -CustomWorld     # a world written here, not shipped by the game: the mod fills in what it leaves out
.\tools\LiveCheck\run.ps1 -CustomWorld -ZeroVolume   # the same world declaring no planet volume: the mod must refuse it
.\tools\LiveCheck\run.ps1 -Strip           # air taken OUT through outdoor cells: what share do cells hold while it leaves
.\tools\LiveCheck\run.ps1 -WalkCost        # what a per-tick walk over every atmosphere costs; add to -Strip or the default
.\tools\LiveCheck\run.ps1 -Clean          # remove what an interrupted run left behind, and nothing else
.\tools\LiveCheck\run.ps1 -MenuPressure     # the new-game menu sees the shipped planet, not the resized one
.\tools\LiveCheck\run.ps1 -Rescale          # terraform size: the planet scales whole and its air does not move
.\tools\LiveCheck\run.ps1 -Rescale -RescaleBy 0.37             # the same, shrinking rather than growing
.\tools\LiveCheck\run.ps1 -Dump tools\Balance\gamedata.json   # game data for tools/Balance
.\tools\LiveCheck\run.ps1 -Model -World Venus -SetAir "CarbonDioxide=23;Oxygen=48"     # judged: the game's temperature against the simulator's
.\tools\LiveCheck\run.ps1 -Observe -World Venus -SetAir "CarbonDioxide=23;Oxygen=48"   # the same, printed and not judged
.\tools\LiveCheck\run.ps1 -Model -World Vulcan2 -Storm VulcanAshStorm -StormTick 40    # judged, with a storm forced on at tick 40
```

| Tool | Proves | Cannot prove |
| --- | --- | --- |
| `tools/PatchCheck` (run by `build.ps1`) | Required targets exist, each tank method asks the question exactly once, the rewritten IL runs (it calls the rewritten `AddEnergy` and `RemoveEnergy`) | Anything reaching Unity native code: outside Unity the CLR refuses to compile it ("ECall methods must be packaged into a system module"). The save guard is one; it reports `bound` and the extras are then not attempted |
| LiveCheck default | In a real headless Mars world: mod loads, the in-game self-test passes, every part compiles under Unity, the tick runs live, 100,000 mol injected outdoors spreads over about 4,300 cells and drains into the tank with tank plus cells level to 1 mol and gaining exactly what was injected, almost all of it as the gas injected; `terraform` status, `curves export`, `curves reload` answer correctly; the planet is exactly the set share of shipped Mars (45,594,999.269 mol) with 8.66 mol CO2 per cell | Play, building, other worlds, sky, weather, multiplayer |
| `-SaveLoad` | A save taken with about 4,300 cells in flight loads back with the total unchanged (D1, D12) | |
| `-Reset` | After terraform, dirtying ice caps, clouds and both heat stores, `terraform reset confirm`, save: the **unmodded** game loads a planet at stock, plus at most what the outdoor cells still standing could hand back afterwards (what they held times how much denser than stock the planet was), and within 0.01 K | |
| `-Vanilla` | The control: the default scenario fails without the mod, so its pass means something | |
| `-BuildOver` | Building into an occupied outdoor cell (D2). A headless run cannot build a structure, so the driver calls what a structure calls, `AtmosphericEventInstance.StructureBlockingGrid`. It makes two neighbouring cells in open sky, gives both gas (a cell holding what the planet holds is culled on the next tick, `Atmosphere.IsLive`), waits for the simulation to link them as open neighbours, tops the first one up, and hands over the grid. Tank plus cells must not move | That a real structure reaches this by the same route, and whether a cell being there afterwards means anything: the neighbours the divide filled push gas straight back and the game rebuilds one |
| `-BuildOver -Unguarded` | The counterfactual, and the reason the run above is worth anything: the same run with the mod's `Deregister` guard unpatched must show the duplicate. **`-Vanilla` is no control here** (D2 is the only defect where that is true): unmodded the planet discards whatever it is handed, so the defect costs nothing until the mod switches the planet simulation on | |
| `-Weather` | A cloud bucket filling while other weather is already running (D6). The driver starts snow, fills the liquid clouds past their volume, and reads what the next tick did. Snow is the case that reaches the defect: the game's own guard only steps aside for a storm or for rain, which leaves its snow branch unreachable. The bucket must come back empty, which is what says the tick reached this code at all, and snow must still be the running event with its length untouched | That clouds fill this way in play rather than by hand, and how often it happens |
| `-Weather -Vanilla` | The control: unmodded, the same run must replace the running snow with rain | |
| `-Strip` | The direction a player stripping a planet actually goes, which the injection scenario does not cover. A headless run cannot build a vent, so the driver does what an inward `ActiveVent` does, cloning the outdoor cell at its own grid and removing gas from it, at `-StripCells` grids for `-StripTicks` ticks, discarding what it takes. Not judged as a conservation test: it reports what share of the planet outdoor cells hold while air is leaving, which is the number the storm rule's measure turns on | That a real vent keeps the same number of cells open. The floor stands in for the vent structure, which cannot be built headless |
| `-WalkCost` | What a per-tick walk over every atmosphere costs, timed from inside the planet tick where such a measure would sit, printed in microseconds against the cell count. Add it to the default scenario, which sweeps 1 cell to about 4,300 and back, or to `-Strip` | Main-thread frame cost. It is timed against the 500 ms tick, which is the right budget for `Guards.Upkeep` and the wrong one for D10 |
| `-CustomWorld` | A world the mod has never seen. `tools/LiveCheck/GameData/TRTestWorld/TRTestWorld.xml` is written here, not shipped by the game, and staged into the test mod's `GameData`, which is where `WorldManager.LoadDataFiles` reads worlds from. It has air and its own `Temperature` curve and leaves out the greenhouse and density curves, so the mod has to supply those two, and add nothing at all while the air is as the file sets it. Nothing of the game's is copied: the world names the terrain, sun and sky the game installed, and `StreamingAssetLoader.GetPathRoots` resolves those against StreamingAssets | That a Workshop world is built this way; only that a world the mod has no knowledge of works |
| `-CustomWorld -ZeroVolume` | The same world declaring a planet of no volume, which every per-cell share would divide by. The mod must warn and leave the planet as shipped | |
| `-MenuPressure` | The new-game menu's mix (D16). While a resized planet is being played, `GlobalGasMix.Create` on the same world data has to give the shipped planet at the shipped volume, because the menu divides one by the other to show a pressure | That the menu screen itself reads it; only that what it reads is built right |
| `-Rescale` | `terraform size <share> confirm` on a live planet, with the ice caps, the clouds and both heat stores loaded first so they are not zero. The command runs from the main thread while the planet ticks on its own, which is how a console command reaches it, and the figures either side are read under the tank lock: the air per outdoor cell, the pressure and both heat offsets in kelvin must not move at all, while volume, moles, cells, cap and cloud contents and the cap volume all move by the same factor. Then forty more rescales from the main thread holding nothing, which is where a rescale that did not take the lock would tear a tick in half: every one must answer, the planet must still be ticking, and it must end back at the share asked for. That share is `-RescaleBy` times the planet's present size, measured in the game, so it can never be the share the planet already is. The refusals are asked for too (zero, not a number, out of range, and no `confirm`), and status must still report that the setting and the planet disagree | That the new size survives a save and a load (the tank's volume is in the save data the `-SaveLoad` path already exercises), and multiplayer. A run that does not hit the unlocked window proves nothing about it |
| `-WallVent` | The wall vent fix (D15). A headless run cannot build a vent, so the driver hands the hook body the two grids a wall vent would, one with a cell and one without, from the planet tick where every mole reads live. A cell must appear at the empty side and tank plus cells must not move | That a real vent's two grids are these two, or what the vent then does with the cell |
| `-Model` | **Judged.** `-Observe`, then `tools/Balance/compare.py` rebuilds the simulator from what the game reported at every sample (air, sun angle, place in the orbit, latent and external heat) and fails the run if the game's temperature and the model's differ by more than `-Tolerance` (0.5 K). `-HeatK n` holds the planet's banked outside heat at n kelvin; `-SetAir2 ... -SetAir2Tick n` sets a second air later without touching clouds or ice caps; `-Storm <id> -StormTick n` forces a weather event on at tick n, because the game schedules one only after a cooldown of days. Passing: Venus, Vulcan, Europa mid-route, the Moon, Mimas with heat, ice caps melting back on Europa, and an ash storm on Vulcan both cooled and untouched, all within 0.35 K (TEMPERATURE.md) | Anything about how a player gets there |
| `-Observe` | Not judged. Prints the planet every five ticks through a fast day (`-DaySpeed`, default 10x) on any `-World`, with or without the mod (`-Vanilla`), optionally after setting the planet's air per outdoor cell (`-SetAir "Gas=mol;Gas=mol"`, unnamed gases emptied): sun angle, place in the orbit, the temperature outdoor cells get, the readout, pressure, gas, liquid, both clouds, ice caps, latent heat, weather, composition. This is how `tools/Balance` is held to the game: set the air a recipe or a path waypoint calls for and compare | Anything about how a player gets there |

Latest results, 2026-09-21, mod 0.9.0, every run at planet size 0.01. **The suite no longer depends
on the size it is run at.** It had been calibrated at 0.05, and at 0.01 two scenarios failed; neither
failure was the mod (see below).

- Default: tank plus cells varies 0.000 mol before the injection and 0.005 mol after it, across up to
  4,280 outdoor cells. The planet and its cells gained 99,999.970 mol of the 100,000 injected, of
  which 99,995.066 is carbon dioxide. Planet exactly 0.01 of shipped Mars, 8.6599 mol CO2 per cell.
- `-SaveLoad`: 0.000 mol across the save and load of 4,309 outdoor cells.
- `-Reset`: the unmodded game loads the planet 1.304 mol above stock, against the 2.575 mol the
  7.178 mol still standing in outdoor cells could hand back to a planet 21.95 % denser than stock.
  Ice caps, clouds and latent heat zero; external heat 2.0e4 J, about 2e-5 K.
- `-Rescale`, three runs, each from 0.01: to 0.025, to 0.137 and to 0.0037. Volume, moles, outdoor
  cells, ice caps, clouds and the ice cap volume all moved by exactly the factor; pressure
  (2.5614 kPa) and both heat offsets in kelvin did not move at all, and the air per outdoor cell was
  unchanged gas by gas. In each run 41 rescales crossed from the main thread into a ticking planet,
  every one answered, and the planet was still ticking and back at the share asked for afterwards.
- `-BuildOver`, first run live, 2026-09-21: a cell holding 5,014.057 mol with 6 open neighbours was
  built over, and tank plus cells held at 2,294,749.963 mol, unmoved to three decimals. With the
  guard off, the same run at the same numbers put 1,734.470 mol into the planet twice. It is less
  than the cell held because the event is queued: the cell goes on draining the ordinary way until
  the atmospherics pass applies it, and what lands twice is whatever is left at that moment. Both
  figures repeated exactly across runs.
- `-Weather`, first run live, 2026-09-21: with snow running and the liquid clouds filled past their
  volume, the tick emptied the bucket into the air and left snow running with its length unchanged at
  218.08. Unmodded, the same run replaced snow with rain and reset the length to 186.98. The bucket
  emptying in both is what says the tick reached the code rather than the check passing by default.
- `-CustomWorld`, first run live, 2026-09-21: a world written by hand loaded and ran. The mod's
  self-test balanced on 8.5 mol, which is the world's own air (6 CO2, 2 nitrogen, 0.5 oxygen per
  outdoor cell), and the planet read 5.999987, 1.999996 and 0.499999 at tick 40. It reported
  `fills greenhouse=True, density=True, base=False` and `adding 0 K now`, with the greenhouse and
  density parts both 0 K: a world nobody has touched reads exactly what its author set.
- `-CustomWorld -ZeroVolume`: the mod warned and stood down, the planet held 0.000 mol and the
  per-cell list was empty, which is the behaviour the guard is for. Three things it says on the way
  are wrong, all readout and none of them behaviour; see DEFECTS.md D19.
- `-Strip` and `-WalkCost`, first runs, 2026-09-21, size 0.05. **Outdoor cells hold almost nothing
  while air is being taken out**: 0.0007 % steady and 0.0402 % at the single tick where all 100 draw
  cells existed at once, against 5.2 % at the peak of a 100,000 mol injection. Of 100 cells built at
  once, 99 were gone by the next planet tick with their air back in the tank, culled for sitting at
  planet density with no open neighbours. Gas pushed out into the world spreads and makes cells; gas
  taken out of the world cannot make any. Conservation exact: unaccounted 0.000 mol.
  **The walk is cheap**: 270 ns per cell, so 1.2 ms at 4,289 cells, 0.25 % of a 500 ms tick, and
  iteration itself is 1.8 ns per entry with the whole cost in `GasMixture.GetTotalMoles`. Break-even
  at 1 % of a tick is about 18,500 cells. Together these are why the storm rule's measure counts the
  planet's five stores and not outdoor cells (docs/STORMS.md).
- `-Model` on Venus at `CarbonDioxide=23;Nitrogen=22;Oxygen=48`: largest gap 0.178 K over 50 samples,
  against a 0.5 K tolerance, with the planet at 322.18 K and its air steady (93.0 mol per cell, no
  liquid, clouds or ice caps).

**The planet trades species, and that is not a leak.** While thousands of outdoor cells hold nearly
pure carbon dioxide and the planet's own mix has shifted under them, the game's mixing moves totals
and takes the composition from whichever side it draws from, so the planet ends a little short of
carbon dioxide and a little over on everything else. Measured at size 0.01: carbon dioxide 4.9 mol
under the 100,000 injected, while oxygen, nitrogen and pollutant each ended 0.021 % over, which is
the same 4.9 mol, with the total exact to 0.005 mol. The trade is as large as the injection shifts
the planet's own composition, so it is about five times bigger at size 0.01 than at 0.05 (5.7 mol
against 1.3 mol on the tank alone). That is why an assertion on one gas within 5 mol only passed at
the size it was measured at. The suite now judges the total, which is what the scenario is about, and
then that what arrived is overwhelmingly the gas injected. The driver logs the planet gas by gas,
which is what told a trade from a leak.

`-Reset` had the same shape of problem: the cells still standing hand back what they hold over the
ticks between the reset and the save, and they were resting at the terraformed planet's density,
which is a larger share of a smaller planet. Its bound now comes from the run's own figures rather
than from a number measured once.

## The first played session

2026-09-20, mod 0.1.0 at tag `v0.1.0`. Mars, creative, planet size 0.01, so 50,000 outdoor cells.
Everything above this line was the headless driver. This is the first time a person played on the
mod, built on it, and read its numbers off the game's own instruments.

**The planet at rest.** Tick 1331: 455,940.230 mol, 2.144 kPa, 226.2 K, 9.118805 mol per outdoor
cell (CO2 8.659737, N2 0.269992, O2 0.131078, pollutant 0.057998). Temperature parts: base 240,
sun distance 10, greenhouse 1.5, density -25.3, weather 0, latent 0, external 0, and the mod
adding 0 K. The untouched-air invariant therefore holds in play, not only at world start. The
planet read 9.76 mol under the exact hundredth share of shipped Mars (455,949.993 mol), which is
the air the one real outdoor cell was holding, so planet size is exact in play too.

**Drawing planet air in.** A powered active vent set Inward (`VentDirection.Inward` is 1, Outward
is 0) fed a pipe network serving a sealed 7,436 L space. By tick 4360 the planet read
448,732.870 mol, so 7,207.360 mol had left it, and the space held 2,395.24 mol at 636.33 kPa and
237.6 K. Its volume, computed from the gas law at every sample, was a constant 7,436 L, which is
the check that the instrument itself is sound.

**What arrives is the planet's own mix.** The drawn gas matched the planet to seven figures at the
in-game gas sensor and again at the vent's pipe side: CO2 share 0.9496571 and 0.9496570, against a
planet that moved only from 0.949656 to 0.949657 across the whole run. Nitrogen, oxygen and
pollutant matched to the same decimal. So a take is a share of the planet, not a fixed recipe, and
draining the planet does not drift its composition.

**Real outdoor cells drain back to the tank.** The door was opened and the space vented (room down
to 10.323 mol, 2.711 kPa). The planet recovered over about two minutes: 452,612.715 mol at tick
4480, 455,905.624 at tick 4582. Of the 7,207.360 mol that left, 7,172.754 came back. The residual
is 34.606 mol, 0.0076 % of the planet and about 3.8 cells' worth, still held in the real outdoor
cells by the door. Nothing overshot, so nothing was created. This settles a question the code left
open: a real outdoor cell does not sit at planet density in equilibrium, it drains.

**External heat, first seen in play.** Venting 636 kPa gas into a 2 kPa planet booked
`ExternalInputEnergyOffset` 0.13 K. It rose to 0.18 K while gas was still returning, because every
returning parcel books its own heat, then fell to 0.17 K once the flow stopped. At the default 60
minute half-life the fade is slow. One room of compressed gas is worth a fifth of a kelvin, so the
50 K cap is a long way off at base scale (ASSUMPTIONS.md M8).

**Save and load in play.** Saved, quit to the menu, reloaded: 455,905.624 to 455,905.530 mol, a
drift of 0.094 mol. That is two parts in ten million, a hundredth of one outdoor cell, against the
5,793 mol defect D12 used to put in the file, but it is not the headless test's 0.000 mol either:
the base was still moving gas when the save was taken. The external heat offset of 0.18 K survived
the save. The self-test re-armed on world load and passed, reporting 9.118 mol per cell against
9.119 earlier, tracking air that really was that much thinner.

**A storm on untouched air.** `storm start` gave a weather offset of -7 K and the parts added up as
reported: 245 + 10 + 1.5 + 11.9 - 7 + 0.17 = 261.6 K. The mod still added 0 K, which is right: on
untouched air a storm offset is deliberately unscaled, and the scaling engages only on a planet the
mod has filled in (DEFECTS.md D17).

**Sensors read the planet.** The in-game Gas Sensor's gas ratios matched the planet to seven
figures throughout, which confirms in play what had only been read in the code: an outdoor sensor
and the atmos analyser see the planet's own air.

Not covered by this session: the sky, frame time in a large base, rain and snow on a small planet,
multiplayer, and any world but Mars.

## The first Vulcan session

2026-09-20, mod 0.1.0, creative, planet size 0.01, so 50,000 outdoor cells. Mars was played; this
world was flown: the planet's air was set by a memory edit, the same thing the driver's `-SetAir`
does, so none of it is gameplay. Every number below was read out of the game's memory rather than off
the console readout. It is the first time the mod's own temperature response drove a planet outside
the test driver.

**At spawn, untouched.** 2,849,897 mol, 56.998 mol per outdoor cell against the shipped 57.000 (CO2
12, volatiles 27, pollutant 15, hydrogen 3). The 103 mol under the exact hundredth share is about two
outdoor cells' worth, the same reading as Mars's 9.76 mol. `AggregateTemperature` 503.41 K; parts sun
angle 506.53, sun distance -3.11, greenhouse 0.00, density 0.00, weather 0, latent 0, external 0.01,
and the mod adding 0 K.

Two things follow that no earlier evidence gave:

- **Vulcan ships neither a greenhouse nor a density curve.** Both parts read exactly 0.00 in the live
  game. That is what the dump said (GAME-MODEL.md), and it is why the mod fills both in here.
- **The untouched-air invariant holds on a world the mod fills in.** On Mars the mod defers to the
  game entirely, so adding 0 K there is true for free. On Vulcan both halves of the rule are live and
  it still adds exactly 0 K on the starting air.

**The response, on a real planet.** The air was then overwritten to the fire-safe recipe (19 CO2, 46
N2, 193 O2, 1 pollutant per cell, the volatiles and hydrogen zeroed): 12,950,000 mol, 259.000 per
cell, the hundredth share exactly. `Climate.LastAdjustment` read **-131.044 K**. The parts were sun
angle 404.72, sun distance -0.65, greenhouse 0.00, density 0.00, external 0.01, so the game alone
would have read 404.08 K; the planet read **273.04 K**.

`tools/Balance` on that air gives 272.85 K at the cold end of the day, 0.19 K under what the game
read. The sample was near dusk: its base term pins the sun angle at 94.2 degrees, and there the model
gives an adjustment of -131.10 K and a planet of 273.00 K, so model and game are 0.04 K apart on the
same inputs. The judged Vulcan runs already covered this air headless (TEMPERATURE.md); what is new
is that the rule behaves the same in a world someone is standing in.

Not covered by this session: playing Vulcan rather than editing its air, and everything the Mars
session did not cover either.

## The census

```powershell
python tools\census\census.py <decompile folder> --check
```

Lists every method in the game that touches the planet or outdoor air and fails unless each has a verdict in
`tools/census/ledger.csv` and every bypass is fixed or accepted in writing (INTERACTIONS.md). Run it after a
game update, against a fresh decompile (`ilspycmd -p -o <folder> Assembly-CSharp.dll`): anything new or moved
comes up as unclassified. It needs a decompile, so it runs locally, not on GitHub; GitHub does check that every
census pattern still matches its sample.

## What runs on GitHub, and what cannot

`.github/workflows/ci.yml` runs on every push. The mod cannot be compiled there: it references the
game's assemblies, which are not ours to publish, and no public reference package for Stationeers
exists (checked nuget.org and nuget.bepinex.dev). So CI runs what needs no game:

- `tools/ci/check_repo.py`: one version everywhere and in the changelog; About.xml and the embedded
  default curves are well-formed; **the temperature rule's constants are identical in `Climate.cs` and
  `planet.py`**; doc links resolve; files `build.ps1` ships exist; no game data, binaries, decompiled
  code, locally excluded file or email address is tracked, and every commit has a noreply identity.
- `tools/ci/test_model.py` on a synthetic fixture (`make_fixture.py`; invented worlds and straight-line
  curves, nothing from the game): untouched air reads as shipped under any settings, a world with its
  own curves is left alone, a stripped hot world reaches equilibrium, thinning never widens the swing,
  coolants are not amplified, no cliff at the anchoring threshold, the freeze rule, and the solver and
  path planner run end to end. Mutation-checked: removing the damping clamp or amplifying coolants
  fails it.

The build, PatchCheck and LiveCheck need the game and run locally. A self-hosted runner on a machine
with the game would close that gap, but a self-hosted runner on a public repository runs strangers'
pull requests on that machine, so it is not set up.

## How LiveCheck works

`tools/LiveCheck/LiveCheck.cs` is a second BepInEx plugin, never shipped, that only acts in batch
mode. It unpauses the world, injects CO2 into an outdoor cell at y = 300 m, runs the console
command, optionally dirties the other reservoirs, resets, saves, or dumps game data, all selected
by environment variables that `run.ps1` sets. It samples tank and outdoor-cell totals from a Harmony
prefix on the planet tick, because that runs on the simulation thread with the previous tick's
workers joined: every value is current and at rest.

`run.ps1` stages the mod and the driver into `<game>\mods`, starts
`rocketstation.exe -batchmode -nographics -logFile <file> -new Mars2` (or `-file start <station> Mars2`
for scenarios that save), reads `BepInEx\LogOutput.log`, judges, kills the instance it started, and
removes what it created. It refuses to start if any of that already exists or the game is running.

## Facts about running the game headless (each cost a failed run)

- Batch mode uses the **game install folder** as its save root, not `Documents\My Games`. It creates
  `mods`, `saves`, `scripts`, `thumbnails`, `modconfig.xml`, `modrepos.xml`, `setting.xml` there. Real
  saves and the real mod list are never touched. `BepInEx\config` **is** shared with normal play.
- LaunchPad lists `<game>\mods` before the game creates that folder, so it must exist beforehand.
- With `-logFile`, plugin output goes to `BepInEx\LogOutput.log`, not the Unity log.
- A headless new world pauses itself (`World.NewAsync` calls `SetGamePause(true)`) and nothing
  unpauses it until a player acts. `GameTickCount` stays 0. The driver calls `SetGamePause(false)`.
- `-settings X Y` launch arguments are overwritten when the settings file loads later.
- The instance opens a LAN host on port 27016 while it runs.
- `-file start <station> <world>` loads the station's latest save or creates the world and saves.
- The one exception in the Unity log, `GameManager.MajorUpdatePopup` NullReference, is the game's
  own in batch mode.
- Totals read from `Update` are a tick stale (the mole cache). The first harness read them there
  and reported a 6,000 mol swing that did not exist.
- A judged sample has to report the exact inputs its own temperature was computed from. The sun
  moves about 0.7 degrees a tick at `-DaySpeed 10` and a base curve can fall 15 K per degree, so
  the driver takes the angle and the orbit position first and hands them to the game, and prints
  them to four decimals. Printing them to one cost 0.7 K of apparent disagreement.
- `-Dump` samples the base temperature curve every degree, not every five. Five-degree samples,
  interpolated, were 5 K out where Vulcan's curve bends at dusk, and a storm's scaling turned that
  into 1.2 K. Only `compare.py` reads the curve off the knots; everything else in `tools/Balance`
  evaluates on them, so the finer grid changes no recipe.
- **A forced weather event need not be the one you named.** `-Storm <id>` and the game's own
  `storm start <id>` both end in `WeatherManager.ImmediatelyActivateWeatherEvent(string)`, which
  looks the id up with `DataCollection.Get<WeatherEvent>` and, when that returns null, silently
  falls back to `GetNextWeatherEvent()`: a random pick from the world's own list. The console
  prints "Started weather event." before the lookup, so the only sign of a miss is a separate
  `error getting WeatherEvent Id:` line. **Seen in play**: `storm start MarsDustStorm` printed
  that error and started an event anyway. Why the lookup misses is **unresolved**. Everything the
  code says should make it work: `WorldManager.LoadDataFiles` loads every xml under `S/Data`,
  `MarsDustStorm` is a `<WeatherEvent>` at game-data level in `S/Data/weather.xml` and has every
  field `WeatherEvent.IsValid` asks for, `Initialize` then registers it under
  `Animator.StringToHash(Id)`, the console passes the id with its case intact, and the lookup
  hashes it the same way. Nothing clears the registry.
  This does not touch the runs already recorded, because `LiveCheck.StartStorm` prints
  `WeatherManager.CurrentWeatherEvent.Id` and the event's day and night offsets **after**
  activating, so the driver reports what really ran. Read that line, not the id asked for. The
  Vulcan runs report -275 K and +150 K, which in `S/Data/weather.xml` is `VulcanAshStorm` and
  nothing else (the world's other event, `VulcanSolarStorm`, is +500 and +100). Mars2 ships one
  event, so a fallback there can only pick `MarsDustStorm`.
