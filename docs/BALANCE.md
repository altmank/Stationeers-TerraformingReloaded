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

What comes out has to go somewhere, and by default there is no bulk sink. Gas released above the
1,000 m space line is given back to the planet, not destroyed (**CODE**, GAME-MODEL.md). Three
candidates for the limit, and only one of them is real, plus a fourth a world can switch on:

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
- **Rockets, if the world deletes gas released in space.** With `SpaceDeletesGas` on (off by default,
  per world), gas a rocket carries up and vents at or above 1,000 m is gone. That makes the tanks
  reusable: fill, launch, vent in space, come back. The limit is then launches, and none of the
  numbers below has been measured. They are **GUESSES**, pending one real launch to measure payload,
  fuel and trip time. Launches are the moles to remove at Standard size (about 250,000 outdoor cells,
  as in the table under *Which constraint binds*) divided by what one trip carries. A Large Liquid
  Capsule Tank is 3,000 L, and liquid carbon dioxide, volatiles and pollutant pack 25 mol a litre,
  hydrogen and acid 35.7 (**CODE** `Chemistry`), so one tank is about 75,000 mol (107,000 of hydrogen
  or acid), 67,500 at a 90 % fill. The basic pressure-fed gas engine gives 40 kN (**WEB**); at a
  thrust to weight of 1.2 and about 1.2 t of rocket and fuel it lifts about 2.6 t of payload on Venus
  (8.87 m/s², **MEASURED**) and about 4.9 t on Vulcan (5.5 m/s²), about one tank and about three. On
  Europa (1.3 m/s²) and Mimas (0.97 m/s²) thrust is not the limit; tank count is. A small rocket is
  one to three large liquid tanks. A stacked rocket is as many tank sections as the rocket takes, about
  ten (tanks per fuselage and the tallest rocket both **GUESSES**):

  | Removal at Standard size | Small rocket (1 to 3 tanks) | Stacked rocket (about 10 sections) | What binds |
  | --- | --- | --- | --- |
  | Vulcan's volatiles (27 per cell, 6.8 M mol) | 50,000 to 200,000 mol a trip: about 34 to 135 launches | about 200,000 mol a trip: about 34 launches | Thrust: the basic engine lifts about three tanks, so stacking adds nothing. The heavy engine (62.6 to 94 kN, Cosmic Curiosities trader, 1,000 credits, **WEB**) lifts about half again: about 300,000 mol, about 23 launches |
  | Venus, stripped (266 per cell, 66 M mol) | about 60,000 mol a trip (one tank, not full: 2.6 t of carbon dioxide is 59,000 mol): about 1,100 launches | the same: about 1,100 launches | Thrust: one tank is all the basic engine lifts. The heavy engine makes it about 90,000 mol, about 730 launches |
  | Mimas's removals (181 per cell, 45 M mol) | 50,000 to 200,000 mol a trip: about 225 to 900 launches | about 675,000 mol a trip: about 67 launches | Tank count: thrust lifts far more than ten sections |
  | Europa's volatiles (80 per cell, 20 M mol) | 50,000 to 200,000 mol a trip: about 100 to 400 launches | about 675,000 mol a trip: about 30 launches | Tank count: thrust lifts far more than ten sections |

  Trip time and fuel per trip are not estimated at all. Heavier worlds cost more fuel per mole lifted,
  which is the rate limit. A gas engine also deletes whatever mixture it is fed once its flame is at or
  above 1,000 m, even with the setting off (**CODE** `RocketEngineBase.Exhaust`), at up to 18 mol a tick
  for the governed engine; whether a parked rocket's flame is above the line is **UNVERIFIED**.
  The presets below are worked out without rockets as a sink.

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

## The planet's air burns: what it does to each route

From 0.13.0, with `PlanetAirBurns` on (off by default), the planet's own air burns when it holds a fuel
beside an oxidiser and would catch fire (docs/PLANET-COMBUSTION.md). This is every shipped world's route with
the planet fire on. `python tools/Balance/fire.py` writes the table; `python tools/Balance/test_fire.py`
recomputes every verdict and fails when one differs from the table below, so a change to the planner,
the cost model or the rule cannot move a verdict without this section moving with it. The chemistry is
the game's (**CODE**: the combustion table, enthalpies, ignition points and smallest amounts), and
`tools/ci/test_model.py` holds it to PLANET-COMBUSTION.md's Appendix C in CI.

**How a route is judged.** `path.py` never puts more than half a mole per cell of a fuel beside more
than half a mole of an oxidiser, so no state the planner writes down is a fire. But removing a gas is
dilution and stops at half a mole per cell (`removal.FLOOR`): the model's timeline takes a removed gas
to zero, the game never does. A route that removes one side and later adds the other therefore holds
that residue beside the other side for a while. Those windows are what the rule can act on, and the
table lists each one with the residue put back in.

- **Lit by heat** needs a fuel above one mole per cell (`GasMixture.IsAutoIgnition`), and is asked at
  the hottest hour of the hottest season plus the world's hottest storm (VulcanSolarStorm +500 K by day,
  Lunar's SolarStorm +50 K, MarsDustStorm +30 K at night; Europa's and Venus's storms only cool).
- **Spark only** is a window no heat can light. It burns while a fire outdoors touches the planet (a
  rocket launch, a vent burning, a leak), at the game's share a tick. How much burns depends on how long
  the spark lasts, so it is priced at its bound: the whole residue, once. It fizzles when the spark ends
  unless its own heat lifts the hottest hour past ignition, which none of these do.
- The heat is booked at the hottest hour half way round the orbit and fades by half every hour. Route
  steps take hours, so it is a transient and is not carried into the next step.
- A product the route still puts in later (carbon dioxide on a cold world) is made for it, and is
  counted as that much less to add. What is left over has to come back out, priced by `removal.py` for
  the reference base at Standard size.

| World | Route | Fuel beside an oxidiser | Ignition crossing | Fire size (per cell) | Heat | Cost change | Verdict |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Mars2 | add CarbonDioxide 67, Oxygen 57, Pollutant 1; remove nothing | never | none | none | none | none | unaffected |
| Lunar | add CarbonDioxide 62, Oxygen 57, Pollutant 2; remove nothing | never | none | none | none | none | unaffected |
| Europa3 | add CarbonDioxide 288, Methane 80, Oxygen 340, Pollutant 2; remove Methane 80, Oxygen 340 | residue Oxygen, steps 2 to 121: Methane 80, Oxygen 0.5 | spark only, fizzles (hottest hour 234 K, storms +0 K, ignition 573 K) | Methane 1.00, Oxygen 0.50 | 286.0 GJ per M cells, +153 K at most, fading | 6.13 mol a cell less to add, +2.5 h removal | slower |
| Europa3 | add CarbonDioxide 288, Methane 80, Oxygen 340, Pollutant 2; remove Methane 80, Oxygen 340 | residue Methane+Oxygen, steps 122 to 122: Methane 0.5, Oxygen 0.5 | spark only, fizzles (hottest hour 307 K, storms +0 K, ignition never (no fuel above 1 mol a cell)) | Methane 0.50, Oxygen 0.25 | 143.0 GJ per M cells, +16 K at most, fading | 6.13 mol a cell less to add, +2.5 h removal | slower |
| Europa3 | add CarbonDioxide 288, Methane 80, Oxygen 340, Pollutant 2; remove Methane 80, Oxygen 340 | residue Methane, steps 123 to 162: Methane 0.5, Oxygen 8.5 | spark only, fizzles (hottest hour 307 K, storms +0 K, ignition never (no fuel above 1 mol a cell)) | Methane 0.50, Oxygen 0.25 | 143.0 GJ per M cells, +15 K at most, fading | 6.13 mol a cell less to add, +2.5 h removal | slower |
| MimasHerschel | add CarbonDioxide 359, Helium 20, Methane 104, Oxygen 114, Pollutant 2; remove Helium 20, Methane 104, Oxygen 57 | residue Oxygen, steps 43 to 205: Methane 80, Oxygen 0.5 | spark only, fizzles (hottest hour 182 K, storms +0 K, ignition 573 K) | Methane 1.00, Oxygen 0.50 | 286.0 GJ per M cells, +125 K at most, fading | 4.50 mol a cell less to add | unaffected |
| Venus | add Oxygen 48; remove CarbonDioxide 177, HydrochloricAcid 89 | never | none | none | none | none | unaffected |
| Vulcan2 | add CarbonDioxide 25, Nitrogen 46, Oxygen 193, Pollutant 8; remove CarbonDioxide 18, Hydrogen 3, Methane 27, Pollutant 22 | model, steps 214 to 333: Methane 0.5, Hydrogen 0.4, Oxygen 4.817 | spark only, fizzles (hottest hour 1184 K, storms +500 K, ignition never (no fuel above 1 mol a cell)) | Hydrogen 0.40, Methane 0.50, Oxygen 0.45 | 265.4 GJ per M cells, +85 K at most, fading | +1.5 h removal | slower |
| Vulcan2 | oxygen dump: 15.0 O2 per cell, then the route from the burnt air | deliberate, at once | lit by heat (hottest hour 1725 K) | all fuel: CarbonDioxide 93.0, Pollutant 55.5, Steam 4.5 | 8640.0 GJ per M cells, +1274 K, fading by half every hour | 232 h against 222 h | slower |

**Mars2, Lunar and Venus: unaffected.** No fuel goes into their air at any point of the route, residues
included. One thing changes in play rather than on the route: a terraformed oxygen world now burns the
methane a fuel-rich rocket leaves in its air the next time a fire outdoors touches it, which only removes
fuel.

**Europa3: slower, by about 2.5 hours on 220.** The route takes the 340 mol per cell of oxygen out to make
room for 80 of methane as a temporary warming gas, then takes the methane out and puts the oxygen back.
Twice the planet holds a pair:

1. 80 methane beside the oxygen's half-mole residue. The hottest hour is 234 K against an ignition point
   of 573 K, and Europa's storms only cool it, so it never lights itself. A spark burns the residue once:
   1 methane and 0.5 oxygen per cell become 3 carbon dioxide and 1.5 pollutant, and the planet warms by
   up to 153 K for an hour or two. Nothing on the route depends on that warmth; what it lets thaw back
   into the air freezes out again as it fades, by the game's own phase change.
2. The methane's half-mole residue beside the returning oxygen: no ignition point at all (no fuel above
   one mole per cell), so spark only, 1.5 carbon dioxide and 0.75 pollutant per cell, up to 16 K.

The carbon dioxide and most of the pollutant are gas the route puts in afterwards anyway, 6.1 mol per
cell less to mine. The last 0.62 mol per cell of pollutant is more than the finished air may hold, and
taking it out is the 2.5 hours. Way round: take the oxygen below half a mole per cell before the
methane goes in, or let the residue burn early, while the route is still taking pollutant out.

**MimasHerschel: unaffected.** The same pattern as Europa: 80 methane beside the oxygen's residue
(hottest hour 182 K, ignition 573 K, no storms). A spark makes 3 carbon dioxide and 1.5 pollutant per
cell and a heat wave of up to 125 K that fizzles; all of it is gas the route adds later, 4.5 mol per cell
less to mine.

**Vulcan2: slower, by about 1.5 hours on 222.** The route takes the methane and hydrogen out before any
oxygen goes in, and the oxygen then goes in beside their residue, 0.5 methane and 0.4 hydrogen per cell.
Vulcan's hottest hour is 1,184 K (1,725 K at the near end of the orbit, 500 K more in a solar storm), but
heat is not the question: with no fuel above one mole per cell this air never lights itself. A spark burns
the residue once, making 1.5 carbon dioxide, 0.75 pollutant and 0.6 steam per cell and up to 85 K of heat
for an hour or two, and that has to come back out. Way round: take the fuels below half a mole per cell
before the oxygen goes in, or put a little oxygen in early and let a launch burn the residue while the
route is still taking pollutant out.

**Vulcan2, the deliberate oxygen dump: slower, by about 10 hours.** The alternative to taking Vulcan's
fuel out by dilution is to burn it in place: 15 mol per cell of oxygen (3.75 million mol on a Standard
planet) is exactly what its 27 methane and 3 hydrogen need. Vulcan always lights itself, so the dump
burns within minutes of arriving and leaves 93 carbon dioxide, 55.5 pollutant and 4.5 steam per cell
where there were 12, 15 and none. The heat is 8.6 MJ per cell, booked as the game books a burnt cell: a
heat wave of about 1,270 K at mid-orbit, which fades by half every hour (under 50 K after about five
hours; PLANET-COMBUSTION.md's 1,280 K for 3 million mol at 1,000 K is the same arithmetic). The route from that air is
232 hours against 222: mining the dumped oxygen adds 2 hours, and taking out 74 carbon dioxide, 54
pollutant and 4 steam per cell, where the dilution route takes out 27 methane, 22 pollutant, 18 carbon
dioxide and 3 hydrogen, adds 8. Not worth it, and while the heat wave lasts the
planet is about 2,400 K, where the game's phase change and storms have never been seen to run.

What this leaves out, and why it does not move a verdict:

- **Sparks are priced at their bound.** A residue burnt only partly makes less product, so the cost
  changes above are the most a spark can cost; a partial burn leaves the rest for the next spark.
- **The heat wave's knock-on.** The model judges a settled planet. A transient of up to 153 K on a cold
  world melts and evaporates what has frozen out and refreezes it as it fades, which the game's own
  phase change does both ways and the route already allows for.
- **Rockets and leaks** are not routes. What a leak does is measured by the leak test (VERIFICATION.md, *The
  leak test*), not modelled here.

## What the model leaves open

docs/ASSUMPTIONS.md is the register. For pacing the ones that matter are S9 (removal is dilution), S10
(what a base runs, now two numbers rather than one), S11 (the planner's costs are upper bounds), S12
(storing what is removed), S13 (the ice a mole of gas costs), S14 (how removals share one stream) and
S15 (nothing prices the burners themselves).
