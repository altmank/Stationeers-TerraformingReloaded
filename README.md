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

> **Release candidate.** Gas conservation, save and load, the reset to stock and the temperature model
> are all checked against the running game by automated runs, on Mars, Venus, Vulcan, Europa, the Moon
> and Mimas. [VERIFICATION.md](docs/VERIFICATION.md) lists what each run covers and what it measured.
> That is testing, not play. Back up your save before its first session, and please open an issue with
> your `Player.log` if something reads wrong.

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
  caps comes back slowly.
- **Cold worlds freeze what you vent.** The game freezes a gas out of the air, all of it, once the
  planet is colder than about 2 K above that gas's freezing point: carbon dioxide below 220 K, pollutant below
  175 K, volatiles below 84 K. It lands in the ice caps and comes back when the planet warms. On
  Europa, carbon dioxide only stays once nights pass 220 K, and getting there takes volatiles (see
  Pace, below, for why that is not simple).
- **Custom worlds** work without being added to the mod; see [WORLDS.md](WORLDS.md).
- **The Moon and Mimas** ship with no air and no base temperature. Their first air settles toward
  the temperature a bare rock reaches at that distance from the sun (the Moon: about 250 K,
  swinging from 195 to 305 K between night and day until the air thickens).
- **Storms** still come, and respond to what you have done. On a world you have cooled, a storm's
  temperature swing shrinks in the same proportion as the planet has, so a finished Vulcan survives
  its ash storms; they are still dangerous. A world stops scheduling its own storms once you have
  taken away most of the air it started with, or once that air has become temperate, thick and clean.
  Neither happens on a world you have not changed. Solar storms are not stopped by taking the air
  away, because air was never what caused them. `terraform` says which of the two is holding storms
  back, and which point in the world's year it judged that at, because a world can be calm in one
  season and stormy in another.
- **Seas.** Enough liquid on the planet (rain that stays liquid) and the game raises a sea 2 to 10 m
  deep, and everything outdoors below it counts as under water. The amount of liquid that takes
  is fixed by the game and does not shrink with planet size.
- **Heat.** Heat your base sheds outdoors warms the planet slightly and fades over time.
- **Small spills can gather at your base (experimental, off by default).** Switched on
  (`terraform set TraceGasGatheringEnabled on`), a gas the planet's air holds only a trace of is drawn
  by the outdoor air beside your base 200 times faster than any other gas (the speed at the trace line,
  settable), and faster still the thinner it is, up to 10 times that, so a small spill (a furnace taken apart outside) burns off or reaches your intakes there
  instead of lingering over the whole planet for hours. Every mole comes out of the planet's air. On a
  world whose air burns, expect a brighter burn beside the base while it clears, and a spill too thin
  to burn at all without this can burn briefly with it. The first moles of a gas released on an
  airless world gather around the base the same way before they spread.
- **Wind turbines** reward thick air: the game clamps the pressure they see to between 5 and
  25 kPa, so a thickened planet runs them up to five times harder, and under 1 kPa they give nothing.
- **Tutorials** are left as shipped.
- **First session on an existing save.** Outdoor cells that already exist were never drawn from
  the planet, so the planet gains their gas once as they are cleaned up. For a large base that is
  a few thousandths of the planet. The planet also keeps the size the game shipped it at, because
  a save owns its own size and the setting below only applies to a new world. On Mars that is
  about 1,500 hours of work for a large base, so run `terraform size` to bring it down to
  something you will finish.

## Pace

**Planet size** sets how long terraforming takes and nothing else: the air, pressure and temperature
you start with are identical at every size, only the amount of planet behind each outdoor cell
changes. Pick it in the StationeersLaunchPad config editor.

| Planet size | Share of the game's planet | Mega base | One-rocket base |
| --- | --- | --- | --- |
| Short | 1/100 | about 15 hours | about 72 hours |
| **Standard** (default) | 1/20 | about 75 hours | about 360 hours |
| Long | 1/4 | about 375 hours | about 1,800 hours |
| Unmodded baseline | 1 | about 1,500 hours | about 7,200 hours |
| Custom | `CustomPlanetSize` | in proportion | in proportion |

Hours are to reach air you can breathe outside without a suit on Mars: 16 kPa of oxygen, no toxins,
no pressure or temperature warnings day or night, at any point in Mars's orbit. That takes about
125 mol per outdoor cell, roughly 57 of oxygen and 67 of carbon dioxide, because Mars nights start
50 K too cold and oxygen and nitrogen both cool the planet a little. A mega base here means four ice
rockets mining 60 % of the time, about 330,000 moles of ice an hour; a one-rocket base about 68,000.

**What sets the pace is ice arriving**, on every world. There is no oxygen tap: every mole you put
outdoors was mined somewhere and melted, and carbon dioxide has to be burnt out of volatiles on top of
that. Taking gas away is much faster than making it, because a vent draws in a share of the whole
planet's air at once. So against Mars: the Moon the same, Venus about half, Vulcan and Europa three
times, Mimas five times and it needs the planet held warm with vented heat to get started.

Venus and Vulcan are still removal projects; it is just that the removal is not what takes the time.
The cost of a removal world is somewhere to put the gas. Venus at Standard size fills about 48 big
tanks, Europa 67. The one place a trader helps is Vulcan, where the gas trader buys volatiles in bulk
and would take most of what you strip off the planet, and pay for it.

Fuel and oxygen do not share the outdoors: the game burns volatiles or hydrogen beside oxygen at the
first spark, at any temperature, and the fire spreads. Vulcan's fuel has to be gone before its oxygen
arrives. Europa is the awkward one, because volatiles are the only gas that warms at 124 K and its
air is oxygen: the safe route takes the oxygen out first, which is why it costs so much.

The numbers come from `tools/Balance`, which models the planet from curves and machine values the
game itself evaluated, plus stated assumptions about what each size of base runs.

The size applies when a planet is created: a new world, or after `terraform reset confirm`. A saved
planet keeps the size it has, and `terraform` shows it. That includes a save from before the mod:
it holds the game's full-size planet, so it plays at Unmodded baseline size until you reset it (which changes
nothing on a planet you have not touched yet).

If the size turns out wrong once you have a feel for it, `terraform size <share> confirm` rescales
the planet you are playing without touching its air. The planet, its clouds and its ice caps all
move together, so pressure, mix and temperature come out exactly where they were and the only thing
that changes is how much is left to do. The air in your base and in every outdoor cell is left
alone. One thing does not move with the planet: the amount of liquid the game raises a sea at is
fixed, so on a wet planet a rescale can raise a sea or drain one. The command says so before it
asks you to confirm, and rescaling back undoes it. The setting is left alone: it stays the default
for the next new world.

That fixed amount is the one planet-scale number the game holds absolute, so size decides how wet
a planet has to get before the sea arrives: a Short planet needs a far larger share of its air as
liquid than an Unmodded baseline one does. A new world starts with the size you set applied to its starting
air, liquid included, so a custom world that ships liquid can start with a sea at one size and
without one at another. No world the game ships starts with any liquid.

At Short size one base is a real share of the planet. Mars's coldest nights sit only about 1.5 K above
the point where carbon dioxide freezes out, and pulling a tenth of its air into tanks crosses it: the
carbon dioxide then snows out every night and returns every morning until you put some back.

## Console

F3 opens the console.

| Command | Does |
| --- | --- |
| `terraform` | Whether the mod is live and why not if it is not, the self-test result, planet pressure and temperature, how many kelvin the mod is adding, gases per outdoor cell, and what the clouds and the ice caps are holding |
| `terraform size <share> confirm` | Rescales the planet you are playing to that share of the size the world ships at, which changes how long terraforming takes and nothing else. Prints the size, cells, moles and pressure before and after. Host only. `terraform size <share>` alone explains and asks |
| `terraform set` | Lists the settings the world you are playing keeps for itself, with the value in force and, where it differs, what a new world would start with. Host only |
| `terraform set <key> <value>` | Changes one of them for this world only, and records it beside the save. Uses the same key names as the config. A change that deletes something for good asks first and needs `confirm` on the end: setting or lowering the pressure ceiling (the prompt names the planet's pressure now and how much air the first tick would delete), lowering the added heat limit, and making added heat fade sooner. Host only. `terraform set <key>` alone reports the one in force |
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

Each world also keeps a small `terraforming-reloaded.xml` beside its save, holding the settings that
belong to that world rather than to your config. The game ignores it, so it is harmless to leave. You
can delete it by hand; the world then starts again from your config, with its pressure ceiling off.

## Multiplayer

**Everyone who joins needs the mod, on the same version as the host.** The planet's air
lives in the host's game, and the mod sends it to every player's game every few seconds.

- **A player without the mod is turned away when they join**, unless both games also run another
  mod that syncs through StationeersLaunchPad. If they do get in, the game still plays correctly for
  them: breathing, damage, machines and the planet itself are all decided by the host. But their game
  never learns what the host has done to the planet. Inside your base, beside machines that vent
  outside, and wherever they stand still, they see the real air. Walking across open ground, their
  helmet, tablet and outdoor sensors show the world as it shipped, and so do the temperature, the
  sea and the sound of the wind. On a planet you have changed a lot, the helmet reading jumps every
  time they start or stop walking.
- **A player on a different version of the mod cannot join**: StationeersLaunchPad turns the
  connection away. A dedicated server updates only when its admin updates it, so update the server
  and the players together.
- **A mod switched on in StationeersLaunchPad loads the next time the game starts.** If a player's
  readings jump like this with the mod switched on, have them restart the game.
- **The host's settings decide the planet.** Planet size, the pressure ceiling, storms and every other
  setting a world keeps are read from the host's world file and config, and so is how often the planet
  is sent. `terraform size`, `set` and `reset` work only on the host; on a joining player's game,
  `terraform` shows the planet the host sent.
- **A joining player gets the host's planet as they join**, before their first look outside, and it is
  refreshed every few seconds after that. Their sky follows it, and their outdoor temperature is worked
  out with the host's greenhouse strength, air density strength and airless reflectivity, so it reads
  the same as the host's.
- **A host without the mod** turns away a player who has it, unless both games also run another mod
  that syncs through StationeersLaunchPad. Then the game runs as shipped, and the player with the mod
  sees exactly that.

[docs/MULTIPLAYER.md](docs/MULTIPLAYER.md) lists everything a joining player's game works out for
itself.

## Config

Every setting shows in the StationeersLaunchPad config editor with a label and a range, and is
also in `BepInEx/config/xceled.stationeers.terraformingreloaded.cfg`.

**The config sets what a new world starts with.** Every setting that affects a world is then kept
by that world, in its own file, and changing the config does not reach a world you have already
made. To change the world you are playing, use `terraform set` at the console. The exceptions are
`Enabled`, which needs a restart, and the multiplayer and diagnostics settings, which apply at once.

**[docs/SETTINGS.md](docs/SETTINGS.md) is the full list**, generated from the source, with every
key, its label, its description and its default.

The few worth knowing before you start:

- **`PlanetSize`** decides how long terraforming takes and nothing else. See Pace above.
- **`MaxPressureKPa`** is **destructive**. 0 means no ceiling, which is the default. Set it, and air
  above it is deleted for good at the planet's hottest hour, and the loss is saved.
  `terraform set MaxPressureKPa <kPa> confirm` changes the one you are playing. A world made before
  this version loads with no ceiling whatever the config says, deliberately, so that tuning one save
  can never eat another. Every other setting a world did not record is taken from the config.
- **`GhgResponseScale`** and **`DensityResponseScale`** tune the temperature response on worlds that
  ship without their own curves. To reshape it rather than scale it, see [CURVES.md](CURVES.md),
  which also ships in the mod's folder.
- **The `Trace gases` section** is experimental and off by default. It switches trace gas gathering on
  and decides how many times faster the air beside your base draws a gas the planet holds only a trace
  of, and how little counts as a trace.
- **The `Storms` section** decides when a world stops getting its own storms: once its air is mostly
  gone, or once that air is mild. Both are on by default and neither does anything to a world you
  have not changed. Every number either rule uses is a setting, so nothing is hidden: the share of
  the starting air below which storms stop, and the coldest and hottest the air may get across a day,
  the pressure it must stay between, and the most toxic gas allowed. The mild rule is judged at the
  point in the orbit the world is at, so a world near the edge of those bounds is calm in a good
  season and stormy in a bad one. Solar storms follow the opposite rule and have their own setting.

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
code outside the game and fails the build if the planet methods can no longer be patched), stages
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
| `docs/` | Design notes: goals, how the game works underneath, defects found, architecture, verification, balance, temperature design, roadmap |
| `tools/Balance` | The simulator: cheapest habitable air per world, whether it can be reached step by step, what each phase of getting there costs, hours per base size |
| `tools/ci` | Checks that need no game; GitHub runs them on every push |

## License

MIT.
