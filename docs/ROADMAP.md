# Roadmap

## Next, in order

1. **Hold the model to the game.** `run.ps1 -Model` judges it and passes within 0.2 K on Venus,
   Vulcan, Europa mid-route, the Moon, Mimas with heat, ice caps melting back, a forced storm, and a gas
   cycling across its freeze threshold every night (TEMPERATURE.md). Still to run: Vulcan at
   the near end of its orbit (the model says 320 K, 3 K inside the limit; needs a way to set the
   orbit position).
2. **Mimas's heat, as a player would make it.** Held at +25 K by the test driver, volatiles stay a
   gas: the mechanism works. Not shown: that a base venting warm gas reaches +25 K (BALANCE.md sizes
   it on paper). That is what decides whether 60 min and 50 K are the right defaults for
   `ExternalHeatHalfLifeMinutes` and `MaxExternalOffsetKelvin`; needs play.
3. **Removal worlds in play.** Vent intake, filtration, storage and the game's own trader buy orders
   are all priced (BALANCE.md). Removal turns out never to be the longer job; the cost of a removal
   world is a tank farm, about 48 big tanks for Venus at Standard size. Unplayed, and nobody has
   counted the vents a real base ends up with, which is what decides whether that holds (S10).
4. **Tighten what a base runs** (S10). Still unmeasured: how fast a base actually burns volatiles for
   its carbon dioxide (S15, the one gap that would move the hours on every world but Venus),
   farm-fed composters (6,000 mol/h each), quarry throughput on ice veins, plants turning CO2 into
   oxygen in place.
5. **Improve `path.py`.** It is greedy: on Vulcan it strips fuel before adding the bulk gas that would
   have kept nights above CO2's freezing point, then pays for helpers. A search over orderings would
   give honest costs rather than upper bounds.
6. **Play it.** One session has been played, on Mars at planet size 0.01 (VERIFICATION.md): a vent
   drew planet air into a sealed room and the planet took it back, composition, external heat, a
   save and load, a storm on untouched air and the gas sensor all behaved, and real outdoor cells
   drain back to the tank. A second world has been seen, Vulcan in creative, with its air set by a
   memory edit rather than played: the mod's own temperature response drove a planet for the first
   time outside the test driver, -131.044 K to 273.04 K. Still not seen by a person: the sky, frame
   time in a large base (defect
   D10, and the temperature postfix recomputes the greenhouse index per call, ASSUMPTIONS.md M9),
   rain and snow frequency on a small planet, and a session long enough, with `StatusLogSeconds`
   set, to say what a real base moves per hour.
7. **Test what has never run live.** D2 (build over an occupied outdoor cell), D6 (weather guard)
   and a custom world are done, headless, 2026-09-21 (VERIFICATION.md). Left: the heat decay rate,
   and multiplayer sync with and without the mod on the client, which needs a second machine.
   The zero-volume custom world found three wrong lines in the readout on that path, D19.

## Before 1.0

Published 2026-09-21 as a release candidate: Workshop id `3805529206`, repository public, `v0.9.0`
and `v0.9.1` tagged at the two uploads. The release-candidate note in About.xml stays until items 1,
6 and 7 are done.

## Open questions

- **Liquid toxins outdoors** (ASSUMPTIONS.md S6): breathing ignores liquids (code); does contact hurt?
- **Outdoor sun heating.** The old mod set `LightManager.SunPathTraceWorldAtmos = true`, which makes
  open outdoor cells take solar heating (`AtmosphericsController.cs:244`). Left as the game ships it.
- **Albedo** from clouds and ice (TEMPERATURE.md option 3).
- **First session on an existing save** gains the gas of outdoor cells that were never debited.
  A one-off correction at first enable is possible but needs a marker in the save.
- Is helium obtainable in quantity (`DepositMaterialGasData` lists it for rocket mining)? Mimas's
  path uses 20 mol per cell of it as the swing damper; hydrogen or nitrogen can stand in.
- A newer fork of the original mod, or another mod doing this, was never searched for.

## Deliberately not done

- Porting the old mod's code (no licence) or its temperature formula (no phase change, ignores the
  game's curves).
- Patching `LatentEnergyOffset`, the game's `GiveToGlobal` body, Mars's curves, or the game's phase
  change (whole-gas freezing is harsh, but it is the game's rule and players can use it).
- Per-world numbers in the mod. One rule for every world, so custom worlds work.
- A guide to curve editing in the Workshop description: CURVES.md ships in the mod folder instead.
