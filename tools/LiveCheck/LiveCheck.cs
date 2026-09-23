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

        // Stripping the planet through outdoor cells, which is the direction a player actually moves
        // planet air: an inward ActiveVent clones the outdoor cell at its own grid and removes gas
        // from it (ActiveVent.PumpGasToPipe), and the planet refills that cell through the ordinary
        // mixing. A headless run cannot build a vent, so the driver does exactly what the vent does,
        // at as many grids as asked for, from the planet tick where every mole reads live. What is
        // taken is discarded, which is where a player's gas goes: into storage, off the planet.
        private static readonly uint StripTick = uint.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_STRIP_TICK"), out uint st) ? st : 0u;
        private static readonly uint StripTicks = uint.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_STRIP_TICKS"), out uint stn) ? stn : 150u;
        private static readonly int StripCells = int.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_STRIP_CELLS"), out int sc) ? sc : 100;
        // How much one draw point may take in one tick. The default is larger than a cell can hold,
        // which is a vent whose pressure per tick is never the limit: the fastest a cell can be
        // emptied, and so the largest share outdoor cells can ever be holding while air is leaving.
        private static readonly double StripPerCell = double.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_STRIP_PER_CELL"), NumberStyles.Float, CultureInfo.InvariantCulture, out double spc) ? spc : 1.0e9;
        // What is left behind in a cell, and it matters. A real vent sits on a grid with a structure
        // on it, and a cell with a structure is kept instantiated (Atmosphere.IsLive asks the centre
        // structure's AlwaysInstanceWorldAtmosphere). A headless run cannot build one, and a cell
        // emptied outright is culled: measured twice, 99 of 100 draw points died on the first tick,
        // once taking everything and once leaving a mole behind, and their air went back to the tank.
        private static readonly double StripFloor = double.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_STRIP_FLOOR"), NumberStyles.Float, CultureInfo.InvariantCulture, out double sf) ? sf : 1.0;
        // So the draw takes a SHARE of what a cell holds, which is what a vent does: it is limited by
        // its pressure per tick and by what the pipe behind it will accept, so it thins the cell
        // rather than emptying it, and the cell stays instantiated and goes on being refilled.
        private static readonly double StripShare = double.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_STRIP_SHARE"), NumberStyles.Float, CultureInfo.InvariantCulture, out double ss) ? ss : 0.5;
        private double _strippedTotal;
        private double _stripSumAtStart;
        private uint _stripTicksDone;
        private bool _stripDone;

        // What a walk over every atmosphere costs, timed where a per-tick measure would sit. Not a
        // check: it prints microseconds against the cell count so the cost can be read off at the
        // counts a real base reaches.
        private static readonly bool WalkCost = Environment.GetEnvironmentVariable("TR_LIVECHECK_WALKCOST") == "1";
        private const int WalkRepeats = 20;
        private static double _walkMoles;
        private static int _walkCells;
        private static int _walkSeen;
        private static readonly Action<Atmosphere> WalkSum = a =>
        {
            if (a != null && a.Mode == AtmosphereHelper.AtmosphereMode.World)
            {
                _walkMoles += a.GasMixture.GetTotalMolesGassesAndLiquids.ToDouble();
                _walkCells++;
            }
        };
        private static readonly Action<Atmosphere> WalkCount = a =>
        {
            if (a != null)
            {
                _walkSeen++;
            }
        };

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
        // Some of what status reports is built the first time it is needed, so at tick 6 the
        // temperature response reads "not evaluated yet". This asks for status once more, later.
        private static readonly uint StatusAgainTick = uint.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_STATUS_TICK"), out uint sa) ? sa : 0u;
        private bool _statusAgain;

        // Per-world settings. The file the mod writes beside the save is rewritten here, one shape
        // at a time, and the mod's own read prefix is called on each, so what is judged is the real
        // read path over a real file in a real world folder, not a copy of the rule.
        //
        // The config is made to ask for a pressure ceiling throughout, because the question these
        // cases exist to answer is not "does a bad file parse" but "can any path other than a
        // world's own file, or the console verb, switch on the one setting that deletes a player's
        // air". Every line prints the config's ceiling beside the one in force, so a run in which
        // the config was not asking cannot be mistaken for a pass.
        private static readonly uint SidecarTick = uint.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_SIDECAR_TICK"), out uint sdc) ? sdc : 0u;

        // The storm rules. Neither can be reached by forcing a storm on, because
        // ImmediatelyActivateWeatherEvent bypasses the scheduler, and neither can be reached by
        // waiting, because scheduling needs a world-start cooldown measured in days. So the driver
        // calls the two lines the game's own ManagerUpdate calls, from the main thread, and reads
        // back whether an event was set. Suppressed means nothing was set.
        private static readonly uint StormsTick = uint.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_STORMS_TICK"), out uint stm) ? stm : 0u;
        private const uint StormsTicksBetween = 3;
        private int _stormsCase;
        private uint _stormsNextTick;
        private bool _stormsDone;
        private string _stormsJudging;

        // Several worlds in one process. World A is created by the launch; the driver changes its
        // settings with terraform set, changes the config, creates world B, then goes back to A and
        // to B again, switching with the console's own file start. Both transition bugs found so far
        // were state carried from one world into the next, and nothing else plays two worlds in one
        // session.
        private static readonly bool Sessions = Environment.GetEnvironmentVariable("TR_LIVECHECK_SESSIONS") == "1";

        // Rain on a world that ships no weather, across a save and a load. The game saves no weather
        // state at all on such a world (WeatherManager.CreateSaveData returns null), so rain that is
        // scheduled or falling at a save is gone after the load. Phase 1 fills a cloud, lets the
        // planet tick schedule the rain for real, and saves; phase 2 loads and reads what came back.
        private static readonly string RainSave = Environment.GetEnvironmentVariable("TR_LIVECHECK_RAINSAVE") ?? "";

        // A world made by the previous release, loaded by this one: what it takes from the config,
        // what it refuses to take, what file it writes, and whether the planet keeps its air.
        private static readonly bool Upgrade = Environment.GetEnvironmentVariable("TR_LIVECHECK_UPGRADE") == "1";
        private int _upgradeStep;
        private bool _upgradeDone;
        private int _rainStep;
        private bool _rainDone;
        private int _sessionStep;
        private bool _sessionsDone;
        private object _sessionTank;
        private string _sessionWant;
        private bool _sessionArrived;
        private uint _sessionSeenTick;

        // Moving the season. Writing SimulationTimeSeconds alone does nothing: the distances the
        // solar percent derives from are recomputed by SetAllBodies, which SetSimulationTime calls.
        // One unit of simulation time is one degree of the world's own orbit, so 360 is a year
        // (CelestialBody.Set: the player body's true anomaly is its longitude at epoch plus this).
        private static readonly double OrbitDegrees = double.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_ORBIT"), NumberStyles.Float, CultureInfo.InvariantCulture, out double orb) ? orb : 0.0;
        private static readonly uint OrbitTick = uint.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_ORBIT_TICK"), out uint ort) ? ort : 0u;
        private bool _orbitMoved;
        private const double SidecarConfigCeiling = 500.0;
        private const uint SidecarTicksBetween = 2;
        private int _sidecarCase;
        private uint _sidecarNextTick;
        private bool _sidecarDone;
        private string _sidecarStation;
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
            if (_instance != null && !_instance._stripDone && StripTick > 0 && GameManager.GameTickCount >= StripTick)
            {
                _instance.StripStep();
            }
            if (_instance != null && WalkCost && GameManager.GameTickCount % ReportEveryTicks == 0)
            {
                _instance.MeasureWalk();
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
            if (Sessions && !_sessionsDone)
            {
                SessionsStep();
            }
            if (RainSave.Length > 0 && !_rainDone && GameManager.GameTickCount >= 15)
            {
                RainSaveStep();
            }
            if (Upgrade && !_upgradeDone)
            {
                UpgradeStep();
            }
            if (!_statusAgain && StatusAgainTick > 0 && GameManager.GameTickCount >= StatusAgainTick)
            {
                _statusAgain = true;
                RunCommand("status");
            }
            // One case per visit, spaced a couple of ticks apart, so the planet goes on ticking
            // between them: a ceiling that was wrongly let through would show up as the tank
            // collapsing in the sampled rows, not only in the line printed here.
            if (!_sidecarDone && SidecarTick > 0 && GameManager.GameTickCount >= SidecarTick
                && GameManager.GameTickCount >= _sidecarNextTick)
            {
                _sidecarNextTick = GameManager.GameTickCount + SidecarTicksBetween;
                SidecarStep();
            }
            if (!_orbitMoved && OrbitDegrees != 0.0 && OrbitTick > 0 && GameManager.GameTickCount >= OrbitTick)
            {
                _orbitMoved = true;
                MoveOrbit(OrbitDegrees, "asked for");
            }
            // One case per visit, a few ticks apart, so the planet tick has run its upkeep and the
            // mod has measured the air each case sets before that case is judged.
            if (!_stormsDone && StormsTick > 0 && GameManager.GameTickCount >= StormsTick
                && GameManager.GameTickCount >= _stormsNextTick)
            {
                _stormsNextTick = GameManager.GameTickCount + StormsTicksBetween;
                StormsStep();
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
        /// Taking planet air out through outdoor cells, one tick at a time: the direction a player
        /// moves air, and the direction the injection scenario does not cover.
        ///
        /// What an inward ActiveVent does is clone the outdoor cell at its own grid
        /// (GetWorkingAtmosphere) and remove gas from it (PumpGasToPipe); the planet then refills
        /// that cell through the ordinary mixing, which is how the planet is drained one cell at a
        /// time. This does the same at STRIP_CELLS grids in open sky, taking up to STRIP_PER_CELL
        /// moles from each one each tick, and discards what it takes: a player's gas goes into
        /// storage, and for the planet that is gone.
        ///
        /// Simulation thread, at the top of the planet tick, so tank and cells are read at rest.
        /// Every tick logs the same four figures the default scenario logs every five, so the share
        /// outdoor cells hold can be read across the whole draw.
        /// </summary>
        private void StripStep()
        {
            try
            {
                GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
                if (tank == null)
                {
                    return;
                }
                if (_stripTicksDone == 0)
                {
                    _stripSumAtStart = TankPlusCells(out int _);
                    Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                        "LiveCheck: strip starting at tick {0} | {1} draw points | {2:0.###} of what each holds per tick | up to {3:0.###} mol | leaving {4:0.###} mol behind | for {5} ticks | planet plus cells {6:0.000} mol",
                        GameManager.GameTickCount, StripCells, StripShare, StripPerCell, StripFloor, StripTicks, _stripSumAtStart));
                }

                WorldGrid origin = new WorldGrid(new Vector3(0f, 300f, 0f));
                int side = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(StripCells)));
                double removedNow = 0.0;
                int made = 0;
                int drawnFrom = 0;
                for (int i = 0; i < StripCells; i++)
                {
                    WorldGrid grid = new WorldGrid(origin.Value.x + (i % side), origin.Value.y, origin.Value.z + (i / side));
                    Atmosphere cell = AtmosphericsManager.Find(grid);
                    double have = cell == null ? 0.0 : cell.GasMixture.GetTotalMolesGassesAndLiquids.ToDouble();
                    if (cell == null || have <= StripFloor || cell.BeingDestroyed)
                    {
                        // The vent's own call (ActiveVent.GetWorkingAtmosphere): a draw point with no
                        // cell builds one, and building it draws its air from the planet rather than
                        // creating any. On a cell that is already there this only marks it active
                        // again, which is what brings a drained one back into the simulation.
                        Atmosphere built = AtmosphericsManager.CloneGlobalAtmosphereThreadSafe(grid);
                        if (built != null && built != cell)
                        {
                            made++;
                        }
                        cell = built;
                        have = cell == null ? 0.0 : cell.GasMixture.GetTotalMolesGassesAndLiquids.ToDouble();
                    }
                    if (cell == null)
                    {
                        continue;
                    }
                    // A share of what the cell holds, capped, and never below the floor: the vent's
                    // behaviour rather than a cell emptied outright, which the game culls.
                    double take = Math.Min(Math.Min(have * StripShare, have - StripFloor), StripPerCell);
                    // The first tick only builds the draw points. A cell created inside the planet
                    // tick has no open neighbours until the next tick's RunOpenNeighboursJobs, and a
                    // cell with no open neighbours is not live (Atmosphere.IsLive), so draining one
                    // the moment it is built is what killed the draw points in the first two runs.
                    if (take <= 0.0 || _stripTicksDone == 0)
                    {
                        continue;
                    }
                    GasMixture taken = cell.Remove(new MoleQuantity(take), AtmosphereHelper.MatterState.All);
                    removedNow += taken.GetTotalMolesGassesAndLiquids.ToDouble();
                    drawnFrom++;
                }
                _strippedTotal += removedNow;
                _stripTicksDone++;

                double inTank = tank.TotalQuantity().ToDouble();
                double inCells = 0.0;
                int cells = 0;
                AtmosphericsManager.AllAtmospheres.ForEach((Action<Atmosphere>)(a =>
                {
                    if (a != null && a.Mode == AtmosphereHelper.AtmosphereMode.World)
                    {
                        inCells += a.GasMixture.GetTotalMolesGassesAndLiquids.ToDouble();
                        cells++;
                    }
                }));
                // The planet's own air per outdoor cell, so what the cells hold can be read against
                // the density they would rest at: a cell at planet density is one grid cell of the
                // planet, and the planet is (volume / grid volume) of them.
                double gridCells = (tank.Volume / Chemistry.GridVolume).ToDouble();
                Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "LiveCheck: strip tick {0} | tank {1:0.000} | outdoor cells {2} holding {3:0.000} | SUM {4:0.000} | took {5:0.000} | took total {6:0.000} | made {7} | drew from {8} | planet per cell {9:0.000000} | planet grid cells {10:0}",
                    GameManager.GameTickCount, inTank, cells, inCells, inTank + inCells, removedNow, _strippedTotal, made, drawnFrom,
                    gridCells > 0.0 ? inTank / gridCells : 0.0, gridCells));

                if (_stripTicksDone >= StripTicks)
                {
                    _stripDone = true;
                    double left = inTank + inCells;
                    Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                        "LiveCheck: strip done | tick {0} | took total {1:0.000} mol | planet plus cells {2:0.000} -> {3:0.000} ({4:+0.000;-0.000}) | unaccounted {5:+0.000;-0.000}",
                        GameManager.GameTickCount, _strippedTotal, _stripSumAtStart, left, left - _stripSumAtStart,
                        left - _stripSumAtStart + _strippedTotal));
                }
            }
            catch (Exception e)
            {
                _stripDone = true;
                Logger.LogInfo("LiveCheck: strip FAIL " + e);
            }
        }

        /// <summary>
        /// What one walk over every atmosphere costs, timed on the simulation thread at the top of
        /// the planet tick, which is where a per-tick measure of the planet's air would have to sit.
        /// Two walks are timed: the one a measure needs (sum the moles in every World-mode cell) and
        /// a bare one that only counts, which separates the cost of iterating from the cost of
        /// reading. Not judged: it prints microseconds against the cell count.
        /// </summary>
        private void MeasureWalk()
        {
            try
            {
                // Warm up: the first walk after a tick's work pays for cold cache lines, and the
                // delegates are cached fields so no allocation is being timed.
                _walkMoles = 0.0;
                _walkCells = 0;
                AtmosphericsManager.AllAtmospheres.ForEach(WalkSum);

                double sumBest = double.MaxValue;
                double sumTotal = 0.0;
                for (int i = 0; i < WalkRepeats; i++)
                {
                    _walkMoles = 0.0;
                    _walkCells = 0;
                    System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                    AtmosphericsManager.AllAtmospheres.ForEach(WalkSum);
                    watch.Stop();
                    double us = watch.Elapsed.TotalMilliseconds * 1000.0;
                    sumTotal += us;
                    sumBest = Math.Min(sumBest, us);
                }

                double countBest = double.MaxValue;
                double countTotal = 0.0;
                for (int i = 0; i < WalkRepeats; i++)
                {
                    _walkSeen = 0;
                    System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                    AtmosphericsManager.AllAtmospheres.ForEach(WalkCount);
                    watch.Stop();
                    double us = watch.Elapsed.TotalMilliseconds * 1000.0;
                    countTotal += us;
                    countBest = Math.Min(countBest, us);
                }

                Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "LiveCheck: walk tick {0} | pool {1} | cells {2} | sum us {3:0.###} mean {4:0.###} | count us {5:0.###} mean {6:0.###} | tickMs {7:0.###} | moles {8:0.000}",
                    GameManager.GameTickCount, AtmosphericsManager.AllAtmospheres.ActiveCount, _walkCells,
                    sumBest, sumTotal / WalkRepeats, countBest, countTotal / WalkRepeats,
                    GameManager.GameTickSpeedSeconds * 1000.0, _walkMoles));
            }
            catch (Exception e)
            {
                Logger.LogInfo("LiveCheck: walk FAIL " + e);
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

        // ---- the storm rules ----------------------------------------------------------------------
        //
        // Neither rule can be reached by the scenarios that already exist. -Storm and -Weather force
        // an event on through ImmediatelyActivateWeatherEvent, which never asks the scheduler, and
        // waiting cannot work either: scheduling needs a world-start cooldown of days no headless run
        // reaches. So the driver runs the two lines WeatherManager.ManagerUpdate runs, from the main
        // thread, with the game's own cooldowns cleared, and reads back whether an event was set.
        // Nothing here reimplements either rule: every verdict judged is the mod's own.

        // ---- several worlds in one session ------------------------------------------------------

        private void SessionsStep()
        {
            // A world started from the console can come up paused, and the launch's own unpause only
            // ever ran once.
            if (WorldManager.IsGamePaused)
            {
                WorldManager.SetGamePause(pauseGame: false);
            }
            object tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            string station = Assets.Scripts.Serialization.XmlSaveLoad.Instance?.CurrentStationName;
            if (_sessionWant != null)
            {
                // Arrived means a new planet under the name asked for. The name alone is not enough:
                // a world being created carries the previous world's name until its first save.
                if (tank == null || ReferenceEquals(tank, _sessionTank) || station != _sessionWant)
                {
                    return;
                }
                if (!_sessionArrived)
                {
                    _sessionArrived = true;
                    _sessionSeenTick = GameManager.GameTickCount;
                    return;
                }
                if (GameManager.GameTickCount < _sessionSeenTick + 10)
                {
                    return;
                }
                _sessionWant = null;
                _sessionArrived = false;
            }
            else if (GameManager.GameTickCount < 15 || string.IsNullOrEmpty(station))
            {
                return;
            }

            switch (_sessionStep++)
            {
                case 0:
                    CheckSizeSlider();
                    LogSession("A-new");
                    RunCommand("set", "GhgResponseScale", "2.5");
                    RunCommand("set", "StormsStopWhenStripped", "off");
                    RunCommand("set", "DynamicSky", "off");
                    RunCommand("set", "MildAtmosphereColdestKelvin", "250");
                    // Lowering the heat limit deletes banked heat, so it must ask before it acts.
                    RunCommand("set", "MaxExternalOffsetKelvin", "20");
                    LogSession("A-asked");
                    RunCommand("set", "MaxExternalOffsetKelvin", "20", "confirm");
                    RunCommand("set", "WeatherOnWeatherlessWorlds", "banana");
                    RunCommand("set", "NoSuchSetting", "1");
                    RunCommand("set");
                    LogSession("A-set");
                    // The config editor, the way a player moves it: through the entry, so BepInEx
                    // fires the same SettingChanged the mod listens to.
                    SetConfigEntry("Climate", "GhgResponseScale", 0.7);
                    SetConfigEntry("Storms", "MildAtmosphereColdestKelvin", 270.0);
                    SetConfigEntry("Heat", "MaxExternalOffsetKelvin", 35.0);
                    LogSession("A-after-config");
                    SwitchWorld(tank, "trsessionb", "Mars2");
                    break;
                case 1:
                    LogSession("B-new");
                    SwitchWorld(tank, "trsessiona", null);
                    break;
                case 2:
                    LogSession("A-again");
                    SwitchWorld(tank, "trsessionb", null);
                    break;
                default:
                    LogSession("B-again");
                    RunCommand("set");
                    _sessionsDone = true;
                    Logger.LogInfo("LiveCheck: sessions done");
                    break;
            }
        }

        /// <summary>
        /// Readings at tick 5, 20 and 60 of the loaded world. Five is as early as the planet has
        /// ticked; sixty is long enough for a ceiling that was wrongly let through to have cut the
        /// planet many times over, since the rule cuts on every tick it is over.
        /// </summary>
        private void UpgradeStep()
        {
            uint[] at = { 5, 20, 60 };
            if (_upgradeStep >= at.Length || GameManager.GameTickCount < at[_upgradeStep])
            {
                return;
            }
            if (WorldManager.IsGamePaused)
            {
                WorldManager.SetGamePause(pauseGame: false);
            }
            // Also run against the earlier release as a control, which has no Sidecar or Effective,
            // so everything that belongs to this build is looked up rather than assumed.
            Type sidecar = AccessTools.TypeByName("TerraformingReloaded.Patching.Sidecar");
            string path = sidecar != null ? AccessTools.Property(sidecar, "FilePath")?.GetValue(null, null) as string : null;
            string text = path != null && File.Exists(path) ? File.ReadAllText(path) : "";
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            double inTank = tank?.TotalQuantity().ToDouble() ?? double.NaN;
            double inReservoirs = 0.0;
            foreach (string field in new[] { "_liquidClouds", "_iceClouds", "_iceCaps" })
            {
                inReservoirs += (AccessTools.Field(typeof(PlanetaryAtmosphereSimulation), field)?.GetValue(null) as GlobalGasMix)?.TotalQuantity().ToDouble() ?? 0.0;
            }
            double store = inTank + inReservoirs;
            // Outdoor cells too: the world was saved with gas spread over them, and it drains back
            // into the tank after the load, so tank and reservoirs alone would read that as a gain.
            double cells = 0.0;
            AtmosphericsManager.AllAtmospheres.ForEach((Action<Atmosphere>)(a =>
            {
                if (a != null && a.Mode == AtmosphereHelper.AtmosphereMode.World)
                {
                    cells += a.GasMixture.GetTotalMolesGassesAndLiquids.ToDouble();
                }
            }));
            store += cells;
            double shipped = WorldSetting.Current?.Data?.GlobalAtmosphereData?.Volume?.Value ?? double.NaN;
            BepInEx.Configuration.ConfigFile config = ModConfig();
            config.TryGetEntry("Pace", "CustomPlanetSizeTyped", out BepInEx.Configuration.ConfigEntry<double> typed);
            config.TryGetEntry("Climate", "MaxPressureKPa", out BepInEx.Configuration.ConfigEntry<double> configCeiling);
            Func<string, string> effective = member => EffectiveType != null ? EffectiveShown(member) : "n/a";
            Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "LiveCheck: upgrade parts tick{0} | tank {1:R} | reservoirs {2:R} | outdoor {3:R}",
                at[_upgradeStep], inTank, inReservoirs, cells));
            Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "LiveCheck: upgrade tick{0} | ceiling {1} | configceiling {2} | ghg {3} | limitK {4} | stripped {5} | size {6:0.####} | typed {7} | store {8:R} | pressure {9:0.###} | fileceiling {10} | fileghg {11} | filelimitK {12} | filestripped {13} | file {14}",
                at[_upgradeStep], effective("MaxPressureKPa"), configCeiling != null ? Shown(configCeiling.Value) : "absent",
                effective("GhgResponseScale"), effective("MaxExternalOffsetKelvin"), effective("StormsStopWhenStripped"),
                tank != null ? tank.Volume.ToDouble() / shipped : double.NaN,
                typed != null ? Shown(typed.Value) : "absent", store, PlanetaryAtmosphereSimulation.GlobalPressure.ToDouble(),
                Element(text, "MaxPressureKPa"), Element(text, "GhgResponseScale"), Element(text, "MaxExternalOffsetKelvin"),
                Element(text, "StormsStopWhenStripped"), path ?? "none"));
            if (_upgradeStep == 0)
            {
                RunCommand("status");
            }
            if (++_upgradeStep >= at.Length)
            {
                _upgradeDone = true;
                Logger.LogInfo("LiveCheck: upgrade done");
            }
        }

        private void RainSaveStep()
        {
            if (WorldManager.IsGamePaused)
            {
                WorldManager.SetGamePause(pauseGame: false);
            }
            if (RainSave == "2")
            {
                // The load. Read once, a few ticks in, so the planet tick has run on the loaded state.
                LogRain("loaded");
                _rainDone = true;
                Logger.LogInfo("LiveCheck: rainsave done");
                return;
            }
            switch (_rainStep)
            {
                case 0:
                    LogRain("before");
                    FillLiquidClouds();
                    _rainStep = 1;
                    return;
                case 1:
                    // Wait for the planet tick to empty the cloud into the air and ask for rain.
                    if (!Weather.WeatherManager.IsWeatherEventScheduled && !Weather.WeatherManager.IsWeatherEventRunning)
                    {
                        return;
                    }
                    LogRain("rain");
                    _rainStep = 2;
                    Logger.LogInfo("LiveCheck: rainsave saving at tick " + GameManager.GameTickCount);
                    Util.Commands.CommandLine.Process("file save");
                    return;
                case 2:
                    // One more reading after the save, so the figure the load is compared with is
                    // taken with the save already on disk.
                    LogRain("saved");
                    _rainDone = true;
                    Logger.LogInfo("LiveCheck: rainsave done");
                    return;
            }
        }

        /// <summary>
        /// The planet's whole store, tank plus the three reservoirs, and the weather state. The whole
        /// store because the cloud empties into the tank on the tick that asks for rain.
        /// </summary>
        private void LogRain(string name)
        {
            double total = PlanetaryAtmosphereSimulation.GetGlobalGasMix()?.TotalQuantity().ToDouble() ?? double.NaN;
            double clouds = 0.0;
            object mixes = AccessTools.Method(AccessTools.TypeByName("TerraformingReloaded.Patching.Planet"), "ReservoirMixes").Invoke(null, null);
            foreach (GlobalGasMix mix in (GlobalGasMix[])mixes)
            {
                if (mix != null)
                {
                    clouds += mix.TotalQuantity().ToDouble();
                }
            }
            Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "LiveCheck: rainsave {0} | tick {1} | world {2} | hasweather {3} | event {4} | scheduled {5} | running {6} | tank {7:R} | reservoirs {8:R} | store {9:R}",
                name, GameManager.GameTickCount, WorldSetting.Current?.Id, Weather.WeatherManager.WorldHasWeather,
                Weather.WeatherManager.CurrentWeatherEvent?.Id ?? "none",
                Weather.WeatherManager.IsWeatherEventScheduled, Weather.WeatherManager.IsWeatherEventRunning,
                total, clouds, total + clouds));
        }

        private void SwitchWorld(object tank, string station, string world)
        {
            _sessionTank = tank;
            _sessionWant = station;
            _sessionArrived = false;
            string command = "file start " + station + (world != null ? " " + world : "");
            Logger.LogInfo("LiveCheck: sessions switching | " + command);
            Util.Commands.CommandLine.Process(command);
        }

        /// <summary>
        /// The planet size dropdown and the slider below it, driven through their config entries the
        /// way the config editor drives them. The slider must show each preset's size and be greyed
        /// out, and a share typed under Custom must survive a trip through the presets.
        /// </summary>
        private void CheckSizeSlider()
        {
            BepInEx.Configuration.ConfigFile config = ModConfig();
            // The preset's enum is the mod's own type, so it is reached untyped.
            BepInEx.Configuration.ConfigEntryBase preset = config[new BepInEx.Configuration.ConfigDefinition("Pace", "PlanetSize")];
            config.TryGetEntry("Pace", "CustomPlanetSize", out BepInEx.Configuration.ConfigEntry<double> slider);
            config.TryGetEntry("Pace", "CustomPlanetSizeTyped", out BepInEx.Configuration.ConfigEntry<double> typed);
            if (preset == null || slider == null || typed == null)
            {
                throw new InvalidOperationException("a planet size config entry is missing");
            }
            Type presets = AccessTools.TypeByName("TerraformingReloaded.PlanetSizePreset");
            Action<string> choose = name => preset.BoxedValue = Enum.Parse(presets, name);
            Action<string> log = step =>
            {
                bool dimmed = false;
                foreach (object tag in slider.Description.Tags)
                {
                    if (tag is System.Collections.Generic.KeyValuePair<string, bool> pair && pair.Key == "Disabled")
                    {
                        dimmed = pair.Value;
                    }
                }
                Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "LiveCheck: sizeui {0} | preset {1} | slider {2:R} | dimmed {3} | typed {4:R} | inforce {5}",
                    step, preset.BoxedValue, slider.Value, dimmed, typed.Value,
                    Shown(AccessTools.Field(AccessTools.TypeByName("TerraformingReloaded.Settings"), "PlanetSize").GetValue(null))));
            };
            object startPreset = preset.BoxedValue;
            log("start");
            choose("Custom");
            slider.Value = 0.123;
            log("custom-typed");
            choose("Long");
            log("long");
            choose("Short");
            log("short");
            choose("UnmoddedBaseline");
            log("unmodded");
            choose("Custom");
            log("custom-again");
            preset.BoxedValue = startPreset;
            log("restored");
        }

        private BepInEx.Configuration.ConfigFile ModConfig()
        {
            BepInEx.BaseUnityPlugin mod = (BepInEx.BaseUnityPlugin)UnityEngine.Object.FindObjectOfType(AccessTools.TypeByName("TerraformingReloaded.Plugin"));
            if (mod == null)
            {
                throw new InvalidOperationException("the mod's plugin component was not found");
            }
            return mod.Config;
        }

        private void SetConfigEntry(string section, string key, double value)
        {
            // StationeersLaunchPad loads the mod, not BepInEx's chainloader, so it is found as the
            // component it is rather than looked up by its GUID.
            BepInEx.BaseUnityPlugin mod = (BepInEx.BaseUnityPlugin)UnityEngine.Object.FindObjectOfType(AccessTools.TypeByName("TerraformingReloaded.Plugin"));
            if (mod == null)
            {
                throw new InvalidOperationException("the mod's plugin component was not found");
            }
            if (!mod.Config.TryGetEntry(section, key, out BepInEx.Configuration.ConfigEntry<double> entry))
            {
                throw new InvalidOperationException("no config entry " + section + "/" + key);
            }
            entry.Value = value;
        }

        private void LogSession(string name)
        {
            Type sidecar = AccessTools.TypeByName("TerraformingReloaded.Patching.Sidecar");
            Type gate = AccessTools.TypeByName("TerraformingReloaded.Patching.Gate");
            string path = AccessTools.Property(sidecar, "FilePath").GetValue(null, null) as string;
            string text = path != null && File.Exists(path) ? File.ReadAllText(path) : "";
            Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "LiveCheck: sessions {0} | station {1} | ghg {2} | coldest {3} | stripped {4} | sky {5} | skygate {6} | limitK {7} | fileghg {8} | filecoldest {9} | filesky {10} | filelimitK {11} | file {12}",
                name, Assets.Scripts.Serialization.XmlSaveLoad.Instance?.CurrentStationName,
                EffectiveShown("GhgResponseScale"), EffectiveShown("MildAtmosphereColdestKelvin"),
                EffectiveShown("StormsStopWhenStripped"), EffectiveShown("DynamicSky"),
                AccessTools.Method(gate, "SkyEnabled").Invoke(null, null),
                EffectiveShown("MaxExternalOffsetKelvin"),
                Element(text, "GhgResponseScale"), Element(text, "MildAtmosphereColdestKelvin"),
                Element(text, "DynamicSky"), Element(text, "MaxExternalOffsetKelvin"),
                path ?? "none"));
        }

        private static string Element(string xml, string name)
        {
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(xml, "<" + name + ">([^<]*)</" + name + ">");
            if (m.Success)
            {
                return m.Groups[1].Value;
            }
            return System.Text.RegularExpressions.Regex.IsMatch(xml, "<" + name + " xsi:nil=\"true\"\\s*/>") ? "nil" : "absent";
        }

        private static Type StormsType => AccessTools.TypeByName("TerraformingReloaded.Patching.Storms");

        /// <summary>
        /// A world setting in force, set directly: the storm cases are about the rules, and the
        /// rules read the per-world values. terraform set itself is exercised by -Sessions.
        /// </summary>
        private static void SetSetting(string field, object value)
        {
            AccessTools.Field(AccessTools.TypeByName("TerraformingReloaded.Effective"), field).SetValue(null, value);
        }

        /// <summary>
        /// Every storm setting back to its shipped default before each case, so a case that moves one
        /// bound is about that bound alone.
        /// </summary>
        private static void ResetStormSettings()
        {
            SetSetting("StormsStopWhenStripped", true);
            SetSetting("StrippedAtmosphereShare", 5.0);
            SetSetting("StormsStopWhenAtmosphereIsMild", true);
            SetSetting("MildAtmosphereColdestKelvin", 263.15);
            SetSetting("MildAtmosphereHottestKelvin", 323.15);
            SetSetting("MildAtmosphereMinPressureKpa", 20.0);
            SetSetting("MildAtmosphereMaxPressureKpa", 607.95);
            SetSetting("MildAtmosphereMaxToxinsKpa", 1.0);
            SetSetting("MildAtmosphereStopsSolarStorms", false);
        }

        private static object StormSnapshot()
        {
            Type type = StormsType;
            return type == null ? null : AccessTools.Property(type, "Now").GetValue(null, null);
        }

        private static object Snap(object snapshot, string field)
        {
            return snapshot == null ? null : AccessTools.Field(snapshot.GetType(), field).GetValue(snapshot);
        }

        private static double SnapNumber(object snapshot, string field)
        {
            return Snap(snapshot, field) is double value ? value : double.NaN;
        }

        private static bool SnapFlag(object snapshot, string field)
        {
            return Snap(snapshot, field) is bool value && value;
        }

        // ---- moving the season --------------------------------------------------------------------

        private static double SolarPercent()
        {
            OrbitalSimulation simulation = OrbitalSimulation.System;
            return simulation.GetSolarEnergyPercentClamped(simulation.GetSolarEnergy(), simulation.CalculateSolarIrradiance());
        }

        /// <summary>
        /// SetSimulationTime is the public way into OrbitalSimulation.SetAllBodies, which is the only
        /// call that moves the bodies: writing SimulationTimeSeconds by hand leaves every distance,
        /// and so the solar percent the seasonal rule turns on, exactly where it was. One unit of
        /// simulation time is one degree of the world's own orbit, so 360 is a year
        /// (CelestialBody.Set sets the true anomaly to the longitude at epoch plus this).
        /// </summary>
        private static void SetOrbit(double simulationTime)
        {
            OrbitalSimulation.SetSimulationTime(simulationTime, publish: false);
        }

        private void MoveOrbit(double degrees, string why)
        {
            double before = OrbitalSimulation.System.SimulationTimeSeconds;
            double percentBefore = SolarPercent();
            SetOrbit(before + degrees);
            Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "LiveCheck: orbit {0} {1:0.###} degrees of a 360 degree year | simulation time {2:0.####} -> {3:0.####} | solar percent {4:0.####} -> {5:0.####} | irradiance {6:0.#####} W/m2",
                why, degrees, before, OrbitalSimulation.System.SimulationTimeSeconds,
                percentBefore, SolarPercent(), OrbitalSimulation.System.CalculateSolarIrradiance()));
        }

        // ---- choosing an air that is mild ----------------------------------------------------------

        /// <summary>The day's coldest and hottest, the way the rule takes them: 0 to 180 in steps of 5.</summary>
        private static double[] Envelope(GlobalGasMix tank, GlobalAtmosphereData data, float percent)
        {
            double coldest = double.MaxValue;
            double hottest = double.MinValue;
            for (int i = 0; i < 37; i++)
            {
                double kelvin = tank.GetGlobalGasMixTemperature(data, i * 5f, percent).ToDouble();
                if (kelvin < coldest)
                {
                    coldest = kelvin;
                }
                if (kelvin > hottest)
                {
                    hottest = kelvin;
                }
            }
            return new[] { coldest, hottest };
        }

        private static void SetTank(GlobalGasMix tank, double cells, double co2PerCell, double nitrogenPerCell)
        {
            tank.ClearQuantities(AtmosphereHelper.MatterState.All);
            tank.Set(new MoleQuantity(co2PerCell * cells), Chemistry.GasType.CarbonDioxide);
            tank.Set(new MoleQuantity(nitrogenPerCell * cells), Chemistry.GasType.Nitrogen);
        }

        private static readonly double[] MildTotals =
            { 70, 100, 140, 180, 220, 260, 320, 400, 500, 650, 800, 1000, 1300, 1600, 1900 };

        private string _mildAir;
        private double _mildColdest;
        private double _mildHottest;
        private double _mildPressureCold;
        private double _mildPressureHot;

        /// <summary>
        /// Searches carbon dioxide against nitrogen for a mix that is inside all five bounds on
        /// whatever world is loaded, using the game's own temperature formula, and leaves it on the
        /// planet. The search is a way to pick the air, not a check: what is judged afterwards is the
        /// mod's own verdict and the game's own scheduler. Carbon dioxide warms and nitrogen cools,
        /// so between them they span the greenhouse index, and the total sets the pressure.
        /// Returns what it set, or null when this world has no such air.
        /// </summary>
        private string FindMildAir()
        {
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            GlobalAtmosphereData data = WorldSetting.Current.Data.GlobalAtmosphereData;
            double cells = (tank.Volume / Chemistry.GridVolume).ToDouble();
            float percent = (float)SolarPercent();
            object gate = AccessTools.Field(typeof(PlanetaryAtmosphereSimulation), "GlobalInteraction").GetValue(null);
            double bestTotal = 0.0;
            double bestShare = 0.0;
            double bestMargin = double.MinValue;
            double bestCold = 0.0;
            double bestHot = 0.0;
            int tried = 0;
            lock (gate)
            {
                foreach (double total in MildTotals)
                {
                    for (int step = 0; step <= 20; step++)
                    {
                        tried++;
                        double share = step / 20.0;
                        SetTank(tank, cells, total * share, total * (1.0 - share));
                        double[] envelope = Envelope(tank, data, percent);
                        if (double.IsNaN(envelope[0]) || double.IsNaN(envelope[1]))
                        {
                            continue;
                        }
                        VolumeLitres gasVolume = tank.VolumeForGas();
                        MoleQuantity gas = tank.TotalQuantityGas();
                        double pressureCold = IdealGas.Pressure(gas, new TemperatureKelvin(envelope[0]), gasVolume).ToDouble();
                        double pressureHot = IdealGas.Pressure(gas, new TemperatureKelvin(envelope[1]), gasVolume).ToDouble();
                        if (envelope[0] < 263.15 || envelope[1] > 323.15 || pressureCold < 20.0 || pressureHot > 607.95)
                        {
                            continue;
                        }
                        // The mix furthest inside the two temperature bounds, so that moving one
                        // bound by a kelvin fails that bound and nothing else.
                        double margin = Math.Min(envelope[0] - 263.15, 323.15 - envelope[1]);
                        if (margin > bestMargin)
                        {
                            bestMargin = margin;
                            bestTotal = total;
                            bestShare = share;
                            bestCold = envelope[0];
                            bestHot = envelope[1];
                        }
                    }
                }
                if (bestMargin == double.MinValue)
                {
                    SetTank(tank, cells, 0.0, 0.0);
                    Logger.LogInfo("LiveCheck: storms FAIL no carbon dioxide and nitrogen mix on this world is inside all five bounds, so there is no mild case to judge (" + tried + " tried)");
                    return null;
                }
                SetTank(tank, cells, bestTotal * bestShare, bestTotal * (1.0 - bestShare));
            }
            _mildAir = string.Format(CultureInfo.InvariantCulture, "CarbonDioxide={0:0.####};Nitrogen={1:0.####}",
                bestTotal * bestShare, bestTotal * (1.0 - bestShare));
            Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "LiveCheck: storms note mild air {0} mol per outdoor cell, chosen from {1} mixes | the game's own formula puts its day between {2:0.###} K and {3:0.###} K at solar percent {4:0.###}",
                _mildAir, tried, bestCold, bestHot, percent));
            return _mildAir;
        }

        // ---- the world's own weather events --------------------------------------------------------

        private WeatherEvent _stormClone;

        /// <summary>
        /// Marks this world's own event as one that happens in orbit, which is what the game calls a
        /// solar storm and what the strip rule is never allowed to stop. Flipping the flag on the
        /// world's own event is how a world with no solar storm is made into one; it is put back
        /// before the run ends.
        /// </summary>
        private static void MakeWorldEventSolar(bool solar)
        {
            List<WeatherEvent> events = WorldSetting.Current.WeatherEvents;
            if (events.Count == 0)
            {
                throw new InvalidOperationException("this world ships no weather event to work with");
            }
            events[0].ActiveInOrbit = solar;
        }

        /// <summary>
        /// A second event beside the world's own, marked as a solar storm, so the world has one that
        /// may be suppressed and one that may not. That is the case the scheduler's own predicate
        /// must not answer, because answering it would stop the solar storm too.
        /// </summary>
        private void AddSolarClone()
        {
            List<WeatherEvent> events = WorldSetting.Current.WeatherEvents;
            if (_stormClone == null)
            {
                _stormClone = new WeatherEvent();
                foreach (System.Reflection.FieldInfo field in typeof(WeatherEvent).GetFields(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    field.SetValue(_stormClone, field.GetValue(events[0]));
                }
                _stormClone.Id = "TRLiveCheckSolarStorm";
                _stormClone.ActiveInOrbit = true;
            }
            if (!events.Contains(_stormClone))
            {
                events.Add(_stormClone);
            }
        }

        private void RemoveSolarClone()
        {
            if (_stormClone != null)
            {
                WorldSetting.Current.WeatherEvents.Remove(_stormClone);
            }
        }

        // ---- the cases ------------------------------------------------------------------------------

        private List<GlobalMoleData> _shippedGases;
        private List<WeatherEvent> _shippedEvents;
        private double _seasonWarmTime;
        private double _seasonColdTime;
        private double _seasonFloor;

        private void StormsStep()
        {
            string name = _stormsJudging ?? "setup";
            try
            {
                if (StormsType == null)
                {
                    Logger.LogInfo("LiveCheck: storms FAIL the mod's Storms type was not found");
                    _stormsDone = true;
                    return;
                }
                if (_stormsJudging != null)
                {
                    JudgeStorms(_stormsJudging);
                    _stormsJudging = null;
                }
                name = "case " + _stormsCase;
                _stormsJudging = SetUpStormsCase(_stormsCase++);
                if (_stormsJudging == null)
                {
                    _stormsDone = true;
                    Logger.LogInfo("LiveCheck: storms done | tick " + GameManager.GameTickCount);
                }
            }
            catch (Exception e)
            {
                _stormsDone = true;
                Logger.LogInfo("LiveCheck: storms FAIL " + name + " " + e);
            }
        }

        /// <summary>Air well under any threshold: about a four-hundredth of what Mars ships.</summary>
        private const string StrippedAir = "CarbonDioxide=0.02;Nitrogen=0.005";

        private string SetUpStormsCase(int index)
        {
            ResetStormSettings();
            GlobalAtmosphereData data = WorldSetting.Current.Data.GlobalAtmosphereData;
            switch (index)
            {
                case 0:
                    // The world exactly as it ships. The mod's own reset is the way back to it, so
                    // every case after this starts from a planet the game would recognise.
                    MakeWorldEventSolar(false);
                    RemoveSolarClone();
                    RunCommand("reset", "confirm");
                    return "untouched";
                case 1:
                    SetPlanetAir(StrippedAir, keepOthers: false);
                    return "stripped";
                case 2:
                    // Removing air cannot stop radiation, so the same stripped planet must still get
                    // this one.
                    MakeWorldEventSolar(true);
                    return "stripped-solar";
                case 3:
                    MakeWorldEventSolar(false);
                    AddSolarClone();
                    return "stripped-mixed";
                case 4:
                    // A world that ships with no air at all: a share of nothing is nothing, so the
                    // rule must not apply to it. The baseline is read from this list and nowhere else.
                    MakeWorldEventSolar(false);
                    RemoveSolarClone();
                    _shippedGases = new List<GlobalMoleData>(data.GlobalGasMixData.GlobalMoleDatas);
                    data.GlobalGasMixData.GlobalMoleDatas.Clear();
                    return "airless";
                case 5:
                    data.GlobalGasMixData.GlobalMoleDatas.AddRange(_shippedGases);
                    return FindMildAir() == null ? null : "mild";
                case 6:
                    SetSetting("StormsStopWhenAtmosphereIsMild", false);
                    return "mild-rule-off";
                case 7:
                    SetSetting("MildAtmosphereColdestKelvin", _mildColdest + 1.0);
                    return "cold-floor";
                case 8:
                    SetSetting("MildAtmosphereHottestKelvin", _mildHottest - 1.0);
                    return "hot-ceiling";
                case 9:
                    SetSetting("MildAtmosphereMinPressureKpa", _mildPressureCold + 1.0);
                    return "pressure-min";
                case 10:
                    SetSetting("MildAtmosphereMaxPressureKpa", _mildPressureHot - 1.0);
                    return "pressure-max";
                case 11:
                {
                    // Hydrazine has no greenhouse index curve in the game's own terraforming data, so
                    // it moves the toxin load and leaves the greenhouse index exactly where it was.
                    // Three times the ceiling, worked out with the game's own gas law.
                    double moles = IdealGas.Quantity(new PressurekPa(3.0), new VolumeLitres(Chemistry.GridVolume.ToDouble()),
                        new TemperatureKelvin(_mildHottest)).ToDouble();
                    SetPlanetAir(string.Format(CultureInfo.InvariantCulture, "Hydrazine={0:0.#####}", moles), keepOthers: true);
                    return "toxins";
                }
                case 12:
                {
                    SetPlanetAir(_mildAir, keepOthers: false);
                    PickSeasons();
                    SetOrbit(_seasonWarmTime);
                    SetSetting("MildAtmosphereColdestKelvin", _seasonFloor);
                    return "season-warm";
                }
                case 13:
                    SetPlanetAir(_mildAir, keepOthers: false);
                    SetOrbit(_seasonColdTime);
                    SetSetting("MildAtmosphereColdestKelvin", _seasonFloor);
                    return "season-cold";
                case 14:
                    SetOrbit(_seasonWarmTime);
                    MakeWorldEventSolar(true);
                    return "solar-mild-off";
                case 15:
                    MakeWorldEventSolar(true);
                    SetSetting("MildAtmosphereStopsSolarStorms", true);
                    return "solar-mild-on";
                case 16:
                    // A world that ships no weather of its own, with the setting that lets one rain
                    // switched off. Nothing may be scheduled on it at all, by the game or by the mod.
                    MakeWorldEventSolar(false);
                    RemoveSolarClone();
                    _shippedEvents = new List<WeatherEvent>(WorldSetting.Current.Data.WeatherEvents);
                    WorldSetting.Current.Data.WeatherEvents.Clear();
                    SetSetting("WeatherOnWeatherlessWorlds", false);
                    return "weatherless";
                case 17:
                    // The readout's last line. Fill a cloud on the weatherless world with the setting
                    // off and let the planet tick run: it empties the cloud and asks for rain, the
                    // mod refuses, and the refusal is counted. Nothing here fakes the refusal.
                    SetSetting("WeatherOnWeatherlessWorlds", false);
                    FillLiquidClouds();
                    return "rain-held";
                case 18:
                    // Read the count the tick left, then put everything back where it started, one
                    // case early, so the last readout is taken of a settled world.
                    RunCommand("status");
                    WorldSetting.Current.Data.WeatherEvents.AddRange(_shippedEvents);
                    SetSetting("WeatherOnWeatherlessWorlds", true);
                    MakeWorldEventSolar(false);
                    RemoveSolarClone();
                    return "settled";
                default:
                    RunCommand("status");
                    return null;
            }
        }

        /// <summary>
        /// Walks a whole year in steps of ten degrees and records where the day's coldest point is
        /// highest and where it is lowest, then puts a temperature floor between the two. That is
        /// what makes the season, and nothing else, decide the verdict of the two cases that follow.
        /// </summary>
        private void PickSeasons()
        {
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            GlobalAtmosphereData data = WorldSetting.Current.Data.GlobalAtmosphereData;
            double start = OrbitalSimulation.System.SimulationTimeSeconds;
            double warmest = double.MinValue;
            double coldest = double.MaxValue;
            for (int i = 0; i < 36; i++)
            {
                SetOrbit(start + i * 10.0);
                double[] envelope = Envelope(tank, data, (float)SolarPercent());
                if (envelope[0] > warmest)
                {
                    warmest = envelope[0];
                    _seasonWarmTime = start + i * 10.0;
                }
                if (envelope[0] < coldest)
                {
                    coldest = envelope[0];
                    _seasonColdTime = start + i * 10.0;
                }
            }
            _seasonFloor = 0.5 * (warmest + coldest);
            Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "LiveCheck: storms note season | coldest point of the day is {0:0.###} K at its warmest season and {1:0.###} K at its coldest, {2:0.###} K apart | floor put at {3:0.###} K",
                warmest, coldest, warmest - coldest, _seasonFloor));
        }

        /// <summary>
        /// Puts enough water in the liquid clouds that the tick's own test, its litres of liquid
        /// against its own volume, is true. It doubles until the cloud's own answer says it is full
        /// rather than working the amount out from a molar volume: the cloud can already hold other
        /// liquids, which a single measurement of one mole of water counts as if they were water.
        /// </summary>
        private void FillLiquidClouds()
        {
            GlobalGasMix clouds = (GlobalGasMix)AccessTools.Field(typeof(PlanetaryAtmosphereSimulation), "_liquidClouds").GetValue(null);
            object gate = AccessTools.Field(typeof(PlanetaryAtmosphereSimulation), "GlobalInteraction").GetValue(null);
            lock (gate)
            {
                double water = 1000.0;
                for (int attempt = 0; attempt < 40; attempt++)
                {
                    clouds.Set(new MoleQuantity(water), Chemistry.GasType.Water);
                    if (clouds.VolumeOfLiquid().ToDouble() >= clouds.Volume.ToDouble())
                    {
                        break;
                    }
                    water *= 2.0;
                }
                Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "LiveCheck: storms note cloud | {0:0.###} mol of water | {1:0.###} of {2:0.###} L | full {3}",
                    water, clouds.VolumeOfLiquid().ToDouble(), clouds.Volume.ToDouble(),
                    clouds.VolumeOfLiquid().ToDouble() >= clouds.Volume.ToDouble()));
            }
        }

        /// <summary>
        /// The game's own cooldowns out of the way, so what is left deciding is the mod. A world-start
        /// cooldown of seven days and an event cooldown of three to twelve is what no headless run can
        /// wait out.
        /// </summary>
        private static void ClearWeatherState()
        {
            AccessTools.PropertySetter(typeof(Weather.WeatherManager), "CurrentWeatherEvent").Invoke(null, new object[] { null });
            Weather.WeatherManager.DaysSinceLastWeatherEvent = 1000;
            Weather.WeatherManager.LastEventCoolDown = 0;
            AccessTools.Field(typeof(WorldManager), "_daysPast").SetValue(null, 1000u);
        }

        /// <summary>
        /// The two lines WeatherManager.ManagerUpdate runs every frame, run here from the same
        /// thread, and retried the way it retries them: on a world where only some events are
        /// suppressed the pick is turned away and the next frame picks again.
        /// </summary>
        private void JudgeStorms(string name)
        {
            ClearWeatherState();
            bool can = false;
            bool scheduled = false;
            string picked = "none";
            int tries = 0;
            for (tries = 1; tries <= 25; tries++)
            {
                can = Weather.WeatherManager.CanScheduleWeatherEvent();
                if (!can)
                {
                    break;
                }
                Weather.WeatherManager.ScheduleWeatherEvent(Weather.WeatherManager.GetNextWeatherEvent());
                scheduled = Weather.WeatherManager.IsWeatherEventScheduled;
                if (scheduled)
                {
                    picked = Weather.WeatherManager.CurrentWeatherEvent?.Id ?? "(no id)";
                    break;
                }
            }
            // One pick is one roll of the game's shared Random. On a world with one event that may be
            // scheduled and one that may not, a single round can only say that something got through;
            // forty say that the ordinary storm was turned away every time it came up, which is the
            // branch the scheduler's own predicate deliberately does not take.
            if (name == "stripped-mixed")
            {
                int gotSolar = 0;
                int gotOther = 0;
                int gotNothing = 0;
                for (int round = 0; round < 40; round++)
                {
                    ClearWeatherState();
                    if (!Weather.WeatherManager.CanScheduleWeatherEvent())
                    {
                        gotNothing++;
                        continue;
                    }
                    Weather.WeatherManager.ScheduleWeatherEvent(Weather.WeatherManager.GetNextWeatherEvent());
                    WeatherEvent got = Weather.WeatherManager.CurrentWeatherEvent;
                    if (got == null)
                    {
                        gotNothing++;
                    }
                    else if (got.ActiveInOrbit)
                    {
                        gotSolar++;
                    }
                    else
                    {
                        gotOther++;
                    }
                }
                Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "LiveCheck: storms note mixed | rounds 40 | solar {0} | not solar {1} | nothing {2}", gotSolar, gotOther, gotNothing));
            }
            object snapshot = StormSnapshot();
            object failures = Snap(snapshot, "MildFailures");
            string failed = "none";
            if (failures is List<string> list && list.Count > 0)
            {
                failed = string.Join(" / ", list.ToArray());
            }
            if (name == "mild")
            {
                _mildColdest = SnapNumber(snapshot, "Coldest");
                _mildHottest = SnapNumber(snapshot, "Hottest");
                _mildPressureCold = SnapNumber(snapshot, "PressureCold");
                _mildPressureHot = SnapNumber(snapshot, "PressureHot");
            }
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "LiveCheck: storms {0} | tick {1} | can {2} | scheduled {3} | event {4} | tries {5} | stripped {6} | share {7:0.#####} | airless {8} | mild {9} | mildknown {10} | coldest {11:0.###} | hottest {12:0.###} | pcold {13:0.####} | phot {14:0.####} | toxins {15:0.#####} | orbit {16:0.###} | tank {17:0.###} | fail {18}",
                name, GameManager.GameTickCount, can, scheduled, picked, tries,
                SnapFlag(snapshot, "Stripped"), SnapNumber(snapshot, "Share"), SnapFlag(snapshot, "Airless"),
                SnapFlag(snapshot, "Mild"), SnapFlag(snapshot, "MildKnown"),
                SnapNumber(snapshot, "Coldest"), SnapNumber(snapshot, "Hottest"),
                SnapNumber(snapshot, "PressureCold"), SnapNumber(snapshot, "PressureHot"),
                SnapNumber(snapshot, "Toxins"), SnapNumber(snapshot, "OrbitPercent"),
                tank == null ? 0.0 : tank.TotalQuantity().ToDouble(), failed.Replace("|", "/")));
            ClearWeatherState();
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

        // ---- per-world settings ----------------------------------------------------------------
        //
        // What this judges is the mod's own read path, called on real files in the real world folder
        // of the world being played: the file is rewritten one shape at a time and
        // Sidecar.WorldStartPrefix is invoked on each, which is exactly what the game calls at a
        // world start. Nothing here reimplements the rule.
        //
        // The config is set to ask for a pressure ceiling, and for five other distinctive values,
        // before the first case. Every line prints the config's ceiling beside the one in force, so
        // a run where the config was not asking cannot be read as a pass, and the five make a
        // fallback to the config tell itself apart from a value the file recorded.

        private static string SidecarFolder()
        {
            return Path.Combine(StationSaveUtils.GetSavePathSavesSubDir().FullName,
                Assets.Scripts.Serialization.XmlSaveLoad.Instance.CurrentStationName ?? "");
        }

        private static Type SidecarType => AccessTools.TypeByName("TerraformingReloaded.Patching.Sidecar");

        private static Type EffectiveType => AccessTools.TypeByName("TerraformingReloaded.Effective");

        private static void SetConfig(string field, double value)
        {
            AccessTools.Field(AccessTools.TypeByName("TerraformingReloaded.Settings"), field).SetValue(null, value);
        }

        private static string Shown(object value)
        {
            if (value == null)
            {
                return "none";
            }
            return value is double d ? d.ToString("R", CultureInfo.InvariantCulture) : value.ToString();
        }

        private static string EffectiveShown(string member)
        {
            System.Reflection.PropertyInfo property = AccessTools.Property(EffectiveType, member);
            if (property != null)
            {
                return Shown(property.GetValue(null, null));
            }
            return Shown(AccessTools.Field(EffectiveType, member).GetValue(null));
        }

        /// <summary>
        /// A settings file with exactly the elements asked for. null leaves an element out, which is
        /// what a version that did not have the field would have written; "nil" writes it as not
        /// recorded; anything else is written literally, so "-5", "NaN" and "-INF" reach the mod's
        /// reader as the file would really carry them.
        /// </summary>
        private static string SidecarXml(int version, string ceiling, string limitK, string halfLife,
            string ghg, string density, string albedo)
        {
            System.Text.StringBuilder x = new System.Text.StringBuilder();
            x.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n");
            x.Append("<TerraformingReloaded xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" Version=\"")
                .Append(version.ToString(CultureInfo.InvariantCulture)).Append("\">\r\n");
            SidecarElement(x, "MaxPressureKPa", ceiling);
            SidecarElement(x, "MaxExternalOffsetKelvin", limitK);
            SidecarElement(x, "ExternalHeatHalfLifeMinutes", halfLife);
            SidecarElement(x, "GhgResponseScale", ghg);
            SidecarElement(x, "DensityResponseScale", density);
            SidecarElement(x, "AirlessAlbedo", albedo);
            x.Append("</TerraformingReloaded>\r\n");
            return x.ToString();
        }

        private static void SidecarElement(System.Text.StringBuilder x, string name, string value)
        {
            if (value == null)
            {
                return;
            }
            x.Append(value == "nil"
                ? "  <" + name + " xsi:nil=\"true\" />\r\n"
                : "  <" + name + ">" + value + "</" + name + ">\r\n");
        }

        /// <summary>A file with every field good, so a case can say which one field it is about.</summary>
        private static string GoodSidecar(string ceiling = "1234", string limitK = "123", string halfLife = "45",
            string ghg = "2.5", string density = "0.75", string albedo = "0.6", int version = 1)
        {
            return SidecarXml(version, ceiling, limitK, halfLife, ghg, density, albedo);
        }

        private void SidecarStep()
        {
            string name = "?";
            try
            {
                if (SidecarType == null || EffectiveType == null)
                {
                    Logger.LogInfo("LiveCheck: sidecar FAIL the mod's Sidecar or Effective type was not found");
                    _sidecarDone = true;
                    return;
                }
                if (_sidecarCase == 0)
                {
                    _sidecarStation = Assets.Scripts.Serialization.XmlSaveLoad.Instance.CurrentStationName;
                    SetConfig("MaxPressureKPa", SidecarConfigCeiling);
                    SetConfig("MaxExternalOffsetKelvin", 77.0);
                    SetConfig("ExternalHeatHalfLifeMinutes", 88.0);
                    SetConfig("GhgResponseScale", 1.5);
                    SetConfig("DensityResponseScale", 0.25);
                    SetConfig("AirlessAlbedo", 0.4);
                    Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                        "LiveCheck: sidecar setup | station {0} | folder {1} | config ceiling {2:0.###} limitK 77 halflife 88 ghg 1.5 density 0.25 albedo 0.4",
                        _sidecarStation, SidecarFolder(), SidecarConfigCeiling));
                }

                string folder = SidecarFolder();
                string path = Path.Combine(folder, "terraforming-reloaded.xml");
                string salvaged = Path.Combine(folder, "terraforming-reloaded.broken.xml");
                bool read = true;

                switch (_sidecarCase)
                {
                    case 0:
                        name = "good";
                        File.WriteAllText(path, GoodSidecar());
                        break;
                    case 1:
                        name = "ceiling-zero";
                        File.WriteAllText(path, GoodSidecar(ceiling: "0"));
                        break;
                    case 2:
                        name = "ceiling-negative";
                        File.WriteAllText(path, GoodSidecar(ceiling: "-5"));
                        break;
                    case 3:
                        name = "ceiling-huge";
                        File.WriteAllText(path, GoodSidecar(ceiling: "99999999"));
                        break;
                    case 4:
                        name = "ceiling-minus-infinity";
                        File.WriteAllText(path, GoodSidecar(ceiling: "-INF"));
                        break;
                    case 5:
                        name = "every-field-negative";
                        File.WriteAllText(path, SidecarXml(1, "-5", "-5", "-5", "-5", "-5", "-5"));
                        break;
                    case 6:
                        name = "scales-not-a-number";
                        File.WriteAllText(path, GoodSidecar(ceiling: "nil", ghg: "NaN", density: "INF", albedo: "NaN"));
                        break;
                    case 7:
                        name = "halflife-zero";
                        File.WriteAllText(path, GoodSidecar(halfLife: "0"));
                        break;
                    case 8:
                        name = "halflife-not-recorded";
                        File.WriteAllText(path, GoodSidecar(halfLife: "nil"));
                        break;
                    case 9:
                        name = "only-one-field";
                        File.WriteAllText(path, SidecarXml(1, null, null, null, "2.5", null, null));
                        break;
                    case 10:
                        name = "missing";
                        File.Delete(path);
                        File.Delete(salvaged);
                        break;
                    case 11:
                        name = "broken";
                        File.Delete(salvaged);
                        File.WriteAllText(path, "this is not xml at all, and the player hand edited it");
                        break;
                    case 12:
                        name = "no-station-name";
                        File.WriteAllText(path, GoodSidecar());
                        Assets.Scripts.Serialization.XmlSaveLoad.Instance.CurrentStationName = "";
                        break;
                    case 13:
                        name = "folder-not-there";
                        Assets.Scripts.Serialization.XmlSaveLoad.Instance.CurrentStationName = _sidecarStation + "-moved-away";
                        break;
                    case 14:
                        name = "control-armed";
                        File.WriteAllText(path, GoodSidecar());
                        break;
                    case 15:
                        // The control. Everything above says a ceiling was NOT switched on; this says
                        // that a ceiling switched on the one way it may be does delete the planet's
                        // air, so "off" above is a result and not a rule that never runs.
                        name = "control-bites";
                        read = false;
                        RunCommand("set", "MaxPressureKPa", "0.5", "confirm");
                        break;
                    case 16:
                        name = "control-cleared";
                        read = false;
                        RunCommand("set", "MaxPressureKPa", "0", "confirm");
                        break;
                    case 17:
                        name = "future-version";
                        File.WriteAllText(path, GoodSidecar(version: SidecarFutureVersion));
                        break;
                    case 18:
                        name = "stood-down";
                        File.WriteAllText(path, GoodSidecar());
                        break;
                    default:
                        Assets.Scripts.Serialization.XmlSaveLoad.Instance.CurrentStationName = _sidecarStation;
                        _sidecarDone = true;
                        Logger.LogInfo("LiveCheck: sidecar done | tick " + GameManager.GameTickCount);
                        return;
                }

                if (read)
                {
                    AccessTools.DeclaredMethod(SidecarType, "WorldStartPrefix").Invoke(null, null);
                }
                ReportSidecar(name, path, salvaged);

                // Anything the case has to say for itself beyond the six values in force.
                switch (name)
                {
                    case "missing":
                        Logger.LogInfo("LiveCheck: sidecar note missing | rewritten " + File.Exists(path)
                            + " | salvaged copy " + File.Exists(salvaged));
                        break;
                    case "broken":
                        Logger.LogInfo("LiveCheck: sidecar note broken | salvaged copy " + File.Exists(salvaged)
                            + " | copy holds the hand edit "
                            + (File.Exists(salvaged) && File.ReadAllText(salvaged).Contains("hand edited"))
                            + " | rewritten as xml "
                            + (File.Exists(path) && File.ReadAllText(path).Contains("<TerraformingReloaded")));
                        break;
                    case "no-station-name":
                    case "folder-not-there":
                        Assets.Scripts.Serialization.XmlSaveLoad.Instance.CurrentStationName = _sidecarStation;
                        break;
                    case "future-version":
                    case "stood-down":
                        Logger.LogInfo("LiveCheck: sidecar note " + name + " | file on disk still says version "
                            + SidecarVersionOnDisk(path) + " | still holds 1234 "
                            + (File.Exists(path) && File.ReadAllText(path).Contains("1234")));
                        break;
                }
                _sidecarCase++;
            }
            catch (Exception e)
            {
                _sidecarDone = true;
                Assets.Scripts.Serialization.XmlSaveLoad.Instance.CurrentStationName = _sidecarStation;
                Logger.LogInfo("LiveCheck: sidecar FAIL " + name + " " + e);
            }
        }

        private const int SidecarFutureVersion = 99;

        private static string SidecarVersionOnDisk(string path)
        {
            if (!File.Exists(path))
            {
                return "no file";
            }
            System.Text.RegularExpressions.Match m =
                System.Text.RegularExpressions.Regex.Match(File.ReadAllText(path), "Version=\"(\\d+)\"");
            return m.Success ? m.Groups[1].Value : "none";
        }

        private void ReportSidecar(string name, string path, string salvaged)
        {
            object refusal = AccessTools.DeclaredMethod(SidecarType, "RecordRefusal").Invoke(null, null);
            object file = AccessTools.Property(SidecarType, "FilePath").GetValue(null, null);
            object version = AccessTools.Property(SidecarType, "FileVersion").GetValue(null, null);
            object source = AccessTools.Property(SidecarType, "Source").GetValue(null, null);
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "LiveCheck: sidecar {0} | tick {1} | config ceiling {2:0.###} | ceiling {3} | halflife {4} | limitK {5} | ghg {6} | density {7} | albedo {8} | version {9} | tank {10:0.000} | file {11} | refusal {12} | source {13}",
                name, GameManager.GameTickCount,
                (double)AccessTools.Field(AccessTools.TypeByName("TerraformingReloaded.Settings"), "MaxPressureKPa").GetValue(null),
                EffectiveShown("MaxPressureKPa"), EffectiveShown("ExternalHeatHalfLifeMinutes"),
                EffectiveShown("MaxExternalOffsetKelvin"), EffectiveShown("GhgResponseScale"),
                EffectiveShown("DensityResponseScale"), EffectiveShown("AirlessAlbedo"),
                version, tank == null ? 0.0 : tank.TotalQuantity().ToDouble(),
                file == null ? "none" : file.ToString(),
                refusal == null ? "none" : refusal.ToString().Replace("|", "/"),
                source == null ? "none" : source.ToString().Replace("|", "/")));
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
