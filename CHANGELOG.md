# Changelog

## 0.1.0

First version.

Checked live in a headless Mars world by `tools/LiveCheck`: 100,000 mol of CO2 put outdoors spreads over
about 4,300 outdoor cells and drains into the planet, the total stays level to 0.005 mol and the planet gains
99,998.8 mol (the game drops quantities under its minimum); a save taken mid-spread loads back with the total changed by 0.000 mol. The same injection
without the mod vanishes and the planet never moves.

Checked against the game, headless, by `tools/LiveCheck -Model`: the temperature rule on Venus, Vulcan,
Europa, the Moon and Mimas, within 0.2 K of the simulator.

Not yet checked live: a played session, building over outdoor cells, the sky, multiplayer.

- Switches on the game's dormant planetary atmosphere: outdoor takes and gives now move real gas.
- Fixes found by reading the game's code with the switch on:
  - loading a save no longer drains the planet by one share per saved outdoor cell;
  - a save no longer records outdoor cells one tick stale beside a current planet, which put a tick's worth of gas flow into the file twice (found by the live test, not by reading);
  - building into an occupied outdoor cell no longer hands its gas to both the neighbours and the planet;
  - heat added to the planet fades and is bounded, instead of accumulating forever, and an airless world no longer divides banked heat by the first puff of gas;
  - a NaN or negative mixture can no longer reach the saved planet state;
  - the planet tick holds the same lock as takes and gives;
  - filled clouds no longer overwrite weather that is already scheduled or running.
- Greenhouse and density temperature response for worlds that ship without one, measured from each world's starting air.
- Base temperature for airless worlds that ship without one.
- Sky follows the air, rebuilt once per game tick instead of once per frame.
- Host to client planet sync through StationeersLaunchPad; optional for clients.
- `terraform` console command: status, `reset confirm`, `curves export`, `curves reload`.
- `terraform reset confirm` puts the whole planet back as the world ships, which is the clean way to remove the
  mod. Checked live: after terraform, reset, save and removing the mod, the unmodded game loads a planet within
  1 mol and 0.01 K of stock.
- Planet size setting with presets (Short, Standard, Long, Shipped, Custom). Default Standard, 1/20 of the
  game's planet: about 85 hours for a mega base to reach shirt-sleeve air on Mars. The starting air per
  outdoor cell is identical at every size. Checked live: the planet is exactly the set share of shipped Mars
  and conservation, save and load, and reset all hold at it.
- `tools/Balance`: a pacing model built on curves, world data and prefab values written out by the game
  itself (`tools/LiveCheck/run.ps1 -Dump`).
- Settings carry labels, ranges and restart flags for the StationeersLaunchPad config editor.
- `CURVES.md` ships in the mod folder: how to reshape the temperature response without a restart.
- Every world can be made habitable. Worlds that start hot under greenhouse air (Venus, Vulcan) are read as bare rock plus the warming of their starting air, so removing that air cools them; thick air evens out a world's own day and night swing in proportion, which is what tames Vulcan. One rule for every world, no per-world numbers. Venus reads 322 K in the game on the recipe the model gives, against 737 K as shipped.
- The temperature rule is one patch on the game's temperature formula. `terraform` shows the real temperature and how much the mod is adding. The planet's clouds and ice caps read the planet's air, so gas that froze out melts back when the planet warms.
- Custom worlds: sunlight comes from the world's own orbit, so nothing needs adding to the mod. `WORLDS.md` ships in the mod folder.
- `tools/Balance` now carries the game's freezing and boiling rules (dumped from the game), searches recipes by optimisation, and checks a recipe can be reached step by step (`path.py`): cold worlds freeze carbon dioxide out of the air below 220 K, so Europa and Mimas are warmed in stages. `tools/LiveCheck -Observe` watches any world through a fast day with any air, to hold the model to the game.
- Neither strength setting can strand a world: greenhouse strength cancels out of the warming side of a world that starts hot, and air density strength is how quickly thick air evens out day and night (0 off, more bites sooner), never whether it can.
- `tools/Balance` keeps fuel and oxidisers apart outdoors, after reading that the game burns them at the first spark at any temperature; Europa's safe route takes its oxygen out before volatiles go in.
- GitHub Actions runs the checks that need no game on every push: repository hygiene, version agreement, that the mod and the simulator share the temperature rule's constants, and the rule's invariants on a synthetic fixture.
- Self-test: stands down if the game switches its own planet on, checks the temperature formula still uses the parts the mod adjusts, and on the first planet tick of every world takes one outdoor cell of air from the planet and gives it back, turning the planet off for the session if that does not balance. `terraform` shows the result.
- A custom world whose planet has no usable volume is left as shipped.
