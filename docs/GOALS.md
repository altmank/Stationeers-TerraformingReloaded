# Goals

## What this is

A Stationeers mod that makes the outdoor atmosphere finite and changeable. Gas vented outdoors stays
on the planet; air drawn in is taken from it. Change it enough and outdoor pressure, temperature,
sky and weather change. It is an endgame project that gives a gas-processing mega base a purpose.

It started as a request to bring Elmotrix's Terraforming Mod
(https://github.com/Elmotrix/TerraformingMod, v0.23, last commit 2024-08-12) up to the current game.
That mod has no licence, so none of its code is used. Analysis showed a port was the wrong job:
the game had since gained its own planetary atmosphere model and left it switched off. This mod
switches that on. See GAME-MODEL.md.

## Requirements

The project is held to these.

1. **No guessing.** Claims about the game come from its code, its data, or a live run. Where
   something is a modelling choice, it is labelled as one.
2. **Verify by running.** A static audit is not enough (three audits missed defect D12; the first
   live save-load run found it). After any change to `src/Patching/`, the live tests run.
3. **Any world can be made habitable.** Harder worlds may be much harder, but none is impossible.
   Met in the model for all six shipped worlds, with a found fire-safe, step-by-step path for each
   under the game's phase change (BALANCE.md). The model is held to the game within 0.2 K on Venus,
   Vulcan, Europa mid-route, the Moon and Mimas (TEMPERATURE.md). No path has been played.
4. **An untouched world reads exactly as it does without the mod.** Temperature, pressure,
   composition. Only the player's changes move anything.
5. **Pace is one simple setting.** The original mod aimed for "a mega base designed to process gas a
   MINIMUM of 100 hours of effective chugging". Players tune pace with a planet-size preset, not by
   editing curves.
6. **Survive game updates.** The mod checks the game's code shape before patching and, on a
   mismatch, patches nothing and says why. Version checks are advisory, never a refusal.
7. **A clean way out.** A player can return a save to stock. Removing the mod alone does not do
   that (the game saves the planet itself), so `terraform reset confirm` exists and is tested.
8. **Public repository hygiene.** No decompiled game code, no game data files, no personal identity;
   commits as `altmank <altmank@users.noreply.github.com>`. Player docs describe the finished
   product, not where things stand today.
9. **Alpha honesty.** The Workshop description leads with what has and has not been tested.
10. **Nothing assumed away.** A game mechanic is modelled from the game's code and checked live, or it
    is in ASSUMPTIONS.md with its justification and how to settle it. Stated after a simulator that
    skipped phase change produced recipes the game would have frozen out of the air.
11. **Custom worlds work.** No world ids and no per-world numbers in the mod (WORLDS.md).
12. **A game update that matters is noticed.** Shape checks before patching, and a behavioural
    self-test in every world (ARCHITECTURE.md).

## Out of scope

- New structures, items or art assets (a solar shade, a terraforming machine). Everything is done
  with the game's existing machines.
- Changing how the game's own Mars curves behave. Mars is the one world the developers tuned.
- Tutorials. The mod is off in tutorial worlds.
- Supporting StationeersMods. The loader is StationeersLaunchPad on BepInEx 5.4.

