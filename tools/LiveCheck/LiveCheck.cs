using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.UI.ImGuiUi;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace TerraformingReloaded.LiveCheck
{
    /// <summary>
    /// Test driver, never shipped. Loaded beside the mod in a headless throwaway world, it:
    ///   1. unpauses the world (a headless new world pauses itself and waits for a player);
    ///   2. after a settling period, puts a known amount of CO2 into one outdoor cell;
    ///   3. logs, every few ticks and from the simulation thread at rest, the planet tank, the gas held in outdoor cells, and their sum.
    ///
    /// What it proves: with the mod live, the sum stays level while gas moves from the cell to the
    /// tank. A sum that climbs is duplication, a sum that falls is a leak.
    ///
    /// Only acts in batch mode, so it is inert if it is ever left in a normal mods folder.
    /// See tools/LiveCheck/run.ps1.
    /// </summary>
    [BepInPlugin("xceled.stationeers.terraformingreloaded.livecheck", "TR LiveCheck", "0.0.1")]
    public class LiveCheck : BaseUnityPlugin
    {
        private const double InjectMoles = 100000.0;
        private const double BuildOverMoles = 5000.0;
        private const float SettleSeconds = 20f;
        private const uint ReportEveryTicks = 5;

        // Set by run.ps1 through the environment, so one driver serves every scenario.
        private static readonly bool Inject_ = Environment.GetEnvironmentVariable("TR_LIVECHECK_INJECT") != "0";
        private static readonly uint SaveAfterTicks = uint.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_SAVE_AFTER_TICKS"), out uint t) ? t : 0u;

        private static readonly bool ResetScenario = Environment.GetEnvironmentVariable("TR_LIVECHECK_RESET") == "1";

        private static readonly string DumpPath = Environment.GetEnvironmentVariable("TR_LIVECHECK_DUMP");

        // Observe scenario: run the sky fast and log the planet through a whole day, optionally after
        // setting the planet's air. SETAIR is "Gas=molPerCell;Gas=molPerCell"; gases not named are emptied.
        private static readonly double DaySpeed = double.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_DAYSPEED"), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0.0;
        private static readonly string SetAir = Environment.GetEnvironmentVariable("TR_LIVECHECK_SETAIR");
        private static readonly bool Observe = Environment.GetEnvironmentVariable("TR_LIVECHECK_OBSERVE") == "1";
        private bool _observeSetUp;

        // A second air, set later in the run without touching clouds or ice caps: freeze a gas out with
        // the first, warm the planet with the second, and watch what comes back.
        private static readonly string SetAir2 = Environment.GetEnvironmentVariable("TR_LIVECHECK_SETAIR2");
        private static readonly uint SetAir2Tick = uint.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_SETAIR2_TICK"), out uint t2) ? t2 : 0u;
        private bool _air2Set;

        // HEATK holds the planet's banked outside heat at this many kelvin, as a base venting warm gas
        // without pause would: written every planet tick, so the mod's fade and cap still act on it.
        private static readonly double HeatK = double.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_HEATK"), NumberStyles.Float, CultureInfo.InvariantCulture, out double h) ? h : 0.0;

        // A weather event forced on at a chosen tick, so a storm can be watched without waiting days
        // for the game to schedule one. STORM is an Id from the game's weather data.
        private static readonly string Storm = Environment.GetEnvironmentVariable("TR_LIVECHECK_STORM");
        private static readonly uint StormTick = uint.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_STORM_TICK"), out uint s) ? s : 0u;
        private bool _stormStarted;

        // The wall vent fix, tested by calling the hook body itself: LiveCheck cannot build a vent.
        private static readonly uint WallVentTick = uint.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_WALLVENT_TICK"), out uint w) ? w : 0u;
        private bool _wallVentDone;

        // Building into an occupied outdoor cell (D2). Staged over several ticks: make two cells,
        // wait for the simulation to link them as open neighbours, hand the grid to the game's own
        // entry point for a structure filling it, then read the total once the event has been applied.
        private static readonly uint BuildOverTick = uint.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_BUILDOVER_TICK"), out uint bo) ? bo : 0u;
        // The counterfactual: the same run with the mod's guard taken off, which must show the
        // duplicate. Unmodded is no control here, because unmodded the planet discards what it is
        // given, so the defect costs nothing until the mod switches the planet simulation on.
        private static readonly bool BuildOverUnguard = Environment.GetEnvironmentVariable("TR_LIVECHECK_BUILDOVER_UNGUARD") == "1";
        private int _buildOverStage;
        private uint _buildOverAt;
        private WorldGrid _buildOverTarget;
        private WorldGrid _buildOverNeighbour;
        private int _buildOverOpen;
        private double _buildOverCellMoles;
        private double _buildOverBefore;
        private int _buildOverCellsBefore;

        // A cloud bucket filling while other weather is already running (D6). Main thread: forcing a
        // weather event on is a main thread call, and what this reads is weather state, not moles.
        // WEATHER_EVENT is the event to have running when the bucket fills; snow is the case that
        // reaches the defect, because the tick's own guard only steps aside for a storm or for rain.
        private static readonly uint WeatherTick = uint.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_WEATHER_TICK"), out uint wt) ? wt : 0u;
        private static readonly string WeatherEventId = Environment.GetEnvironmentVariable("TR_LIVECHECK_WEATHER_EVENT");
        private int _weatherStage;
        private uint _weatherAt;
        private string _weatherIdBefore;
        private float _weatherLengthBefore;
        private double _weatherCloudsVolume;

        // The mix the new-game menu builds to describe a world, which must not carry the planet size.
        private static readonly bool MenuMix = Environment.GetEnvironmentVariable("TR_LIVECHECK_MENUMIX") == "1";
        private bool _menuMixDone;

        // terraform size <share> confirm on the planet being played. RESCALE_BY is how many times its
        // present size to ask for, measured off the planet itself so the share asked for can never be
        // the share it already is, which would make the scenario a no-op. RESCALE is an absolute share
        // instead, for asking a particular question.
        private static readonly string RescaleTo = Environment.GetEnvironmentVariable("TR_LIVECHECK_RESCALE");
        private static readonly double RescaleBy = double.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_RESCALE_BY"), NumberStyles.Float, CultureInfo.InvariantCulture, out double rb) ? rb : 0.0;
        private static readonly uint RescaleTick = uint.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_RESCALE_TICK"), out uint rs) ? rs : 0u;
        private static bool RescaleWanted => !string.IsNullOrEmpty(RescaleTo) || RescaleBy > 0.0;
        private bool _rescaleDone;

        // After the measured rescale, the same command again and again from the main thread while the
        // planet ticks on its own thread. A rescale that did not hold the tank lock would be free to
        // tear a tick in half here; an even number of them, alternating, ends back at the share the
        // measured rescale asked for.
        private const int HammerRescales = 40;
        private int _hammered;
        private bool _rescaleReported;
        private string _shareAsked;
        private string _shareBefore;

        private bool _dumped;
        private bool _commandsRun;
        private uint _dirtiedAtTick;
        private bool _dirtied;
        private bool _resetDone;
        private uint _injectedAtTick;
        private bool _saved;
        private float _worldSeenAt = -1f;
        private bool _unpaused;
        private bool _injected;
        private bool _failed;

        private static LiveCheck _instance;

        private void Awake()
        {
            _instance = this;
            // Sample at the top of the planet tick: it runs on the simulation thread after the
            // previous tick's atmosphere workers have been joined and before this tick's start, so
            // every cell and the tank are read at rest and uncached. Off this thread a mole reports a
            // quantity cached at the start of the tick, so sampling from Update is a tick stale.
            new Harmony("xceled.stationeers.terraformingreloaded.livecheck").Patch(
                AccessTools.DeclaredMethod(typeof(PlanetaryAtmosphereSimulation), "TickPlanetarySimulation"),
                prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(LiveCheck), nameof(TickPrefix))));
        }

        private static void TickPrefix()
        {
            if (HeatK != 0.0 && _instance != null && _instance._observeSetUp)
            {
                PlanetaryAtmosphereSimulation.ExternalInputEnergyOffset =
                    new MoleEnergy(HeatK * PlanetaryAtmosphereSimulation.GetHeatCapacity().ToDouble());
            }
            // On this thread and at this point a mole reports its live quantity and the tick's workers
            // are joined, so tank and cells can be summed either side of the call and compared.
            if (_instance != null && !_instance._wallVentDone && WallVentTick > 0 && GameManager.GameTickCount >= WallVentTick)
            {
                _instance._wallVentDone = true;
                _instance.CheckWallVent();
            }
            if (_instance != null && BuildOverTick > 0 && _instance._buildOverStage < 3 && GameManager.GameTickCount >= BuildOverTick)
            {
                _instance.CheckBuildOver();
            }
            if (_instance != null && !_instance._menuMixDone && MenuMix && GameManager.GameTickCount >= 20)
            {
                _instance._menuMixDone = true;
                _instance.ReportMenuMix();
            }
            if (_instance == null || _instance._failed || GameManager.GameTickCount % ReportEveryTicks != 0)
            {
                return;
            }
            try
            {
                _instance.Report();
            }
            catch (Exception e)
            {
                _instance._failed = true;
                _instance.Logger.LogError("LiveCheck report stopped: " + e);
            }
        }

        private void Update()
        {
            if (_failed || !GameManager.IsBatchMode || GameManager.GameState != GameState.Running)
            {
                return;
            }
            try
            {
                Step();
            }
            catch (Exception e)
            {
                _failed = true;
                Logger.LogError("LiveCheck stopped: " + e);
            }
        }

        private void Step()
        {
            float now = Time.unscaledTime;
            if (_worldSeenAt < 0f)
            {
                _worldSeenAt = now;
            }
            if (!_unpaused && WorldManager.IsGamePaused)
            {
                WorldManager.SetGamePause(pauseGame: false);
                _unpaused = true;
                Logger.LogInfo("LiveCheck: world unpaused");
            }
            if (Observe && !_observeSetUp && GameManager.GameTickCount > 5)
            {
                _observeSetUp = true;
                SetUpObserve();
            }
            if (Observe && !_air2Set && !string.IsNullOrEmpty(SetAir2) && SetAir2Tick > 0 && GameManager.GameTickCount >= SetAir2Tick)
            {
                _air2Set = true;
                SetPlanetAir(SetAir2, keepOthers: true);
            }
            if (!_stormStarted && !string.IsNullOrEmpty(Storm) && StormTick > 0 && GameManager.GameTickCount >= StormTick)
            {
                _stormStarted = true;
                StartStorm();
            }
            if (WeatherTick > 0 && _weatherStage < 2 && GameManager.GameTickCount >= WeatherTick)
            {
                CheckWeather();
            }
            // From Update, which is the main thread: a console command reaches the planet from there,
            // and the planet tick runs on another thread, so this is the only arrangement in which a
            // rescale that forgot the tank lock could show.
            if (!_rescaleDone && RescaleWanted && RescaleTick > 0 && GameManager.GameTickCount >= RescaleTick)
            {
                _rescaleDone = true;
                CheckRescale();
            }
            if (_rescaleDone && !_rescaleReported && !string.IsNullOrEmpty(_shareAsked))
            {
                if (_hammered < HammerRescales)
                {
                    _hammered++;
                    RunCommand("size", _hammered % 2 == 1 ? _shareBefore : _shareAsked, "confirm");
                }
                else
                {
                    _rescaleReported = true;
                    RunCommand("status");
                    Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                        "LiveCheck: rescale done | from the main thread {0} more times | tick {1}", _hammered, GameManager.GameTickCount));
                }
            }
            if (!_injected && Inject_ && now - _worldSeenAt > SettleSeconds && GameManager.GameTickCount > 10)
            {
                Inject();
                _injected = true;
                _injectedAtTick = GameManager.GameTickCount;
            }
            if (!_dumped && !string.IsNullOrEmpty(DumpPath) && GameManager.GameTickCount > 5)
            {
                _dumped = true;
                DumpGameData(DumpPath);
                Logger.LogInfo("LiveCheck: dumped game data to " + DumpPath);
            }
            if (!_commandsRun && GameManager.GameTickCount > 5)
            {
                _commandsRun = true;
                RunCommand("curves", "export");
                RunCommand("curves", "reload");
                RunCommand("status");
            }
            if (ResetScenario)
            {
                ResetSteps();
                return;
            }
            // Save while the injected gas is still spread over thousands of outdoor cells: that is
            // the state in which a load that wrongly debits the tank per saved cell loses the most.
            if (_injected && !_saved && SaveAfterTicks > 0 && GameManager.GameTickCount >= _injectedAtTick + SaveAfterTicks)
            {
                _saved = true;
                Logger.LogInfo("LiveCheck: saving at tick " + GameManager.GameTickCount);
                Util.Commands.CommandLine.Process("file save");
            }
        }

        /// <summary>
        /// Reset scenario: let the injected gas drain into the planet, then also load the ice caps,
        /// the clouds and both heat stores, so every part of the saved planet state is off stock.
        /// Run the mod's reset, save. run.ps1 then loads that save without the mod.
        /// </summary>
        private void ResetSteps()
        {
            uint tick = GameManager.GameTickCount;
            if (_injected && !_dirtied && tick >= _injectedAtTick + 80)
            {
                Dirty();
                _dirtied = true;
                _dirtiedAtTick = tick;
                Logger.LogInfo("LiveCheck: dirtied ice caps, clouds and heat stores at tick " + tick);
            }
            if (_dirtied && !_resetDone && tick >= _dirtiedAtTick + 10)
            {
                _resetDone = true;
                Logger.LogInfo("LiveCheck: running terraform reset confirm at tick " + tick);
                Util.Commands.CommandLine.Process("terraform reset confirm");
            }
            if (_resetDone && !_saved && tick >= _dirtiedAtTick + 25)
            {
                _saved = true;
                Logger.LogInfo("LiveCheck: saving at tick " + tick);
                Util.Commands.CommandLine.Process("file save");
            }
        }

        private void SetPlanetAir(string spec, bool keepOthers)
        {
            object gate = AccessTools.Field(typeof(PlanetaryAtmosphereSimulation), "GlobalInteraction").GetValue(null);
            lock (gate)
            {
                GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
                double cells = (tank.Volume / Chemistry.GridVolume).ToDouble();
                if (!keepOthers)
                {
                    tank.ClearQuantities(AtmosphereHelper.MatterState.All);
                }
                foreach (string part in spec.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] pair = part.Split('=');
                    Chemistry.GasType type = (Chemistry.GasType)Enum.Parse(typeof(Chemistry.GasType), pair[0].Trim());
                    tank.Set(new MoleQuantity(double.Parse(pair[1], CultureInfo.InvariantCulture) * cells), type);
                }
            }
            Logger.LogInfo("LiveCheck: planet air set to " + spec + " (mol per outdoor cell" + (keepOthers ? ", other gases kept" : "") + ") at tick " + GameManager.GameTickCount);
        }

        private void SetUpObserve()
        {
            if (!string.IsNullOrEmpty(SetAir))
            {
                SetPlanetAir(SetAir, keepOthers: false);
            }
            if (DaySpeed > 0.0)
            {
                OrbitalSimulation.SetTimeScale((float)(OrbitalSimulation.System.TimeScale * DaySpeed));
                Logger.LogInfo(string.Format(CultureInfo.InvariantCulture, "LiveCheck: sky running {0}x, timescale {1}", DaySpeed, OrbitalSimulation.System.TimeScale));
            }
        }

        /// <summary>
        /// Forces a weather event on by Id. The game only schedules one after a cooldown measured in
        /// days, which no headless run is long enough to reach, so a storm is otherwise untestable.
        /// </summary>
        private void StartStorm()
        {
            Weather.WeatherManager.ImmediatelyActivateWeatherEvent(Storm);
            WeatherEvent running = Weather.WeatherManager.CurrentWeatherEvent;
            Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "LiveCheck: storm {0} asked for at tick {1}: event {2}, running {3}, day offset {4:0.##} K, night offset {5:0.##} K",
                Storm, GameManager.GameTickCount, running?.Id ?? "(none)", Weather.WeatherManager.IsWeatherEventRunning,
                running?.TemperatureOffset?.GetOffset(0f) ?? 0f, running?.TemperatureOffset?.GetOffset(180f) ?? 0f));
        }

        /// <summary>
        /// The mix the new-game menu builds to describe a world. It is not the planet being played, so
        /// the planet size must not apply to it: the menu divides its moles by the world's unscaled
        /// volume to show a pressure (DEFECTS D16). Called from the planet tick, which is outside
        /// planet building and is where the live figure can be summed at rest: outdoor cells have
        /// already drawn their share by now, so the tank on its own is short by what they hold.
        /// </summary>
        private void ReportMenuMix()
        {
            GlobalAtmosphereData data = WorldSetting.Current?.Data?.GlobalAtmosphereData;
            GlobalGasMix live = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            if (data == null || live == null)
            {
                Logger.LogInfo("LiveCheck: menumix no world loaded");
                return;
            }
            GlobalGasMix menu = GlobalGasMix.Create(data);
            double total = TankPlusCells(out int cells);
            Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "LiveCheck: menumix world {0} | menu {1:0.000} mol in {2:0.000} L | live {3:0.000} mol in {4:0.000} L | {5} outdoor cells | shipped volume {6:0.000} L",
                WorldSetting.Current.Id, menu.TotalQuantity().ToDouble(), menu.Volume.ToDouble(),
                total, live.Volume.ToDouble(), cells, data.GetVolume().ToDouble()));
        }

        /// <summary>
        /// The wall vent fix (DEFECTS D15), tested by calling the hook body itself: a headless run has
        /// no way to build a vent. A wall vent hands its two grids to the prefix; with a real cell on
        /// one side and none on the other the prefix must build one, and building it draws its air from
        /// the planet, so tank plus cells must not move.
        /// </summary>
        private void CheckWallVent()
        {
            try
            {
                Type guards = AccessTools.TypeByName("TerraformingReloaded.Patching.Guards");
                System.Reflection.MethodInfo prefix = guards == null ? null : AccessTools.DeclaredMethod(guards, "WallVentPrefix");
                if (prefix == null)
                {
                    Logger.LogInfo("LiveCheck: wallvent FAIL the mod's Guards.WallVentPrefix was not found");
                    return;
                }

                // Open sky, well above the terrain and below the height the game treats as space.
                WorldGrid roomSide = new WorldGrid(new Vector3(0f, 300f, 0f));
                if (AtmosphericsManager.CloneGlobalAtmosphereThreadSafe(roomSide) == null)
                {
                    Logger.LogInfo("LiveCheck: wallvent FAIL no cell could be made at " + Where(roomSide));
                    return;
                }
                WorldGrid outSide = WorldGrid.INVALID;
                foreach (Vector3 step in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back, Vector3.up })
                {
                    WorldGrid candidate = new WorldGrid(roomSide.Value.x + (int)step.x, roomSide.Value.y + (int)step.y, roomSide.Value.z + (int)step.z);
                    if (AtmosphericsManager.Find(candidate) == null)
                    {
                        outSide = candidate;
                        break;
                    }
                }
                if (outSide == WorldGrid.INVALID)
                {
                    Logger.LogInfo("LiveCheck: wallvent FAIL every grid next to " + Where(roomSide) + " already has a cell");
                    return;
                }

                double before = TankPlusCells(out int cellsBefore);
                prefix.Invoke(null, new object[] { roomSide, outSide });
                bool built = AtmosphericsManager.Find(outSide) != null;
                double after = TankPlusCells(out int cellsAfter);
                bool conserved = Math.Abs(after - before) <= 1.0;
                Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "LiveCheck: wallvent {0} built a cell at {1} next to {2}: cells {3} -> {4}, tank plus cells {5:0.000} -> {6:0.000} mol ({7:+0.000;-0.000})",
                    built && conserved ? "PASS" : "FAIL", Where(outSide), Where(roomSide), cellsBefore, cellsAfter, before, after, after - before));
            }
            catch (Exception e)
            {
                Logger.LogInfo("LiveCheck: wallvent FAIL " + e);
            }
        }

        /// <summary>
        /// Building into an occupied outdoor cell (DEFECTS D2). The game copies the cell's gas into
        /// its open neighbours and then removes the cell, and removing a world cell hands its mixture
        /// to the planet as well, so the same gas is counted twice. A headless run cannot build a
        /// structure, but what a structure calls is public: AtmosphericEventInstance.StructureBlockingGrid.
        /// Three passes, because the simulation has to link the cells and the event is queued.
        /// Simulation thread, at the top of the planet tick, so the tank and the cells are read at rest.
        /// </summary>
        private void CheckBuildOver()
        {
            try
            {
                if (_buildOverStage == 0)
                {
                    // Open sky, well above the terrain and below the height the game treats as space.
                    _buildOverTarget = new WorldGrid(new Vector3(0f, 300f, 0f));
                    _buildOverNeighbour = new WorldGrid(_buildOverTarget.Value.x + 1, _buildOverTarget.Value.y, _buildOverTarget.Value.z);
                    Atmosphere target = AtmosphericsManager.CloneGlobalAtmosphereThreadSafe(_buildOverTarget);
                    Atmosphere neighbour = AtmosphericsManager.CloneGlobalAtmosphereThreadSafe(_buildOverNeighbour);
                    if (target == null || neighbour == null)
                    {
                        Logger.LogInfo("LiveCheck: buildover FAIL no pair of cells could be made at " + Where(_buildOverTarget));
                        _buildOverStage = 3;
                        return;
                    }
                    // A world cell holding what the planet holds is culled on the next tick
                    // (Atmosphere.IsLive), so both cells are given gas to hold them open. That gas is
                    // also what makes a duplicated cell stand out against the planet total.
                    AddCarbonDioxide(target, BuildOverMoles);
                    AddCarbonDioxide(neighbour, BuildOverMoles);
                    _buildOverAt = GameManager.GameTickCount;
                    _buildOverStage = 1;
                    Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                        "LiveCheck: buildover cells made at {0} and {1} with {2:0} mol CO2 each, waiting for the simulation to link them",
                        Where(_buildOverTarget), Where(_buildOverNeighbour), BuildOverMoles));
                    return;
                }

                if (_buildOverStage == 1)
                {
                    if (GameManager.GameTickCount < _buildOverAt + 15)
                    {
                        return;
                    }
                    Atmosphere target = AtmosphericsManager.Find(_buildOverTarget);
                    if (target == null)
                    {
                        Logger.LogInfo("LiveCheck: buildover FAIL the cell at " + Where(_buildOverTarget) + " went away before anything was built over it");
                        _buildOverStage = 3;
                        return;
                    }
                    lock (target.OpenNeighbors)
                    {
                        _buildOverOpen = target.OpenNeighbors.Count;
                    }
                    // Most of what was put in has spread to the neighbours and drained to the planet
                    // by now, which would leave only a few moles to duplicate. Top the cell up so the
                    // gas a defect would copy is far larger than anything rounding can account for.
                    AddCarbonDioxide(target, BuildOverMoles);
                    _buildOverCellMoles = target.GasMixture.GetTotalMolesGassesAndLiquids.ToDouble();
                    if (_buildOverOpen == 0)
                    {
                        Logger.LogInfo("LiveCheck: buildover FAIL the cell at " + Where(_buildOverTarget)
                            + " has no open neighbour, and an open neighbour is what the defect needs");
                        _buildOverStage = 3;
                        return;
                    }
                    if (_buildOverCellMoles < BuildOverMoles * 0.5)
                    {
                        Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                            "LiveCheck: buildover FAIL the cell holds {0:0.000} mol, too little to tell a duplicate from rounding", _buildOverCellMoles));
                        _buildOverStage = 3;
                        return;
                    }
                    if (BuildOverUnguard && !TakeOffDeregisterGuard())
                    {
                        _buildOverStage = 3;
                        return;
                    }
                    _buildOverBefore = TankPlusCells(out _buildOverCellsBefore);
                    AtmosphericEventInstance.StructureBlockingGrid(_buildOverTarget);
                    _buildOverAt = GameManager.GameTickCount;
                    _buildOverStage = 2;
                    return;
                }

                if (GameManager.GameTickCount < _buildOverAt + 15)
                {
                    return;
                }
                _buildOverStage = 3;
                // Whether a cell is at that grid afterwards says nothing: the neighbours the divide
                // filled push gas straight back, and the game rebuilds a cell there. Conservation is
                // the judgement, and the unguarded run is what proves the event ran at all.
                bool removed = AtmosphericsManager.Find(_buildOverTarget) == null;
                double after = TankPlusCells(out int cellsAfter);
                double moved = after - _buildOverBefore;
                bool conserved = Math.Abs(moved) <= 1.0;
                Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "LiveCheck: buildover {0} cell at {1} held {2:0.000} mol with {3} open neighbour(s), guard {4}, gone afterwards {5}: cells {6} -> {7}, tank plus cells {8:0.000} -> {9:0.000} mol ({10:+0.000;-0.000})",
                    conserved ? "PASS" : "FAIL", Where(_buildOverTarget), _buildOverCellMoles, _buildOverOpen,
                    BuildOverUnguard ? "off" : "on", removed,
                    _buildOverCellsBefore, cellsAfter, _buildOverBefore, after, moved));
            }
            catch (Exception e)
            {
                _buildOverStage = 3;
                Logger.LogInfo("LiveCheck: buildover FAIL " + e);
            }
        }

        /// <summary>
        /// A cloud bucket filling while other weather is already running (DEFECTS D6). When the liquid
        /// clouds fill, the planet tick gives them back to the air and then schedules rain without
        /// asking what the weather is doing, and ScheduleWeatherEvent overwrites the running event,
        /// its start time and its length. Snow is the case that reaches it: the tick's own guard only
        /// steps aside for a storm or for rain, which leaves its snow branch unreachable.
        /// Main thread, which is where a weather event can be forced on.
        /// </summary>
        private void CheckWeather()
        {
            try
            {
                GlobalGasMix clouds = (GlobalGasMix)AccessTools.Field(typeof(PlanetaryAtmosphereSimulation), "_liquidClouds").GetValue(null);
                if (_weatherStage == 0)
                {
                    string wanted = string.IsNullOrEmpty(WeatherEventId) ? "Snow" : WeatherEventId;
                    Weather.WeatherManager.ImmediatelyActivateWeatherEvent(wanted);
                    WeatherEvent running = Weather.WeatherManager.CurrentWeatherEvent;
                    if (running == null || !Weather.WeatherManager.IsWeatherEventRunning)
                    {
                        Logger.LogInfo("LiveCheck: weather FAIL " + wanted + " would not start, so there is no running event for a cloud to overwrite");
                        _weatherStage = 2;
                        return;
                    }
                    _weatherIdBefore = running.Id;
                    _weatherLengthBefore = Weather.WeatherManager.WeatherEventLength;

                    double filled;
                    object gate = AccessTools.Field(typeof(PlanetaryAtmosphereSimulation), "GlobalInteraction").GetValue(null);
                    lock (gate)
                    {
                        _weatherCloudsVolume = clouds.Volume.ToDouble();
                        clouds.ClearQuantities(AtmosphereHelper.MatterState.All);
                        // Measure what a mole of it takes up rather than assuming, then overfill.
                        clouds.Set(new MoleQuantity(1000.0), Chemistry.GasType.LiquidNitrogen);
                        double litresPerMole = clouds.VolumeOfLiquid().ToDouble() / 1000.0;
                        if (litresPerMole > 0.0)
                        {
                            clouds.Set(new MoleQuantity(_weatherCloudsVolume / litresPerMole * 1.2), Chemistry.GasType.LiquidNitrogen);
                        }
                        filled = clouds.VolumeOfLiquid().ToDouble();
                    }
                    if (filled < _weatherCloudsVolume)
                    {
                        Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                            "LiveCheck: weather FAIL the liquid cloud bucket would not fill: {0:0} of {1:0} L", filled, _weatherCloudsVolume));
                        _weatherStage = 2;
                        return;
                    }
                    Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                        "LiveCheck: weather {0} running at tick {1}, length {2:0.##}, world has weather {3}, liquid clouds filled to {4:0} of {5:0} L",
                        _weatherIdBefore, GameManager.GameTickCount, _weatherLengthBefore, Weather.WeatherManager.WorldHasWeather, filled, _weatherCloudsVolume));
                    _weatherAt = GameManager.GameTickCount;
                    _weatherStage = 1;
                    return;
                }

                if (GameManager.GameTickCount < _weatherAt + 4)
                {
                    return;
                }
                _weatherStage = 2;
                WeatherEvent now = Weather.WeatherManager.CurrentWeatherEvent;
                string nowId = now == null ? "(none)" : now.Id;
                bool stillRunning = Weather.WeatherManager.IsWeatherEventRunning;
                double left = clouds.VolumeOfLiquid().ToDouble();
                // The tick empties the bucket into the air before it schedules anything, so an empty
                // bucket is what proves the branch ran at all and the check is not vacuous.
                bool emptied = left < _weatherCloudsVolume * 0.5;
                bool kept = nowId == _weatherIdBefore && stillRunning;
                Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "LiveCheck: weather {0} bucket emptied {1} ({2:0} L left of {3:0}), event {4} -> {5}, running {6}, length {7:0.##} -> {8:0.##}",
                    emptied && kept ? "PASS" : "FAIL", emptied, left, _weatherCloudsVolume, _weatherIdBefore, nowId, stillRunning,
                    _weatherLengthBefore, Weather.WeatherManager.WeatherEventLength));
            }
            catch (Exception e)
            {
                _weatherStage = 2;
                Logger.LogInfo("LiveCheck: weather FAIL " + e);
            }
        }

        /// <summary>
        /// terraform size &lt;share&gt; confirm on a live planet, run from the main thread while the
        /// planet ticks on its own: that is how the command reaches the planet in a real game, and a
        /// rescale crossing threads is the hazard worth testing.
        ///
        /// The share asked for is measured off the planet itself (RESCALE_BY times its present size),
        /// so it cannot be the share the planet already is, which the command would rightly refuse.
        ///
        /// The two sets of figures are read under the tank lock along with the command, because a tick
        /// in between melts ice caps into the planet and the two readings would then differ by more
        /// than the rescale. The unlocked path is exercised straight afterwards, by the hammering in
        /// Step: forty more rescales from this thread with nothing held.
        ///
        /// The ice caps, the clouds and both heat stores are loaded first (Dirty), or the parts of the
        /// planet that are not the tank would all be zero and prove nothing. The refusals are asked
        /// for first: they must answer, not throw, and must not rescale.
        /// </summary>
        private void CheckRescale()
        {
            try
            {
                Dirty();
                double shipped = WorldSetting.Current.Data.GlobalAtmosphereData.GetVolume().ToDouble();
                double now = PlanetaryAtmosphereSimulation.GetGlobalGasMix().Volume.ToDouble() / shipped;
                double target = RescaleBy > 0.0 ? now * RescaleBy : double.Parse(RescaleTo, CultureInfo.InvariantCulture);
                _shareBefore = now.ToString("0.##########", CultureInfo.InvariantCulture);
                _shareAsked = target.ToString("0.##########", CultureInfo.InvariantCulture);
                Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "LiveCheck: rescale asking for {0} of shipped at tick {1} | planet is {2} | factor {3:0.##########}",
                    _shareAsked, GameManager.GameTickCount, _shareBefore, target / now));

                RunCommand("size", "0", "confirm");
                RunCommand("size", "banana", "confirm");
                RunCommand("size", "1000", "confirm");
                RunCommand("size", _shareAsked);            // no confirm: must explain and ask

                object gate = AccessTools.Field(typeof(PlanetaryAtmosphereSimulation), "GlobalInteraction").GetValue(null);
                string before;
                string after;
                lock (gate)
                {
                    before = RescaleFigures();
                    RunCommand("size", _shareAsked, "confirm");
                    after = RescaleFigures();
                }
                Logger.LogInfo("LiveCheck: rescale before " + before);
                Logger.LogInfo("LiveCheck: rescale after " + after);
                RunCommand("status");
            }
            catch (Exception e)
            {
                _rescaleReported = true;
                Logger.LogInfo("LiveCheck: rescale FAIL " + e);
                Logger.LogInfo("LiveCheck: rescale done | failed before it could ask");
            }
        }

        /// <summary>
        /// Everything a rescale is allowed to move and everything it is not, in one line. The pressure
        /// is computed from the tank, not read from the game's per-tick planet readout, so that two
        /// samples taken inside one tick are two answers rather than one reading printed twice.
        /// </summary>
        private static string RescaleFigures()
        {
            Type sim = typeof(PlanetaryAtmosphereSimulation);
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            GlobalGasMix caps = (GlobalGasMix)AccessTools.Field(sim, "_iceCaps").GetValue(null);
            GlobalGasMix ice = (GlobalGasMix)AccessTools.Field(sim, "_iceClouds").GetValue(null);
            GlobalGasMix liquid = (GlobalGasMix)AccessTools.Field(sim, "_liquidClouds").GetValue(null);
            double cells = (tank.Volume / Chemistry.GridVolume).ToDouble();
            System.Text.StringBuilder gases = new System.Text.StringBuilder();
            foreach (Chemistry.GasType type in (Chemistry.GasType[])Enum.GetValues(typeof(Chemistry.GasType)))
            {
                double moles = Mole.MatterState(type) == AtmosphereHelper.MatterState.None ? 0.0 : tank.Get(type).ToDouble();
                if (moles > 0.0)
                {
                    gases.AppendFormat(CultureInfo.InvariantCulture, " {0}={1:0.000000}", type, moles / cells);
                }
            }
            // Six decimals, not three: these are compared either side of a rescale against a factor,
            // and on a planet shrunk to a third of its size three decimals is coarser than the
            // agreement being asked for.
            return string.Format(CultureInfo.InvariantCulture,
                "volume {0:0.000000} | mol {1:0.000000} | cells {2:0.000000} | P {3:0.00000} | caps {4:0.000000} | clouds {5:0.000000} | capsVolume {6:0.000000} | latentK {7:0.00000} | extK {8:0.00000} | gases{9}",
                tank.Volume.ToDouble(), tank.TotalQuantity().ToDouble(), cells,
                IdealGas.Pressure(tank.TotalQuantityGas(), PlanetaryAtmosphereSimulation.AggregateTemperature, tank.VolumeForGas()).ToDouble(),
                caps.TotalQuantity().ToDouble(), liquid.TotalQuantity().ToDouble() + ice.TotalQuantity().ToDouble(),
                caps.Volume.ToDouble(),
                PlanetaryAtmosphereSimulation.GetLatentTemperatureOffset().ToDouble(),
                PlanetaryAtmosphereSimulation.GetExternalInputEnergyOffset().ToDouble(),
                gases);
        }

        /// <summary>A grid as coordinates; WorldGrid itself prints as its type name.</summary>
        private static string Where(WorldGrid grid)
        {
            return string.Format(CultureInfo.InvariantCulture, "({0},{1},{2})", grid.Value.x, grid.Value.y, grid.Value.z);
        }

        /// <summary>The planet plus every outdoor cell, read on the simulation thread and at rest.</summary>
        private static double TankPlusCells(out int cells)
        {
            double total = PlanetaryAtmosphereSimulation.GetGlobalGasMix()?.TotalQuantity().ToDouble() ?? 0.0;
            double inCells = 0.0;
            int count = 0;
            AtmosphericsManager.AllAtmospheres.ForEach((Action<Atmosphere>)(a =>
            {
                if (a != null && a.Mode == AtmosphereHelper.AtmosphereMode.World)
                {
                    inCells += a.GasMixture.GetTotalMolesGassesAndLiquids.ToDouble();
                    count++;
                }
            }));
            cells = count;
            return total + inCells;
        }

        private static void Dirty()
        {
            Type sim = typeof(PlanetaryAtmosphereSimulation);
            object gate = AccessTools.Field(sim, "GlobalInteraction").GetValue(null);
            lock (gate)
            {
                // The game's own phase change will start moving these straight away (ice caps melt into
                // the planet, a thousand moles a tick). That is fine: the point is only that every part
                // of the saved planet state is off stock when the reset runs.
                ((GlobalGasMix)AccessTools.Field(sim, "_iceCaps").GetValue(null)).Set(new MoleQuantity(5000.0), Chemistry.GasType.LiquidNitrogen);
                ((GlobalGasMix)AccessTools.Field(sim, "_liquidClouds").GetValue(null)).Set(new MoleQuantity(300.0), Chemistry.GasType.LiquidNitrogen);
                ((GlobalGasMix)AccessTools.Field(sim, "_iceClouds").GetValue(null)).Set(new MoleQuantity(200.0), Chemistry.GasType.LiquidNitrogen);
                PlanetaryAtmosphereSimulation.LatentEnergyOffset = new MoleEnergy(2.0e9);
                PlanetaryAtmosphereSimulation.ExternalInputEnergyOffset = new MoleEnergy(3.0e9);
            }
        }

        // ---- game data dump for tools/Balance ---------------------------------------------------------

        /// <summary>
        /// Writes what a balance model needs, evaluated by the game itself rather than re-derived:
        /// every greenhouse curve, every world's temperature curves, starting air and volume, and the
        /// per-gas constants. Unity's weighted-tangent curves are not worth re-implementing when the
        /// real evaluator is right here.
        /// </summary>
        private static void DumpGameData(string path)
        {
            var gases = ((Chemistry.GasType[])Enum.GetValues(typeof(Chemistry.GasType)))
                .Where(g => Mole.MatterState(g) != AtmosphereHelper.MatterState.None).ToArray();

            double[] density = Grid(0.0, 0.02, 0.2, 0.05, 2.0, 0.25, 10.0, 1.0, 60.0, 5.0, 300.0);
            double[] index = Grid(-300.0, 5.0, -20.0, 1.0, 20.0, 5.0, 400.0);
            // Every degree, not every five: a base curve can drop 11 K per degree around dusk, where
            // interpolating between five-degree samples was 5 K out, and a storm's share term turns
            // that into a kelvin of apparent disagreement with the game (tools/Balance/compare.py).
            double[] angle = Grid(0.0, 1.0, 180.0);
            double[] percent = Grid(0.0, 5.0, 100.0);
            double[] pressure = Grid(0.0, 0.5, 10.0, 2.0, 60.0, 10.0, 400.0, 100.0, 7000.0);

            var root = new Dictionary<string, object>
            {
                ["gameVersion"] = GameManager.GetGameVersion(),
                ["gridVolumeLitres"] = Chemistry.GridVolume.ToDouble(),
                ["tickSeconds"] = GameManager.GameTickSpeedSeconds,
                ["currentWorld"] = WorldSetting.Current?.Id,
                ["currentSolarIrradiance"] = OrbitalSimulation.SolarIrradiance,
                ["gases"] = gases.ToDictionary(g => g.ToString(), g => (object)new Dictionary<string, object>
                {
                    ["state"] = Mole.MatterState(g).ToString(),
                    ["specificHeat"] = Mole.SpecificHeat(g).ToDouble(),
                    // Phase change, as the planet tick applies it: a gas freezes whole below
                    // freezingK + 2, and condenses below its boiling point at the planet's total
                    // pressure + 2 once that pressure reaches minLiquidPressureKPa.
                    ["freezingK"] = Mole.FreezingTemperature(g).ToDouble(),
                    ["canFreeze"] = Mole.CanFreeze(g),
                    ["canCondense"] = MoleHelper.CanCondense(g),
                    ["minLiquidPressureKPa"] = Mole.MinLiquidPressure(g).ToDouble(),
                    ["maxLiquidTemperatureK"] = Mole.MaxLiquidTemperature(g).ToDouble(),
                    ["latentHeatOfVaporization"] = Mole.LatentHeatOfVaporization(g),
                    ["boilingKByPressureKPa"] = new Dictionary<string, object>
                    {
                        ["x"] = pressure,
                        ["y"] = pressure.Select(p => new Mole(g, new MoleQuantity(1.0), MoleEnergy.Zero).EvaporationTemperatureClamped(new PressurekPa(p)).ToDouble()).ToArray(),
                    },
                    ["ghgIndexByMmolPerLitre"] = TerraForming.TerraformingGasCurves.TryGetValue(g, out TerraformingGasCurveData curve) && curve.Curve != null
                        ? (object)Sample(curve.Curve, density) : null,
                }),
            };

            var worlds = new Dictionary<string, object>();
            foreach (WorldSetting world in WorldSetting.AllWorldSettings)
            {
                GlobalAtmosphereData data = world?.Data?.GlobalAtmosphereData;
                if (data == null || world.IsTutorial)
                {
                    continue;
                }
                GlobalGasMix start = GlobalGasMix.Create(data);
                // The same throwaway simulation the new-game menu builds to describe a world.
                OrbitalSimulation orbit = OrbitalSimulation.GetSimulation(world, OrbitalSimulation.GetDayLengthSeconds());
                ValueRange sun = orbit.GetSolarEnergy();
                worlds[world.Id] = new Dictionary<string, object>
                {
                    ["solarIrradianceMin"] = sun.Minimum,
                    ["solarIrradianceMax"] = sun.Maximum,
                    ["solarConstant"] = orbit.SolarConstant,
                    ["noonSolarAngle"] = orbit.GetSolarAngle(world),
                    ["gravity"] = world.Gravity,
                    ["volumeLitres"] = data.GetVolume().ToDouble(),
                    ["startMolesTotal"] = gases.Where(g => start.Get(g).ToDouble() != 0.0).ToDictionary(g => g.ToString(), g => (object)start.Get(g).ToDouble()),
                    ["startGhgIndex"] = TerraForming.GetGhgIndex(start),
                    ["startDensityMmolPerLitre"] = IdealGas.GetMilliMolesPerLitre(start.Volume, start.TotalQuantityGas()),
                    ["baseTemperatureBySolarAngle"] = Curve(data.SolarAngleTemperatureCurveData, angle),
                    ["solarOffsetByPercent"] = DayNight(data.SolarRadiationTemperatureOffset, percent),
                    ["ghgOffsetByIndex"] = DayNight(data.GHGTemperatureOffset, index),
                    ["densityOffsetByMmolPerLitre"] = DayNight(data.DensityOffset, density),
                };
            }
            root["worlds"] = worlds;
            root["prefabs"] = DumpPrefabs();
            File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(root, Newtonsoft.Json.Formatting.Indented));
        }

        // Numbers that exist only as serialized prefab data, not in code or XML: how much gas an ice
        // holds, how much a device moves per tick and what it draws. Read off the prefabs by field
        // name, so a field that moves to a base class or a new device type is still picked up.
        private static readonly HashSet<string> WantedFields = new HashSet<string>
        {
            "pressurePerTick", "UsedPower", "MaxSetting", "volume", "litres", "gridMixingDepth",
            "iceSpeedMultiplier", "oreSpeedMultiplier", "IceYieldMultiplier", "OreYieldMultiplier", "_maxFuelFlowRate",
            "MineCompletionTime", "MineAmount", "MaxPowerGenerated", "_panelArea", "Efficiency",
            "meltTemperature", "temperature", "MaxQuantity", "FilterLife", "_maxPressureKpa", "DrillMovementSpeed",
        };

        private static readonly string[] GasListFields = { "SpawnContents", "ExpelledGas", "ExspelledGas", "_expelledGasses", "SpawnGasList" };

        private static object DumpPrefabs()
        {
            var result = new SortedDictionary<string, object>(StringComparer.Ordinal);
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            foreach (Assets.Scripts.Objects.Thing prefab in Assets.Scripts.Objects.Prefab.AllPrefabs)
            {
                if (prefab == null)
                {
                    continue;
                }
                var entry = new Dictionary<string, object>();
                for (Type type = prefab.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                {
                    foreach (System.Reflection.FieldInfo field in type.GetFields(all))
                    {
                        try
                        {
                            object value = field.GetValue(prefab);
                            if (WantedFields.Contains(field.Name) && value != null && (value is float || value is int || value is double))
                            {
                                entry[field.Name] = Convert.ToDouble(value);
                            }
                            else if (GasListFields.Contains(field.Name) && value is System.Collections.IEnumerable list)
                            {
                                var gasList = new List<object>();
                                foreach (object item in list)
                                {
                                    if (item is Assets.Scripts.Objects.SpawnGas gas)
                                    {
                                        gasList.Add(new Dictionary<string, object> { ["gas"] = gas.Type.ToString(), ["moles"] = gas.Quantity, ["kelvin"] = gas.Kelvin });
                                    }
                                }
                                if (gasList.Count > 0)
                                {
                                    entry[field.Name] = gasList;
                                }
                            }
                        }
                        catch (Exception)
                        {
                            // A field that cannot be read on a prefab is simply not reported.
                        }
                    }
                }
                // Only devices and gas-bearing items: a bare "temperature" on every ore is noise.
                bool useful = entry.Keys.Any(k => GasListFields.Contains(k)) || entry.ContainsKey("pressurePerTick")
                    || entry.Keys.Any(k => k != "temperature" && k != "meltTemperature" && k != "UsedPower" && k != "volume");
                if (useful)
                {
                    entry["class"] = prefab.GetType().Name;
                    result[prefab.PrefabName] = entry;
                }
            }
            return result;
        }

        private static double[] Grid(params double[] spec)
        {
            // start, step, end, step, end, ... : a grid that is fine where the curves bend.
            var values = new List<double>();
            double x = spec[0];
            for (int i = 1; i + 1 < spec.Length; i += 2)
            {
                for (; x < spec[i + 1] - 1e-9; x += spec[i])
                {
                    values.Add(Math.Round(x, 6));
                }
            }
            values.Add(spec[spec.Length - 1]);
            return values.ToArray();
        }

        private static object Sample(AnimationCurve curve, double[] xs)
        {
            return new Dictionary<string, object>
            {
                ["x"] = xs,
                ["y"] = xs.Select(x => (double)curve.Evaluate((float)x)).ToArray(),
            };
        }

        // Built from the keys, which are always deserialised, rather than trusting that the game has
        // initialised the curve object of a world that is not the one loaded.
        private static object Curve(AnimationCurveData data, double[] xs)
        {
            if (data?.Keys == null || data.Keys.Count == 0)
            {
                return null;
            }
            return Sample(AnimationCurveData.Create(data), xs);
        }

        private static object DayNight(GlobalTemperatureCurveOffset offset, double[] xs)
        {
            object day = Curve(offset?.DayCurveOffsetData, xs);
            object night = Curve(offset?.NightCurveOffsetData, xs);
            if (day == null && night == null)
            {
                return null;
            }
            return new Dictionary<string, object> { ["day"] = day, ["night"] = night };
        }

        /// <summary>Runs the mod console command the way the console would and logs what it answered.</summary>
        private void RunCommand(params string[] args)
        {
            if (!Util.Commands.CommandLine.CommandsMap.TryGetValue("terraform", out Util.Commands.CommandBase command))
            {
                Logger.LogInfo("LiveCheck: cmd terraform is not registered");
                return;
            }
            string answer;
            try
            {
                answer = command.Execute(args) ?? "(null)";
            }
            catch (Exception e)
            {
                answer = "THREW " + e;
            }
            Logger.LogInfo("LiveCheck: cmd " + string.Join(" ", args) + " -> " + answer.Replace("\r", "").Replace("\n", " / "));
        }

        /// <summary>
        /// Takes the mod's Deregister guard off, so the next removal of a world cell hands the planet
        /// a cell the game has already copied elsewhere. Only for the counterfactual run: what it
        /// measures is how much gas the guard is worth.
        /// </summary>
        private bool TakeOffDeregisterGuard()
        {
            Type guards = AccessTools.TypeByName("TerraformingReloaded.Patching.Guards");
            System.Reflection.MethodInfo body = guards == null ? null : AccessTools.DeclaredMethod(guards, "DeregisterPrefix");
            System.Reflection.MethodInfo deregister = AccessTools.DeclaredMethod(typeof(AtmosphericsManager), "Deregister", new[] { typeof(Atmosphere) });
            if (body == null || deregister == null)
            {
                Logger.LogInfo("LiveCheck: buildover FAIL the mod's Deregister guard could not be found to take it off");
                return false;
            }
            new Harmony("xceled.stationeers.terraformingreloaded.livecheck").Unpatch(deregister, body);
            Logger.LogInfo("LiveCheck: buildover the mod's Deregister guard is off for this event");
            return true;
        }

        /// <summary>Warm carbon dioxide into one outdoor cell, the same way the injection scenario does.</summary>
        private static void AddCarbonDioxide(Atmosphere cell, double moles)
        {
            MoleQuantity quantity = new MoleQuantity(moles);
            GasMixture mix = GasMixtureHelper.Create();
            mix.CarbonDioxide = new Mole(Chemistry.GasType.CarbonDioxide, quantity,
                IdealGas.Energy(new TemperatureKelvin(293.15), Mole.SpecificHeat(Chemistry.GasType.CarbonDioxide), quantity));
            cell.Add(mix);
        }

        private void Inject()
        {
            // Open sky well above the terrain and well below the 1000 m line the game treats as space.
            WorldGrid grid = new WorldGrid(new Vector3(0f, 300f, 0f));
            Atmosphere cell = AtmosphericsManager.CloneGlobalAtmosphereThreadSafe(grid);
            if (cell == null)
            {
                throw new InvalidOperationException("no outdoor cell at " + grid);
            }
            MoleQuantity quantity = new MoleQuantity(InjectMoles);
            TemperatureKelvin temperature = new TemperatureKelvin(293.15);
            GasMixture mix = GasMixtureHelper.Create();
            mix.CarbonDioxide = new Mole(Chemistry.GasType.CarbonDioxide, quantity,
                IdealGas.Energy(temperature, Mole.SpecificHeat(Chemistry.GasType.CarbonDioxide), quantity));
            cell.Add(mix);
            Logger.LogInfo(string.Format(CultureInfo.InvariantCulture, "LiveCheck: injected {0:0} mol CO2 into an outdoor cell", InjectMoles));
        }

        private static double Total(GlobalGasMixSaveData mix)
        {
            double sum = 0.0;
            if (mix == null)
            {
                return sum;
            }
            foreach (System.Reflection.FieldInfo field in typeof(GlobalGasMixSaveData).GetFields())
            {
                if (field.Name != "Volume" && field.GetValue(mix) is DoubleReference value)
                {
                    sum += value.Value;
                }
            }
            return sum;
        }

        private void ReportObserve(GlobalGasMix tank)
        {
            Type sim = typeof(PlanetaryAtmosphereSimulation);
            double cells = (tank.Volume / Chemistry.GridVolume).ToDouble();
            GlobalGasMix caps = (GlobalGasMix)AccessTools.Field(sim, "_iceCaps").GetValue(null);
            GlobalGasMix ice = (GlobalGasMix)AccessTools.Field(sim, "_iceClouds").GetValue(null);
            GlobalGasMix liquid = (GlobalGasMix)AccessTools.Field(sim, "_liquidClouds").GetValue(null);
            System.Text.StringBuilder gases = new System.Text.StringBuilder();
            foreach (Chemistry.GasType type in (Chemistry.GasType[])Enum.GetValues(typeof(Chemistry.GasType)))
            {
                if (Mole.MatterState(type) == AtmosphereHelper.MatterState.None)
                {
                    continue;
                }
                double moles = tank.Get(type).ToDouble();
                if (moles > 0.0)
                {
                    gases.AppendFormat(CultureInfo.InvariantCulture, " {0}={1:0.####}", type, moles / cells);
                }
            }
            // The temperature asked of the game the way outdoor cells ask it, not the debug readout.
            // The sun moves between reads, so the angle and the point in the orbit are taken first and
            // handed to the overload the one-argument call makes: the sample then reports the exact
            // inputs its own temperature was computed from. They are printed to four decimals because
            // a base curve can fall 15 K per degree, so a tenth of a degree is most of a kelvin.
            float angle = Vector3.Angle(Vector3.up, OrbitalSimulation.WorldSunVector);
            float percent = OrbitalSimulation.System.GetSolarEnergyPercentClamped(
                OrbitalSimulation.System.GetSolarEnergy(), OrbitalSimulation.System.CalculateSolarIrradiance());
            double kelvin = tank.GetGlobalGasMixTemperature(WorldSetting.Current.Data.GlobalAtmosphereData, angle, percent).ToDouble();
            double stormK = Weather.WeatherManager.IsWeatherEventRunning
                ? Weather.WeatherManager.CurrentWeatherEvent?.TemperatureOffset?.GetOffset(angle) ?? 0f : 0f;
            Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "LiveCheck: obs tick {0} | angle {1:0.0000} | orbit {13:0.000}% | T {2:0.00} | readout {3:0.00} | P {4:0.000} | gas/cell {5:0.####} | liquid/cell {6:0.####} | iceClouds/cell {7:0.####} | liquidClouds/cell {8:0.####} | caps/cell {9:0.####} | latentK {10:0.###} | weather {11} |{12} | extK {14:0.###} | stormK {15:0.###}",
                GameManager.GameTickCount, angle, kelvin,
                PlanetaryAtmosphereSimulation.AggregateTemperature.ToDouble(), PlanetaryAtmosphereSimulation.GlobalPressure.ToDouble(),
                tank.TotalQuantityGas().ToDouble() / cells, tank.TotalQuantityLiquid().ToDouble() / cells,
                ice.TotalQuantity().ToDouble() / cells, liquid.TotalQuantity().ToDouble() / cells, caps.TotalQuantity().ToDouble() / cells,
                PlanetaryAtmosphereSimulation.LatentOffset.ToDouble(), Weather.WeatherManager.WeatherState, gases,
                percent, PlanetaryAtmosphereSimulation.GetExternalInputEnergyOffset().ToDouble(), stormK));
        }

        private void Report()
        {
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            if (tank == null)
            {
                return;
            }
            double inTank = tank.TotalQuantity().ToDouble();
            // Gas and liquid, so carbon dioxide that condenses is still counted where it is.
            double tankCo2 = tank.Get(Chemistry.GasType.CarbonDioxide).ToDouble() + tank.Get(Chemistry.GasType.LiquidCarbonDioxide).ToDouble();
            double inCells = 0.0;
            // The injected gas that has not reached the planet yet. Outdoor cells rest at the planet's
            // own density, so the denser the injection leaves the planet the more they hold back, and
            // on a small planet the same injection is a much larger density rise. Counted here, what
            // was injected is the rise in tank plus cells, which is the same at any planet size.
            double cellCo2 = 0.0;
            int cells = 0;
            AtmosphericsManager.AllAtmospheres.ForEach((Action<Atmosphere>)(a =>
            {
                if (a != null && a.Mode == AtmosphereHelper.AtmosphereMode.World)
                {
                    inCells += a.GasMixture.GetTotalMolesGassesAndLiquids.ToDouble();
                    cellCo2 += a.GasMixture.CarbonDioxide.Quantity.ToDouble() + a.GasMixture.LiquidCarbonDioxide.Quantity.ToDouble();
                    cells++;
                }
            }));
            if (Observe)
            {
                ReportObserve(tank);
            }
            PlanetaryAtmosphereSaveData state = PlanetaryAtmosphereSimulation.Save();
            // The planet gas by gas as well as in total. A total that holds while one gas falls and
            // another rises is a different thing from a leak, and only this line can tell them apart.
            System.Text.StringBuilder gases = new System.Text.StringBuilder();
            foreach (Chemistry.GasType type in (Chemistry.GasType[])Enum.GetValues(typeof(Chemistry.GasType)))
            {
                double moles = Mole.MatterState(type) == AtmosphereHelper.MatterState.None ? 0.0 : tank.Get(type).ToDouble();
                if (moles != 0.0)
                {
                    gases.AppendFormat(CultureInfo.InvariantCulture, " {0}={1:0.000}", type, moles);
                }
            }
            Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "LiveCheck: state tick {0} | caps {1:0.000} | clouds {2:0.000} | latentJ {3:0.###} | externalJ {4:0.###} | tank gases{5}",
                GameManager.GameTickCount, Total(state.IceCaps), Total(state.LiquidClouds) + Total(state.IceClouds),
                state.LatentOffset?.Value ?? 0.0, state.ExternalOffset?.Value ?? 0.0, gases));
            Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "LiveCheck: tick {0,5} | tank {1:0.000} | tank CO2 {2:0.000} | outdoor cells {3,5} holding {4:0.000} | cell CO2 {9:0.000} | SUM {5:0.000} | T {6:0.00} K | P {7:0.0000} kPa | ext {8:0.###} K",
                GameManager.GameTickCount, inTank, tankCo2, cells, inCells, inTank + inCells,
                PlanetaryAtmosphereSimulation.AggregateTemperature.ToDouble(),
                PlanetaryAtmosphereSimulation.GlobalPressure.ToDouble(),
                PlanetaryAtmosphereSimulation.ExternalInputOffset.ToDouble(), cellCo2));
        }
    }
}
