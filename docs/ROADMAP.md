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
3. **Removal worlds in play.** Vent intake, filtration and storage are priced (BALANCE.md): the cost is
   a tank farm, about 47 big tanks for Venus at Standard size. Unplayed; trader sales unpriced.
4. **Tighten the base tiers** (S10). Combustion is priced in ices (BALANCE.md) and agrees with the
   presets. Still unmeasured: farm-fed composters (6,000 mol/h each), quarry throughput on ice veins,
   plants turning CO2 into oxygen in place.
5. **Improve `path.py`.** It is greedy: on Vulcan it strips fuel before adding the bulk gas that would
   have kept nights above CO2's freezing point, then pays for helpers. A search over orderings would
   give honest costs rather than upper bounds.
6. **Play it.** One hour on Mars with `StatusLogSeconds` set, on a copy of a save: what a real base
   moves per hour, the sky, frame time in a large base (defect D10, and the temperature postfix now
   recomputes the greenhouse index per call, ASSUMPTIONS.md M9), rain and snow frequency on a small
   planet.
7. **Test what has never run live**: D2 (build over an occupied outdoor cell), D6 (weather guard),
   heat decay rate, multiplayer sync with and without the mod on the client, a custom world.

## Before publishing

- Repository is private; the Workshop description links to it. Make it public or drop the link.
- `WorkshopHandle` is 0 until first publish.
- The alpha warning in About.xml stays until items 1, 6 and 7 are done.

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
