# Balance: how long terraforming takes

Goal: a mega base built to process gas needs about 100 hours (the original mod's stated aim), and
players change pace with one setting. This is where the planet-size presets come from.

## Method

1. `tools/LiveCheck/run.ps1 -Dump tools/Balance/gamedata.json` has the **unmodded game** write out
   what it computes: every per-gas greenhouse curve and every world's base, solar, GHG and density
   tables as evaluated by Unity; starting air and volume; per-gas specific heat; and serialized
   prefab values that exist nowhere in code or XML (gas per ice, `pressurePerTick`, `UsedPower`,
   tank volumes, 765 prefabs). `gamedata.json` is gitignored: it is game data. Regenerate it.
2. `tools/Balance/planet.py` interpolates those tables and adds them up as the game does, with the
   mod's rule for worlds lacking curves and the game's freeze and boil rules. **Held to the live game**
   within 0.2 K on six worlds by `run.ps1 -Model` (TEMPERATURE.md).
3. `tools/Balance/balance.py` searches the cheapest gas mix meeting each habitability level (the
   game's own thresholds, GAME-MODEL.md) and converts moles to hours for three base tiers.
4. `tools/Balance/solve.py` finds the cheapest mix by constrained optimisation (SLSQP, many starts, every
   answer re-checked by the model's own `report()`). It replaced a coarse grid that missed mixes needing
   a precise dose; it found Mars at 125 and the Moon at 121 mol per cell where the grid said 145 and 152.
5. `tools/Balance/path.py` checks that a recipe can be reached step by step (TEMPERATURE.md).
6. `tools/Balance/removal.py` prices removal as dilution through vents.
7. `tools/Balance/compare.py` is the judge behind `run.ps1 -Model`: simulator against the game.

Work is counted as moles moved: added plus removed.

## Machine rates (**MEASURED** prefab values in **CODE** formulas)

| Thing | Rate |
| --- | --- |
| Active vent, outward | `pressurePerTick` 10 kPa: 10 x 8000 / (R T) per tick, about 247,000 mol/h at 280 K. 100 W |
| Powered vent / large | 20 and 40 kPa per tick (large about 990,000 mol/h). 250 / 500 W. Multi-grid depth 4 / 6 |
| Passive vent | No power; re-splits by volume each tick, about 99 % of a small network per tick |
| Inward, active vent on Mars | Limited by cell refill: 5-9 mol per tick, 36,000-66,000 mol/h |
| Filtration / Industrial Filtration | `pressurePerTick` 1000 / 200; industrial draws 4 kW extra |
| Ice crusher | 1 ice per 100 ms while warm enough; `pressurePerTick` 2000 |
| Volume pump / turbo | Max setting 10 / 100 L; 200 / 600 W |
| Big tank | 50,000 L; pipes burst at 60,795 kPa |

**Venting is never the bottleneck.** Sources are:

| Source | Rate | Basis |
| --- | --- | --- |
| Ices | Oxite 22.5 O2 + 2.5 N2; volatiles 20 CH4 + 2 H2; nitrice 22.5 N2 + 2.5 N2O; water ice 20 water + 5 N2; pure ices 50 mol; stack 100 | MEASURED |
| Hand mining | 5-9 items per minable at normal yield, veins finite. 150 minables an hour | yield CODE, rate ASSUMED |
| Rocket ice miner | 10-13 items per 8.4-9.2 s cycle at the richness floor, 24 mol per space-ice item, x1.2 with the ice head: about 137,000 mol/h while mining. Deposits deplete; new sites are unlimited | CODE |
| Rocket gas collector | 19,500-186,000 mol/h by site; **never depletes** | CODE |
| Large gas trader bought out | 72,000-129,000 mol per visit, O2 at $0.05 per mol; trader type is random per slot | CODE |
| Composter | 100 mol (50 CH4 + 50 N2) per item per 60 s: 6,000 mol/h each, renewable. Feed rate unknown | MEASURED gas, CODE cycle |
| Burning volatiles | 2 CH4 + 1 O2 -> 6 CO2 + 3 pollutant: triples moles, makes the warming gas Mars needs, but 1 pollutant per 2 CO2 must be scrubbed (toxic above 1 kPa). Rocket exhaust is the same chemistry | CODE |
| CO2 -> O2 in place | The Carbon Sequester, one for one (it builds a real outdoor cell, so the planet is debited and credited). Plants only where they breathe a real cell: an outdoor plant on open ground breathes the read-only copy and changes nothing (INTERACTIONS.md). Mars already holds 8.66 CO2 per cell | CODE |
| Small | Solid fuel 25 mol (20 pollutant), coal 13, biomass 12, uranium ore 35 pollutant, other ores 2-4; deep miner about 1,000 mol/h; electrolysis mole-neutral; no world places geysers | MEASURED |

## Base tiers (**ASSUMED**, in `balance.py`)

| Tier | Sustained | Made of |
| --- | --- | --- |
| Small | 13,000 mol/h | One player hand-mining ice half the time |
| Medium | 93,000 mol/h | One ice rocket at 50 % duty, a trader buy-out every four hours |
| Mega | 378,000 mol/h | Four ice rockets at 60 % duty, a trader buy-out every two hours |

These omit composters and in-place CO2 to O2 conversion, so they are a **lower bound** on what a
clever base can do. Combustion is priced further down, in ices, and agrees with them. The two values
that would tighten them most: how fast a farm can feed composters, and quarry throughput on ice veins.

## Cost to reach air you can breathe without a suit

No suit needed = ppO2 at least 16 kPa, toxins under 0.5 kPa, 273-323 K at the coldest and hottest the
planet gets over every sun angle and both ends of its orbit, 20-304 kPa, and every gas in the air
stable against freezing and rain (TEMPERATURE.md). "End state" is `solve.py`; "path" is `path.py`,
which has to get there step by step under the game's phase change and is an upper bound (greedy
planner, ASSUMPTIONS.md S11). Mol per outdoor cell, added plus removed.

| World | Start, coldest / hottest | End state | Found path | How |
| --- | --- | --- | --- | --- |
| Lunar | 195 / 305 K, vacuum | 121 | 121 | +57 O2, +62 CO2, +2 pollutant |
| Mars2 | 221 / 288 K, 2 kPa | 125 | 125 | +57 O2, +67 CO2, +1 pollutant |
| Venus | 737 K, 239 kPa | 314 | 314 | -177 CO2, -89 HCl, +48 O2. **Checked live: the game reads 322.2 K on this air** |
| Vulcan2 | 400 / 1,725 K | 288 | 340 | All its fuel has to go before any oxygen arrives (fire, below). Stripping the fuel drops the greenhouse and, with the swing still full, takes nights under CO2's 220 K; about 18 CO2 and 8 pollutant go in temporarily to hold nights up, then +193 O2 and +46 N2 kill the swing and the helpers come out. **Checked live on this air, rounded to whole moles: 272.9 to 293.4 K through a day, within 0.2 K of the model** (the rounding costs the 0.3 K it sits under 273.15 at night) |
| MimasHerschel | 22 / 132 K, vacuum | 418 | 780 + heat | No gas both warms and stays a gas at Mimas's 77 K mean. 20 helium to soften the swing, the planet held about 25 K warm by vented heat, then about 100 volatiles (gas above 84 K) lift it past 220 K, CO2 goes in, helpers out, oxygen last |
| Europa3 | 124 / 134 K, 44 kPa O2 | 290 | 1,130 (470 with fire risk) | CO2 freezes below 220 K and only volatiles warm at 124 K, but Europa's air is oxygen. Safe route: take the 340 O2 out, +80 volatiles to reach 224 K, +288 CO2, volatiles out, oxygen back. Leaving the oxygen in saves 660 mol per cell and burns at the first spark |

**Fire decides the hard routes.** A sparked outdoor cell burns any fuel beside any oxidiser, at any
temperature, and sparks its neighbours (GAME-MODEL.md). `solve.py` allows no fuel in habitable air and
`path.py` never puts fuel and an oxidiser outdoors together (`SAFE = True`); `SAFE = False` prices
the risk.

Totals at shipped size are these times 5,000,000 cells; Mars is 625 M mol, about 1,650 h for the mega
tier. Order of difficulty: Moon and Mars, then Venus and Vulcan, then Mimas, then Europa.

**Removal is dilution, and it turns out not to be the bottleneck** (`tools/Balance/removal.py`). A base
draws outdoor air in, filters out what it does not want and returns the rest, so each mole drawn
carries only the unwanted gas's current share (dn/dt = -intake x n/N). Intake per vent is the game's
rule (`ActiveVent.PumpGasToPipe`): per tick, the moles that `pressurePerTick` kPa of an 8000 L cell
holds at the outdoor temperature. At Standard size:

| World | Removes, mol per cell | Outdoor air through the filters | 20 active vents | 20 large powered vents |
| --- | --- | --- | --- | --- |
| Venus | CO2 177, HCl 89 | 100 M mol to take out 66 M: x1.5 | 34 h | 11 h |
| Vulcan2 | volatiles 27, pollutant 14, hydrogen 3 | 45 M mol to take out 11 M: x4.2 | 62 h | 33 h |

Venus's penalty is small because what it has to lose is 93 % of its air, and its dense air comes in
fast. So vents are not what limits a removal world, and neither are the filters (a filter wears by
hours in use, not by moles, and a Filtration unit outruns the vents; ASSUMPTIONS.md S12). What
limits it is somewhere to put 66 million moles. A big tank holds about
1.2 M mol of gas at the pipe limit, or as liquid 1.25 M mol of CO2 or 1.8 M mol of acid, so Venus at Standard size is about 47
big tanks and at Short about 10. There is no dumping it: gas released above the 1,000 m space line
goes back to the planet. That tank farm is the real price of a removal world. ASSUMPTIONS.md S9, S12.
Letting a gas rain or freeze out instead is free, and `path.py` uses it when the planet offers it.

**The Mimas heat kick**, sized: the game banks (T_gas - T_planet) x heat capacity for everything
vented; the mod fades it (half-life 60 min) and caps it at 50 K. Holding +28 K on a Standard-size
Mimas with 10 mol per cell of helium takes gas vented about 130 K warmer than the planet at the mega
tier's 378,000 mol/h: ordinary room-temperature gas is 200 K warmer than Mimas. It gets harder as the
air thickens (+8 K at 280 mol per cell needs gas 1,000 K warmer), so the volatiles have to go in early.
`ExternalHeatHalfLifeMinutes` is the knob: at 240 min every figure drops four-fold. **Checked live**, with
the test driver holding the heat: at +25 K nights read 86.5 K and the volatiles stayed a gas. Not shown:
a real base reaching +25 K by venting.

**In ices, the way a base would really do it** (`balance.py`, `ice_budget`). There is no CO2 ice to mine:
a base makes CO2 by burning volatiles (**CODE** `Combustion.ResultMethaneOxygen`: 2 volatiles + 1 oxygen
-> 6 CO2 + 3 pollutant), so a mole of CO2 costs a third of a mole of volatiles and a sixth of oxygen and
leaves half a mole of pollutant to catch before it reaches the air, and store. Additions only, at
Standard size, one ice rocket mining about 5,700 ices an hour while it mines:

| World | Ices per cell (oxite / volatiles / nitrice) | Pollutant to store per cell | Rocket-hours |
| --- | --- | --- | --- |
| Lunar | 4.0 (3.0 / 1.0 / 0) | 31 | 177 |
| Mars2 | 4.2 (3.0 / 1.1 / 0) | 34 | 182 |
| Europa3 | 6.9 (2.1 / 4.8 / 0) | 144 | 305 |
| Vulcan2 | 9.8 (8.6 / 0.1 / 1.1) | 4 | 432 |
| MimasHerschel | 11.2 (5.2 / 6.0 / 0) | 180 | 491 |

Burning is about 20 % cheaper in ices than the plain mol count suggests (Mars 4.2 ices per cell against
5.2), and it agrees with the presets: the mega tier's four rockets at 60 % duty are 2.4 rockets, so
Mars at Standard is 76 hours of mining against the 85 quoted. The price is the pollutant: 34 mol per
cell on Mars is 8.4 M mol at Standard size, seven big tanks.

Why Mars needs CO2 as well as oxygen: nights start 50 K too cold, oxygen and nitrogen slightly cool
the planet in the game's index, and the potent warmers (pollutant, volatiles) are toxic above 1 kPa.

## Presets

Time scales with planet size and nothing else does, so:

| Planet size | Share | Mega base, Mars | One-rocket base |
| --- | --- | --- | --- |
| Short | 0.01 | about 17 h | about 70 h |
| **Standard** (default) | 0.05 | about 85 h | about 340 h |
| Long | 0.25 | about 410 h | about 1,700 h |
| Shipped | 1 | about 1,650 h | about 6,700 h |

Implemented as a postfix on `GlobalGasMix.Create`: volume and quantities scale together, so air per
cell is identical. **Verified live**: at 0.05 the tank starts at exactly 2,279,749.963 mol.
The clouds and ice caps keep the game's fixed volumes, so on a small planet they fill sooner.

## What the model leaves open

docs/ASSUMPTIONS.md is the register. For pacing the ones that matter are S10 (the base tiers are
assumed), S11 (the planner's costs are upper bounds) and S12 (storing what is removed).
