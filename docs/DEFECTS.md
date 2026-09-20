# Defects switching the planet on exposes

The take and give code is dormant in the shipped game, so nobody had run it. Three static audits
(conservation, energy and phase change, threading and lifecycle) plus live testing found these.
`D/` paths and build as in README.md.

| # | Defect | Evidence | Fix in the mod | Status |
| --- | --- | --- | --- | --- |
| D1 | Every save load drains the tank. The loader builds each saved outdoor cell through `CloneGlobalAtmosphere`, which debits the tank, then `Atmosphere.Load` overwrites the cell. Exit never credits back (`ClearAll` uses `DensePool.Clear`). | `XmlSaveLoad.cs:1069,1107`; `AtmosphericsController.cs:306-309`; `PAS:110`; `Atmosphere.cs:1412+` | `Gate.Enabled()` is false unless `GameState` is Running or Paused | **Verified live**: save mid-spread with 4,300 cells, load, total changed by 0.000 mol |
| D2 | Building into an occupied outdoor cell duplicates its gas: the mix is copied to open neighbours, then the cell is removed, and removal gives the still-full mix to the tank. | `AtmosphericEventInstance.cs:470-491`; `Atmosphere.cs:3118-3121`; `AtmosphericsManager.cs:391` | Prefix and finalizer on `DivideWorldAtmosphere` set a thread-static flag; a prefix on `Deregister` empties the cell first when it had open neighbours | Not yet tested live |
| D3 | `ExternalInputEnergyOffset` only accumulates, is saved, never decays or clamps. Temperature drifts without bound, either way. | `PAS:164,176,188,238-239`; `TemperatureKelvin.cs:89-92` | Tick prefix: exponential decay (half-life setting) and a clamp on the kelvin it can apply | Clamp exercised in the reset test; decay rate untuned |
| D4 | Airless worlds accrue energy against zero heat capacity (objects radiate against a substituted 50 K); the first vented gas divides it by a tiny capacity. | `PAS:168-178`; `AtmosphereHelper.cs:468-472`; `IdealGas.cs:5-11` | Covered by the D3 clamp, which scales with heat capacity: at zero capacity it holds the energy at zero | Not tested live (needs the Moon) |
| D5 | A take removes moles but not their share of stored offset energy. | `PAS:126-152` vs `:163-164` | None. Bounded by D3 | Accepted |
| D6 | When a cloud bucket fills, the tick schedules rain or snow without checking for weather already scheduled or running, overwriting it mid-flight. Also ignores `WorldHasWeather`. Live in the unmodded game, but only matters once clouds fill. | `PAS:335-362`; `WeatherManager.cs:257-263,448` | Prefix on `ScheduleWeatherEvent`, active only inside the planet tick: skip when weather is scheduled or running, or the world has none (setting) | Not tested live |
| D7 | The tank adds whatever it is handed. One NaN cell would poison the saved planet for good. | `GlobalGasMix.cs:246-320` | Prefix on `GiveToGlobal` refuses NaN, infinite and negative mixtures | Not tested live (no NaN source found) |
| D8 | The tick mutates the tank without the lock takes and gives use. Only main-thread `OnDestroy` clones can collide; worst case 2e-7 of the tank. | `PAS:332-370`; `DynamicGenerator.cs:199`; `PortableAtmospherics.cs:111` | Tick prefix and finalizer hold the lock | In place; race not reproducible |
| D9 | The sky blend allocates about thirty objects per frame once on. | `AtmosphericScattering.cs:305-318` | Prefix lets it run once per game tick and repeats its one per-frame effect (fog scaling) in between | Loads and runs; look unverified |
| D10 | `GiveToGlobal` evaluates the full temperature formula under the lock, once per open-air cell per tick. | `PAS:160-165` | None: the game already pays the same cost twice per cell per tick in the take path | Accepted; unmeasured in a big base |
| D11 | `WorldAtmosphereSpawnData.cs:80` resets a cell without give-back, world setup only. | as cited | None | Accepted |
| D12 | **Found live, missed by all three audits.** The save collects cells on the main thread, where `Mole.Quantity` returns a value cached at tick start, beside a tank that is current. One tick of cell-to-tank flow lands in the file twice. Measured: +5,793 mol across one save and load. | `Mole.cs:73-80`; `AtmosphereHelper.cs:59`; `XmlSaveLoad.cs:728-729` | Prefix on `XmlSaveLoad.GetWorldData`: `GasMixture.UpdateCache()` on every atmosphere (the call the game makes every tick) | **Verified live**: 0.000 mol |

## Confirmed sound with the switch on

- The lerp between an outdoor cell and the tank conserves each species (`Mole.cs:1191-1199`).
- World mixing takes and gives exactly (`Atmosphere.cs:1665,1785`; `GlobalGasMix.cs:279-287`).
- Every removal from `AllAtmospheres` reaches `Deregister` (`DensePool.cs:151-211`), except `ClearAll` (D1).
- Every outdoor cell creation path either debits the tank or starts empty.
- All 28 species are handled symmetrically across the tank's methods and its save data.
- The existing callers cannot drive the tank negative.
- Phase change conserves moles across tank, clouds and ice caps.
- `_globalGasMix` is non-null whenever the gated methods can run, bar a pre-existing exit race that is caught.
- **MEASURED**: 100,000 mol injected outdoors spreads over about 4,300 cells and drains into the tank;
  tank plus cells stays level to 0.005 mol; the tank gains 99,999.9 mol.

## Known, small, not fixed

- First session on a pre-mod save: existing outdoor cells were never debited, so the tank gains
  their gas once as they are cleaned up.
- Cells created during a load by things other than the saved-atmosphere list are not debited.
- The game deletes quantities below its minimum-mole threshold (`Mole.cs:1042-1047`): about 0.03 mol
  lost per 100,000 injected.
