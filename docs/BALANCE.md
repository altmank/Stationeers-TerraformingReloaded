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
3. `tools/Balance/solve.py` finds the cheapest air that meets each habitability level by constrained
   optimisation (SLSQP, many starts, every answer re-checked by the model's own `report()`).
4. `tools/Balance/path.py` walks from the world's starting air to that recipe a step at a time under
   the game's phase change and fire rules, and reports what went in, what came out, and the air after
   every change.
5. `tools/Balance/cost.py` prices that route. This is the part that decides the hours.
6. `tools/Balance/removal.py` is the standalone version of the dilution sum, and a second opinion on
   it: on Venus it reads 11 hours where `cost.py` reads 12, on Vulcan 11 against 13.
7. `tools/Balance/balance.py` is the report. `tools/Balance/compare.py` is the judge behind
   `run.ps1 -Model`: simulator against the game.

## What limits each phase

Adding a mole and removing a mole are different jobs, on different machines, with different limits.
Pricing both off one number was the mistake this model exists to correct: it made Venus, whose route
is 85 % removal, cost whatever an ice rocket happened to mine.

### Adding gas: the limit is ice arriving

There is no oxygen tap. Every mole you put outdoors was mined as ice somewhere and melted, so the
honest unit is **moles of ice mined**, not moles delivered.

A rocket mines space ice, and the item it brings back holds the deposit's own mixture, 24 moles of it
(**CODE** `MineableDeposit.SpawnIce`; the compositions are in the game's `rocketlocations.xml`). You
take the whole item, so a mole of what you want costs a little more than a mole of ice. The richest
shipped deposit for oxygen, nitrogen, volatiles or helium gives 22 of its 24 moles as that gas.

**Carbon dioxide is the interesting one.** Air you can breathe is mostly oxygen and nitrogen, and both
of those shed heat, so a planet you have made breathable is a cold planet unless you put something
back that traps heat. Carbon dioxide is what you use, and a breathable Mars ends up holding more of it
than oxygen: about 75 moles per outdoor cell against 57. There are two ways to get it.

- **Burn volatiles.** The game's chemistry (**CODE** `Combustion.ResultMethaneOxygen`) is 2 volatiles
  plus 1 oxygen giving 6 carbon dioxide and 3 pollutant. Three moles in, nine out. A mole of carbon
  dioxide costs a third of a mole of volatiles and a sixth of a mole of oxygen.
- **Mine it.** Carbon dioxide ice deposits do exist; the richest shipped one is 14 of its 24 moles.

Burning wins, because of that mole multiplier: 99 moles of ice per cell on Mars against 176. Every
world below is cheaper burnt than mined, by a quarter on Mars and by nearly half on Mimas and Europa.
The price of burning is the pollutant. It is toxic, so it has to be caught in the pipes before it
reaches the air and then stored, and the burn makes half a mole of it for every mole of carbon
dioxide: 32 moles per cell on Mars, 178 on Mimas. It also makes far more than any recipe asks for, so
the pollutant a recipe wants costs no ice.

### Removing gas: the limit is dilution, and production has nothing to do with it

A base cannot reach into the planet and pick the acid out. It draws outdoor air in, keeps what it does
not want and puts the rest back, so **every mole drawn carries only the unwanted gas's current share**:

    dn/dt = -intake x n/N

A gas that is most of the air comes out nearly mole for mole. A trace gas is nearly free the first
time and costs as much again for every halving after that. Taking Venus's acid from 90 moles per cell
down to 1 needs ln(90) = 4.5 planet-volumes of air through the filters.

Intake is the game's own rule (**CODE** `ActiveVent.PumpGasToPipe`): each tick a vent moves the moles
that `pressurePerTick` kPa of the outdoor cell's 8000 L holds at the outdoor temperature, or what the
cell has if that is less. Colder, denser air therefore comes in faster, and thin air is limited by the
cell rather than by the vent. `pressurePerTick` is 10 kPa for the active vent and 40 for the large
powered vent. Gas production appears nowhere in any of it.

One stream of air can carry filters for several gases at once, so removals that run at the same time
cost what the deepest of them costs, not the sum. Removals separated in the route are charged
separately, because one had finished before the other began.

Removal stops at **half a mole per outdoor cell**. That is the game's own line: the fire rule stops
seeing a fuel below it, and at a habitable temperature half a mole per cell is under 0.2 kPa, which is
under every threshold in the habitability test. Dilution never reaches zero, so without a floor the
sum would not end.

### Somewhere to put it: a build cost, not a rate

What comes out has to go somewhere, and there is no bulk sink. Gas released above the 1,000 m space
line is given back to the planet, not destroyed (**CODE** `Atmosphere.LerpToGlobalAtmosphere`). Three
candidates for the limit, and only one of them is real:

- **Filters do not limit it.** A filter wears by ticks in use, not by moles (**CODE** `GasFilter`
  counts `_usedTicks`), and a Filtration unit pulls 1000 kPa of its input pipe per tick, which outruns
  three large powered vents. You need about one Filtration unit per three vents and that is that.
- **Traders take a little.** The game ships buy orders for exactly this: a "World Atmosphere" block in
  `tradeables.xml` where the gas trader buys carbon dioxide on Venus, volatiles and hydrogen on
  Vulcan, oxygen on Europa. A unit is 100 moles, an order is 50 to 200 units, and a large trader
  doubles it (**CODE** `TraderDataInstance.ApplyBulkMultiplier`). Bought out on every visit that is
  about 18,000 moles an hour on Venus and 36,000 on Vulcan. Against Venus's 44 million moles of carbon
  dioxide that is 2,400 hours, sixty times the whole project, so on Venus it is pocket money. On
  Vulcan it is not: the volatiles order would clear Vulcan's 6.8 million moles in about 190 hours
  against a 222 hour project, and pay you for them. Nothing buys hydrochloric acid.
- **Storage is the real cost**, and it is paid in steel rather than in hours. A big tank is 50,000 L
  and holds about 1.2 million moles of gas at the pressure a pipe bursts at (**CODE**
  `Chemistry.Limits.MAXPressureGasPipe`, 60,795 kPa), or more as liquid where the game gives the gas a
  molar volume: 1.25 million moles of carbon dioxide, 1.8 million of acid. Venus at Standard size is
  about 48 big tanks, Europa 67.

### Staged, or side by side

Rockets and crushers make gas; vents and filters take it away. Neither queues behind the other, so the
two phases can run at once unless the game forbids it. Two things do:

- **A gas that goes in and comes out again.** Vulcan needs about 18 carbon dioxide and 8 pollutant per
  cell as a temporary blanket while its fuel comes out, because stripping the fuel drops the
  greenhouse and nights would fall below the point where carbon dioxide freezes. Europa's oxygen has
  to go into tanks before volatiles can warm the planet, and come back afterwards. That is one job
  after another by definition.
- **The fire rule.** A sparked outdoor cell burns any fuel beside any oxidiser, at any temperature,
  and sparks its neighbours (**CODE** `Atmosphere.TryCombust`, GAME-MODEL.md). Vulcan's fuel has to be
  gone before its oxygen arrives.

Where neither applies the model takes the longer of the two phases. Where either applies it adds them.
On the shipped worlds Venus runs its two phases together; Vulcan, Mimas and Europa do not; the Moon
and Mars have nothing to remove at all.

## Machine rates (**MEASURED** prefab values in **CODE** formulas)

| Thing | Rate |
| --- | --- |
| Active vent | `pressurePerTick` 10 kPa: 10 x 8000 / (R T) per tick, about 247,000 mol/h at 280 K. 100 W |
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
| Rocket ice mining | 10-13 items per 8.4-9.2 s cycle at the richness floor, 24 mol an item, x1.2 with the ice head: about 137,000 mol/h while mining. Each item holds the deposit's own mixture. Deposits deplete; new sites are unlimited | CODE |
| Ice deposits | Every gas has a site. Richest shipped: 22 of 24 moles for oxygen, nitrogen, volatiles and helium; 20 of 24 for pollutant; 14 of 24 for carbon dioxide | MEASURED, `rocketlocations.xml` |
| Ground ices | Oxite 22.5 O2 + 2.5 N2; volatiles 20 CH4 + 2 H2; nitrice 22.5 N2 + 2.5 N2O; water ice 20 water + 5 N2; pure ices 50 mol; stack 100 | MEASURED |
| Hand mining | 5-9 items per minable at normal yield, veins finite. 150 minables an hour | yield CODE, rate ASSUMED |
| Rocket gas collector | 19,500-186,000 mol/h by site; **never depletes** | CODE |
| Gas trader sells | 72,000-129,000 mol a visit, but only 30,000-44,000 of that is oxygen, nitrogen, carbon dioxide or volatiles; the rest is ozone, silanol, hydrazine and steam. Trader type is random per slot | CODE |
| Gas trader buys | Venus carbon dioxide, Vulcan volatiles and hydrogen, Europa oxygen, Mars carbon dioxide. 100 mol a unit, 50-200 units an order, doubled by a large trader | CODE |
| Burning volatiles | 2 CH4 + 1 O2 -> 6 CO2 + 3 pollutant: three moles in, nine out. Rocket exhaust is the same chemistry | CODE |
| Composter | 100 mol (50 CH4 + 50 N2) per item per 60 s: 6,000 mol/h each, renewable. Feed rate unknown | MEASURED gas, CODE cycle |
| CO2 -> O2 in place | The Carbon Sequester, one for one (it builds a real outdoor cell, so the planet is debited and credited). Plants only where they breathe a real cell: an outdoor plant on open ground breathes the read-only copy and changes nothing (INTERACTIONS.md) | CODE |
| Small | Solid fuel 25 mol (20 pollutant), coal 13, biomass 12, uranium ore 35 pollutant, other ores 2-4; deep miner about 1,000 mol/h; electrolysis mole-neutral; no world places geysers | MEASURED |

## What a base runs (**ASSUMED**, in `cost.BASES`)

Two capacities, because the two jobs use different machines. Vents are inward vents; each needs a
partner pushing the filtered air back out, so the structure count is double.

| Tier | Ice mined | Vents | Made of |
| --- | --- | --- | --- |
| Small | 13,000 mol/h | 4 active | One player hand-mining ice half the time |
| Medium | 68,000 mol/h | 8 active | One ice rocket mining half the time |
| Mega | 328,000 mol/h | 20 large powered | Four ice rockets mining 60 % of the time |

Gas traders are not counted on either side. Their useful stock is about 37,000 moles a visit, under a
sixth of what four rockets deliver in the two hours between visits, and whether a gas trader turns up
at all is a random pick per contact slot. Composters and in-place carbon dioxide conversion are left
out as well. All three make these a **lower bound** on what a clever base can do.

## Cost to reach air you can breathe without a suit

No suit needed = ppO2 at least 16 kPa, toxins under 0.5 kPa, 273-323 K at the coldest and hottest the
planet gets over every sun angle and both ends of its orbit, 20-304 kPa, and every gas in the air
stable against freezing and rain (TEMPERATURE.md). Moles per outdoor cell. "In" and "out" count a
temporary gas on both sides, which is why they do not net out.

| World | Start, coldest / hottest | In | Out | Ice mined | Pollutant to store | How |
| --- | --- | --- | --- | --- | --- | --- |
| Lunar | 195 / 305 K, vacuum | 121 | 0 | 96 | 29 | +57 O2, +62 CO2, +2 pollutant |
| Mars2 | 221 / 288 K, 2 kPa | 125 | 0 | 99 | 32 | +57 O2, +67 CO2, +1 pollutant |
| Venus | 737 K, 239 kPa | 48 | 266 | 53 | 0 | -177 CO2, -89 HCl, +48 O2. **Checked live: the game reads 322.2 K on this air** |
| Vulcan2 | 400 / 1,725 K | 272 | 70 | 274 | 4 | All its fuel has to go before any oxygen arrives. Stripping the fuel drops the greenhouse and, with the swing still full, takes nights under CO2's 220 K; about 18 CO2 and 8 pollutant go in temporarily to hold nights up, then +193 O2 and +46 N2 kill the swing and the helpers come out. **Checked live on this air, rounded to whole moles: 272.9 to 293.4 K through a day, within 0.2 K of the model** |
| MimasHerschel | 22 / 132 K, vacuum | 599 | 181 | 393 | 178 | No gas both warms and stays a gas at Mimas's 77 K mean. 20 helium to soften the swing, the planet held about 25 K warm by vented heat, then about 104 volatiles (a gas above 84 K) lift it past 220 K, CO2 goes in, helpers out, oxygen last |
| Europa3 | 124 / 134 K, 44 kPa O2 | 710 | 420 | 244 | 142 | CO2 freezes below 220 K and only volatiles warm at 124 K, but Europa's air is oxygen. The 340 oxygen goes into tanks, +80 volatiles reach 224 K, +288 CO2, volatiles out, oxygen back. That oxygen costs no mining: it is the planet's own, banked and returned |

**Fire decides the hard routes.** A sparked outdoor cell burns any fuel beside any oxidiser, at any
temperature, and sparks its neighbours (GAME-MODEL.md). `solve.py` allows no fuel in habitable air and
`path.py` never puts fuel and an oxidiser outdoors together (`SAFE = True`); `SAFE = False` prices
the risk, and on Europa it saves 660 moles per cell.

## Which constraint binds

At Standard size and the mega base. "Vents" is the inward vent count below which removal becomes the
longer job. It is the health warning on every "addition" in this table: that verdict is bought with
vents, and vents are cheap next to rockets.

| World | Hours | Adding | Removing | Binds | Vents | Air through the filters | Big tanks |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Venus | 40 | 40 | 12 | addition, side by side | 6 | 146 M mol to take out 66 M: x2.2 | 48 |
| Lunar | 73 | 73 | 0 | addition, nothing to remove | - | - | 0 |
| Mars2 | 75 | 75 | 0 | addition, nothing to remove | - | - | 0 |
| Europa3 | 220 | 186 | 34 | addition, then removal | 4 | 665 M mol to take out 105 M: x6.3 | 67 |
| Vulcan2 | 222 | 209 | 13 | addition, then removal | 1 | 170 M mol to take out 17 M: x9.9 | 14 |
| MimasHerschel | 360 | 300 | 60 | addition, then removal | 4 | 998 M mol to take out 45 M: x22.0 | 33 |

**Every world is addition-bound**, at every tier, and that is the headline. Venus looked like the
removal project and in shape it is: 85 % of its route is removal. But taking 266 moles per cell out of
dense, hot air is 12 hours of vent time, while the 48 moles of oxygen it needs back is 40 hours of
mining. What makes Venus a removal world is not the clock. It is the 48 big tanks.

The old model read Venus at 208 hours. It got there by dividing every mole moved, in or out, by a gas
production rate, so 266 moles per cell of removal were priced as though a rocket had to mine them.

## Presets

Hours are exactly proportional to planet size: the ice bill and the air the filters have to pass both
scale with the number of outdoor cells, and nothing else in the model does.

| Planet size | Share | Mega base, Mars | One-rocket base |
| --- | --- | --- | --- |
| Short | 0.01 | about 15 h | about 72 h |
| **Standard** (default) | 0.05 | about 75 h | about 361 h |
| Long | 0.25 | about 376 h | about 1,804 h |
| Unmodded baseline | 1 | about 1,503 h | about 7,215 h |

Implemented as a postfix on `GlobalGasMix.Create`: volume and quantities scale together, so air per
cell is identical. **Verified live**: at 0.05 the tank starts at exactly 2,279,749.963 mol.
The clouds and ice caps keep the game's fixed volumes, so on a small planet they fill sooner.

**The Mimas heat kick**, sized: the game banks (T_gas - T_planet) x heat capacity for everything
vented; the mod fades it (half-life 60 min) and caps it at 50 K. Holding +28 K on a Standard-size
Mimas with 10 mol per cell of helium takes gas vented about 130 K warmer than the planet at 378,000
mol/h of gas reaching the air: ordinary room-temperature gas is 200 K warmer than Mimas. A mega base
on Mimas runs a little above that, about 450,000 mol/h once the burn has multiplied its ice, so the
figure is on the safe side. It gets harder as the air thickens
(+8 K at 280 mol per cell needs gas 1,000 K warmer), so the volatiles have to go in early.
`ExternalHeatHalfLifeMinutes` is the knob: at 240 min every figure drops four-fold. **Checked live**, with
the test driver holding the heat: at +25 K nights read 86.5 K and the volatiles stayed a gas. Not shown:
a real base reaching +25 K by venting.

Why Mars needs carbon dioxide as well as oxygen: nights start 50 K too cold, oxygen and nitrogen both
cool the planet a little in the game's index, and the potent warmers (pollutant, volatiles) are toxic.

## What the model leaves open

docs/ASSUMPTIONS.md is the register. For pacing the ones that matter are S9 (removal is dilution), S10
(what a base runs, now two numbers rather than one), S11 (the planner's costs are upper bounds), S12
(storing what is removed), S13 (the ice a mole of gas costs), S14 (how removals share one stream) and
S15 (nothing prices the burners themselves).
