using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using BepInEx.Logging;
using HarmonyLib;
using TerraformingReloaded.Patching;
using UnityEngine;

namespace TerraformingReloaded.LiveCheck
{
    /// <summary>
    /// -PlanetBurn: the planet's air burns (docs/PLANET-COMBUSTION.md, Appendix H, headless cases 1 to 5).
    ///
    /// The driver keeps a block of 20 outdoor cells beside open ground, standing in for the cells
    /// around a base (each is kept from being culled, as a structure on it would keep it), and a leak
    /// cell 200 m away that it fills directly. Actions run on the main thread (<see cref="Step"/>).
    /// Every reading is taken inside the mod's own planet tick, by a postfix on
    /// PlanetCombustion.PublishHold: there the cells are at rest after the last tick's mixing and
    /// burning, and the hold-back that was in force during that mixing is the one published the tick
    /// before. One line per tick for the first 400 ticks after the release, then every tenth.
    ///
    /// Leaks are absolute (a cell holds what was put in it, whatever the planet's size); what goes
    /// into the planet itself is scaled to the planet's size against the 250,000 outdoor cells the
    /// specification measures with, so every per-cell figure is the specification's.
    /// </summary>
    internal sealed class PlanetBurnCheck
    {
        private const double SpecCells = 250000.0;
        private const int BlockSide = 5;
        private const int BlockDepth = 4;
        private const float BlockHeight = 300f;
        private const float BlockX = 200f;
        private const float BlockZ = 200f;
        private const float LeakRadius = 40f;
        private const double DayBelowDegrees = 70.0;
        private const double NightAboveDegrees = 95.0;   // the sun below the horizon; Vulcan2's headless site never sees it past about 109 degrees
        private const uint PhaseTimeoutTicks = 4000;
        private const uint ShareTimeoutTicks = 600;
        private const uint OutForTicks = 240;

        private static PlanetBurnCheck _current;

        private readonly ManualLogSource _log;
        private readonly Action<string[]> _command;
        private readonly int _case;
        private readonly bool _on;
        private readonly uint _startTick;
        private readonly uint _ticks;
        private readonly double _daySpeed;

        private enum Stage { Setup, WaitShare, WaitPhase, Release, Running, Done }

        private volatile Stage _stage = Stage.Setup;
        private uint _stageTick;
        private double _scale = double.NaN;
        private readonly List<Atmosphere> _block = new List<Atmosphere>(BlockSide * BlockDepth);
        private Atmosphere _leak;
        private Atmosphere _sparked;
        private WorldGrid _leakGrid;
        private uint _releaseTick;

        // Sampling state, planet tick only.
        private HeldGases _heldDuringLastMixing;
        private Dictionary<Chemistry.GasType, double> _blockBefore;
        private Dictionary<Chemistry.GasType, double> _totalsAtRelease;
        private int _violations;
        private string _firstViolation;
        private uint _firstBurnTick;
        private uint _lastBurnTick;
        private bool _sawHeat;
        private bool _sawSpark;
        private HeldGases _firstHeld;
        private double _leadOxidiser;
        private double _blockOxidiserMax;
        private uint _lastBlockInflamed;
        private int _blockEverInflamed;
        private uint _lastSparkedInflamed;
        private int _leakInflamedAtNight;
        private int _rateMismatches;
        private string _firstRateMismatch;
        private double _fireHeatMaxKelvin;
        private double _blockHottest;
        private double _shareBeforeOn = double.NaN;
        private bool _flashSeen;

        private PlanetBurnCheck(ManualLogSource log, Action<string[]> command, int which, bool on, uint startTick, uint ticks, double daySpeed)
        {
            _log = log;
            _command = command;
            _case = which;
            _on = on;
            _startTick = startTick;
            _ticks = ticks;
            _daySpeed = daySpeed;
        }

        /// <summary>The check run.ps1 asked for, or null when this is another scenario.</summary>
        public static PlanetBurnCheck FromEnvironment(ManualLogSource log, Action<string[]> command, double daySpeed)
        {
            if (!int.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_BURN_CASE"), out int which) || which < 1 || which > 5)
            {
                return null;
            }
            bool on = Environment.GetEnvironmentVariable("TR_LIVECHECK_BURN_SWITCH") != "off";
            uint start = uint.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_BURN_TICK"), out uint s) ? s : 30u;
            uint ticks = uint.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_BURN_TICKS"), out uint t) ? t : 4800u;
            return new PlanetBurnCheck(log, command, which, on, start, ticks, daySpeed);
        }

        /// <summary>The postfix every reading is taken from. Refused, with the reason, if the mod's upkeep is not where it was.</summary>
        public void Install(Harmony harmony)
        {
            _current = this;
            System.Reflection.MethodInfo upkeep = AccessTools.DeclaredMethod(typeof(PlanetCombustion), "PublishHold");
            if (upkeep == null)
            {
                Fail("the mod's PlanetCombustion.PublishHold was not found");
                return;
            }
            harmony.Patch(upkeep, postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(PlanetBurnCheck), nameof(UpkeepPostfix))));
            // The totals the products are judged against, taken at the start of the first planet tick after
            // the release, before its burn: cells are at rest there. Taken on the main thread they raced the
            // atmosphere workers and could catch a cell's exchange half done (about half a cell's worth).
            harmony.Patch(AccessTools.DeclaredMethod(typeof(PlanetCombustion), "Upkeep"),
                prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(PlanetBurnCheck), nameof(UpkeepPrefix))));
            // The block's own burns, read at the burn itself: what a cell held when it burnt this tick, what
            // it burnt and the heat. A cell that is handed oxidiser in a tick's mixing and burns it in the same
            // tick never shows it to the upkeep, so this is the only way to see the lead.
            System.Reflection.MethodInfo tryCombust = AccessTools.DeclaredMethod(typeof(Atmosphere), "TryCombust", new[] { typeof(double), typeof(bool) });
            harmony.Patch(tryCombust,
                prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(PlanetBurnCheck), nameof(TryCombustPrefix))),
                postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(PlanetBurnCheck), nameof(TryCombustPostfix))));
        }

        public bool Finished => _stage == Stage.Done;

        // ---- actions, main thread ------------------------------------------------------------------

        public void Step()
        {
            uint tick = GameManager.GameTickCount;
            switch (_stage)
            {
                case Stage.Setup when tick >= _startTick:
                    SetUp(tick);
                    break;
                case Stage.WaitShare:
                    if (BlockHoldsItsShare() || tick - _stageTick >= ShareTimeoutTicks)
                    {
                        _shareBeforeOn = BlockShare();
                        Next(Stage.WaitPhase, tick);
                    }
                    break;
                case Stage.WaitPhase:
                    if (RightTimeOfDay())
                    {
                        Release(tick);
                    }
                    else if (tick - _stageTick >= PhaseTimeoutTicks)
                    {
                        Fail(string.Format(CultureInfo.InvariantCulture, "no {0} came within {1} ticks (sun at {2:0.0} degrees)", WantsNight ? "night" : "day", PhaseTimeoutTicks, SunAngle()));
                    }
                    break;
                case Stage.Running:
                    if (Over(tick))
                    {
                        Judge();
                    }
                    break;
            }
        }

        private void SetUp(uint tick)
        {
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            if (!PlanetCombustion.Installed || tank == null)
            {
                Fail("the mod did not install the planet fire: " + (PlanetCombustion.Refusal ?? "no planet"));
                return;
            }
            _scale = (tank.Volume / Chemistry.GridVolume).ToDouble() / SpecCells;
            if (_daySpeed > 0.0)
            {
                OrbitalSimulation.SetTimeScale((float)(OrbitalSimulation.System.TimeScale * _daySpeed));
            }
            SetSwitch(_case == 3 ? false : _on);
            for (int i = 0; i < BlockSide; i++)
            {
                for (int j = 0; j < BlockDepth; j++)
                {
                    WorldGrid grid = new WorldGrid(new Vector3(BlockX + 2f * i, BlockHeight, BlockZ + 2f * j));
                    Atmosphere cell = AtmosphericsManager.CloneGlobalAtmosphereThreadSafe(grid);
                    if (cell == null)
                    {
                        Fail("no outdoor cell could be made for the block at " + grid);
                        return;
                    }
                    cell.IsAwaitingEvent = true;
                    _block.Add(cell);
                }
            }
            _leakGrid = new WorldGrid(new Vector3(0f, BlockHeight, 0f));
            _log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "LiveCheck: burn setup case {0} switch {1}: planet of {2:N0} cells (scale {3:0.####} of the specification's), block of {4} cells, sun at {5:0.0} degrees",
                _case, _on ? "on" : "off", _scale * SpecCells, _scale, _block.Count, SunAngle()));
            switch (_case)
            {
                case 3:
                    Command("gas", "add", "Oxygen", Moles(50000.0));
                    Next(Stage.WaitShare, tick);
                    return;
                case 4:
                    Command("gas", "add", "Methane", Moles(1000.0));
                    break;
            }
            Next(Stage.WaitPhase, tick);
        }

        private void Release(uint tick)
        {
            switch (_case)
            {
                case 1:
                case 5:
                    _leak = AtmosphericsManager.CloneGlobalAtmosphereThreadSafe(_leakGrid);
                    if (_leak == null)
                    {
                        Fail("no outdoor cell could be made for the leak at " + _leakGrid);
                        return;
                    }
                    Add(_leak, Chemistry.GasType.Oxygen, 5000.0);
                    break;
                case 2:
                    Command("gas", "add", "Oxygen", Moles(1000.0));
                    Command("gas", "add", "NitrousOxide", Moles(1000.0));
                    break;
                case 3:
                    SetSwitch(_on);
                    break;
                case 4:
                    // Its own cell, beside open ground like the block but away from it, so what it burns
                    // cannot reach the block by cell-to-cell mixing and read as the planet handing it out.
                    _sparked = AtmosphericsManager.CloneGlobalAtmosphereThreadSafe(new WorldGrid(new Vector3(-200f, BlockHeight, -200f)));
                    if (_sparked == null)
                    {
                        Fail("no outdoor cell could be made for the fire that sparks the planet");
                        return;
                    }
                    _sparked.IsAwaitingEvent = true;
                    Add(_sparked, Chemistry.GasType.Oxygen, 5.0);
                    Add(_sparked, Chemistry.GasType.Methane, 10.0);
                    _sparked.Sparked = true;
                    break;
            }
            _releaseTick = tick;
            _totalsAtRelease = null;
            _log.LogInfo(string.Format(CultureInfo.InvariantCulture, "LiveCheck: burn release case {0} at tick {1}, sun at {2:0.0} degrees", _case, tick, SunAngle()));
            Next(Stage.Running, tick);
        }

        private bool Over(uint tick)
        {
            if (tick - _releaseTick >= _ticks)
            {
                return true;
            }
            // Out for a while after a fire, or long enough to have seen one on a control run.
            bool burntAndOut = _firstBurnTick > 0 && _lastBurnTick + OutForTicks <= tick;
            return burntAndOut || (!_on && tick - _releaseTick >= 1200);
        }

        // ---- readings, planet tick -----------------------------------------------------------------

        public static void UpkeepPrefix()
        {
            PlanetBurnCheck check = _current;
            if (check != null && check._stage == Stage.Running && check._totalsAtRelease == null)
            {
                check._totalsAtRelease = PlanetTotals(PlanetaryAtmosphereSimulation.GetGlobalGasMix());
            }
        }

        public static void UpkeepPostfix()
        {
            PlanetBurnCheck check = _current;
            if (check == null || check._stage == Stage.Setup || check._stage == Stage.Done)
            {
                return;
            }
            try
            {
                check.Sample();
            }
            catch (Exception e)
            {
                check.Fail("a reading threw: " + e);
            }
        }

        // ---- the block's burns, as they happen (atmosphere workers) ----------------------------------

        private readonly object _burnLock = new object();
        private HashSet<Atmosphere> _blockSet = new HashSet<Atmosphere>();
        private double _tickHeld;          // oxidiser the block's cells held as they came to burn this tick
        private double _tickHeldMax;       // most one cell held
        private double _tickBurnt;         // oxidiser they burnt
        private double _tickEnergy;        // joules of combustion
        private int _tickLit;              // cells that burnt
        private double _leadHeld, _leadHeldMax, _leadBurnt, _leadEnergy;
        private int _leadLit, _leadTicks;
        private double _runHeld, _runBurnt, _runEnergy;
        private int _runLitCellTicks;

        public static void TryCombustPrefix(Atmosphere __instance, out double __state)
        {
            PlanetBurnCheck check = _current;
            __state = check != null && check._blockSet.Contains(__instance) ? Oxidiser(__instance.GasMixture) : double.NaN;
        }

        public static void TryCombustPostfix(Atmosphere __instance, double __state)
        {
            if (double.IsNaN(__state))
            {
                return;
            }
            PlanetBurnCheck check = _current;
            double after = Oxidiser(__instance.GasMixture);
            lock (check._burnLock)
            {
                if (__state > 0.0)
                {
                    check._tickHeld += __state;
                    check._tickHeldMax = Math.Max(check._tickHeldMax, __state);
                }
                if (__instance.Inflamed)
                {
                    check._tickLit++;
                    check._tickBurnt += Math.Max(0.0, __state - after);
                    check._tickEnergy += __instance.CombustionEnergy.ToDouble();
                }
            }
        }

        private static double Oxidiser(GasMixture mix) => mix.TotalOxidiser.ToDouble();

        private void Sample()
        {
            uint tick = GameManager.GameTickCount;
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            foreach (Atmosphere cell in _block)
            {
                cell.IsAwaitingEvent = true;
            }
            if (_blockSet.Count != _block.Count - (_block.Contains(_sparked) ? 1 : 0))
            {
                HashSet<Atmosphere> set = new HashSet<Atmosphere>(_block);
                set.Remove(_sparked);
                _blockSet = set;
            }
            double held, heldMax, burntNow, energy;
            int lit;
            lock (_burnLock)
            {
                held = _tickHeld; heldMax = _tickHeldMax; burntNow = _tickBurnt; energy = _tickEnergy; lit = _tickLit;
                _tickHeld = _tickHeldMax = _tickBurnt = _tickEnergy = 0.0;
                _tickLit = 0;
            }
            if (_sparked != null)
            {
                _sparked.IsAwaitingEvent = true;
            }
            Dictionary<Chemistry.GasType, double> block = BlockTotals(out int inflamed, out double hottest);
            double oxidiser = block[Chemistry.GasType.Oxygen] + block[Chemistry.GasType.NitrousOxide] + block[Chemistry.GasType.Ozone];
            int leakInflamed = LeakInflamed();
            int worldCells = 0, leakCells = 0;
            double leakFarthest = 0.0;
            Vector3 leakAt = _leakGrid.Value.ToVector3();
            AtmosphericsManager.AllAtmospheres.ForEach((Action<Atmosphere>)(a =>
            {
                if (a == null || a.Mode != AtmosphereHelper.AtmosphereMode.World)
                {
                    return;
                }
                worldCells++;
                if (_leak != null)
                {
                    double d = Vector3.Distance(a.WorldGrid.Value.ToVector3(), leakAt);
                    if (d <= 150.0)
                    {
                        leakCells++;
                        leakFarthest = Math.Max(leakFarthest, d);
                    }
                }
            }));
            double lerp = AtmosphereHelper.LerpRate();
            double sun = SunAngle();
            FireReport report = PlanetCombustion.Now;
            Burning burning = report?.Tick as Burning;
            HeldGases heldNow = PlanetCombustion.HeldBackFrom(tank);
            double capacity = PlanetaryAtmosphereSimulation.GetHeatCapacity().ToDouble();

            if (_stage == Stage.Running)
            {
                if (_totalsAtRelease == null)
                {
                    _totalsAtRelease = PlanetTotals(tank);
                }
                // A held gas the block gained during a mixing the hold was in force for was handed to it.
                if (_blockBefore != null)
                {
                    foreach (Chemistry.GasType type in FireGases.Burnable)
                    {
                        if (_heldDuringLastMixing.Contains(type) && block[type] > _blockBefore[type] + 1e-9)
                        {
                            _violations++;
                            _firstViolation ??= string.Format(CultureInfo.InvariantCulture, "tick {0}: the block gained {1:G4} mol of {2} while it was held back", tick, block[type] - _blockBefore[type], type);
                        }
                    }
                }
                _runHeld += held;
                _runBurnt += burntNow;
                _runEnergy += energy;
                _runLitCellTicks += lit;
                if ((_firstBurnTick == 0 || tick <= _firstBurnTick) && (held > 0.0 || lit > 0))
                {
                    _leadHeld += held;
                    _leadHeldMax = Math.Max(_leadHeldMax, heldMax);
                    _leadBurnt += burntNow;
                    _leadEnergy += energy;
                    _leadLit = Math.Max(_leadLit, lit);
                    _leadTicks++;
                }
                if (burning != null)
                {
                    if (_firstBurnTick == 0)
                    {
                        _firstBurnTick = tick;
                        _firstHeld = burning.Scarce;
                        _leadOxidiser = oxidiser;
                    }
                    _lastBurnTick = tick;
                    _sawHeat |= burning.Cause == FireCause.Heat;
                    _sawSpark |= burning.Cause == FireCause.Spark;
                    CheckRate(report, burning, tick);
                }
                if (inflamed > 0)
                {
                    _lastBlockInflamed = tick;
                    _blockEverInflamed = Math.Max(_blockEverInflamed, inflamed);
                    _flashSeen |= _case == 3 && tick - _releaseTick <= 20;
                }
                if (_sparked != null && _sparked.Inflamed)
                {
                    _lastSparkedInflamed = tick;
                }
                if (leakInflamed > 0 && sun > NightAboveDegrees)
                {
                    _leakInflamedAtNight++;
                }
                _blockOxidiserMax = Math.Max(_blockOxidiserMax, oxidiser);
                _blockHottest = Math.Max(_blockHottest, hottest);
                _fireHeatMaxKelvin = Math.Max(_fireHeatMaxKelvin, capacity > 0.0 ? PlanetCombustion.Heat / capacity : 0.0);
            }
            _heldDuringLastMixing = heldNow;
            _blockBefore = block;

            uint since = _stage == Stage.Running ? tick - _releaseTick : 0;
            if (_stage != Stage.Running || since <= 400 || since % 10 == 0)
            {
                _log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "LiveCheck: burn tick {0} | +{1} | planet O2 {2:0.###} N2O {3:0.###} CH4 {4:0.###} H2 {5:0.###} | fire {6} | held {7} | block ox {8:G4} inflamed {9} hottest {10:0.#} K | leak inflamed {11} | sun {12:0.0} | peak {13:0.#} K | fire heat {14:0.###} K | ext {15:0.###} K | block burns: held {16:G4} mol (max {17:G4} a cell), burnt {18:G4} mol, {19:G4} J, {20} cells | world cells {21} | leak cells {22} out to {23:0.#} m | lerp {24:0.###}",
                    tick, since, tank.Get(Chemistry.GasType.Oxygen).ToDouble(), tank.Get(Chemistry.GasType.NitrousOxide).ToDouble(),
                    tank.Get(Chemistry.GasType.Methane).ToDouble(), tank.Get(Chemistry.GasType.Hydrogen).ToDouble(),
                    Kind(report), Names(heldNow), oxidiser, inflamed, hottest, leakInflamed, sun, report?.PeakKelvin ?? double.NaN,
                    capacity > 0.0 ? PlanetCombustion.Heat / capacity : 0.0,
                    capacity > 0.0 ? PlanetaryAtmosphereSimulation.ExternalInputEnergyOffset.ToDouble() / capacity : 0.0,
                    held, heldMax, burntNow, energy, lit, worldCells, leakCells, leakFarthest, lerp));
            }
        }

        /// <summary>The share a tick against Atmosphere.GetCombustionMultiplierCurved written out, and the oxidiser burnt against it.</summary>
        private void CheckRate(FireReport report, Burning burning, uint tick)
        {
            GasMixture air = report.Air;
            double cells = _scale * SpecCells;
            double oxidiser = air.TotalOxidiser.ToDouble();
            double fuel = air.TotalFuel.ToDouble();
            double least = AtmosphereHelper.MinimumMolesForProcessing.ToDouble();
            double expected;
            if ((fuel / cells < least || oxidiser / cells < least) && air.TotalHypergolics.ToDouble() / cells < least)
            {
                expected = 1.0;
            }
            else
            {
                double kelvin = report.PeakKelvin + 273.15;
                double nitrous = (air.NitrousOxide.Quantity + air.Ozone.Quantity).ToDouble() / oxidiser;
                double share = nitrous > 0.1 ? 0.05 + 1.0 / Math.Pow(0.0025 * kelvin, 1.01) : 0.05 + 1.0 / Math.Pow(0.002 * kelvin, 1.6);
                expected = Math.Max(0.0, Math.Min(1.0, share)) / 5.0;
            }
            bool rateOk = Math.Abs(burning.Rate - expected) <= 1e-9;
            bool burntOk = !burning.Scarce.Contains(Chemistry.GasType.Oxygen)
                || Math.Abs(burning.Burnt.Oxidiser - burning.Rate * oxidiser) <= 1e-6 * Math.Max(1.0, oxidiser);
            if (!rateOk || !burntOk)
            {
                _rateMismatches++;
                _firstRateMismatch ??= string.Format(CultureInfo.InvariantCulture,
                    "tick {0}: share {1:R} against the game's formula {2:R} at {3:0.##} K; oxidiser burnt {4:R} of {5:R}",
                    tick, burning.Rate, expected, report.PeakKelvin, burning.Burnt.Oxidiser, oxidiser);
            }
        }

        // ---- the verdict ---------------------------------------------------------------------------

        private void Judge()
        {
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            List<string> problems = new List<string>();
            List<string> notes = new List<string>();
            notes.Add(string.Format(CultureInfo.InvariantCulture, "first burn tick {0}, last {1}, heat cause {2}, spark cause {3}", _firstBurnTick, _lastBurnTick, _sawHeat, _sawSpark));
            notes.Add(string.Format(CultureInfo.InvariantCulture, "block oxidiser at most {0:G4} mol, {1:G4} mol when the planet lit (handed out before it lit), most cells burning at once {2}, last burning tick {3}, hottest {4:0.#} K",
                _blockOxidiserMax, _leadOxidiser, _blockEverInflamed, _lastBlockInflamed, _blockHottest));
            notes.Add(string.Format(CultureInfo.InvariantCulture, "fire heat at most {0:0.###} K", _fireHeatMaxKelvin));
            notes.Add(string.Format(CultureInfo.InvariantCulture,
                "lead (block burns up to the tick the planet lit): {0} tick(s), up to {1} cells at once, oxidiser held {2:G4} mol in all (at most {3:G4} mol a cell), burnt {4:G4} mol, {5:G4} J ({6:G4} J a cell lit)",
                _leadTicks, _leadLit, _leadHeld, _leadHeldMax, _leadBurnt, _leadEnergy, _leadLit > 0 ? _leadEnergy / _leadLit : 0.0));
            notes.Add(string.Format(CultureInfo.InvariantCulture, "whole run: block cells burnt on {0} cell-ticks, {1:G4} mol of oxidiser, {2:G4} J",
                _runLitCellTicks, _runBurnt, _runEnergy));
            bool burnt = _firstBurnTick > 0;
            if (_on)
            {
                Need(_violations == 0, "a held gas was handed to the block " + _violations + " time(s), first " + _firstViolation, problems);
                Need(_rateMismatches == 0, "the planet burnt at other than the game's share " + _rateMismatches + " time(s), first " + _firstRateMismatch, problems);
                Need(burnt, "the planet never burnt", problems);
                switch (_case)
                {
                    case 1:
                        Need(_sawHeat, "the planet was not lit by heat", problems);
                        Need(!_firstHeld.IsEmpty && _firstHeld.Contains(Chemistry.GasType.Oxygen) && !_firstHeld.Contains(Chemistry.GasType.Methane),
                            "the oxidisers were not the side held back", problems);
                        Need(_lastBlockInflamed <= _firstBurnTick + 3, "the block burnt more than 3 ticks after the planet lit", problems);
                        Need(tank.Get(Chemistry.GasType.Oxygen).ToDouble() < 0.0003 * _scale * SpecCells, "oxygen was left in the planet's air", problems);
                        break;
                    case 2:
                        Need(_firstHeld.Contains(Chemistry.GasType.Oxygen) && _firstHeld.Contains(Chemistry.GasType.NitrousOxide), "oxygen and nitrous oxide were not both held back", problems);
                        Products(tank, problems, notes);
                        break;
                    case 3:
                        notes.Add(string.Format(CultureInfo.InvariantCulture, "the block held {0:0.##} of its share before the switch went on", _shareBeforeOn));
                        Need(_shareBeforeOn >= 0.8, "the block never held its share of the oxygen before the switch went on", problems);
                        notes.Add(string.Format(CultureInfo.InvariantCulture, "first flash seen {0}, the block's last fire {1} ticks after the switch went on",
                            _flashSeen, _lastBlockInflamed > _releaseTick ? _lastBlockInflamed - _releaseTick : 0));
                        break;
                    case 4:
                        Need(_sawSpark && !_sawHeat, "a cold planet was not lit by the spark alone", problems);
                        Need(_lastBurnTick <= _lastSparkedInflamed + 2, string.Format(CultureInfo.InvariantCulture,
                            "the planet burnt until tick {0}, more than 2 ticks after the sparked cell's last fire at {1}", _lastBurnTick, _lastSparkedInflamed), problems);
                        Need(_fireHeatMaxKelvin < 6.0, "the planet fire's heat reached " + _fireHeatMaxKelvin.ToString("0.###", CultureInfo.InvariantCulture) + " K", problems);
                        break;
                    case 5:
                        Need(_leakInflamedAtNight == 0, "the leak's cells burnt at night without a spark on " + _leakInflamedAtNight + " tick(s)", problems);
                        break;
                }
            }
            else
            {
                Need(!burnt, "the planet burnt with the switch off", problems);
                switch (_case)
                {
                    case 1:
                        Need(_blockOxidiserMax > 1e-6 && _blockEverInflamed > 0, "control void: with the switch off the block was never handed oxygen and lit", problems);
                        break;
                    case 5:
                        Need(_blockOxidiserMax > 1e-6, "control void: with the switch off the block was never handed oxygen", problems);
                        break;
                }
            }
            _log.LogInfo(string.Format(CultureInfo.InvariantCulture, "LiveCheck: burn case {0} {1} {2}: {3}{4}",
                _case, _on ? "on" : "off", problems.Count == 0 ? "PASS" : "FAIL", string.Join("; ", notes.ToArray()),
                problems.Count == 0 ? "" : " | FAILED: " + string.Join("; ", problems.ToArray())));
            _stage = Stage.Done;
            _log.LogInfo("LiveCheck: burn done");
        }

        /// <summary>Case 2: tank and every outdoor cell together moved by Appendix C's two-oxidiser row, to 0.5 %.</summary>
        private void Products(GlobalGasMix tank, List<string> problems, List<string> notes)
        {
            Dictionary<Chemistry.GasType, double> now = PlanetTotals(tank);
            (Chemistry.GasType Type, double By)[] row =
            {
                (Chemistry.GasType.CarbonDioxide, 7200.0), (Chemistry.GasType.Pollutant, 2700.0), (Chemistry.GasType.Steam, 400.0),
                (Chemistry.GasType.Nitrogen, 1900.0), (Chemistry.GasType.Methane, -2700.0), (Chemistry.GasType.Hydrogen, -300.0),
            };
            foreach ((Chemistry.GasType type, double by) in row)
            {
                double moved = now[type] - (_totalsAtRelease?[type] ?? 0.0);
                double want = by * _scale;
                notes.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1:+0.###;-0.###} (Appendix C {2:+0.###;-0.###})", type, moved, want));
                Need(Math.Abs(moved - want) <= 0.005 * Math.Abs(want), type + " moved other than Appendix C says", problems);
            }
        }

        // ---- helpers -------------------------------------------------------------------------------

        private bool WantsNight => _case == 5;

        private bool RightTimeOfDay()
        {
            double sun = SunAngle();
            return _case == 1 || _case == 3 ? sun < DayBelowDegrees : _case != 5 || sun > NightAboveDegrees;
        }

        private static double SunAngle() => Vector3.Angle(Vector3.up, OrbitalSimulation.WorldSunVector);

        private void SetSwitch(bool on)
        {
            Effective.PlanetAirBurns = on;
            _log.LogInfo("LiveCheck: burn switch " + (on ? "on" : "off") + " at tick " + GameManager.GameTickCount);
        }

        private string Moles(double atSpecificationSize) => (atSpecificationSize * _scale).ToString("R", CultureInfo.InvariantCulture);

        private void Command(params string[] args) => _command(args);

        private static void Add(Atmosphere cell, Chemistry.GasType type, double moles)
        {
            MoleQuantity quantity = new MoleQuantity(moles);
            GasMixture mix = GasMixtureHelper.Create();
            mix.Add(new Mole(type, quantity, IdealGas.Energy(new TemperatureKelvin(293.15), Mole.SpecificHeat(type), quantity)));
            cell.Add(mix);
        }

        private Dictionary<Chemistry.GasType, double> BlockTotals(out int inflamed, out double hottest)
        {
            Dictionary<Chemistry.GasType, double> totals = new Dictionary<Chemistry.GasType, double>();
            foreach (Chemistry.GasType type in FireGases.Burnable)
            {
                totals[type] = 0.0;
            }
            inflamed = 0;
            hottest = 0.0;
            foreach (Atmosphere cell in _block)
            {
                if (cell == _sparked)
                {
                    continue;
                }
                foreach (Chemistry.GasType type in FireGases.Burnable)
                {
                    totals[type] += cell.GasMixture.GetMoleValue(type).Quantity.ToDouble();
                }
                inflamed += cell.Inflamed ? 1 : 0;
                hottest = Math.Max(hottest, cell.GasMixture.Temperature.ToDouble());
            }
            return totals;
        }

        private double BlockShare()
        {
            GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
            double perCell = tank.Get(Chemistry.GasType.Oxygen).ToDouble() / (_scale * SpecCells);
            double least = double.MaxValue;
            foreach (Atmosphere cell in _block)
            {
                least = Math.Min(least, cell.GasMixture.Oxygen.Quantity.ToDouble() / perCell);
            }
            return least;
        }

        private bool BlockHoldsItsShare() => BlockShare() >= 0.9;

        private int LeakInflamed()
        {
            if (_leak == null)
            {
                return 0;
            }
            int burning = 0;
            Vector3 leak = _leakGrid.Value.ToVector3();
            AtmosphericsManager.AllAtmospheres.ForEach((Action<Atmosphere>)(a =>
            {
                if (a != null && a.Mode == AtmosphereHelper.AtmosphereMode.World && a.Inflamed && !_block.Contains(a)
                    && Vector3.Distance(a.WorldGrid.Value.ToVector3(), leak) <= LeakRadius)
                {
                    burning++;
                }
            }));
            return burning;
        }

        /// <summary>The tank and every outdoor cell, gas by gas: what a burn in the planet changes and mixing does not.</summary>
        private static Dictionary<Chemistry.GasType, double> PlanetTotals(GlobalGasMix tank)
        {
            Dictionary<Chemistry.GasType, double> totals = new Dictionary<Chemistry.GasType, double>();
            foreach (Chemistry.GasType type in FireGases.Gases)
            {
                totals[type] = tank.Get(type).ToDouble();
            }
            AtmosphericsManager.AllAtmospheres.ForEach((Action<Atmosphere>)(a =>
            {
                if (a != null && a.Mode == AtmosphereHelper.AtmosphereMode.World)
                {
                    foreach (Chemistry.GasType type in FireGases.Gases)
                    {
                        totals[type] += a.GasMixture.GetMoleValue(type).Quantity.ToDouble();
                    }
                }
            }));
            return totals;
        }

        private static string Kind(FireReport report)
        {
            return report?.Tick switch
            {
                null => "none",
                Burning burning => (burning.Cause == FireCause.Heat ? "burning-heat " : "burning-spark ") + (burning.Rate * 100.0).ToString("0.0##", CultureInfo.InvariantCulture) + "%",
                Unlit _ => "unlit",
                NothingToBurn _ => "nothing",
                _ => "unknown",
            };
        }

        private static string Names(HeldGases held)
        {
            List<string> names = new List<string>(4);
            foreach (Chemistry.GasType type in FireGases.Burnable)
            {
                if (held.Contains(type))
                {
                    names.Add(type.ToString());
                }
            }
            return names.Count == 0 ? "-" : string.Join("+", names.ToArray());
        }

        private void Next(Stage stage, uint tick)
        {
            _stage = stage;
            _stageTick = tick;
        }

        private static void Need(bool condition, string problem, List<string> problems)
        {
            if (!condition)
            {
                problems.Add(problem);
            }
        }

        private void Fail(string why)
        {
            _log.LogInfo("LiveCheck: burn FAIL " + why);
            _stage = Stage.Done;
            _log.LogInfo("LiveCheck: burn done");
        }
    }
}
