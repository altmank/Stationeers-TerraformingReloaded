# Fuel and oxidiser burn in the planet's air

How the planet's own air burns (0.13.0, off by default): what the rule does and why, the decisions behind
it, what the runs measured and the known limits. The appendices carry the code evidence and the numbers.

## In short

With the rule on, fuel and oxidiser that drain into the planet's air burn there, in a simulated
planet-wide fire. The fire uses the game's own ignition rules and the game's own combustion products,
and its heat is real.

The planet's air is treated as one big cell. When that cell would catch fire, it burns every tick at
the rate the game burns any cell. A cell catches fire because it is hot enough or because something
sparks it:

- **Hot enough.** The planet's hottest point of the day is past the game's self-ignition temperature
  for that air.
- **Sparked.** An outdoor cell trading air with the planet is on fire.

The burn happens inside the planet's air: no gas is moved out of it to burn, and the products stay in it.

While the planet is on fire, it holds back from the outdoor air the side of the reaction that will run
out: all the oxidisers together, or all the fuels together. The cells beside a base are then never
handed anything they could burn, and a fire already burning there uses up what it holds and goes
out. Everything else is shared out as usual, including the abundant partner and the combustion
products, which build up gradually.

The heat of the burn goes into the planet's added heat. That heat fades as it always has, but it has
no cap. A large burn becomes a planet-wide heat wave, and a heat wave can keep the fire going.

On Vulcan the planet is always hot enough, so the planet burns day and night. A leak still burns where
it leaks, in the real cells, for as long as the game's own fire and spread keep it there. Everything
that drains away burns in the planet's air within a minute and never comes back to the base, apart
from the share handed out in the tick it arrives (the one-tick lead, which the armed hold-back closes).

A terraforming route that puts a fuel and an oxidiser in the air together is affected too. BALANCE.md,
*The planet's air burns: what it does to each route*, prices every shipped world's route with the fire on.

Three world settings carry the rule, all off by default: the fire itself (`PlanetAirBurns`), the armed
hold-back that closes the one-tick lead (`PlanetHoldsBackWhileIgnitable`, which acts only with the fire
on), and the trace hold that keeps gas too thin for outdoor air in the planet (`PlanetKeepsTraceGas`,
which works with or without the fire).

## Why

The case it was built for: a Vulcan save of 250,000 outdoor cells (size 0.05), its air mostly methane
with pollutant, carbon dioxide and hydrogen.

- **Gathering on.** Trace gas gathering pulled about 1.5 mol of oxygen next to the base. It burned and
  set off a 467 mol oxygen tank. That put 5,000 to 10,000 mol of oxygen outdoors, which burst the
  outdoor tank row and destroyed active vents.
- **Gathering off.** Oxygen that had drained into the planet's air came back to the base anyway.
  - The planet hands every outdoor cell beside open ground its share of the planet's air every tick.
  - On 2026-09-26, after a furnace was taken apart outdoors, all 14 such cells beside the base were on
    fire, burning the nitrous oxide the planet had handed them (**MEASURED**).
  - Two days later six outdoor active vents burned to their broken state, the furnace intakes among them.
  - Left alone, that smoulder lasts hours.

The game moves air from the planet to the outdoor cells in only a few places, and every one of them
hands out a proportional share of what the planet holds (Appendix A). So holding the scarce side back
while the planet burns is enough to stop drained gas coming back to the base. Burning it in the planet
makes sure the held-back gas is used up rather than stored.

## Decisions, with their background

**The planet is one big cell.** The game has one rule for when air burns and how fast, and it applies
it to each cell. Applying that same rule to one cell's worth of the planet's air means every threshold
is the game's and moves with it: the ignition temperatures, the mole of fuel needed, the smallest
amounts and the burn rate. The test is made on one cell's worth rather than on the planet's totals
because the planet as a whole holds thousands of moles of anything, so every "more than a mole" test in
the game would pass trivially for it and never for a real cell.

**Hot enough means the hottest point of the day.** The planet is modelled with one temperature, but
that temperature swings with the sun, and a real planet with that swing always has a side facing the
sun. So the gate asks whether the day's peak is past the game's self-ignition point. The peak comes from
the game's formula swept over the sun angles, with the mod's temperature changes included. **This is a
deliberate exception to the one-temperature model**; everything else about the planet still uses the
current temperature. On Vulcan the peak never drops below about 975 K, so the planet burns day and
night. On Mars, Europa and any breathable world the peak is hundreds of kelvin short, and they light
only when sparked.

**The rise is immediate.** Added heat enters the planet's temperature on the next evaluation: the
formula divides the stored heat by the heat capacity every time it is asked
(`PlanetaryAtmosphereSimulation.cs:307-313`, `GlobalGasMix.cs:549` onwards), and the daily sweep re-adds
the stored heats every tick (`Storms.cs:344-345`). The fade is 60 minutes, far slower than a burn, so a
burn's own heat can carry the planet past its ignition point.

**A spark, and self-sustain or fizzle.** A burning outdoor cell sparks the cells it gives air to
(`Atmosphere.cs:1787-1790`) but never the planet: the give to the planet skips that line. Here the
planet counts as sparked in any tick in which an outdoor cell trading air with it was on fire. Once lit,
it burns while the spark lasts, and after that only if its own heat has lifted its peak past ignition,
as a cell would. On a cold world an ordinary leak never makes enough heat: Mars must gain about 280 K to
reach 573 K, about 18 GJ at its 63.5 MJ/K, some 60,000 mol of methane burnt. Ten sparked ticks of
1,000 mol of methane on Mars (17.5 % a tick at 291 K) burn 854 mol and add 3.5 K; **MEASURED** in
LiveCheck case 4, the fire went out 2 ticks after the spark, having added 4.13 K.

**The rate is the game's cell rate.** The game burns a share of the scarcer side each tick
(`Atmosphere.GetCombustionMultiplierCurved`, `Atmosphere.cs:2223-2238`):
`(0.05 + 1 / (0.002 (T + 273.15))^1.6) / 5`, or with nitrous oxide or ozone over a tenth of the
oxidiser, `(0.05 + 1 / (0.0025 (T + 273.15))^1.01) / 5`. When either side is under 0.0003 mol it burns
all of it at once. Applied to one cell's worth at the planet's peak:

| Vulcan peak | Share a tick | Half-life |
| --- | --- | --- |
| 975 K, far from the sun | 5.6 % | 6.0 s |
| 1,166 K, mid-orbit | 4.7 % | 7.3 s |
| 1,725 K, nearest the sun | 3.2 % | 10.8 s |

The last 0.0003 mol per cell (75 mol on this planet) of the scarcer side goes at once. By the model,
5,000 mol of oxygen is gone in 36 to 65 seconds and 3 M mol from 1,000 K in about 3.4 minutes; a big
burn's heat wave slows it, because the curve falls with temperature. **MEASURED**: 1,000 to 20,000 mol
burnt in 23 to 52 s, and the 3 M mol night dump was half gone in 13 s, 99 % gone in 103 s and out at
243 s, about four minutes.

**Hold back the scarce side, all of it.** While the planet is burning, the side of the reaction the burn
will use up completely is not shared out to cells: every oxidiser together or every fuel together,
whichever runs out first by the game's own ratios. A tank of oxygen and nitrous oxide on a fuel planet
holds back both. The game finds the side: a copy of one cell's worth burnt to completion
(`GasMixture.Combust` at a share of 1, `GasMixture.cs:2353-2778`) leaves the scarce side empty. Hydrazine
on its own is both sides and is held back itself. The scarce side is consumed anyway, so holding it back
loses nothing; one side missing is enough to stop a cell burning; so the cells beside a base burn what
they already hold and go out. Every way the game hands the planet's air to a cell goes through
`GasMixtureHelper.Create(GlobalGasMix, ...)`, including the read-only copy vents and sensors read
(Appendix A), so one postfix covers them all; the census carries it as a wrapper, and `CheckShape` refuses
a game build that copies the tank any other number of times.

**The one-tick lead, and the armed hold-back.** The hold while burning is decided at the planet tick,
before the tick's mixing, so gas that reaches the planet during one tick's mixing can be handed to cells
in that same mixing, before the fire lights at the next tick. **MEASURED**: every live outdoor cell
beside open ground gets that share, at 100 m as at 16 m (1.3e-4 mol a cell from a 1,000 mol leak, 5.6e-4
from 5,000, 2.2e-3 from 20,000), and on Vulcan by day burns it for 1 to 6 ticks, costing the vent that
keeps the cell 3.3 % (200 to 1,000 mol), 6.7 % (5,000) or 10 to 11.7 % (20,000). By dusk and night it
does not light and drains back. The armed hold-back (`PlanetHoldsBackWhileIgnitable`) closes the lead:
with it and the fire on, the side the fire would use up is held whenever one cell's worth of the planet's
air would light itself at the day's hottest hour plus the 5 K margin (`FireRule.Armed`), burning or not.
A side the planet holds none of counts as used up (`SideToHold`): on Vulcan every oxidiser, at 0 mol. It
is decided on the tank after the tick's burn, so the tick a leak drains, the copies the cells get already
carry none of it. A fire a spark lit with the gate closed still holds its scarce side while it burns.
**MEASURED**: with it on, a 1,000 mol midday leak put no oxidiser in any far station's cell and burnt
none of them; the control reproduced the lead exactly.

**The trace hold.** The game deletes every gas a cell holds under 1e-5 mol (`Mole.Cleanup`,
`Mole.cs:1042-1048`, per cell per tick from `AtmosphericsController.cs:262`). An exchanging cell takes one
cell's worth of the planet and keeps a share t of the difference (`Mole.Lerp`, `Mole.cs:1191-1200`; t is
`AtmosphereHelper.LerpRate`, `AtmosphereHelper.cs:435-438`). So a gas whose cell's worth times t is under
1e-5 is drawn and deleted every tick by every exchanging cell. With the trace hold on
(`PlanetKeepsTraceGas`), the planet keeps every such gas, by the game's own test at this tick's t
(`TraceHoldRule.Deleted`). It covers oxidiser a fire leaves below what the planet can burn. **MEASURED**: a
200 mol leak left about 0.81 mol (3.3e-6 a cell) the planet could not burn; with the trace hold on it
stayed whole for 850 ticks, off 0.0234 mol was drawn and deleted.

**One postfix carries all three holds.** `PlanetHold` is the tick's hold: a fire part (nothing, armed, or
while burning) and a trace part. `terraform` lists every held gas, what the planet holds of it and why.
Trace gas gathering is handed the whole hold and never gathers a held gas. The self-test judges a take by
the moles it actually handed over (`SelfTest.RoundTripProblem`), so neither hold can make it fail.

**Everything else is shared out normally**, including Vulcan's methane, the abundant partner, and the
products, which build up in the planet and reach the outdoor air like any gas. **The products are the
game's**, from its combustion tables, each oxidiser shared between the fuels by need (`GasMixture.Combust`,
Appendix C). **MEASURED**: oxygen and nitrous oxide, 1,000 mol each, moved every gas by exactly Appendix C's
row.

**The heat is real, uncapped, and booked as the game books a burnt cell.**

- **How it is booked.** The game puts the heat of combustion into the burning gas and carries the
  reactants' heat into products that hold more heat per kelvin (`CombustionResult.RunCombustion`). So what
  raises the temperature is the heat of combustion less the products' extra heat capacity times the
  current temperature, and that is what is booked. Booking the whole heat would overstate the rise by half:
  for 3 M mol of oxygen on Vulcan, 1.73 TJ over the 931 MJ/K the planet holds afterwards is about 1,860 K.
- **What the rise depends on.** The netted term grows with the planet's temperature when it burns, so the
  same burn warms a cooler planet more. 3 M mol from a planet at 1,000 K adds 1,279.8 K (PatchCheck prints
  it). **MEASURED**: the 3 M mol night dump added 1,362.7 K at its peak (run twice, the same to 0.01 K),
  6 % above that; the night run started cooler, which goes the same way, and the gap was not broken down
  further.
- **Where it goes.** The planet's added heat, faded by `ExternalHeatHalfLifeMinutes` (60 minutes) but not
  held to the 50 K limit: a big burn is a heat wave that can keep the planet past ignition. **MEASURED**: a
  half-life of about 61 minutes after the dump, and no errors in the log with the planet at 2,800 K.

**Margin against flapping.** Once lit by heat, the planet keeps burning until its peak falls 5 K below the
ignition point, so it does not switch on and off every tick near the threshold.

**Nitrous oxide in a leak.** The game lowers the ignition point by 250 K when a cell holds more than a
mole of nitrous oxide (`GasMixture.cs:2330`, `Chemistry.cs:267`), so a nitrous oxide leak's own cells can
light at Vulcan's 400 K night. The planet's gate is not affected: one cell's worth of a planet-wide leak is
a tiny fraction of a mole. **MEASURED**: a 5,000 mol oxygen and nitrous oxide leak at night burnt its own
cells with the fire on or off alike (the vents at 2 and 4 m about 50 %, at 8 m 10 %).

**From any ordinary leak, hydrazine lights the planet only by spark.** Its threshold is its evaporation
temperature at 6,000 kPa (`Chemistry.cs:379`, `:617`), 520.808 K (PatchCheck prints it), but the game also
requires more than 1 mol of hydrazine in the cell (`GasMixture.cs:2346`, `MinCombustionMoles`), and one
cell's worth of a planet of 250,000 cells holds that only above 250,000 mol. The one-cell rule stands.

**Vents near a leak count as local damage.** They pull the spreading cloud toward them and burn with it:
the leak hurting where it is made, which the rule keeps.

**Oxygen dumps on Vulcan need no design change.** Burning 3 M mol of oxygen turns 9 M mol of reactants
into 25.2 M mol of products, mostly carbon dioxide and toxic pollutant, and heats the planet by 1,280 to
1,360 K, depending on its temperature when it burns (*The heat*, above). BALANCE.md prices the full
dump, 3.75 M mol (15 per cell, enough to burn all the fuel), as a route: slower than taking the fuel
out, 232 hours against 222.

**Three world settings, all off.** The rule needs an off position, so that a world can be played as the
game ships it. `PlanetAirBurns`, `PlanetHoldsBackWhileIgnitable` and `PlanetKeepsTraceGas` default off in
the config and so on a new world, and a world that never recorded them takes the config's; a world turns
them on with `terraform set` (Appendix E). There is no rate setting: the rate is the game's.

## Measured

On the dedicated test server (game build 0.2.6428.27798, no player connected): LiveCheck `-PlanetBurn`,
41 leak runs on Vulcan2 at size 0.05 (the leak test, VERIFICATION.md), a 3 M mol planet dump at night, and
the armed hold-back and trace hold runs. All **MEASURED**:

- **Fire off, by day,** any oxygen leak from 50 mol up sets every live outdoor cell on the planet burning
  for as long as the oxygen lasts and breaks every powered vent at every station, 100 m away included. At
  night with the fire off the leaked oxygen sits unburnt in the planet (99.9 % left at the end of the run).
- **Fire on,** the planet burns the leak in 23 to 52 s (1,000 to 20,000 mol). The leak's own cloud
  spreads about a metre a tick at first and reaches about 6 m; the vents at 2 and 4 m are damaged from
  50 mol up (half to nine tenths, broken by a pipe burst). Beyond that only the one-tick lead reaches.
- **Only live cells burn.** With no player, the live outdoor air is the cells kept by powered vents,
  passive vents and batteries, one each: 7 on a bare world, 26 on the leak site, up to 133 live and 87
  exchanging during a 5,000 mol leak. A frame, a wall, a solar panel and an unpowered vent keep none. Walls,
  frames, solar panels, tanks, pipes and cables were never damaged, except a cable 2 m from a 20,000 mol
  leak (65 to 68 %).
- **No second fire.** No 467 mol oxygen tank broke in any run, fire on or off.
- **The exchange rate** t was 0.200 to 0.207 on every logged tick.
- **The first flash** (LiveCheck case 3): 50,000 mol held unlit, then the fire switched on by day; the
  block's last fire was 17 ticks later, and the burn added 60.1 K.

## Known limits

- **The first flash.** When the planet lights after holding a burnable mix unlit, each outdoor cell
  already holds its share.
  - This happens when a cold mix is warmed, when the rule is switched on, or when a spark lights a cold
    world.
  - The hold-back stops more coming, but the cells burn what they hold if they can ignite: by heat or
    spark, by day on Vulcan.
  - With a rich mix that flash can damage exposed vents and cables.
  - On Vulcan, where the planet never stops burning, it happens only when the rule is first switched on
    with a mix already in the planet.
- **The one-tick lead,** with the armed hold-back off (Decisions).
- **Gas already indoors.** Gas that intakes pulled indoors before the planet lit is not covered. It burns
  or not by the game's rules where it is.
- **Too fast for a planet.** A planet-wide fire burns out in under a minute for a leak and in about four
  minutes for 3 M mol. A real flame front would take far longer to cross a planet.
- **Night leaks.** A leak on Vulcan at night mostly drains unburnt from its own cells unless something
  sparks them; 400 K is under methane's 573 K. Nitrous oxide is the exception above. What drains burns
  in the planet, which is always lit on Vulcan.
- **Hydrazine** lights the planet by spark only, not by heat, below 250,000 mol on this size.

---

## Appendix A. How the game moves air between cells and the planet

Citations use `D/` for the game decompile (build 0.2.6428.27798).

**Cell to cell** (`D/Assets.Scripts.Atmospherics/Atmosphere.cs`, `MixInWorld`, `:1720-1747`):

- **When it runs.** Every registered world cell mixes once a tick (0.5 s,
  `D/Assets.Scripts/GameManager.cs:151`). Cells are mixed in sequential mix groups
  (`D/Assets.Scripts/AtmosphericsController.cs:167-201`).
- **The take.** Each atmosphere in the set gives a share 1/(n+1) of its gas to a pool, n being the number
  of open neighbours. A neighbour is skipped if this cell is more than 1.2 times its pressure
  (`:1750-1773`).
- **The give.** The pool is shared back by the pressure ratio clamped to 0.1 to 10, times the volume
  share (`:1937-1976`). So a high-pressure leak cell pushes about 14 % of its gas a tick, mostly into
  lower neighbours.
- **New cells.** A cell that is not within `GlobalAtmosphereNeighbourThreshold` (100, scaled by the
  planet's pressure as a share of one atmosphere and by the cell-count suppression) of the planet's air
  makes real cells for its empty neighbours (`:2021-2068`). One that is within it mixes with the planet
  directly. On a Vulcan day that threshold is about 58 mol, about 23 at night.
- **Culling.** A cell within a sixth of the threshold is not "live" (`:1134-1150`). After two ticks it is
  removed (`D/Assets.Scripts.Atmospherics/AtmosphericsManager.cs:180-205`, `:722`) and its gas is given
  to the planet (`:382-391`).
- **Sparking.** A burning cell sparks the cells it gives to (`:1787-1790`). The planet is skipped by the
  `continue` at `:1785`.

**Planet to cell.** All of these are proportional to the tank, so a held-back gas reaches none of them:

| Way | Where | Through `GasMixtureHelper.Create(GlobalGasMix, ...)` |
| --- | --- | --- |
| The exchange take | `Atmosphere.LerpToGlobalAtmosphere`, `Atmosphere.cs:1713`, then `PlanetaryAtmosphereSimulation.TakeGlobalGasMix`, `PlanetaryAtmosphereSimulation.cs:126-139` | yes, `:129` |
| The mixing take from open ground | `Atmosphere.TakeAtmospherePortion`, `Atmosphere.cs:1663-1668`, then `TakeGlobalMoles`, `PlanetaryAtmosphereSimulation.cs:141-152`, then `GlobalGasMix.Remove`, `GlobalGasMix.cs:279-288` | yes, `:285` |
| A new outdoor cell | `AtmosphericsController.cs:309`, then `CloneGlobalGasMix`, `PlanetaryAtmosphereSimulation.cs:89-104` | yes, `:101` |
| An atmospheric event filling a cell | `AtmosphericEventInstance.cs:423`, then `TakeGlobalGasMix` | yes |
| The read-only copy (sensors, vents' readings, 17 small bypass users) | `PlanetaryAtmosphereSimulation.cs:223`, `:286`, `:363-367` | yes |
| The mod's trace gas gathering | `TraceGases.TakeForLerp` | no; skips held gases in `Refresh` |

`GlobalGasMix.ToInstancedGasMixture` (`GlobalGasMix.cs:154-157`) is a one-line wrapper and might be
inlined, so the hook goes on the method it calls, `GasMixtureHelper.Create(GlobalGasMix, MatterState)`
(`D/Assets.Scripts.Atmospherics/GasMixtureHelper.cs:19`). That method is long enough not to be inlined.
The take removes from the tank exactly the mixture it returns (`GlobalGasMix.Remove`, `:246-276`), so
zeroing held gases in the returned mixture keeps the planet conserved.

**Cell to planet.**

- The exchange give (`Atmosphere.cs:1716`).
- The mixing share to open ground (`:1784`).
- A removed cell (`AtmosphericsManager.cs:391`).

All three go through `GiveToGlobal` (`PlanetaryAtmosphereSimulation.cs:154-166`). With the mod's switch on,
these debit and credit the tank. The exchange moves a share `t` of the difference (`AtmosphereHelper.cs:435-438`,
0.2 rising to 1 with the world's atmosphere count). An edge cell loses roughly 30 % of its excess a tick to
mixing plus exchange (**UNVERIFIED**).

**Order within a tick** (`GameManager.cs:760-815`):

1. The planet tick, with the mod's upkeep first.
2. Mixing: spread, exchange and planet takes and gives.
3. Burning (`InternalReactions`, then `AtmosphericsController.AtmosphereJob`: `TryCombust`, then
   `Cleanup`, `AtmosphericsController.cs:258-262`).
4. Removal of settled cells.

Gas leaves a cell before that cell burns in the tick. The planet's own burn happens in step 1, before
any cell takes from it that tick.

**When a cell burns.** `Atmosphere.TryCombust` (`Atmosphere.cs:2205-2221`) burns when a cell is sparked,
forced, or `GasMixture.IsAutoIgnition()` holds, and holds enough to burn. Enough is hydrazine at least
0.00001 mol, or fuel and oxidiser each at least that (`Chemistry.cs:193`). `IsAutoIgnition`
(`GasMixture.cs:2328-2351`) ignores the oxidiser:

| Fuel | Needs in that air | Ignites above | Citation |
| --- | --- | --- | --- |
| Methane (gas plus liquid) | over 1 mol | 573.15 K + offset | `Chemistry.cs:311` |
| Hydrogen (gas plus liquid) | over 1 mol | 573.15 K + offset | `Chemistry.cs:359` |
| Liquid alcohol | over 1 mol | 673.15 K + offset | `Chemistry.cs:403` |
| Hydrazine (gas plus liquid) | over 1 mol | evaporation temperature at 6,000 kPa, 520.808 K, no offset | `Chemistry.cs:379`, `:617` |

The offset is -250 K with over 1 mol of nitrous oxide (`Chemistry.cs:267`), -150 K with over 1 mol of ozone
(`:489`), the lower if both.

## Appendix B. The rule, per planet tick

In `Guards.Upkeep`, under the tank lock, after `Planet.KeepPhaseChangeInProportion()` and before the
pressure ceiling. Host only. Skipped when the world's fire setting is off.

1. **The sample.** One cell's worth of the planet's air: the tank's gases
   (`GasMixtureHelper.Create(tank, MatterState.Gas)`, with the hold suppressed for the mod's own calls)
   scaled by `Chemistry.GridVolume / tank.Volume`, as the game builds its read-only copy
   (`PlanetaryAtmosphereSimulation.cs:363-364`).
2. **The peak.** The day's hottest temperature at the current point in the orbit. It comes from the mod's
   existing cached sweep of the game's formula over the sun angles, which includes the mod's temperature
   changes and the current stored heats (`Storms.DayPeak`, from `Storms.TakeSweep`; heats re-added at
   `Storms.cs:344-345`). The sweep is shared, not copied.
3. **The gate.**
   - **Lit by heat:** `sample.IsAutoIgnition()` with the sample at the peak. While already lit by heat,
     the sample is taken at the peak plus 5 K, so the planet goes out only once the peak is 5 K under the
     threshold.
   - **Sparked:** an exchanging outdoor cell was on fire last tick. A prefix of its own on
     `Atmosphere.MixInWorld` records a cell that is `Inflamed` and has open ground with no cell beside it.
     It is separate from the space rule's prefix, so either rule refusing does not take the other with it.
   - **Enough to burn:** the sample passes `TryCombust`'s quantity test.
   - The planet burns this tick if it is enough to burn and either lit by heat or sparked.
4. **The rate.** The sample's own `GetCombustionMultiplierCurved()` at the peak temperature, on a scratch
   `Atmosphere` per thread; the game's method is called, not copied.
5. **The scarce side.** A copy of the sample burnt by `GasMixture.Combust(1.0)`. The side of the reaction
   that comes out empty (oxidisers, fuels, or both) is held back this tick. Hydrazine alone is held back
   itself.
6. **The burn.** A gas-only copy of the whole tank, `GasMixture.Combust(rate)`. The game splits each
   oxidiser between the fuels by need, burns the share of each pair's limiting side
   (`CombustionResult.RunCombustion`, `CombustionResult.cs:47-95`), adds the products and returns the heat
   `E`. Every gas quantity is written back with `GlobalGasMix.Set`. Liquids are never read or written.
7. **The heat.**
   - Let `C0` and `C1` be the heat capacity before and after (`GetHeatCapacity`,
     `PlanetaryAtmosphereSimulation.cs:293-296`) and `T` the current temperature.
   - Scale both stored heats by `C1 / C0`, as `Planet.AddGas` does, so their kelvin hold.
   - Add `E - (C1 - C0) x T` to `ExternalInputEnergyOffset`, and the same amount to the mod's own
     **combustion heat** counter.
   - The upkeep's fade still applies to all of it (`Guards.Settle`). The 50 K clamp applies only to the
     external heat that is not combustion heat. The counter fades by the same half-life and is saved in
     the world's settings file, like the lost-to-space total.
   - A world loaded without the counter has none, so its next upkeep clamps what was there to 50 K: a
     fail-safe loss of the excess, logged. A counter its save cannot hold within the limit is cut toward
     the save's added heat at load, never past zero, so an older save loaded beside a newer file can only
     lose heat (SIDECAR.md).
8. **The armed hold and the trace hold.** After the burn and the pressure ceiling, on the tank the cells
   will draw from, `PlanetCombustion.PublishHold` works out the armed part (if that setting is on) and the
   trace part (if that one is), and publishes the tick's `PlanetHold`.
9. **Visibility.** On the tick the planet lights or goes out, one log line with the gases, moles, peak,
   rate and cause (heat or spark). A running total for the readout.

The postfix on `GasMixtureHelper.Create(GlobalGasMix, ...)` reads the published hold and zeroes the held
gases, quantity and heat together, in the mixture it returns when the mixture is the planet's tank and the
call is not the mod's own (a `[ThreadStatic]` suppression). It costs one volatile read on every call while
nothing is held. Three errors in a session stand the rule down, with nothing held back.

## Appendix C. Products and heat, by the game's tables

**The table** (`D/Assets.Scripts.Atmospherics/Combustion.cs:33-90`), fuel + oxidiser to products:

| Fuel + oxidiser | Products |
| --- | --- |
| methane 2 + oxygen 1 | carbon dioxide 6 + pollutant 3 |
| methane 1 + nitrous oxide 1 | carbon dioxide 2 + nitrogen 2 |
| methane 3 + ozone 2 | carbon dioxide 6 + pollutant 3 + steam 1 |
| hydrogen 2 + oxygen 1 | steam 3 |
| hydrogen 1 + nitrous oxide 1 | steam 1 + nitrogen 1 |
| hydrogen 3 + ozone 1 | steam 4 |
| hydrazine 2 (split in halves as its own fuel and oxidiser, `GasMixture.cs:2366-2380`) | pollutant 8 |

**Heat.**

- 286 kJ per mol of methane and 306 kJ per mol of hydrogen or hydrazine, doubled with nitrous oxide or
  ozone (`Mole.cs:117-126`, `:550-563`), plus hydrazine's own enthalpy as oxidiser (`CombustionResult.cs:78-82`).
- Specific heats in J/K per mol (`Mole.cs:503`): oxygen 21.1, methane 20.4, hydrogen 20.4, carbon dioxide
  28.2, pollutant 24.8, steam 72, nitrogen 20.6, nitrous oxide 37.2, ozone 38.6, hydrazine 48.4.

**The Vulcan used throughout.** 250,000 cells, shipped air per cell carbon dioxide 12, methane 27,
hydrogen 3, pollutant 15 (`VulcanV2.xml`). That is 6.75 M mol of methane and 0.75 M of hydrogen, and a
heat capacity of 1,322 J/K per cell, 330.6 MJ/K in all (the tank plus clouds plus ice caps). Both fuels
burn two to one with oxygen and one to one with nitrous oxide, so each oxidiser splits 9 to 1 between them.

| Burnt | Oxidiser | Methane | Hydrogen | Pollutant | Carbon dioxide | Steam | Nitrogen | Heat `E` | Rise at 1,000 K, booked as the game |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 5,000 mol oxygen | -5,000 | -9,000 | -1,000 | +13,500 | +27,000 | +1,500 | 0 | 2.88 GJ | about 6 K |
| 3 M mol oxygen | -3 M | -5.4 M | -0.6 M | +8.1 M | +16.2 M | +0.9 M | 0 | 1.73 TJ | 1,279.8 K (1,860 K unnetted) |
| 1,000 oxygen + 1,000 nitrous oxide | -2,000 | -2,700 | -300 | +2,700 | +7,200 | +400 | +1,900 | 1.15 GJ | about 2.6 K |
| 26 mol nitrous oxide | -26 | -23.4 | -2.6 | 0 | +46.8 | +2.6 | +49.4 | 15 MJ | about 0.05 K |

For the 3 M mol row the heat capacity is 394 MJ/K with the oxygen added and 931 MJ/K after the burn. The
air goes from 14.25 M mol (17.25 M with the oxygen added) to about 30.4 M mol, more than double.

## Appendix D. Interactions

- **Starting airs.** No world the game ships starts with both a fuel and an oxidiser (Europa oxygen 340;
  Mars2 and tutorials 1 and 2 carbon dioxide 8.66, nitrogen 0.27, oxygen 0.131, pollutant 0.058; Venus carbon
  dioxide 200, HCl 90, nitrogen 21.5, pollutant 0.05; Vulcan methane 30, carbon dioxide 12, pollutant 15;
  VulcanV2 the same with methane 27 and hydrogen 3; Lunar, Mimas and tutorials 3 to 6 none), so an untouched
  world never burns.
- **Pressure ceiling.** It runs after the burn and trims what the products added.
- **Storms.** The mild rule reads the same sweep, so a heat wave can stop a world counting as mild. Storms
  that cool the day lower the peak and can put a cold-world fire out; on Vulcan the peak stays far above 573 K.
- **Ice caps, clouds, the sea.** Only tank gas burns; gas melting or evaporating into the tank joins the fire.
- **Self-test.** The round trip runs before the upkeep with the suppression flag set and is judged by what
  its take handed over. The rule's own load-time check is `CheckArithmetic` (Appendix H).
- **Rescale, gas add or remove, reset.** The first two scale the combustion heat counter with the other
  heats; reset zeroes it.
- **Multiplayer.** Host only. Clients receive the tank and both stored heats through the planet sync
  (`Sync.cs:111-112`), so they see the heat wave; the settings and the counter are not sent.
- **Saves.** The planet save is unchanged. The settings file gains the three settings and the counter, all
  nullable, schema version unchanged.
- **Performance.** Per planet tick, only while a burnable pair is present: one sample, three game calls on
  it, one tank copy and burn, and a re-sweep of the day (37 evaluations) while the fire lasts. Per copy of
  the planet's air: one volatile read while nothing is held.

## Appendix E. The settings

| Section | Key | Label | Description | Default | Read from |
| --- | --- | --- | --- | --- | --- |
| Fire | `PlanetAirBurns` | The planet's air burns | Off by default. When the planet's air holds a fuel and an oxidiser and would catch fire (its hottest hour is past the point where that air lights by itself, or a fire outdoors touches it), it burns as one big cell, by the game's own burning rules. While it burns, it stops handing the outdoor air the gas it is using up, so gas that drains from a leak never comes back to burn around your base. The heat warms the planet with no limit and fades like any other added heat; a big burn becomes a heat wave. A world saved without this setting takes the config's. | off | The world |
| Fire | `PlanetHoldsBackWhileIgnitable` | Hold back what would burn whenever the air would light | Off by default; acts only with PlanetAirBurns on. Whenever the planet's air would light itself (its hottest hour is past the point where that air lights by itself), the planet holds back from the outdoor air the gas its fire would use up, burning or not, even before there is any. On Vulcan that is every oxidiser, all the time, so the tick a leak drains into the planet hands none of it to the outdoor air around your base. Off, it is held back only while the planet burns, and that first tick's share reaches every outdoor cell beside open ground. A world saved without this setting takes the config's. | off | The world |
| Trace gases | `PlanetKeepsTraceGas` | The planet keeps gas too thin for outdoor air | Off by default. The game deletes any gas an outdoor cell holds less than 0.00001 mol of, so a gas the planet's air holds so little of that one outdoor cell's draw of it falls under that is drawn and deleted every tick by every outdoor cell beside open ground, until it is gone. On, the planet keeps such a gas instead of handing it out: nothing is deleted, and none of it reaches the outdoor air. A world saved without this setting takes the config's. | off | The world |

- Tier: they change behaviour, so a world that never recorded them takes the config's (SIDECAR.md, missing
  sidecar).
- `terraform set PlanetAirBurns <on/off>` acts at once. Turning it on asks for `confirm` when the planet holds a
  fuel and an oxidiser each at or above 0.001 mol per outdoor cell (a held mix, not a leak), naming both totals,
  the peak against the ignition point, and the first flash (Known limits). A client is refused.
- The descriptions are the ones `Plugin.cs` binds (SETTINGS.md is generated from the same source). Sort order:
  90 and 91 in `Fire`, after Rockets; 63 in `Trace gases`.

## Appendix F. Code touchpoints

`src/Patching/FireRule.cs` holds the rule of Appendix B as virtual parts, so PatchCheck can hand the check
broken versions; `PlanetCombustion.cs` the upkeep step, spark flag, combustion heat counter, `PublishHold`,
readout, `CheckShape` and the fault counter that stands the rule down after three errors; `PlanetHold.cs` the
tick's hold and `TraceHoldRule`; `PlanetCombustionCheck.cs` Appendix H. `Patcher.cs` installs the postfix and
the spark prefix and refuses the rule if either target is missing; `Guards.Upkeep` calls the rule before the
ceiling and `Guards.Settle` clamps only the non-combustion part of external heat; `Storms.DayPeak` gives the
peak; `TraceGases.Refresh` skips held gases; `Settings.cs`, `Sidecar.cs` and `Plugin.cs` carry the three
settings and the saved counter; `Planet.cs` scales the counter on rescale and gas add or remove and zeroes it
on reset. ARCHITECTURE.md, *The planet's air burns*, has the same from the build side.

## Appendix G. Readout

World settings block: `, the planet's air burns` or `, the planet's air does not burn (fuel and oxidiser burn
only in outdoor cells)`.

Its own lines, after the trace gas line:

- Nothing to burn: `planet fire: none, the planet's air holds no fuel beside an oxidiser`
- Could burn, not lit: `planet fire: not burning; 1,000.000 mol of Methane beside Oxygen, hottest hour 291.4 K against the 573.15 K at which it lights itself; a fire outdoors would light it`
- Burning: `planet fire: burning since tick 18,430 (heat: hottest hour 1,166.2 K, lights at 573.15 K), 4.7 % a tick; burning 2,412.336 mol of Oxygen against Methane and Hydrogen; ...; this fire: 2,587.664 mol of oxidiser burnt, 1.49 GJ of heat, 4.5 K added`
- What is held: `planet holds back from the outdoor air: Oxygen 0 mol (armed hold-back), NitrousOxide 0 mol (armed hold-back), ...`, each gas with what the planet holds of it and why (while burning, armed hold-back, trace hold), or `nothing`
- Combustion heat: `planet fire heat: 6.2 K now, fading by half every 60 min, not limited`
- Off: `planet fire: off for this world (terraform set PlanetAirBurns on)`
- Stood down: `planet fire: off for this session because ...`
- On a joining player's game: `planet fire: run by the host`

Log, once per change: `Planet fire started (heat|spark): <gases and moles>, hottest hour <K>, <rate> a tick` and
`Planet fire out: <moles> burnt, <GJ>, <K> added`.

## Appendix H. Tests of the rule

### Offline, in `CheckArithmetic`

`PlanetCombustionCheck.CheckArithmetic` runs every case on the game's own types at load, before the rule is
installed; PatchCheck runs it with the game's rate written out (the game's method calls `Math.Clamp`, which
PatchCheck's runtime lacks; its constants are read off the IL). The tank is 250,000 cells of VulcanV2 air
unless stated, the peak is passed in, and every expected figure is worked out independently of the rule.

| # | Case | Holds when |
| --- | --- | --- |
| 1 | The gate | The rule's lit-by-heat answer equals the game's `IsAutoIgnition()` on one cell's worth at the peak |
| 2 | The rate | At 975, 1,166 and 1,725 K the share is 0.0563, 0.0468, 0.0318 (to 1e-4); the second branch with nitrous oxide; 1 under 0.0003 mol a cell |
| 3 | Vulcan and oxygen | 5,000 mol at 1,000 K burns at the game's share to Appendix C's row (1e-6 relative); the last 75 mol go at once |
| 4 | Two oxidisers | 1,000 mol each of oxygen and nitrous oxide: both held, the fuels shared, Appendix C's row, 1.15 GJ before netting |
| 5 | Fuel scarce | Europa3 plus 300,000 mol of methane: methane held, oxygen shared; lit at 580 K, not 560 K |
| 6 | Hold-back conserves | Burning, the tank copy the postfix sees holds no oxygen and the tank loses exactly it; not burning, every copy carries it (the game's own copies need a running world: LiveCheck) |
| 7 | Flapping | Peak 573.2, 572.0, 569.0, 567.0 K: lights, stays lit, stays lit, goes out |
| 8 | Spark | Mars2 plus 1,000 mol of methane at 291 K: not lit by heat; burns 10 sparked ticks, stops on the 11th; under 10 K added (3.5 K) |
| 9 | Self-sustain | A cold planet whose burn crosses the threshold within the sparked ticks keeps burning after the spark |
| 10 | Heat booking | `L = L0 C1/C0`; `X = X0 C1/C0 + E - (C1 - C0) T`; `H = H0 C1/C0 + E - (C1 - C0) T` |
| 11 | No cap | 300 K of combustion heat survives settling apart from its fade; other added heat is held to 50 K |
| 12 | Hydrazine | Threshold 520.808 K printed; a trace never lit by heat but burns sparked; 1.2 mol a cell lit just above it, not 5 K below |
| 13 | Nitrous oxide offset | 26 mol on Vulcan leaves 573.15 K; 1.2 mol a cell lowers it to 323.15 K |
| 14 | Liquids | Liquid oxygen and methane in the tank never burnt, written or held |
| 15 | Self-test | With the mod's own copies unheld and the planet burning, a take and give return the exact total |
| 16 | Gathering | A gas held by the fire or the trace hold gets no gathering budget; an unrelated trace does |
| 17 | Armed hold-back | On Vulcan past ignition every oxidiser is held with none in the air and no fuel is; armed 5 K short, not 6 K short or at 500 K; Europa holds the fuel; oxygen arriving after the decision reaches no copy; a spark-lit cold planet is not armed |
| 18 | Both settings | Armed off: held only while burning. On, past ignition: held burning or not. On, gate closed, spark burning: held while burning. Trace off: nothing kept as a trace |
| 19 | Trace hold | The game's test at the exchange rate: 4e-5 a cell at 0.2 kept; 6e-5, 4e-5 at 1.0 and none not; a kept gas reaches no copy |
| 20 | Self-test's count | A take under a hold balances by what it handed over, not against the planet's whole share |

**Broken rules.** PatchCheck hands `CheckArithmetic` twelve broken rules and needs every one to fail it: a gate
on planet totals; a fixed 573 K ignition point; a hold-back of one gas instead of a side; a hold-back while not
burning; fire heat held to the 50 K limit; heat without the heat-capacity term; liquids written back; an armed
hold-back that ignores the ignition gate; one without the 5 K margin; one that waits for an oxidiser to arrive;
a trace test without the exchange rate; and a trace hold that keeps everything.

### Headless, LiveCheck `-PlanetBurn`

VulcanV2 at size 0.05 (case 4 on Mars2), a new world per case and switch position. The driver makes a block of
20 exchanging outdoor cells beside open ground and a leak cell it can fill directly, reads everything inside
the mod's fire upkeep one line a tick, and catches every burn of its cells at the burn itself. Runs are 4,800
ticks, with the fire on and off. Every case passed both ways on 0.13.0 (**MEASURED** figures below).

- **Case 1, leak by day.** 5,000 mol of oxygen into the leak cell, 280 m from the block. The block is never
  handed a held gas during a mixing the hold was in force for, and the planet's oxygen falls at the game's
  share. The one-tick lead is reported: 1 tick, 6 cells, 2.9e-4 mol, 166 J (a lower bound: 20 cells showed
  `Inflamed`). Off is the control: the block burnt for about 1,200 ticks, 9 mol, 5.2 MJ.
- **Case 2, two oxidisers.** 1,000 mol of oxygen and 1,000 of nitrous oxide into the tank. Both are held back
  from the block, and tank plus outdoor cells move by Appendix C's row to 0.5 % (exact on the run).
- **Case 3, first flash.** With the fire off, 50,000 mol of oxygen into the tank; wait until the block holds
  its share. Switch on by day. The cells burn their share once and are not handed more: last fire 17 ticks
  after the switch, +60.1 K.
- **Case 4, spark on a cold world.** Mars2. 1,000 mol of methane into the tank, then a cell away from the
  block set on fire with oxygen and a spark, so what it burns cannot reach the block by cell-to-cell mixing.
  The planet lights by spark, burns while that cell burns, and goes out within 2 ticks of it; the heat added
  stays under 6 K (4.13 K).
- **Case 5, night leak.** At Vulcan night, 5,000 mol into the leak cell. The leak's cells do not burn without
  a spark, the planet burns what drains (always lit on Vulcan), and the block burns nothing.

Leaks are absolute moles; what goes into the planet is scaled to its size against 250,000 cells. The leak
test (VERIFICATION.md) does the same with built vents, tanks and cables at distances from a real release.

## Appendix I. Defaults

| What | Default | Why |
| --- | --- | --- |
| `PlanetAirBurns` | off | The rule changes how a world plays; a world opts in, and one without the setting takes the config's |
| `PlanetHoldsBackWhileIgnitable` | off | It holds gas back from the outdoor air before any fire; a world opts in |
| `PlanetKeepsTraceGas` | off | It changes what the outdoor air holds on any world, fire or not; a world opts in |
| Gate | the game's `IsAutoIgnition` on one cell's worth at the day's peak, or a spark from an exchanging cell | Every threshold is the game's |
| Rate | the game's `GetCombustionMultiplierCurved` on that sample | One rule for cells and planet |
| Hold-back | the scarce side, every oxidiser or every fuel, while burning, at every copy of the tank | Consumed anyway; one side missing stops a cell burning |
| Combustion heat | booked as the game books a burnt cell, faded at 60 minutes, not capped | The netting is the game's own |
| Flapping margin | 5 K | Stops the planet switching every tick near the threshold |
