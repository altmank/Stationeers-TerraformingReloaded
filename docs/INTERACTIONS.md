# Interaction census

Four read-throughs, and each found things the one before had missed, because a review is a hunt: it
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
| gated | Goes through one of the eight switched calls or builds a real cell first, so the planet is debited or credited | 109 |
| read | Only reads planet state | 103 |
| none | A name collision, a declaration, dead code | 51 |
| internal | The planet's own bookkeeping | 42 |
| display | UI, debug windows, menus, sky | 37 |
| bypass | Changes gas or heat around the switch | 21 |

`census.py <decompile> --check` fails if any site has no verdict, if the ledger names a site the game
no longer has, or if a bypass has neither a fix nor a written acceptance. **Coverage is 363 of 363.**

Closure: the first pass (309 sites) listed every member that hands a sampled atmosphere on to other
code: the wrappers, such as `BreathingAtmosphere`, `ScannedAtmosphere`, the `Smelt` hand-off and the
world-mixing list. Those names were added to the symbol list, which brought in 54 more sites and new
lines in 42 known ones. That pass found no wrapper that opens further sites, so the list is closed.

Two things went wrong on the way and are now guarded. The first closure pass reported "8 new sites,
none a bypass": a shell had turned every `\b` in the new patterns into a backspace character, so they
matched nothing. It was caught by checking one known case (plant breathing) by hand; `tools/ci/check_repo.py`
now fails on any pattern holding a control character or not matching its sample. And one reader reported a
device moving 330 mol a tick, which was the cap in its formula, not a flow: the device could only fill a
small internal volume once. Every rate that drives a decision below was checked against what bounds it.

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

The rule for the 21 bypasses: fix what can move an amount that matters to a planet (a Standard Mars
holds 2.3 million mol; the smallest base tier moves 13,000 mol an hour) or that ordinary venting goes
through; accept the rest with the size written down.

| Bypass | Decision |
| --- | --- |
| Wall vent to outdoors: averaged a whole room with the bottomless copy every tick, a source for a thin room and a sink for a thick one, and the ordinary way to open a base to outside | **Fixed** (`Guards.WallVentPrefix` builds the missing real cell) |
| Portable tank, generator, scrubber contents vented on destroy or release; portable generator exhaust; portable air conditioner waste | Accepted: one portable's volume, at most about 3,300 mol one-off, or the fuel a player feeds a generator (about 10,000 mol of exhaust a canister). Bulk CO2 making goes through burners and furnaces, which are gated |
| Portable scrubber drawing from neighbouring grids | Accepted: one portable's intake |
| Robotic arm docking vent | Accepted: only with no pipe docked, and then it can only fill or empty the dock's own small volume once |
| Gas mask, filter mask, suit waste and pressure relief, jetpack propellant, animals and unhelmeted humans breathing | Accepted: a breath or a mask volume per tick (a human breathes out at most 0.0024 mol of CO2 a tick) |
| Powered fridge and refrigerated vending machine shedding heat outdoors | Accepted: heat only, at most 2 kW each, never reaches the planet's heat counter |
| Plants on open ground: breathing in, breathing out, and the cooling of the endothermic ones | Accepted: about 0.0012 mol a tick per plant each way. Every tray but the automated one hands the plant its `WorldAtmosphere`, which on open ground is the copy, so an outdoor farm does not change the planet; plants in a room or an automated tray breathe real air |

Also recorded, not bypasses: rocket exhaust above the 1,000 m space line is discarded (propellant,
not planet air); `DivideWorldAtmosphere` with no open neighbour resets a cell; geysers, composters and
explosions create matter into real cells (sources, as intended); the game's developer window can
regenerate the planet; several devices (battery, turbine, stirling engine, fire extinguisher) keep a
real outdoor cell alive every tick, which costs the planet nothing but is work per tick.

**What the mod changes in the exchange itself.** One thing, and only for a trace: the take in
`Atmosphere.LerpToGlobalAtmosphere` is swapped for `TraceGases.TakeForLerp`, which makes the same take
and then, for any gas the planet holds less than `TraceGasLine` of per outdoor cell, draws
`TraceGasGathering - 1` times as much again out of the tank, under the tank lock, before the cell
lerps toward it. The cell keeps its usual share `t` of what it was handed and gives the rest back
through the game's own `GiveToGlobal`, so the site stays `gated` and the total is exact. Every other
take (`CloneGlobalGasMix` building a cell, `AtmosphericEventInstance` refilling one, the self-test's
own round trip) is untouched. The bounds are in ARCHITECTURE.md.

**A hazard.** World mixing does put the read-only copy into a cell's mixing list, and
gas is safe there because both directions test for it and go through the switched calls. Liquid mixing
(`Atmosphere.MixLiquids`) has no such test and is safe only because the copy is built from gas alone, so
it holds no liquid. Anything that ever put the planet's liquid into the copy would turn liquid mixing
into a free source and sink at 40 % a tick. The mod does not touch how the copy is built.

`tools/census/wrappers_and_constants.csv` lists the wrappers and every absolute constant the readers
met that governs planet-scale behaviour. The ones that mattered are handled in `Planet.cs`
(cloud and ice-cap volumes, melt and freeze rates follow planet size); the global sea's threshold is
evaluated once by the game and cannot be scaled, and is documented for players instead.
