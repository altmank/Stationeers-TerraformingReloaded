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
.\tools\LiveCheck\run.ps1 -MenuPressure     # the new-game menu sees the shipped planet, not the resized one
.\tools\LiveCheck\run.ps1 -Dump tools\Balance\gamedata.json   # game data for tools/Balance
.\tools\LiveCheck\run.ps1 -Model -World Venus -SetAir "CarbonDioxide=23;Oxygen=48"     # judged: the game's temperature against the simulator's
.\tools\LiveCheck\run.ps1 -Observe -World Venus -SetAir "CarbonDioxide=23;Oxygen=48"   # the same, printed and not judged
.\tools\LiveCheck\run.ps1 -Model -World Vulcan2 -Storm VulcanAshStorm -StormTick 40    # judged, with a storm forced on at tick 40
```

| Tool | Proves | Cannot prove |
| --- | --- | --- |
| `tools/PatchCheck` (run by `build.ps1`) | Required targets exist, each tank method asks the question exactly once, the rewritten IL runs (it calls the rewritten `AddEnergy` and `RemoveEnergy`) | Anything reaching Unity native code: outside Unity the CLR refuses to compile it ("ECall methods must be packaged into a system module"). The save guard is one; it reports `bound` and the extras are then not attempted |
| LiveCheck default | In a real headless Mars world: mod loads, the in-game self-test passes, every part compiles under Unity, the tick runs live, 100,000 mol injected outdoors spreads over about 4,300 cells and drains into the tank with tank plus cells level to 1 mol and the tank gaining what was injected; `terraform` status, `curves export`, `curves reload` answer correctly; the planet is exactly the set share of shipped Mars (45,594,999.269 mol) with 8.66 mol CO2 per cell | Play, building, other worlds, sky, weather, multiplayer |
| `-SaveLoad` | A save taken with about 4,300 cells in flight loads back with the total unchanged (D1, D12) | |
| `-Reset` | After terraform, dirtying ice caps, clouds and both heat stores, `terraform reset confirm`, save: the **unmodded** game loads a planet within 1 mol and 0.01 K of stock | |
| `-Vanilla` | The control: the default scenario fails without the mod, so its pass means something | |
| `-MenuPressure` | The new-game menu's mix (D16). While a resized planet is being played, `GlobalGasMix.Create` on the same world data has to give the shipped planet at the shipped volume, because the menu divides one by the other to show a pressure | That the menu screen itself reads it; only that what it reads is built right |
| `-WallVent` | The wall vent fix (D15). A headless run cannot build a vent, so the driver hands the hook body the two grids a wall vent would, one with a cell and one without, from the planet tick where every mole reads live. A cell must appear at the empty side and tank plus cells must not move | That a real vent's two grids are these two, or what the vent then does with the cell |
| `-Model` | **Judged.** `-Observe`, then `tools/Balance/compare.py` rebuilds the simulator from what the game reported at every sample (air, sun angle, place in the orbit, latent and external heat) and fails the run if the game's temperature and the model's differ by more than `-Tolerance` (0.5 K). `-HeatK n` holds the planet's banked outside heat at n kelvin; `-SetAir2 ... -SetAir2Tick n` sets a second air later without touching clouds or ice caps; `-Storm <id> -StormTick n` forces a weather event on at tick n, because the game schedules one only after a cooldown of days. Passing: Venus, Vulcan, Europa mid-route, the Moon, Mimas with heat, ice caps melting back on Europa, and an ash storm on Vulcan both cooled and untouched, all within 0.35 K (TEMPERATURE.md) | Anything about how a player gets there |
| `-Observe` | Not judged. Prints the planet every five ticks through a fast day (`-DaySpeed`, default 10x) on any `-World`, with or without the mod (`-Vanilla`), optionally after setting the planet's air per outdoor cell (`-SetAir "Gas=mol;Gas=mol"`, unnamed gases emptied): sun angle, place in the orbit, the temperature outdoor cells get, the readout, pressure, gas, liquid, both clouds, ice caps, latent heat, weather, composition. This is how `tools/Balance` is held to the game: set the air a recipe or a path waypoint calls for and compare | Anything about how a player gets there |

Latest results (planet size 0.05): sum varies 0.000 mol before and 0.005 mol after injection; the
planet's CO2 up 99,998.6 of 100,000 with the rest still in the last cells; save-load change 0.000 mol;
after reset and mod removal +0.18 mol and 8e3 J (about 1e-4 K).

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
