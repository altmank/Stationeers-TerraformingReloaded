# How the mod is built

## Shape

| File | Holds |
| --- | --- |
| `src/Plugin.cs` | BepInEx entry. Binds config (with LaunchPad editor tags), loads curves, applies patches, registers sync and the console command, optional status logging from `Update` |
| `src/Settings.cs` | Plain static values the patches read, plus the `Log` sinks and the `PlanetSizePreset` enum |
| `src/Patching/Gate.cs` | `Enabled()` and the transpiler that swaps the game's eight `get_IsGlobalInteraction` calls for it |
| `src/Patching/Guards.cs` | Patch bodies for the defects (DEFECTS.md) and the sky throttle |
| `src/Patching/Climate.cs` | Temperature rule for worlds that ship without curves (anchored greenhouse, proportional swing damping, airless base); the curves file |
| `src/Patching/Planet.cs` | Whole-planet operations: planet size at creation, rescaling the planet being played, reset to shipped |
| `src/Patching/SelfTest.cs` | Game-change alarms: is the game's own switch still off, does the temperature formula still use the parts the mod adjusts, and a once-per-world take-and-give round trip on the live planet |
| `src/Patching/Patcher.cs` | Applies everything, in an order that cannot leave the game half converted |
| `src/Sync.cs` | Host to client planet state through LaunchPadBooster |
| `src/TerraformCommand.cs` | `terraform` console command |
| `tools/PatchCheck` | Out-of-game patch pre-flight |
| `tools/LiveCheck` | Headless in-game test driver and runner |
| `tools/Balance` | Pacing and temperature design model |
| `tools/census` | Every game method that touches the planet or outdoor air, with a verdict each (INTERACTIONS.md) |
| `tools/ci` | Checks that need no game |

Nothing under `src/Patching/` references BepInEx, so `tools/PatchCheck` can load and run it.

## Patches

Required set, all or nothing. If any target is missing or a call site count is off, nothing is
patched and the game runs as shipped:

| Target | Kind | Purpose |
| --- | --- | --- |
| PAS `CloneGlobalGasMix`, `GetGlobalMoles`, `TakeGlobalGasMix`, `TakeGlobalMoles`, `GiveToGlobal`, `AddEnergy`, `RemoveEnergy` | transpiler | The switch. Each must contain exactly one getter call, counted before and after |
| PAS `TickPlanetarySimulation` | prefix + finalizer | Hold the tank lock (D8); the once-per-world self-test; upkeep: fade and bound outside heat and latent heat (D3, D4, D14), keep clouds, ice caps and phase rates in proportion to the planet (D16), pressure ceiling |
| PAS `GiveToGlobal` | prefix | Refuse bad mixtures (D7) |
| `AtmosphericEventInstance.DivideWorldAtmosphere` | prefix + finalizer | Flag for D2 |
| `AtmosphericsManager.Deregister(Atmosphere)` | prefix | Empty an already-distributed cell (D2) |
| PAS `CreateGlobalAtmosphere` | postfix | World start: allow or disallow (tutorials, a planet with no volume), invalidate the climate cache, arm the self-test |
| `XmlSaveLoad.GetWorldData` | prefix | Refresh caches before the save reads them (D12). Applied last and on its own, because it reaches Unity native code and PatchCheck must tell that apart from a real failure |

Extras, each independent; a failure costs only that part. The temperature pair is applied even when the
required set stands down, so a planet already terraformed keeps its temperature:

| Target | Purpose |
| --- | --- |
| `WeatherManager.ScheduleWeatherEvent` prefix | D6 |
| `GlobalGasMix.GetGlobalGasMixTemperature(data, angle, percent)` postfix, `PAS.CacheTemperatureCurveOffsets` postfix | Temperature response and its readout (TEMPERATURE.md). Refused if the formula no longer calls the four part getters, or the one-argument overload no longer calls this one |
| `GlobalGasMix.Create` postfix, with a prefix and finalizer on PAS `CreateGlobalAtmosphere` and `RegenerateGlobalFromData` | Planet size, applied only while the game builds the planet being played (D16) |
| `WallVent.OnAtmosphericTick` prefix | A wall vent to outdoors mixes with a real cell, not the read-only copy (D15) |
| `AtmosphericScattering.UpdateAtmosphericScatteringToGlobalAtmosphere` prefix + postfix, then `ManagerUpdate` transpiler | Sky follows the air, throttled (D9). Throttle first, so the sky is never on without it |

## Detecting a game update that matters

Shape is not behaviour, so there are three layers:

1. **Shape, before patching** (`Patcher`): every required target exists and each tank method asks the
   switch question exactly once. Any miss: nothing is patched.
2. **Meaning, before patching** (`SelfTest`): the game's own `IsGlobalInteraction` still answers false
   (if the developers switch the planet on themselves the mod stands down rather than stack on it),
   and `GetGlobalGasMixTemperature` still calls the four part getters the temperature rule is worked
   out against, and its one-argument overload still calls it (otherwise that part is off).
3. **Behaviour, in the world** (`SelfTest.RunIfPending`, first live planet tick of every world, under
   the tank lock): take one outdoor cell of air from the planet, check it fell by exactly that, give it
   back, check it returned, restore the heat counter. Pass leaves the planet as found to within the
   rounding of one cell's air. Fail sets `Gate.Fault`: takes and gives go back to shipped behaviour
   until restart (a new world does not clear it), an error in the log, `terraform` status says so.
   The temperature rule keeps running, so a planet already terraformed does not jump. Airless
   planets report skipped once, quietly, and run when the first air arrives. **Verified live**: "passed:
   take and give of 9.119 mol balanced" on Mars, and LiveCheck now requires that line.

A world whose `GlobalAtmosphere` has no usable `Volume` (possible in a custom world) is left as shipped.

## `Gate.Enabled()`

True only when: patches armed, no self-test fault, config enabled, the world is not a tutorial, not a network client,
and `GameState` is Running or Paused. Called per outdoor cell per tick on worker threads: field
reads only. `Gate.Describe()` says which condition is false, for the status readout.

## Decisions, with the reason

| Decision | Reason |
| --- | --- |
| Rewrite call sites instead of patching the getter | A two-byte static getter is an inlining candidate |
| Hook `GlobalGasMix.Create`, not `GlobalAtmosphereData.GetVolume` | Same: one-line getter |
| Off while loading | D1 |
| Off on clients | A client can give (through `Deregister`) but never take, so its tank would only grow |
| Keep the game's `GiveToGlobal` body | Survives game updates |
| `LatentEnergyOffset` fades and is bounded like outside heat | The game books phase-change heat unevenly, so it is not bounded by the inventory: measured, 17 % stays after one freeze-and-return (DEFECTS.md D14). Earlier versions of these notes said to leave it alone; that was wrong |
| Every temperature term is measured from the world's starting air, and that is evaluated at world start | Requirement 4: an untouched world reads as stock. If it does not, the response switches itself off for that world |
| One postfix on the long temperature method, not three on one-line getters | The swing term multiplies base plus sun distance, which no single part can express; and one-line getters are inlining candidates |
| Sunlight for the anchor comes from the game's orbit range, no table | Custom worlds |
| A storm's temperature offset shrinks with the planet | Offsets are sized for the shipped world; unscaled, one ash storm freezes a finished Vulcan solid (D17) |
| Only one of the 17 read-only-copy bypasses is fixed | The owner's rule: no effort on minor sources. The wall vent moves rooms of gas and is how bases open to outdoors; the rest are a breath, a mask, or one portable's contents (INTERACTIONS.md) |
| Settings tagged as needing a restart do not apply live | The pressure ceiling deletes air for good; dragging its slider must not wipe a planet |
| The clouds and ice caps read the planet's air | The game asks the caps for their own temperature, and they hold no gas (DEFECTS.md D13) |
| Neither strength setting can change where a world ends up | Greenhouse strength is divided out of a hot world's gain, density strength is an exponent: a setting that silently makes Venus or Vulcan impossible is a trap (TEMPERATURE.md) |
| The temperature postfix catches its own exceptions and builds its per-world entry once, under a lock | It runs on every worker thread for every outdoor cell; a custom world that throws must not throw there every tick |
| Worlds with their own curves are never adjusted | Mars is the developers' tuning |
| Guards check `Gate.Enabled()`, not config | A guard acting while the gate is off would change the unmodded game. Exception: `Climate` runs on clients too, since they evaluate the same formula |
| Sync is optional for clients | LaunchPadBooster sections are skipped by a client without the mod; cells themselves are synced by the game |
| Sync payload walks the game's save object by reflection | A gas added in a later game build is carried without a change |
| The planet size **setting** applies at creation only | The save stores the tank's volume, so a setting that applied live would rescale every save a player loads, including one 40 hours in |
| A live planet is rescaled only by `terraform size <share> confirm`, and it leaves the setting alone | A player picks the size before they have any feel for what it means, and the only other way to change it was `reset confirm`, which throws the progress away. Deliberate, on this planet, once |
| A rescale moves the clouds, the ice caps and both heat stores with the tank | What is frozen out is part of what is left to terraform, so a shrunk planet that kept its ice would melt the old share back into a smaller atmosphere; and the heat stores are energies divided by a heat capacity that has just moved, so leaving them would move the temperature |
| Pressure ceiling off by default | It was the old mod's behaviour, not the game's |
| `terraform reset` needs `confirm` | It cannot be undone except by loading an earlier save |
| Status logging runs from `Update`, not the tick | It must still report when the simulation is paused or the planet is off, which is when it is needed |

## Console

`terraform` (status), `terraform size <share> confirm`, `terraform reset confirm`,
`terraform curves export`, `terraform curves reload`. Status totals are read off the simulation
thread and so lag a tick while gas is moving. The rescale takes the tank lock, so it cannot
interleave with a planet tick, and it prints size, cells, moles and pressure either side of itself;
the pressure there is computed from the tank, not read from the game's per-tick planet readout,
which inside one command would be the same reading twice.

## Settings

`PlanetSize` preset and `CustomPlanetSize`; `Enabled`, `DynamicSky` (restart); `GhgResponseScale`,
`DensityResponseScale`, `AirlessAlbedo`, `MaxPressureKPa`, `WeatherOnWeatherlessWorlds`;
`ExternalHeatHalfLifeMinutes`, `MaxExternalOffsetKelvin`; `SyncIntervalSeconds`; `StatusLogSeconds`.
Player-facing descriptions are in the root README.
