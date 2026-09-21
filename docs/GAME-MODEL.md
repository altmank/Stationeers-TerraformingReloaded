# How the game works underneath

Game build 0.2.6428.27798. `D/` and `S/` as in README.md. Everything here is **CODE** unless tagged.

## The dormant planetary atmosphere

`D/Assets.Scripts/PlanetaryAtmosphereSimulation.cs` (PAS) and `D/Assets.Scripts/GlobalGasMix.cs`.

- One `GlobalGasMix` per world, the **tank**: 28 gas quantities as doubles plus a Volume. Default
  volume 4e10 L = 5,000,000 outdoor cells of 8000 L (`GlobalAtmosphereData.cs:23-26`,
  `Chemistry.cs:161`). No shipped world sets a different one (three tutorials state the default explicitly).
- Three more reservoirs of the same type: `_liquidClouds` and `_iceClouds` (100,000 L each),
  `_iceCaps` (10,000,000 L).
- Two energy counters: `LatentEnergyOffset` (phase change) and `ExternalInputEnergyOffset` (heat
  from outdoor cells and objects, and from gas given to the planet).
- A read-only 8000 L `Atmosphere` rebuilt from the tank every tick; this is what everything that
  samples "outside" reads (67 `SampleGlobalAtmosphere` call sites, plus wind turbines, rockets, sea
  level). No gameplay system reads the static world composition.
- Every outdoor interaction already goes through it: `Atmosphere.TakeAtmospherePortion` (:1661),
  `LerpToGlobalAtmosphere` (:1710), `GiveAtmospheresMixInWorld` (:1775),
  `AtmosphericsManager.Deregister` (:382), `AtmosphericsController.CloneGlobalAtmosphere` (:289),
  heat via `ReactWithCell` (:2285) and `AtmosphereHelper.DoEntropy` (:522).
- **The read-only copy.** `SampleGlobalAtmosphere(grid)` returns the real outdoor cell at a grid or, where
  there is none, one shared read-only copy of the planet's air (`PAS.ReadOnlyGlobal`). Writes to it are
  ignored (`Mole.Quantity` and `Mole.Energy` setters test `ReadOnly`); `Remove` still returns the moles asked
  for. `DynamicThing.SetWorldAtmosphere` caches it as `WorldAtmosphere`. Everything that moves real amounts
  builds a real cell first with `CloneGlobalAtmosphere`; 17 small things do not (INTERACTIONS.md).
- **The global sea.** When the tank's liquid passes a fixed volume (`GlobalAtmosphereLiquid.RenderThreshold`,
  1.5e6 L times a multiplier evaluated once at start-up) the game renders a sea between 2 and 10 m and treats
  everything outdoors below it as submerged. Tank liquid reaches cells only through that; cells give liquid to
  the tank through the ordinary lerp.
- **No sink above the space line.** Outdoor cells at or above 1,000 m (`PAS.SpaceHeight`) relax toward
  vacuum instead of the planet's air, but what they shed is still handed to `GiveToGlobal`
  (`Atmosphere.LerpToGlobalAtmosphere`), so with the switch on, gas released up there returns to the
  planet. Cells either side of the line are not neighbours. Vented gas cannot be dumped to space; rocket
  exhaust above the line is simply discarded (`RocketEngineBase.Exhaust`), which is propellant, not planet air.
- **Wind turbines read the planet, not the cell they stand in.**
  `WindTurbineGenerator.CalculateGenerationRate` gives 0 unless the turbine is operable, completed,
  has an open grid and `GetRoom()` is null. It then reads
  `AtmosphericsController.ReadonlyGlobalAtmosphere(WorldGrid).PressureGassesAndLiquids`, which is
  `PAS.ReadOnlyGlobal`: always the planet's read-only copy, or the space copy above the 1,000 m
  line, so a turbine up there makes nothing. Under 1 kPa the pressure is taken as zero and the
  turbine makes nothing either; otherwise it is clamped between `_minPressure` 5 kPa and
  `_maxPressure` 25 kPa. Output is
  `clamp(kPa * WindStrength * stormMultiplier * NoiseIntensity, floor, ceiling)`. `WindStrength` is
  one shared simplex noise, `NoiseIntensity` is 10 (25 on the large turbine), and outside a storm
  the multiplier is 1, the floor 0 and the ceiling `MAXPowerOutput` 500 W (1,000 W large). While a
  weather event with a `StormEffect` runs, the multiplier becomes the event's own `WindStrength`
  times `WeatherUtilisationMultiplier` 3 (20 large), the ceiling becomes `MaxPowerOutputStorm`
  1,000 W (20,000 W large), and the floor becomes the event's `WindStrength` times a hundredth of
  that ceiling; every shipped storm has `WindStrength` 15, so 150 W (3,000 W). **MEASURED** in play
  on untouched Mars at 2.1 to 2.2 kPa: 5.34 W and 9.35 W, which is the clamped 5 kPa times the
  noise, not the real pressure. So thickening a planet's air is worth up to five times the power
  from the same turbine, reaching the cap at 25 kPa, and on a world under 1 kPa it is the
  difference between nothing and something. `Objects/WindTurbineGenerator.cs:133-212`,
  `Objects/LargeWindTurbineGenerator.cs`.
- **The switch.** Each of those first asks `PAS.IsGlobalInteraction`, which is
  `public static bool IsGlobalInteraction => false;` (IL: `ldc.i4.0; ret`). Eight call sites:
  `CloneGlobalGasMix`, `GetGlobalMoles`, `TakeGlobalGasMix`, `TakeGlobalMoles`, `GiveToGlobal`,
  `AddEnergy`, `RemoveEnergy` in PAS, and `AtmosphericScattering.ManagerUpdate`. With it false,
  takes copy gas without debiting the tank and gives discard it. **MEASURED**: in the unmodded game
  100,000 mol injected outdoors vanishes and the tank never moves.
- **Already live regardless of the switch**: `TickPlanetarySimulation` runs every tick on host
  (`GameManager.cs:760`) and client (`AtmosphericsManager.cs:472`): melt, freeze, evaporate,
  condense between tank, clouds and ice caps, rain and snow scheduling when a cloud bucket fills,
  the temperature formula, the read-only rebuild. The shipped changelog records a helium fix to
  this code, so the developers maintain it.
- **Saved and loaded by the game itself**: `WorldData.PlanetaryAtmosphere`
  (`XmlSaveLoad.cs:729`, `:1069`). Saves older than revision 26973 are reset on load.
- **Not networked**: `TerraForming.SerializeOnJoin/DeserializeOnJoin/Serialize/Deserialize` are
  called at the right points and have empty bodies. A client builds its tank from the world file.

## Temperature

`GlobalGasMix.GetGlobalGasMixTemperature` (:549):

    T = base(solar angle) + weather + solarDistance(angle, irradiance %) + GHG(angle, index)
        + density(angle, mmol/L) + latent/heatCapacity + external/heatCapacity

- `index` = sum over gases of that gas's own curve at its own density in mmol/L
  (`TerraForming.GetGhgIndex`, curves in `S/Data/terraforming.xml`). **MEASURED** per mmol/L near
  zero: pollutant +3.9, volatiles +2.9, steam +1.5, CO2 +1.4, H2 -0.9, N2O -3.3, N2 -0.03, O2 -0.01.
  At 50 mmol/L: N2 -35.6, O2 -10.3. Hydrazine, helium, silanol, HCl, ozone have no curve.
- Each offset is a `GlobalTemperatureCurveOffset` with a Day and a Night curve, day inside 70
  degrees of overhead, night past 110, eased between. A missing curve evaluates to 0.
- Which worlds ship what (**MEASURED** by dump):

| World | base | solar distance | GHG | density | start air, mol per cell |
| --- | --- | --- | --- | --- | --- |
| Mars2 | yes | yes | yes | yes | CO2 8.66, N2 0.27, O2 0.131, pollutant 0.058 |
| Europa3 | yes | - | - | - | O2 340 |
| Venus | yes (flat 737 K) | - | - | - | CO2 200, HCl 90, N2 21.5, pollutant 0.05 |
| Vulcan2 | yes (975 K noon, 400 K night) | yes (day only, up to +750 K) | - | - | CO2 12, volatiles 27, pollutant 15, H2 3 |
| Lunar | - | - | - | - | none |
| MimasHerschel | - | - | - | - | none |

  So in the unmodded game only Mars's temperature can respond to its air.
- Mars's own curves (**MEASURED**): GHG offset about +2 K per index point near zero (+10 K at 5,
  +28 at 10, +68 at 20, +250 at 100, -50 at -100). Density offset day/night: +50/-60 at vacuum,
  +15/-25 at Mars's 1.14 mmol/L, 0/-11 at 20, the gap closing to zero near 35 mmol/L.

## Phase change on the planet

`PAS.HandleGlobalStateChange`, every tick, in this order: melt ice caps into the tank, freeze tank
liquid into the caps, evaporate tank liquid, condense tank gas into the liquid clouds, freeze tank gas
into the ice clouds. It runs in the unmodded game too.

- **Freezing takes the whole gas in one tick.** `FreezeGlobalGasToIceClouds` passes
  `MoleQuantity.MaxValue`; `Mole.FreezeGlobalMoles` tests only temperature: below freezing point + 2 K
  (`GlobalTemperatureStateChangeOffset`) everything goes. Helium cannot freeze (`Mole.CanFreeze`).
- **Condensing** needs the planet's total pressure at or above the gas's minimum liquid pressure and
  the temperature below the boiling point at that total pressure + 2 K; it moves 10 % per tick
  (`Mole.ChangeState`, gas branch).
- A full cloud bucket (100,000 L of liquid) is tipped back into the tank as liquid and rain or snow is
  scheduled. Tank liquid freezes into the caps at max(3,000 mol, 4.2e-5 of it) per tick; caps melt
  back at max(1,000 mol, 1.4e-5 of them) per tick once above freezing + 2 K. **These rates are
  absolute**, so per outdoor cell they run 20 times faster on a Standard-size planet.
- **The ice caps are asked for their own temperature.** `GlobalGasMix.MeltMoleQuantity` (`:775`)
  evaluates the temperature formula on the caps, not the tank. The caps hold no gas, so their
  greenhouse index and density read 0: on Mars the caps see the density curve's vacuum end (+50 K by
  day, -60 K by night) and no greenhouse term, so they differ from the air by tens of kelvin. That is
  the game's behaviour on Mars and is left alone. On worlds where the mod supplies the response it
  would have made the caps a one-way sink (a Europa warmed to 280 K has caps that still read 125 K
  and never melt), so there the mod evaluates its terms from the planet's air for the caps and both
  clouds (`Climate.IsReservoir`). Found by code review.
- Phase change books latent heat into `LatentEnergyOffset`, which warms the planet while material is
  frozen out. It does not all come back: freezing a gas books vaporisation plus fusion, evaporating it
  returns vaporisation only, and melting the caps returns more than freezing into them gave
  (**MEASURED**: 17 % left after one freeze-and-return; DEFECTS.md D14).
- **MEASURED** thresholds, dumped from the game (freezing point; add 2 K): hydrogen 15.2, nitrogen
  40.0, ozone 51.0, oxygen 56.4, volatiles 81.5, silanol 143.4, pollutant 173.3, CO2 217.8, hydrazine
  246.2, HCl 247.3, N2O 251.4, steam 273.1 K. Minimum liquid pressure is 6.3 kPa for most; CO2 517,
  pollutant 1,800, N2O 800, silanol 516, ozone 250 kPa, so those four never rain on a habitable planet,
  they only freeze. Boiling points at 50 / 100 / 300 kPa: oxygen 78 / 86 / 102, nitrogen 64 / 75 / 96,
  volatiles 106 / 116 / 133, steam 351 / 383 / 441, HCl 310 / 335 / 378 K.
- **MEASURED** live: 20 mol per cell of CO2 added to Europa at 134 K left the air within 5 ticks,
  snow was scheduled, and it moved to the caps at exactly 3,000 mol per tick; latent heat +1.9 K.
  Stock Mars nights are 226.2 K, 6 K above CO2's threshold: the developers' Mars does not collapse.

## Habitability, by the game's own code

- Breathing: efficiency = clamp(ppO2 / 16 kPa, 0, 1.5) (`Lungs.cs:36`); break-even about 5.3 kPa.
- Toxins: pollutant + volatiles + hydrazine + silanol + HCl, summed partial pressure. Warning at
  0.5 kPa, damage above 1.0 (`Entity.cs:91-93`, `Atmosphere.cs:812-823`). N2O stuns above 5 kPa.
  CO2, N2, ozone, steam, helium, hydrogen are not toxic.
- Lungs are safe from 263.15 to 323.15 K (`Lungs.cs:28-30`). UI cautions below 273.15 and above 323.15.
- There is **no pressure damage** to an unsuited human, only warnings below 20 kPa and above 304 kPa.
- Crops: CO2 mole fraction at least 1 %, 25-200 kPa, 273-323 K, toxins under 1 kPa
  (`S/Data/plantliferequirements.xml`).
- Liquid water is stable outdoors above 6.3 kPa between 273.15 K and the boiling point.
- **Fire.** `Atmosphere.TryCombust` (`Atmosphere.cs:2204`) burns a cell when it is sparked, forced, or
  at auto-ignition, provided it holds any fuel (volatiles, hydrogen, alcohol) beside any oxidiser
  (oxygen, nitrous oxide, ozone): there is no minimum ratio and no minimum temperature for a sparked
  cell. A burning cell sparks its neighbours (`:1789`). Auto-ignition is 573.15 K for volatiles and
  hydrogen, lowered by 250 K with nitrous oxide present and 150 K with ozone. The planet tank never
  burns, only cells. So fuel beside oxygen outdoors is a standing fire around the base waiting for
  one spark, at any temperature: adding oxygen to Vulcan before removing its fuel burns at once, and
  volatiles on Europa (340 mol of oxygen per cell) burn at the first spark.

## Threads and ticks

- Game tick 0.5 s. The planet tick runs on the thread pool; atmosphere mixing and reactions are
  parallel worker jobs that start after it and are joined before the next tick.
- **Off the simulation thread, `Mole.Quantity` returns a value cached at tick start**
  (`Mole.cs:73-80`, `AtmosphereHelper.cs:59`). Anything that totals gas from `Update`, a console
  command, or the save path reads a tick-stale number. This caused defect D12 and a false alarm
  in the first test harness.
- Saving pauses the tick and waits for it to come to rest before collecting data
  (`SaveHelper.cs:228-246`).

## The loader

StationeersLaunchPad 0.5.1 on BepInEx 5.4.23.5 (HarmonyX 2.9). `LaunchPadBooster.dll` provides
`Mod.Networking`: per-mod, length-prefixed join and update sections that a client without the mod
skips, so a mod can sync state without breaking the wire format. Its config editor shows every
bound BepInEx entry and reads `AcceptableValueRange` plus tags `DisplayName`, `Order`, `Format`,
`RequireRestart`, `Visible`, `Disabled` (plain `KeyValuePair`s).
