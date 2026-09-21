# Terraforming Reloaded

A Stationeers mod that makes the air outside finite. Every world gets a planet-sized atmosphere
that loses what your base draws in and keeps what you vent. Change it enough and the pressure,
temperature, sky and weather outside change with it.

Stationeers already contains a planetary atmosphere model: one shared reservoir of gas per world,
ice caps, clouds that rain out, and a temperature built from sunlight, greenhouse gases and air
density. It ships switched off, so vented gas vanishes and drawn-in air comes from nowhere. This
mod switches it on and fixes what goes wrong when it runs for real.

Inspired by [Elmotrix's Terraforming Mod](https://github.com/Elmotrix/TerraformingMod), which did
this by hand before the game had a model of its own. This is a new mod, not a port of that code.

> **Alpha, and barely played.** Automated tests on Mars show gas is conserved, saves load back
> exactly, and the reset returns a save to stock; the temperatures are checked against the game by
> automated runs on Venus, Vulcan, Europa, the Moon and Mimas. One session has been played, on Mars:
> a vent drew planet air into a sealed room and the planet took all of it back, a save and load held,
> a storm read as it should, and the gas sensor agreed with the planet. Vulcan has been looked at in
> creative: with the air set to a finished recipe the planet sat at 273 K where it reads 404 K without
> the mod. Nobody has played it through, and the sky, multiplayer and every world but Mars have not
> been played by a person.
> If you install it you are an alpha tester: back up your save first, expect wrong numbers, and
> please open an issue with your `Player.log`.

## Install

1. Install [StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad).
2. Subscribe on the Workshop, or build from source (below).
3. Enable it in the in-game mods list.

Back up a save before its first session with the mod. The mod changes what the game writes into
the planet section of the save; the game reads that section with or without the mod.

## What to expect

- **Scale.** Terraforming is an endgame project for a base built to process gas. How long it
  takes is one setting, below. `terraform` in the console shows where you are.
- **Mars** uses the greenhouse and density curves the game ships for it.
- **Europa, Venus, Vulcan** ship without those curves, so the mod supplies the response, measured
  from each world's starting air. Untouched, they read exactly as they do without the mod.
- **Venus and Vulcan** start hot because of the air they start with. The mod reads that heat as
  bare rock plus greenhouse warming, so taking the greenhouse gases out cools them, and thick air
  evens out Vulcan's day and night. They are removal projects. On Vulcan put bulk gas in **before** the
  fuel is more than half gone: its greenhouse is what keeps nights warm, and once nights fall under
  220 K the carbon dioxide freezes out, then everything else, and an atmosphere frozen into the ice
  caps comes back very slowly.
- **Cold worlds freeze what you vent.** The game freezes a gas out of the air, all of it, once the
  planet is colder than about 2 K above that gas's freezing point: carbon dioxide below 220 K, pollutant below
  175 K, volatiles below 84 K. It lands in the ice caps and comes back when the planet warms. On
  Europa, carbon dioxide only stays once nights pass 220 K, and getting there takes volatiles (see
  Pace, below, for why that is not simple).
- **Custom worlds** work without being added to the mod; see [WORLDS.md](WORLDS.md).
- **The Moon and Mimas** ship with no air and no base temperature. Their first air settles toward
  the temperature a bare rock reaches at that distance from the sun (the Moon: about 250 K,
  swinging from 195 to 305 K between night and day until the air thickens).
- **Storms** still come. On a world you have cooled, a storm's temperature swing shrinks in the same
  proportion as the planet has, so a finished Vulcan survives its ash storms; they are still dangerous.
- **Seas.** Enough liquid on the planet (rain that stays liquid) and the game raises a sea, 2 to 10 m
  above its datum, and everything outdoors below that level counts as under water. The amount of liquid that takes
  is fixed by the game and does not shrink with planet size.
- **Heat.** Heat your base sheds outdoors warms the planet slightly and fades over time.
- **Wind turbines** pay for thick air: the game clamps the pressure they see to between 5 and
  25 kPa, so a thickened planet runs them up to five times harder, and under 1 kPa they give nothing.
- **Tutorials** are left as shipped.
- **First session on an existing save.** Outdoor cells that already exist were never drawn from
  the planet, so the planet gains their gas once as they are cleaned up. For a large base that is
  a few thousandths of the planet.

## Pace

**Planet size** sets how long terraforming takes and nothing else: the air, pressure and temperature
you start with are identical at every size, only the amount of planet behind each outdoor cell
changes. Pick it in the StationeersLaunchPad config editor.

| Planet size | Share of the game's planet | Mega base | One-rocket base |
| --- | --- | --- | --- |
| Short | 1/100 | about 17 hours | about 70 hours |
| **Standard** (default) | 1/20 | about 85 hours | about 340 hours |
| Long | 1/4 | about 410 hours | about 1,700 hours |
| Shipped | 1 | about 1,650 hours | about 6,700 hours |
| Custom | `CustomPlanetSize` | in proportion | in proportion |

Hours are to reach shirt-sleeve air on Mars: 16 kPa of oxygen, no toxins, no pressure or temperature
warnings day or night, at any point in Mars's orbit. That takes about 125 mol per outdoor cell,
roughly 57 of oxygen and 67 of carbon dioxide, because Mars nights start 50 K too cold and oxygen
itself cools the planet a little. A mega base here means about 380,000 mol an hour sustained (four
ice-mining rockets and gas traders); a one-rocket base about 93,000.

Every world can be made habitable. Roughly, against Mars: the Moon the same, Venus and Vulcan two
and a half to three times the gas moved, Mimas six times and it needs the planet held warm with vented
heat to get started, Europa nine times. Venus and Vulcan are mostly removal: vents and filters keep
up easily, and the real cost is somewhere to put the gas (Venus at Standard size fills about 47 big
tanks).

Fuel and oxygen do not share the outdoors: the game burns volatiles or hydrogen beside oxygen at the
first spark, at any temperature, and the fire spreads. Vulcan's fuel has to be gone before its oxygen
arrives. Europa is the awkward one, because volatiles are the only gas that warms at 124 K and its
air is oxygen: the safe route takes the oxygen out first, which is why it costs so much.

The numbers come from `tools/Balance`, which models the planet from curves and machine values the
game itself evaluated, plus stated assumptions about what each size of base runs.

The size applies when a planet is created: a new world, or after `terraform reset confirm`. A saved
planet keeps the size it has, and `terraform` shows it. That includes a save from before the mod:
it holds the game's full-size planet, so it plays at Shipped size until you reset it (which changes
nothing on a planet you have not touched yet).

If the size turns out wrong once you have a feel for it, `terraform size <share> confirm` rescales
the planet you are playing without touching its air. The planet, its clouds and its ice caps all
move together, so pressure, mix and temperature come out exactly where they were and the only thing
that changes is how much is left to do. The air in your base and in every outdoor cell is left
alone. One thing does not move with the planet: the amount of liquid the game raises a sea at is
fixed, so on a wet planet a rescale can raise a sea or drain one. The command says so before it
asks you to confirm, and rescaling back undoes it. The setting is left alone: it stays the default
for the next new world.

At Short size one base is a real share of the planet. Mars's coldest nights sit only about 1.5 K above
the point where carbon dioxide freezes out, and pulling a tenth of its air into tanks crosses it: the
carbon dioxide then snows out every night and returns every morning until you put some back.

## Console

F3 opens the console.

| Command | Does |
| --- | --- |
| `terraform` | Whether the mod is live and why not if it is not, the self-test result, planet pressure and temperature, how many kelvin the mod is adding, gases per outdoor cell |
| `terraform size <share> confirm` | Rescales the planet you are playing to that share of the size the world ships at, which changes how long terraforming takes and nothing else. Prints the size, cells, moles and pressure before and after. Host only. `terraform size <share>` alone explains and asks |
| `terraform reset confirm` | Puts the whole planet back exactly as the world ships: starting air, empty clouds and ice caps, no stored heat. Host only. `terraform reset` alone explains and asks |
| `terraform curves export` | Writes the built-in temperature curves to `BepInEx\config\TerraformingReloaded.curves.xml`. Never overwrites |
| `terraform curves reload` | Applies that file at once, no restart |

The game's own `atmos global` overlay also works. The totals `terraform` prints lag by one game
tick while gas is moving, because the game caches gas quantities off its simulation thread.

## Removing the mod

The game saves the planet whether or not the mod is installed, so removing the mod alone leaves
your changed planet in the save: frozen, no longer fed by what you vent. On the Moon and Mimas it
is worse, because the game gives those worlds no temperature of their own and their new air would
sit near 0 K. On Venus, Vulcan and Europa the temperature snaps back to what the world ships with:
a breathable Venus is 737 K again the moment the mod is gone. The same happens if you switch the mod
off with `Enabled`.

To go back to stock: `terraform reset confirm`, save, quit, disable the mod. This is tested: the
unmodded game then loads a planet within 1 mol and 0.01 K of stock. Your base
and the air inside it are untouched.

## Multiplayer

The host needs the mod. Clients with it receive the planet from the host every few seconds.
Clients without it can still join; they miss the changed outdoor readings and sky, nothing else.

## Config

Every setting shows in the StationeersLaunchPad config editor with a label and a range, and is
also in `BepInEx/config/xceled.stationeers.terraformingreloaded.cfg`. All apply at once except
those marked.

| Setting | Default | Meaning |
| --- | --- | --- |
| `PlanetSize` | Standard | How long terraforming takes. See Pace |
| `CustomPlanetSize` | 0.05 | Share of the shipped planet, when `PlanetSize` is Custom |
| `Enabled` | true | Master switch. Off leaves the game as shipped. Needs a restart |
| `DynamicSky` | true | Sky follows the air. Needs a restart |
| `GhgResponseScale` | 1 | Greenhouse strength on worlds without their own curve. On Venus and Vulcan it changes only the cooling side. 0 is off. 0 to 5 |
| `DensityResponseScale` | 1 | How quickly thickening air evens out day and night on worlds without their own curve. 0 is off, higher is sooner. 0 to 5 |
| `AirlessAlbedo` | 0.3 | Reflectivity used for the settle temperature of the Moon and Mimas. Lower is warmer |
| `MaxPressureKPa` | 0 | Planet pressure ceiling. 0 is none. **Destructive**: air above it, measured at the hottest hour, is deleted for good. Needs a restart |
| `WeatherOnWeatherlessWorlds` | false | Let filled clouds rain on worlds that ship with no weather |
| `ExternalHeatHalfLifeMinutes` | 60 | How fast heat added to the planet fades. 0 never |
| `MaxExternalOffsetKelvin` | 50 | Most that added heat can shift the planet. Mimas cannot be started without it |
| `SyncIntervalSeconds` | 5 | Host to client update interval |
| `StatusLogSeconds` | 0 | Write the `terraform` readout to the log this often. 0 is off |

The two strength settings tune the temperature response. To reshape it, see [CURVES.md](CURVES.md),
which also ships in the mod's folder.

## If a game update breaks it

The mod checks the game's code before touching it. If the planetary code has changed shape, it
logs what it found, patches nothing, and the game runs as shipped. In every world it also takes one
outdoor cell of air from the planet and gives it back; if that does not balance, takes and gives go
back to the game's own behaviour until the game restarts, and `terraform` says so. Look for `Terraforming Reloaded`
in `%USERPROFILE%\AppData\LocalLow\Rocketwerkz\rocketstation\Player.log`.

## Build

Needs the .NET SDK and a Stationeers install with StationeersLaunchPad.

```powershell
.\build.ps1 -Deploy
```

This builds the DLL, runs `tools/PatchCheck` (which applies the patches to the installed game's
code outside the game and fails the build if the planet methods no longer convert), stages
`package/`, and copies it to `Documents\My Games\Stationeers\mods\TerraformingReloaded`.

After a game update, also run the live tests. They start a headless throwaway Mars world, never
touch your saves or mod list, and take about four minutes each. Close the game first.

```powershell
.\tools\LiveCheck\run.ps1            # gas put outdoors must reach the planet, mole for mole
.\tools\LiveCheck\run.ps1 -SaveLoad  # a save taken mid-spread must load back unchanged
.\tools\LiveCheck\run.ps1 -Reset     # terraform, reset, save, load WITHOUT the mod: planet must be stock
.\tools\LiveCheck\run.ps1 -Vanilla   # control: without the mod the gas vanishes
.\tools\LiveCheck\run.ps1 -Model -World Venus -SetAir "CarbonDioxide=23;Nitrogen=22;Oxygen=48"   # the game's temperature must match the simulator's
```

To re-run the pacing model after a game update, let the game write its own curves and machine
values, then model from them:

```powershell
.\tools\LiveCheck\run.ps1 -Dump tools\Balance\gamedata.json
python tools\Balance\balance.py
```

Set `STATIONEERS_DIR` or pass `-GameDir` if the game is not in the default Steam location.

## Layout

| Path | Holds |
| --- | --- |
| `src/Patching/Gate.cs` | The switch: rewrites the game's eight "is the planet live" checks |
| `src/Patching/Guards.cs` | Fixes for what the game's planetary code gets wrong once live |
| `src/Patching/Climate.cs` | Temperature response for worlds that ship without one |
| `src/Patching/Planet.cs` | Planet size, and the reset to stock |
| `src/Patching/SelfTest.cs` | Notices a game update that changes what the mod depends on |
| `src/Patching/Patcher.cs` | Applies it all, all-or-nothing for the parts that must agree |
| `src/Sync.cs` | Host to client planet state |
| `tools/PatchCheck` | Fast pre-flight: do the patches still fit this game build |
| `tools/LiveCheck` | Headless in-game tests: conservation, save and load, reset, and the simulator against the game |
| `docs/` | Design corpus: goals, how the game works underneath, defects found, architecture, verification, balance, temperature design, roadmap |
| `tools/Balance` | The simulator: cheapest habitable air per world, whether it can be reached step by step, hours per base size |
| `tools/ci` | Checks that need no game; GitHub runs them on every push |

## License

MIT.
