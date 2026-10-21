# Multiplayer

What a player who joins someone else's game sees, with and without the mod. Everything here is
**CODE** (read from `D/` and from LaunchPadBooster 0.2.0) unless tagged. Nobody on this project has
run a multiplayer session; one player has, and their report is at the end.

The short version: the host decides everything that happens, and a joining player's game shows it.
The one thing the game never sends is the planet's own air, so a player without the mod, where they
can join at all, sees the world as it ships wherever the host has not built a real outdoor cell.
Nothing they see is wrong in the host's simulation, and nothing they do can change the host's planet.

## How the planet reaches a joining player

- **The host decides.** `GameManager.RunSimulation` is `!NetworkManager.IsClient`
  (`GameManager.cs:273`). Every mix, every device tick, breathing, damage, fire, electricity and
  logic sit inside `if (RunSimulation)` blocks of the game tick (`GameManager.cs:738-830`).
- **A joining player's game builds its own planet from the shipped world.**
  `WorldManager.DeserializeOnJoin` calls `InitializeWorldEnvironment`, which calls
  `PAS.CreateGlobalAtmosphere(WorldSetting.Current.Data.GlobalAtmosphereData)`
  (`WorldManager.cs:1953`, `:1673`). The game has a hook for sending the planet and leaves it empty
  (`TerraForming.SerializeOnJoin`, `Serialize` and their readers have no bodies).
- **Real cells are sent.** Rooms and every outdoor cell that exists come across with the rest of
  the game state (`AtmosphericsManager.DeserialiseDeltaState` -> `AtmosphereHelper.ReadStatic`).
- **Outdoors, a player's game reads a real cell or its own planet.** `SampleGlobalAtmosphere(grid)`
  returns the cell at that grid if one exists, else `PAS.ReadOnlyGlobal`, the one-cell copy the
  player's own game rebuilds from its own planet (`AtmosphericsController.cs:315-322`).
- **A player standing still in a suit has a real cell.** The suit exchanges heat with the air
  around it, and handed the planet's copy `AtmosphereHelper.DoConvection` builds a real cell at that
  grid (`AtmosphereHelper.cs:530-548`, called from `Atmosphere.cs:2384`). The host does this, and
  the cell is sent. A walking player keeps stepping onto grids where no cell exists yet.
- **A joining player's game runs the planet tick on its own planet.**
  `AtmosphericsManager.ProcessAtmospheresClient` calls `PAS.TickPlanetarySimulation`
  (`AtmosphericsManager.cs:466-487`) every time a state update from the host carries atmosphere
  data, about once per 0.5 s game tick. Freezing, melting, condensing and the rebuild of the copy all
  run there against that game's own planet.

### What the mod adds

- **The planet state, from the host.** `Sync` rides LaunchPadBooster's per-mod sections. At join,
  the section is read after the game's own cells (`ModNetworking.ReadJoinSuffix` wraps
  `AtmosphericsManager.DeserializeOnJoin`) and after the player's game has built its planet, so
  `PAS.Load` replaces it at once. After that, the full tank, clouds, ice caps and both heat counters
  are sent every `SyncIntervalSeconds` (default 5).
- **Only an exact version match can join.** A mod present on both sides must have the same version
  string, or LaunchPadBooster closes the connection with "version incompatible"
  (`DefaultVersionValidator`, `ConnectionState.DoJoinValidateModList`).
- **A peer without LaunchPadBooster networking is refused, both ways.** The first touch of
  `Mod.Networking` constructs `ModNetworking`, whose `Initialize` patches `VerifyPlayer` and
  `VerifyPlayerRequest` to append a join header on write and read one on read. A game where no mod
  touched `Networking` writes no header; the reader's `JoinValidateHeader.Read` takes whatever byte
  comes next, or 0 at the end of the stream, and `ConnectionState.ReceiveJoinValidateHeader` closes
  the connection with "Invalid booster networking version" unless it is 2. StationeersLaunchPad
  itself does not touch `Networking`. So a player without this mod gets in only when both games run
  another mod that does (any mod using LaunchPadBooster's messages, sections or prefabs); then
  `Networking.Required = false`, which this mod sets, lets the mod lists differ. The same holds for a
  player with the mod joining a host without it.
- **The section is sent with every state update, not once per window.** The host sends state every
  50 to 100 ms (`NetworkServer.NetworkUpdate`, `TickIntervalForBacklog`). `Sync.SerializeUpdateSuffix`
  decides from the tick count and fills a one-second window every five seconds, so the planet goes
  out about 20 times per window, about 1 KB each, and 9 bytes otherwise. About 4 KB/s per player.
- **The host's settings travel with the planet.** After the planet come the four world settings a
  joining player's game evaluates itself: `GhgResponseScale`, `DensityResponseScale`, `AirlessAlbedo`
  (the temperature rule) and `DynamicSky` (the sky), 25 bytes. The player's game checks each number
  against the same `Limits` a world's settings file is checked against, keeps its own for one that
  fails, and puts the rest in `Effective` (`Sync.Apply`). None of the other world settings is read on
  a joining player's game.
- **Gas released in space, for the readout.** Last come whether the host's world deletes gas released
  in space, whether that rule is installed on the host, and the host's lost-to-space total, 10 bytes.
  The joining player's `terraform` shows those, marked as the host's. Nothing on their game acts on
  them: the deleting happens in the mixing, which only the host runs. The total is sent as it stands
  with every planet update, so the host's `terraform reset confirm`, which sets it back to 0, shows on
  joining players' games within `SyncIntervalSeconds`.
- **The storm floor and ceiling.** After space come the host's `StormFloorKelvin` and
  `StormCeilingKelvin`, each a flag and a number, 18 bytes. The joining player's game applies them in
  the temperature formula, so during a storm it reads what the host reads; one outside the setting's
  range keeps the player's own for that bound.
- **The comet in force.** Then a flag and, when set, the comet event the host is playing
  (`docs/COMET.md` section 1), for a player who joins during one or missed its own message.
- **The Celestial terraforming settings.** Then come all of the host's Celestial terraforming settings,
  a count and then each value, 4 + 8 bytes a setting (132 bytes for the sixteen). The joining player's
  game works out a comet's orbit, the sky rule's disc and whatever else it shows of a comet from them.
  Each is checked against its range, declared once beside its key (`Comets.CelestialSettings`); one
  outside it keeps the player's own for that setting, and a count that is not this build's is not read.
- **The comets on the space map.** Last comes every comet on the map, each as the state it is in or set
  off with: its gas, mass, engines and fuel, its route as node ids and each hop's delta-v, and its start on
  the shared clock (`OrbitalSimulation.System.TotalRealTimeSeconds`, which the game sends in every state
  update). A joining player's game places each flight by the same rule the host does, so nothing is sent
  while one flies. The host also sends the whole list the moment it changes (made, sent, arrived, out of
  fuel, back on the clock after a load) as a mod message of its own. A joining player's game replaces its
  list with the host's whenever one arrives; its own copy only draws the map icons and answers
  `terraform comet list`. About 150 bytes a comet of one gas on a three-hop route. The list carries its own
  layout byte: 1 for 0.18.0, 2 for 0.19.0, which adds each comet's kind, ore and Cut cycles, 3 for 0.20.0,
  which adds after the comets the target of every payload bay on a rocket at Entry (its PositionX and
  PositionZ, 16 bytes a bay), which the game itself neither saves nor sends, and 4 from 0.21.0, in which a
  bay's target may be NaN (a bay aiming untracked) and after the targets come the comets brought down and still
  falling: each comet's id, kind, size, where it lands and its fall on the shared clock, 61 bytes a fall. A
  reader that does not know the byte ignores the list. While a rocket action changes a comet every tick (Cut, Fuel, Burn), or a chip moves
  a bay's target, the host sends the list at most every 2 s.
- **The comet in the sky.** Every machine, the dedicated server included, puts a comet parked at Entry into
  its own sky as a body circling the planet, from the list it already has: the orbit comes from the comet's
  id, the world and the host's Celestial terraforming settings, and the game places every body by the sky's
  clock, which it sends in every state update. So the comet is in the same place on every machine and
  nothing is sent while it orbits. A Ground Telescope works its lock out on every machine, as it does for the
  game's own bodies; its logic values, and the payload bay's comet readouts, are read on the host, where
  logic runs. The Map motherboard's two icons and the glow in the sky are drawn by each player's game from
  its own copy of the comets and the bays' targets.
- **A comet brought down falls the same on every machine.** The host's burn takes the comet off the list and puts
  its fall on it, sent at once; each machine warns its own player from its own copy, with that player's own
  distance, on its own console, once, and again after a load. The impact is the comet event above, sent the same
  way. Only the host changes the planet's air at the end of the fall; the joining players get the new air with
  the planet sync. The body leaves each machine's sky at that machine's own game tick main-thread moment.
- **A console comet called by a joined player** with the game's `serverrun terraform comet ...` (which needs the
  same `ServerAuthSecret` in both games' `setting.xml`) is aimed from that player: where they stand and the way
  their character faces, as their game sends it. Called from the host's own console on a dedicated server, it is
  aimed from the first player online (`docs/COMET.md` section 2).
- **The comet actions run on the host.** A rocket's mode reaches every player as the game sends any mode, a
  raw byte the game does not check, so a joining player's panel and buttons show Cut, Fuel, Burn and Launch
  from it, and the comet's line from that player's own copy of the comets. The miner's cycles, the deploy, the
  fuel moving and the launch happen only where the game's simulation runs.
- **The section's first byte says what follows.** 0 nothing, 1 the planet alone (0.10.0 and earlier),
  2 the planet and the settings (0.10.1 to 0.11.3), 3 the planet, the settings and space (0.11.4 to 0.13.2), 4 those and the storm bounds (0.14.0 to 0.15.0), 5 those and the comet (0.16.0 and 0.16.1), 6 those and the Celestial terraforming settings (0.17.0), 7 those and the comets on the space map (0.18.0 on). A reader takes the planet from 1 to 7 and skips any other
  value; a layout, once shipped, keeps its byte and a new one takes the next. Mixed versions never
  meet in practice, because the exact version match above refuses the join, so this guards a
  hand-built or mis-versioned peer, not a supported mix: a 0.10.0 game reading a 2 would skip the
  whole section rather than misread it.

## A player without the mod

Only possible when both games run another mod that uses LaunchPadBooster networking (above).

**Right** wherever a real cell exists: inside rooms, beside vents, machines and anything else that
exchanges with outside, and under the player while they stand still in a suit. **The world as
shipped** everywhere else. Their game never learns the host's planet, so the difference grows as the
host terraforms.

### What they see and hear differently

From `tools/census/client.csv`: of 312 game methods that touch the planet or outdoor air, 138 run on
a joining player's game, and 106 of those read that game's own planet wherever no cell exists. 97 of
the 106 change something a player can see or hear. Grouped:

| Area | What is wrong on their game | Where it comes from |
| --- | --- | --- |
| Helmet readouts | Outside pressure and temperature, and the inside readouts with the helmet open, flip between the host's air (standing still, near a base) and the shipped world (walking). Oxygen and nitrous oxide tooltips, and the pressure, temperature and toxin warnings while breathing outside air, follow the same source. HUD mods that list the gas mix read it too | `PlayerStateWindow.Update`, `StatusUpdates.RefreshAtmosphereValues`, `OxygenTooltip`, `StunTooltip` |
| Temperature | Where no cell exists, the shipped formula on the shipped air: no greenhouse gas the host added, no heat the host vented, no latent heat the host's planet stored. Helmet frost and heat haze follow it | `GlobalGasMix.GetGlobalGasMixTemperature`, `TerraForming.GetGhgIndex`, `PAS.GetExternalInputEnergyOffset`, `InventoryManager.HandleHeatHaze` |
| Handheld tools | The tablet's atmosphere analyser and the plant analyser outdoors; the jetpack's low and critical propellant warnings | `AtmosAnalyser.PrepareText`, `PlantAnalyserCartridge.*`, `Jetpack.PropellantLow`, `PropellantCritical` |
| Things placed outdoors | Where no cell exists at the device: an outdoor gas sensor on a console or configuration cartridge, suit and laptop external readings through a logic transmitter, weather station spin, wind turbine blade speed and sound, pipe stress creak and tooltip, pipe leak particles, furnace over-pressure, portable air conditioner range, shower and toilet pressure warnings, egg too hot or too cold, a portable tank's release hiss | `GasSensor.*`, `SuitBase.GetLogicValue`, `WeatherStation.OnThreadUpdate`, `WindTurbineGenerator.GetWorldAtmospherePressure`, `AtmosphericsNetwork.EvaluateStressClient`, `PipeLeak.IsEmitting`, `FurnaceBase.IsOverpressure`, `DynamicAirConditioner.IsWithinOperatingRange`, `WaterDevice.IsMinimumWorldPressure`, `FertilizedEgg.GetExtendedText`, `DynamicGasCanister.OnAtmosphereClient` |
| Sound | Wind and storm sound cut out below 1 kPa, outdoor volume (pressure over 3 kPa), muffling, reverb, wall creak, breathing sounds and the helium voice with the helmet open | `AtmosphericAudioHandler.ManageWindAudio`, `GameAudioSource.CalculateAndSetAtmosphericVolume`, `ListenerEffectManager.ManagerUpdate`, `EnvironmentalAudioHandler.HandleReverb`, `Human.UpdateBreathingState` |
| Sea | The global sea is drawn, near and far, from their own planet: a sea the host made is missing and a sea the host drained is still there. The camera goes underwater, footsteps splash and bodies their game moves float by that sea | `GlobalAtmosphereLiquid.UpdateLiquid` (outside the `RunSimulation` block, `GameManager.cs:746`), `LiquidSolver.BuildGlobalRenderBatches`, `BuildDistantGlobalWater`, `GlobalAtmosphereLiquid.IsUnderGlobalLiquid` |
| Sky and views | The sky keeps the world's shipped look (the unmodded game never blends it: `AtmosphericScattering.cs:344`). Orbital view haze and the launch and re-entry plasma follow the shipped air | `AtmosphericScattering.ManagerUpdate`, `OrbitalViewController.SampleWorldAtmosphere`, `CrewModule.SamplePlasmaWorldAtmosphere` |
| Weather stations | If their own planet fills a cloud, their game schedules rain or snow locally: `ScheduleWeatherEvent` has no guard, and the host's weather sync overwrites the event and whether it is running but not `WeatherState` or the stations' scheduled flag. The weather that actually runs is the host's | `PAS.TickPlanetarySimulation` (`PAS.cs:335-361`), `WeatherManager.ScheduleWeatherEvent`, `DeserialiseDeltaState` (`WeatherManager.cs:661-680`) |
| Debug tools | `atmosphere global` and the world setting window show their own planet | `AtmosphereCommands.Execute`, `WorldSettingToolsImguiWindow.Draw` |

Three checks are made on the player's own game before it asks the host to act, and each compares a
canister with the air outside: jetpack thrust (`Jetpack.HasPropellent`, from `MovementController`),
whether the pneumatic drill is available (`PneumaticMiningDrill.IsOperable`), and whether the fire
extinguisher fires (`FireExtinguisher.UseExtinguisher`). A tool held by a player reads the same air
as the player, and a player standing still has a real cell, so these are usually right. Jetpack
thrust is the one used while moving; it matters only as a canister runs empty.

**UNVERIFIED, from code:** every state update carries this mod's section, and LaunchPadBooster logs
`Missing update suffix serializer for Unknown Mod <hash>` for a section no local mod claims
(`ModNetworking.ReadUpdateSuffix`). A player with StationeersLaunchPad but without this mod would
get that warning in `Player.log` with every update, 10 to 20 a second. Not seen in a real log.

### What is not affected

Decided by the host and sent: breathing, damage and oxygen (`LifeTicksTick`), every device tick
(`ThingAtmosphereTick`), every mix, fire, power, automation (`LogicStackTick`: chips read the host's
sensors), plants, wind turbine power, which weather runs, and the planet itself. The census has 167
sites that run only on the host. A joining player's game never takes from or gives to the host's
planet: its own takes and gives go to its own copy, which nothing sends back.

## A player with the mod

- **The host's planet from the first planet tick after joining**, refreshed every sync interval.
  Between refreshes their own game ticks the copy on its own (freezing, melting, cloud tips), which
  the next refresh overwrites.
- **Temperature.** The temperature rule runs on a joining player's game (`Climate.TemperaturePostfix`
  checks only `Settings.Enabled`), on the host's three response settings and storm bounds once the first
  planet state has arrived (`Sync.Apply`). `Climate.Lookup` rebuilds its per-world entry when those change, so the
  planet's temperature matches the host's. Before 0.10.1 it used the player's own config for them.
- **Sky: follows the host's air.** On a joining player's game `Gate.SkyEnabled()` asks every
  condition of `Enabled()` except the client check, plus `Gate.HostPlanet`: set when `Sync.Read` has
  loaded a planet state, cleared at every world start (`Patcher.WorldStartPostfix`). The game rebuilds
  the sky from `PAS.ReadOnlyGlobal`, which that player's own planet tick rebuilds from the synced
  planet (`AtmosphericsManager.ProcessAtmospheresClient`), and `Effective.DynamicSky` is the host's,
  sent with it. Until the host's planet arrives, and for good under a host without the mod, the sky
  keeps the shipped look. The sky throttle keys on `GameManager.GameTickCount`, which the tick loop
  advances on a joining player's game too (`GameManager.cs:849`, outside the `RunSimulation` blocks).
  Before 0.10.1 the sky never followed the air on any joining player's game.
- **Weather stations** behave as in the unmodded game: the mod's two weather hooks let everything
  through when `Gate.Enabled()` is false (`Guards.ScheduleWeatherPrefix`, `CanScheduleWeatherPrefix`).
- **Whose settings.** Everything the host's world file and config set runs on the host and reaches
  the player only as the synced planet. `SyncIntervalSeconds` is read where the section is written
  (`Sync.SerializeUpdateSuffix`), so only the host's counts. On the joining player's game, `Sync.Read`
  loads the host's planet and settings whatever that player's `Enabled` says (it checks only
  `IsClient`); their `Enabled` decides only whether the temperature rule and the sky run at all.
- **Trace gases gather on the host only.** The rule lives in the exchange between outdoor cells and
  the planet, which only the host runs (`GameManager.RunSimulation`), and it asks `Gate.Enabled()`,
  which is false on a joining player's game. Its two settings are not sent: nothing on a joining
  player's game reads them, and the cells it fills reach them with the rest of the game state.
- **Gas released in space is deleted on the host only, once.** With the host's world set to delete it,
  gas any player vents at or above 1,000 m is deleted by the host's mixing, which is the only mixing
  there is; the joining players' games never run it (`Gate.Enabled()` is false there, and
  `GameManager.RunSimulation` keeps the mixing off). They receive the smaller planet with the rest of it,
  and the host's setting and total for their readout. A dedicated server is the host.
- **Rocket engines burn on the host only.** Engines burn inside the `RunSimulation` block, and a
  joining player's game is sent each engine's force, exhaust speed and exhaust temperature
  (`RocketEngineBase.cs:703-735`), so the host's world setting decides how completely they burn.
  `Rockets.CombustionRate` asks `Gate.Enabled()`, false on a joining player's game, and the setting is
  not sent.
- **The planet's air burns on the host only.** The fire runs in the planet upkeep and the hold-back
  asks `Gate.Enabled()`, false on a joining player's game. Joining players are sent the tank and both
  stored heats with the planet state (`Sync`), so they see what the fire leaves and its heat wave; the
  setting and the fire heat figure are not sent, and their `terraform` says `planet fire: run by the host`.
- **`terraform`** reports `planet: off: client, planet comes from the host` with the synced figures;
  `size`, `set` and `reset` answer "Can only be run on the server".

## The report that prompted this (2026-09-24)

A player joined a dedicated server running the mod: Venus at planet size 0.5, 4 players. They
reported outside pressure near zero while standing and rising the moment they walked, and a gas mix
of CO2 200, HCl 90, N2 21.5 and pollutant 0.05 mol while walking.

- **MEASURED** from the host's autosaves: the planet held 8.47 mmol/L, 22 % of shipped Venus's
  38.94, and the temperature rule held it at 371.8 K. A cell at that density and temperature reads
  26 kPa; shipped Venus reads about 239 kPa at 737 K. 1,790 of the saved outdoor cells were within
  2 % of the host's density, all but one of them at 372 to 378 K.
- The mix they quoted is shipped Venus per 8000 L exactly: 1e9 mol CO2, 4.5e8 HCl, 1.075e8 N2 and
  2.5e5 pollutant over 5,000,000 cells. Walking, their game read its own planet; standing, it read
  the cell the host built under them.
- Their logs: one launch that loaded 43 mods but not this one, and a relaunch 84 s later with it
  (`Active:`, then `Planet tick is running. off: client, planet comes from the host`), after which
  the problem stopped. The mod had been switched on in StationeersLaunchPad but was not loaded until
  the game restarted.

This is also the only multiplayer session anyone has reported on. It shows sync working on a
dedicated server at least for readouts; it is a player's account, not a measurement.

## Evidence and its limits

- `tools/census/client.csv`: every non-trivial site in `ledger.csv` (312 of 363; the 51 marked
  `none` are name collisions), classified `server-only`, `client` (runs only on a joining player's
  game), `both`, `unreachable` or `unknown`, with what it reads there and a caller chain. Four
  parallel passes, each traced to a guard or a known entry point; none left `unknown`. A snapshot for
  game build 0.2.6428.27798, and `census.py --check` does not cover it, so re-run it after a game
  update that touches networking.

## Open

- **The per-update warning and the sync bandwidth** need a multiplayer session to measure.
- **Not seen on a second machine:** the sky and the host's settings reaching a joining player
  (0.10.1). Both are worked out from code only. `tools/Stage8` (bt only, written for 0.22.1 and not yet run)
  judges the planet sync, the host's settings and every comet path with a dedicated server and a rendered client
  on one machine.
- **The planet goes out about 20 times per sync window.** Harmless; sending once needs state the
  sync does not keep.
- **A joining player without the mod** (possible only beside another LaunchPadBooster networking mod)
  could be spared the per-update warning only by not riding the update suffix, for example a mod
  message sent to the players known to have the mod. LaunchPadBooster has `SendToClient`; whether it
  records which players have which mods was not checked.
