# Interaction census

Four reviews by reading each found things the one before had missed, because a review is a hunt: it
finds what the reader thought to look for. This replaces the hunt with a list that can be checked.

## How it works

Anything in the game that can reach the planet's gas, heat or temperature, or outdoor air, has to go
through a short, closed set of symbols: the planet class and the tank type, the outdoor-air sampler
and the read-only copy it can return, the calls that build a real outdoor cell, a thing's cached
`WorldAtmosphere`, the global sea, the greenhouse index, the world's atmosphere data, weather
temperature offsets, the two heat counters, the space line. `tools/census/census.py` finds every
method in a decompile of the game that references one of them.

Each site is then read in full and given a verdict with the guard that justifies it, in
`tools/census/ledger.csv`:

| Verdict | Meaning | Sites |
| --- | --- | --- |
| gated | Goes through one of the eight switched calls or builds a real cell first, so the planet is debited or credited | 108 |
| read | Only reads planet state | 90 |
| internal | The planet's own bookkeeping | 41 |
| none | A name collision | 34 |
| display | UI, debug windows, menus, sky | 27 |
| bypass | Changes gas or heat around the switch | 17 |

`census.py <decompile> --check` fails if any site has no verdict, if the ledger names a site the game
no longer has, or if a bypass has neither a fix nor a written acceptance. The first pass is complete:
309 of 309 sites.

Closure: the first pass listed every member that hands a sampled atmosphere on to other code
(wrappers). Those names were added to the symbol list, which brought in 54 more sites and new lines in
42 known ones; their verdicts are being added, and the pass repeats until it finds no new wrapper.
Until then the counts above are the first pass only.

After a game update, run the check: new or moved sites come up as unclassified.

## What it found

**The read-only copy.** `AtmosphericsController.SampleGlobalAtmosphere(grid)` returns the real outdoor
cell at that grid, or, where none exists, one shared read-only copy of the planet's air. Writes to
the copy are ignored; removals from it still return the moles asked for. `DynamicThing` caches the
result as `WorldAtmosphere`, and many devices treat "`WorldAtmosphere` is null" as their only guard,
which the copy passes. So a device that adds to it destroys gas, and one that removes from it gets
gas the planet never gave. Every vent, pipe, tank, canister, furnace and burner builds a real cell
first (`CloneGlobalAtmosphere`), which is why the earlier audits, which followed the eight switched
calls, never saw this.

The rule for the 17 bypasses: fix what can move an amount that matters to a planet (a Standard Mars
holds 2.3 million mol; the smallest base tier moves 13,000 mol an hour) or that ordinary venting goes
through; accept the rest with the size written down.

| Bypass | Decision |
| --- | --- |
| Wall vent to outdoors: averaged a whole room with the bottomless copy every tick, a source for a thin room and a sink for a thick one, and the ordinary way to open a base to outside | **Fixed** (`Guards.WallVentPrefix` builds the missing real cell) |
| Portable tank, generator, scrubber contents vented on destroy or release; portable generator exhaust; portable air conditioner waste | Accepted: one portable's volume, at most about 3,300 mol one-off, or the fuel a player feeds a generator (about 10,000 mol of exhaust a canister). Bulk CO2 making goes through burners and furnaces, which are gated |
| Portable scrubber drawing from neighbouring grids | Accepted: one portable's intake |
| Robotic arm docking vent | Accepted: only with no pipe docked, and then it can only fill or empty the dock's own small volume once |
| Gas mask, filter mask, suit waste and pressure relief, jetpack propellant, animals breathing | Accepted: a breath or a mask volume per tick |
| Powered fridge and refrigerated vending machine shedding heat outdoors | Accepted: heat only, at most 2 kW each, never reaches the planet's heat counter |
| Outdoor plants (through `WorldAtmosphere`, where no cell exists at the plant) | Accepted: moles per hour. It does mean an outdoor farm does not oxygenate the planet; plants indoors or in a tray with its own air are unaffected |

Also recorded, not bypasses: rocket exhaust above the 1,000 m space line is discarded (propellant,
not planet air); `DivideWorldAtmosphere` with no open neighbour resets a cell; geysers, composters and
explosions create matter into real cells (sources, as intended); the game's developer window can
regenerate the planet; outdoor liquid mixing can pull planet liquid through the copy once the planet
holds a sea's worth; several devices (battery, turbine, stirling engine, fire extinguisher) keep a
real outdoor cell alive every tick, which costs the planet nothing but is work per tick.

`tools/census/wrappers_and_constants.csv` lists the wrappers and every absolute constant the readers
met that governs planet-scale behaviour. The ones that mattered are handled in `Planet.cs`
(cloud and ice-cap volumes, melt and freeze rates follow planet size); the global sea's threshold is
evaluated once by the game and cannot be scaled, and is documented for players instead.
