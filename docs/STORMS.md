# Storms respond to terraforming

**Built in 0.10.0.** `src/Patching/Storms.cs` holds both rules, the day forecast and the readout;
`Guards.ScheduleWeatherPrefix` and `Guards.CanScheduleWeatherPrefix` are the two hooks;
`tools/LiveCheck/run.ps1 -Schedule` judges them. Part three, at the end, is what changed between the
specification and the thing, and what was measured.

**Part one is the specification.** Build from it.
**Part two is the research behind it.** Read it before changing a number or a rule, because most of
them are the way they are for a measured reason.

The mod already changes storms on every world by default: `Climate.StormInProportion` shrinks a
storm's temperature offset in proportion to how far the planet has been cooled, and honours the
untouched-world promise by doing exactly nothing at a proportion of 1. The rules below hold to the
same bar, which is why they are on by default rather than opt-in.

---

# Part one: the specification

## Scope

Two independent rules. Either one stops a world scheduling its own storm. They change **whether a
storm is scheduled** and nothing else.

## Settings

A new `Storms` section. `Climate` already carries five settings and nine more would swamp it, so these
group by topic the way `Heat` and `Pace` do. Sort order band 50 to 58, clear of the existing 0 to 40.

House style: a short label carrying the unit in brackets, plus a longer description
(`Plugin.cs:178-191`).

| Section | Key | Label | Description | Default |
| --- | --- | --- | --- | --- |
| Storms | `StormsStopWhenStripped` | Stripping the air stops storms | Once most of a world's starting air is gone, it stops scheduling its own storm. Does not apply to solar storms. | on |
| Storms | `StrippedAtmosphereShare` | Stripped below (% of start) | Share of the air the world started with, below which storms stop. 0 means every last mole. | 5 |
| Storms | `StormsStopWhenAtmosphereIsMild` | Mild air stops storms | A world whose air is temperate, thick and clean stops scheduling its own storm. | on |
| Storms | `MildAtmosphereColdestKelvin` | Coldest air (K) | Coldest the air may get across a day, in kelvin, and still count as mild. | 263.15 |
| Storms | `MildAtmosphereHottestKelvin` | Hottest air (K) | Hottest the air may get across a day, in kelvin, and still count as mild. | 323.15 |
| Storms | `MildAtmosphereMinPressureKpa` | Minimum pressure (kPa) | Least air pressure that counts as mild. | 20 |
| Storms | `MildAtmosphereMaxPressureKpa` | Maximum pressure (kPa) | Most air pressure that counts as mild. | 607.95 |
| Storms | `MildAtmosphereMaxToxinsKpa` | Most toxins (kPa) | Most toxic gas allowed, in kilopascals, measured at the hottest point of the day. | 1.0 |
| Storms | `MildAtmosphereStopsSolarStorms` | Mild air stops solar storms too | Air shields radiation, so a mild world stops solar storms as well. Off by default: on the Moon they give four times normal solar power. | off |

Every threshold is the game's own number, not ours. `Chemistry.Limits.PressureMinimumSafe` is 20 and
`PressureMaximumSafe` is 607.9499816894531 (`Chemistry.cs:15,17`). The toxin list is the game's
(`Atmosphere.cs:812-823`: pollutant, methane, hydrazine, silanol, hydrochloric acid) and 1 kPa is the
threshold the hours table already uses.

Every threshold is a setting. There is no hidden rule.

## Rule one: stripped

The planet's air falls below a share of what the world shipped, and the world stops scheduling its own
storm.

- Measured in **moles per outdoor cell**, counted across tank gas, tank liquid, liquid clouds, ice
  clouds and ice caps. Real outdoor cells are deliberately not counted; see Part two.
- The baseline is `WorldSetting.Current.Data.GlobalAtmosphereData.TotalMolesGasses()`, which is already
  moles per 8000 L cell and already free of planet size. An untouched world sits at exactly 1.000.
- A share of 0 means every last mole, floored at `Chemistry.MINIMUM_QUANTITY_MOLES`, because
  `GlobalGasMix` has no minimum-quantity cleanup and a literal zero test would never fire.
- **Worlds that ship with no air are exempt.** Ten percent of zero is zero, so without this an
  untouched airless world would lose its weather on turn one.
- **Does not apply to solar storms**, the events marked `ActiveInOrbit` in `Data/weather.xml`.
  Removing air cannot stop radiation.
- **A storm already scheduled still arrives.** The game schedules an event 120 to 900 seconds
  before it starts, and the prefix can stop a scheduling but not cancel one already made, so a
  player who finishes stripping can still get one last storm up to fifteen minutes later.
  Accepted rather than cancelled.

## Rule two: mild

At the current season, across a full day, five bounds on three axes all hold.

| Axis | Bound | Measured at |
| --- | --- | --- |
| Temperature | above the floor | coldest point of the day |
| Temperature | below the ceiling | hottest point of the day |
| Pressure | above the minimum | coldest point, where the air is thinnest |
| Pressure | below the maximum | hottest point, where it is thickest |
| Toxins | below the ceiling | hottest point, because heat raises partial pressures |

- **Seasonal, not year round.** Evaluated at the current orbital position. A marginal world is calm in
  a good season and stormy in a bad one. This is what stops the rule assuming every world can be
  finished, which custom worlds in particular cannot guarantee.
- **No latch and no timer.** Day extremes stop it oscillating daily, and a season runs far longer than
  the 3 to 12 day storm cooldown.
- **No oxygen term.** A storm does not care whether anyone can breathe.
- **Applies to solar storms only when `MildAtmosphereStopsSolarStorms` is on**, which it is not by
  default.

### The forecast

Take the current tank. Evaluate planet temperature at 37 sun angles, 0 to 180 in steps of 5, at the
current orbital position. Minimum and maximum are the day's coldest and hottest.

Call the three-argument `GetGlobalGasMixTemperature(data, solarAngle, solarEnergyPercent)`. The
one-argument overload recomputes the angle and the percent from the sun on every call, which is wrong
for a sweep.

**Caching on the air alone is not enough.** Three inputs move the forecast without the air moving:

- **The two heat offsets.** Latent and external are both added unconditionally, and `Guards.Upkeep`
  itself decays external every tick. `MaxExternalOffsetKelvin` defaults to 50, so this term is worth
  up to ±50 K against a 60 K temperature window.
- **The orbital position.** The whole point of the rule being seasonal. A cache keyed on air alone
  would report last season's verdict for ever.
- **A running storm.** Its offset is added whenever one is running, so a forecast taken during a storm
  stays storm-shifted after it ends.

The split is clean, because latent and external are angle-independent constants across the sweep.
Cache the 37-angle sweep on the air; recompute the two heat offsets every tick, which is free since
`Upkeep` already reads both energies and the heat capacity. Key the cache additionally on the orbit
percent within about a percentage point, and on whether weather is running.

**Key the air on what the temperature actually consumes**, the greenhouse index and the gas density,
plus the toxin moles. Not on total moles: swapping Vulcan's 42 mol of methane and pollutant for 42 mol
of nitrogen leaves the total untouched and changes both, which is this design's own argument for the
toxin bound.

The greenhouse index depends on the air and not on the angle, so compute it once per sweep. The
temperature postfix currently recomputes it on every call, which is already flagged as a performance
worry (assumption M9, defect D10).

## The readout

`terraform` gains a `storms:` block. Its job is that a player never has to guess why weather is or is
not happening. **It never reports a bare no.** Every negative names the bound it failed, with the
measured value and the bound beside it.

It reports:

1. **The verdict**, and which rule produced it.
2. **Stripped**: current air as a share of the world's starting air, and the threshold. Or that the
   world ships with no air and the rule does not apply.
3. **Mild**: all five bounds with their measured values, and which one fails.
4. **The season it was evaluated at**, because the mild rule is seasonal and a player who reads "mild"
   today needs to know it can lapse later in the year. This is the line that stops an annual storm
   season being reported as a bug.
5. **Solar storms separately**, since they follow the opposite rule.
6. **Rain held back by a setting.** On a world that ships no weather of its own, when a full cloud is
   refused its rain or snow because `WeatherOnWeatherlessWorlds` is off, say so by name. Otherwise the
   player sees clouds fill and drain with no rain and no explanation.

Shape, not exact text:

    storms: suppressed, the air is mild
      stripped:  100.0% of this world's starting air, stops below 5%
      mild:      yes, at this point in the orbit
                 coldest 268.4 K (floor 263.15)    hottest 291.2 K (ceiling 323.15)
                 pressure 38.1 kPa (20 to 607.95)  toxins 0.02 kPa (ceiling 1.0)
      solar:     not suppressed, the setting is off

and when it is not suppressed:

    storms: scheduled, MarsDustStorm
      stripped:  100.0% of this world's starting air, stops below 5%
      mild:      no, coldest 241.0 K is below the 263.15 floor
      solar:     this world has none

## Never touched

- **Running storms.** Only new scheduling is suppressed. Cutting one short means calling the game's
  stop function mid-storm, which is the defect D6 exists to prevent.
- **Solar dimming.** `GetSolarRatioAt` is left alone. Solar panels, daylight sensors and outdoor cell
  heating all read it.
- **Storm temperature.** `Climate.StormInProportion` keeps owning it, so nothing is shrunk twice.
- **Rain and snow.** They are how the cloud buckets drain.

## Implementation constraints

All from the analysis pass on rule one, with citations in Part two.

- **Hook `CanScheduleWeatherEvent`, not `ScheduleWeatherEvent`.** The game evaluates
  `GetNextWeatherEvent()` as the *argument* to `ScheduleWeatherEvent`, so prefixing that call still
  rolls a random pick off a shared static `Random` about 60 times a second, for ever, on any suppressed
  world. `CanScheduleWeatherEvent` is public static, a pure predicate, and has exactly one caller, so
  returning false there stops the pick being evaluated at all. Costs one more patched method and one
  more `Extra` in `Patcher`.
- **The existing hook does not see ordinary storms.** The planet tick runs on a thread pool worker, the
  scheduler runs on the main thread, and the flag gating the current guard is thread static. Needs a
  new out-of-tick branch in `Guards.ScheduleWeatherPrefix`, with the D6 branch kept first and byte
  identical, including its `weatherEvent == null` guard, which prevents a crash.
- **The new branch must check `Gate.Enabled()`.** The current prefix does not, and is correct only
  because being in-tick implies it. Without the check the rule would act on a client, in a tutorial,
  and after a self-test fault.
- **The prefix runs about 60 times a second while suppression is active**, because the game retries
  scheduling every frame. It must read a cached field, never a sum, and never take a lock on the main
  thread. The cache itself is written under the tank lock already held in `Guards.Upkeep`.
- **Nothing is persisted.** The rule is a pure function of live state and the game already saves its
  own weather state.
- **Forced storms cannot test this.** `ImmediatelyActivateWeatherEvent` bypasses the scheduler, so the
  existing `-Storm` and `-Weather` scenarios do not reach the new branch. Waiting does not work either:
  scheduling needs both a world-start cooldown in days and an event cooldown, and no headless run
  reaches either. The harness needs a `-Schedule` switch that calls the scheduler from the main thread
  and reads back whether an event was set, and an `-Orbit` switch, because moving the season needs
  `OrbitalSimulation.SetAllBodies` and writing `SimulationTimeSeconds` alone does not recompute the
  distances the percent derives from.

Mild rule, from its own analysis pass:

- **It shares `Guards.Upkeep` with the strip rule and adds no new locks.** `Guards.TankLock` and
  `PAS.GlobalInteraction` are the same object, set by reflection in `Patcher`. The forecast touches no
  atmosphere pool, so it does not have the lock-order hazard the strip measure's walk would have had.
  `Climate.BuildLock` can be taken on first use, and cannot invert, because `BuildEntry` takes no lock
  and never touches the tank.
- **Run it last in `Upkeep`**, after `KeepPhaseChangeInProportion` and after the pressure-ceiling
  scale, or it forecasts air the same tick is about to change.
- **Fail open before the first `Upkeep`.** Nothing can schedule that early anyway.
- **Partial pressures at a forecast temperature** are not on `GlobalGasMix`; build them with
  `IdealGas.Pressure(tank.Get(type), new TemperatureKelvin(hot), tank.VolumeForGas())`. Use
  `VolumeForGas()`, not a flat 8,000 L, because the two separate as soon as the tank holds liquid.
  `Get` throws on `Air` and `Fuel`, so skip any type whose `Mole.MatterState` is `None`, which is the
  guard `TerraformCommand` already uses.
- **The toxin list has no data source in the game**; it is hand-written inside
  `Atmosphere.PartialPressureHumanToxins`. Hardcode the five and add a self-test that the property
  still references all five, standing the bound down if a game update changes it. The 1.0 kPa default
  is the game's own `Entity.ToxicPartialPressureForDamage`.
- **Methane is called Volatiles in player-facing text**, which is its own XML name.
- **On a client the verdict would differ from the host's**, because the temperature postfix gates only
  on `Settings.Enabled` while the rule gates on `Gate.Enabled()`. Print the host's or print nothing.

---

# Part two: the research

## What counts as the planet's air

Five stores, and phase change moves gas between them without any of it leaving the planet. Only a
player's vents actually add or remove. All five paths are in `HandleGlobalStateChange` (`PAS:432-497`).

| Store | Gains from | Loses to |
| --- | --- | --- |
| Tank gas | vents giving, tank liquid evaporating, a cloud emptying when full | vents taking, condensing to liquid clouds, freezing to ice clouds |
| Tank liquid | gas condensing, ice caps melting | freezing to ice caps, evaporating back to gas |
| Liquid clouds | tank gas condensing | empties whole into the tank when it fills |
| Ice clouds | tank gas freezing | empties whole into the tank when it fills |
| Ice caps | tank liquid freezing | melts back to tank liquid |

**The rates are wildly asymmetric, and that is the whole problem.** Gas leaving the air passes
`MoleQuantity.MaxValue`, so the whole of a gas goes in one tick. Coming back is rate limited: caps
melt at 1/72,000 of their contents per tick and freeze at 1/24,000, and a cloud only empties when it
fills.

### Why the stores that are not in the air still count

The rule asks whether the player has removed the air from the planet, not whether there is air in the
sky at this moment. Those are different questions, and the second one is a trap.

Measured live on Mars, held just under CO2's freeze threshold (TEMPERATURE.md):

> All 5.5 mol per cell left the air in one tick at dusk, 222.1 K to 217.2 K between two samples,
> pressure 1.38 to 0.10 kPa. It sat in the tank as liquid all night, draining into the ice caps at
> 150 mol per tick. After dawn it came back out of the liquid at exactly 10% a tick and was 5.45 of
> 5.5 back in the air within 60 ticks. The 0.8% per night that reaches the ice caps melts back by day.

So **tank liquid has to count**: all 5.5 mol per cell left the air, so a measure of tank gas alone
reads a share of zero and trips at any threshold, every night, on a world nobody has touched.
Storms would switch off at dusk and on at dawn for ever. Shipped Mars sits 1.49 K above that freeze
line and its own dust storm carries a −20 K day offset, so this is not a contrived mix.

**Ice caps have to count for the same reason on a longer clock.** The Europa run parked 19.9 of 20 mol
per cell in the caps, where a cold world can hold nearly its whole atmosphere for hours of game time
before melting it back.

A sky-only measure is a coherent rule on its own terms, just not this one. It would need its own
threshold and would have to accept nightly flicker on Mars, Europa and anything else cycling a gas
across a threshold.

### Outdoor cells

**Settled: excluded.** Measured live, 2026-09-21.

| Case | Peak share | Steady |
| --- | --- | --- |
| Removal, the direction this rule cares about | 0.0024% to 0.0402% | 0.0007% |
| Injection, the opposite direction | 5.2% | back to 0.08% within 50 ticks |

**The deciding number.** An outdoor cell resting at planet density is exactly one part in
(planet volume / 8,000 L) of the planet: 0.0004% at size 0.05, 0.002% at size 0.01. The error from
leaving cells out can never exceed cells open divided by planet grid cells, and cells being drained
are thinner than the planet, so it is strictly smaller than that. Against a 5% threshold that is four
orders of magnitude of margin. It would take 25,000 simultaneous outdoor cells at size 0.05 for the
omission to be worth even 10%.

**Removal touches about a thousand times fewer cells than injection**, which is why the injection
figure is misleading. Of 100 cells built at once, 99 were gone by the next planet tick with their air
back in the tank, culled for sitting at planet density with no open neighbours (`Atmosphere.IsLive`).
Gas pushed out into the world instantiates cells by spreading; gas taken out of the world cannot
instantiate anything. A real vent keeps one cell, because the structure on its grid is instantiated.

**Cost was never the objection.** A walk summing every cell costs 270 ns per cell: 1.2 ms at 4,289
cells, which is 0.25% of a 500 ms tick. Break-even at 1% of a tick is about 18,500 cells. Iteration
itself is free at 1.8 ns per entry; the whole cost is `GasMixture.GetTotalMoles` summing ~30 mole
quantities per call. The game already walks the same pool five or six times a tick on the same thread.

**Two further reasons found while measuring:**

- `Mode == World` includes sealed base rooms, so a naive sum would count the player's own pressurised
  interior as planet air. Excluding cells avoids needing a `Room == null` filter whose correctness
  nobody has argued.
- A walk from `Guards.Upkeep` would take the atmosphere pool lock while holding the tank lock. Safe
  from the simulation thread, but it establishes a lock order a future main-thread caller under
  `Planet.UnderTankLock` could invert.

## Why moles and not pressure

The two measures differ by exactly the temperature ratio, so a share of shipped pressure is a share of
shipped moles times how far the temperature has moved. What one 10% pressure threshold would actually
mean, per world:

| World | Air left when a 10% pressure rule fires |
| --- | --- |
| Europa | 4.6% |
| Mars | 8.7% |
| Venus | 22.8% |
| Vulcan | 35.6% |

A 7.7x spread set by the world's shipped temperature, not by anything the player did, and it runs
backwards: Venus and Vulcan are the two worlds whose route is stripping, and they are where the
pressure rule switches weather off soonest.

Worse, there is no shipped pressure to compare against. The world file stores moles and volume, no
temperature, so a shipped pressure has to be synthesised from a chosen sun angle. Vulcan's varies
4.31x across a day. On moles the baseline is a literal number in the world file and the share is
exactly 1.000 at world start.

## Why toxins, and not composition in general

One setting rather than a composition model, because a general composition measure needs a threshold
nobody can justify and this one is already in the game.

- **The shipped data correlates storm severity with a dirty atmosphere.** Vulcan is 73.7% toxins
  (methane 27, pollutant 15 per cell) and has the worst storm in the game, −275 K and 5% sun. Venus is
  28.9% (HCl 90) and is second at −98 K. Mars is 0.6% and Europa is 0%, and both are mild. Each storm
  is named for what is in the air: ash, acid, dust, snow. Pressure had no such correlation.
- **Without it the rules cannot tell a cleaned atmosphere from an untouched one.** Swap Vulcan's 42
  mol of methane and pollutant for 42 mol of nitrogen and nothing changes: same share, same
  temperature, same ash storm. Vulcan's ash storm would stop because the planet got comfortable rather
  than because the ash was removed.
- **It closes a hole left when the oxygen term was dropped.** Toxins went with it. Oxygen deserved to
  go, because a storm does not care whether anyone can breathe. Toxins were collateral. Without this,
  a Mars holding 300 mol per cell of pollutant at 290 K and 50 kPa counts as mild, and pollutant is
  exactly what a player adds to warm a cold world.

## Why there is no freeze or condense check

There was one, and it came out as redundant and as a duplicate of the temperature floor.

Every gas freezes below 263.15 K except steam at 273.15: nitrous oxide 251.42, HCl 247.29, hydrazine
246.24, CO2 217.82, the rest at 173 or lower. So at the default floor the freeze half could only ever
fire on steam, and steam condensing is rain, which is the weather the design wants.

At 20 to 200 kPa the condense half is the same story: CO2 needs 517 kPa before it can be liquid at all,
pollutant 1800, nitrous oxide 800.

The check only earned its keep if a player lowered the floor below 251 K or thickened the air past
about 517 kPa. The maximum pressure setting covers the second properly. The first is a player saying
that temperature is acceptable, and a hidden rule should not override them.

## Other findings from the analysis

- **No burst when air returns.** The cooldown is already satisfied, so exactly one storm schedules
  immediately, and the normal 3 to 12 day cadence resumes from it.
- **A running storm survives a save unsuppressed**, because loading calls the start function directly
  rather than the scheduler.
- **VulcanV2 is the only shipped world with two events**, an ash storm and a solar storm. Stripping it
  gives 100% solar storms instead of about 50%, since the ash storm is rejected and the solar storm is
  never suppressed. Accepted as correct.

## Known gaps

**The forecast does not model phase change, and that is accepted.** It evaluates the current mix at 37
angles, so at the coldest angle a gas may in fact be on the ground rather than in the air.

The cold floor closes it at the default. For the gap to bite, the forecast's coldest point must be at
or above the floor while a gas drops out at that temperature, and every gas freezes below 251.42 K
except steam at 273.15. At the 263.15 default the only thing that can leave is steam, and that is rain.
The same argument removed the freeze and condense check, which is two independent routes to the same
boundary. The measured Mars collapse, all 5.5 mol per cell leaving the air in one tick, sits at 217 to
222 K and fails the cold floor by more than 40 K before the gap could matter.

It opens only if a player lowers the cold floor: below about 251 K for nitrous oxide, hydrochloric acid
and hydrazine, below 217.8 K for CO2. There the forecast reads a warmer coldest point than reality,
because it does not model the greenhouse index collapsing as the gas leaves, and a pressure that still
counts gas on the ground. The toxin bound errs the safe way, since a toxin that would have condensed is
still counted. That is a player having said such a temperature is acceptable.

It remains a second reason the mod and `tools/Balance` will not agree.

**A shared greenhouse index cache is worth taking independently of this feature.** It halves the index
work on every one of the per-cell-per-tick calls defect D10 describes. It must be keyed on mix
identity, used only when the mix being evaluated is the tank itself, or it would corrupt the new-world
menu's temperature range and the dev window, both of which evaluate the formula against a freshly built
shipped mix.

**The mod and `tools/Balance` will not agree.** An earlier claim that they would, by construction, is
false: the offline model sweeps both ends of the orbit and the mild rule sweeps only the current
season. Both are right; they are different tests. Nothing should be built on them matching.

**WORLDS.md carries the note already**, under Things to get right. It describes behaviour that is not
built, so it must not ship before the feature does. *Both now ship in 0.10.0, so the note is true.*

---

# Part three: what was built, and what the specification got wrong

## Where it lives

| Piece | Where |
| --- | --- |
| Both rules, the day forecast, the cache, the readout | `src/Patching/Storms.cs` |
| The predicate the scheduler asks before it picks | `Guards.CanScheduleWeatherPrefix`, an `Extra` named `storm scheduling` |
| Turning away a pick that has already been made | the out-of-tick branch of `Guards.ScheduleWeatherPrefix`, after the D6 branch |
| Working the rules out, once a tick, under the tank lock | `Storms.Update()`, last in `Guards.Upkeep` |
| Checking the game still counts the same five gases as toxic | `SelfTest.CheckToxinList`, called from `Patcher` |
| The live test | `tools/LiveCheck/run.ps1 -Schedule`, and `-Orbit <degrees> -OrbitTick <n>` for the other scenarios |

The nine settings are **global, not world-scoped**, and the per-world file stays at schema version 1.
Suppressing an event writes nothing a save carries, so turning a rule back on schedules one storm at
once and resumes the world's own cadence. The evidence is in SIDECAR.md, *The nine `Storms` settings*.

## Three things the specification did not say, and two it got wrong

**The predicate may only be answered when EVERY event a world ships is suppressed.** Part two already
knew that stripping VulcanV2 should give it 100 % solar storms rather than none, but the
implementation constraint reads as though `CanScheduleWeatherEvent` alone carries both rules. It
cannot: it is asked before the pick, so it does not know which event is coming, and answering it
false on a world with an ordinary storm beside a solar one would stop the solar one too. So it is
answered only when nothing this world ships could be scheduled, and the mixed world goes on picking
with the pick turned away instead. That costs one roll of the game's shared `Random` per frame on
such a world, which is the cost the constraint exists to avoid, and there is no way to have both.
Measured over two runs, 40 picks each on a stripped world with one of each: the solar storm came up
24 and 20 times and was scheduled every time, the ordinary storm came up 16 and 20 times and was
turned away every time, and the ordinary storm was never once scheduled.

**The strip rule stands down when the clouds and the ice caps cannot be read.** The measure counts
all five stores, so with `Planet.ReservoirsKnown` false it would read low, and low is the direction
that switches weather off. It reports why rather than judging on four fifths of a measure.

**The toxin check reads fields, not calls.** `Atmosphere.PartialPressureHumanToxins` reaches the five
gases as public fields of `GasMixture`, so `SelfTest.StillCalls` cannot see them;
`SelfTest.CheckToxinList` reads the field references out of the property's own IL instead. It also
found that three of the five (hydrazine, silanol and hydrochloric acid) have no entry in the game's
`Data/terraforming.xml`, so they move the toxin load and leave the greenhouse index exactly where it
was. That is what makes a clean test o**The readout's sixth item had to count, not look.** A full cloud lasts at most one tick:
`PlanetaryAtmosphereSimulation.TickPlanetarySimulation` (`PAS:334-342`) empties the cloud into the air
and *then* calls `ScheduleWeatherEvent`, so the bucket is already empty by the time the rain is
turned away, and a player typing `terraform` would never catch a full cloud. So the weather guard
counts each refusal as it happens, on the planet tick, and the readout reports the count since the
world loaded. The count resets at every world start. The live run fills a cloud, lets the planet tick
refuse the rain for real, and reads the line afterwards:

    rain:      a full cloud has been refused its Rain 1 time since this world loaded, because this
               world ships no weather of its own and "Rain or snow on worlds with no weather" is off

r" is off, so nothing falls

**Part two has no worked examples with computed expected values on Mars.** The live run calibrates
instead: it searches carbon dioxide against nitrogen with the game's own formula for a mix inside all
five bounds, and every bound is then broken on its own against what the mod itself measured.

## What Mars measures

Planet size 0.05, mod 0.10.0, `run.ps1 -Schedule`, 2026-09-22. None of it depends on the size: the
baseline the share is measured against is per outdoor cell, and so is the air the run sets.

| State | Coldest | Hottest | Pressure cold | Pressure hot | Toxins | Share | Verdict |
| --- | --- | --- | --- | --- | --- | --- | --- |
| As shipped | 226.24 K | 287.80 K | 2.14 kPa | 2.73 kPa | 0.017 kPa | 1.0000 | not mild: the cold floor and the pressure minimum both fail |
| 450 CO2 + 550 N2 mol per cell | 281.74 K | 296.24 K | 292.81 kPa | 307.87 kPa | 0 kPa | 109.66 | mild, every bound inside |
| The same, at the far end of its orbit | 272.27 K | 291.15 K | 282.97 kPa | 302.59 kPa | 0 kPa | 109.66 | 9.90 K colder; mild or not depending on where the floor is |
| Stripped to 0.0027 of its starting air | 190.07 K | 320.90 K | 0.00 kPa | 0.01 kPa | 0 kPa | 0.0027 | stripped |

An untouched world reads **exactly 1.0000** of its own starting air, which is the whole reason the
baseline is the world file's figure and not a pressure.

**The season is worth 9.90 K on Mars**, between the coldest point of its day at the warmest part of
its orbit and the same point at the coldest. That is the margin the mild rule lives or dies on for a
world near its bounds, and it is why the readout names the season it judged at.

**Hydrazine moves the toxins and nothing else.** Adding 2.43 kPa of it moved the day from
281.74-296.24 K to 282.04-296.54 K, 0.30 K, which is the density term alone: hydrazine has no
greenhouse index curve in the game's data.

## No shipped world starts mild

Checked against the world files rather than assumed, because the mild rule doing nothing on an
untouched world is the promise the whole feature rests on. Per outdoor cell, and taken at each
world's own shipped temperature:

| World | Shipped air | Which bound it is outside |
| --- | --- | --- |
| Mars | 9.119 mol, 2.1 kPa at 226 K | too thin, and below the cold floor |
| Europa | 340 mol, about 44 kPa, base curve 124 to 134 K | pressure is inside the band; about 130 K below the cold floor |
| Venus | 311.55 mol, about 239 kPa, base curve 737 K | pressure is inside the band; 414 K above the hot ceiling |
| Vulcan | 57 mol, about 58 kPa, base curve 400 to 975 K | pressure is inside the band; 652 K above the hot ceiling |
| The Moon, Mimas | none | no pressure at all, so below the minimum |

So on every shipped world the rule is a no-op at world start, and it is the temperature bounds, not
the pressure ones, that do that work on three of the five. Only Mars was measured live; the rest are
arithmetic on the shipped mole counts and the worlds' own temperature curves. A **custom** world
could ship an atmosphere inside all five, which is what WORLDS.md warns its authors about.
