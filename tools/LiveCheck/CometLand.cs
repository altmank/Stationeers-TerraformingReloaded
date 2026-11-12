using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Motherboards;
using BepInEx.Logging;
using Networks;
using Objects.Rockets;
using TerraformingReloaded.Comets;
using TerraformingReloaded.Patching;
using UnityEngine;

namespace TerraformingReloaded.LiveCheck
{
    /// <summary>
    /// -CometLand: comets brought down from Entry by a rocket's Deorbit (docs/COMET-ROCKETS.md, stage 6), on
    /// -CometSky's setup: a launch pad, a rocket at Entry with a payload bay and a Rocket Celestial Tracker, and
    /// a Ground Telescope facing north.
    ///
    /// Phase "make", a new world. Five comets are made at a site the game's own discovery made and sent to
    /// Entry: an ice comet of water and carbon dioxide with a large reserve, a mineral comet with a small one,
    /// two more ice comets and a last that stays parked. Then, one at a time:
    ///   A  tracked, its target set half its reach (at most 300 m) from where the bay says it lands now: it
    ///      lands within a metre of the target, the fuel the rule's for that steer, at the landing the bay read
    ///      out in the frame the burn used; the comet leaves the map at once and the sky at the game tick's
    ///      main-thread moment, the tracker pointed at it set back to the first body; the planet gains each
    ///      gas's arriving moles exactly, stored heat in kelvin unchanged;
    ///   B  the mineral comet, its target 500 m past its reach: it lands at the reach along the line from the
    ///      sky point to the target, the fuel the whole reach's; at the impact it takes the rule's moles from
    ///      each gas in the planet's own proportions, stored heat in kelvin unchanged;
    ///   C  untracked, the bay's target NaN: it lands at the sky point, inside the disc around the pad nearest
    ///      the world's origin, spending nothing on steering;
    ///   D  brought down and saved 8 s into its fall, with E still parked.
    /// Phase "load": D falls on from where it was saved and lands at the same clock time, its event made again,
    /// its gas delivered once; E is still parked and in the sky. Phase "vanilla" is CometLandVanilla.
    /// </summary>
    internal sealed class CometLandCheck
    {
        private const uint StartTick = 20;
        private const double StageSeconds = 900.0;
        private const double SaveAfterSeconds = 8.0;
        private const double HeatTolerance = 1e-9;

        private readonly ManualLogSource _log;
        private readonly Action<string[]> _command;
        private readonly bool _load;

        private enum Stage { Setup, Flying, Aim, Order, Fired, Sky, Impact, Clear, SaveWait, Saved, Done }

        private Stage _stage = Stage.Setup;
        private double _stageClock;
        private bool _failed;

        private readonly List<int> _comets = new List<int>();
        private int _case;
        private double _premix;
        private Rocket _rocket;
        private RocketPayloadBay _bay;
        private GroundTelescope _telescope;
        private RocketCelestialTracker _tracker;

        // The case in hand.
        private int _cometId;
        private CometView _before;
        private double _fuelAtEntry;
        private float _targetX, _targetZ;
        private bool _untracked;
        private double _planetBefore;
        private int _shortened;
        private int _shortenedInFrame;
        private double _firedAt;
        private double _orderedAt;
        private LandingView _landing;
        private readonly Dictionary<int, (double X, double Z, double Reach, double Steered, double Spent)> _readouts = new Dictionary<int, (double, double, double, double, double)>();

        private CometLandCheck(ManualLogSource log, Action<string[]> command, bool load)
        {
            _log = log;
            _command = command;
            _load = load;
        }

        public static CometLandCheck FromEnvironment(ManualLogSource log, Action<string[]> command)
        {
            string phase = Environment.GetEnvironmentVariable("TR_LIVECHECK_COMETLAND");
            return phase == "make" || phase == "load" ? new CometLandCheck(log, command, phase == "load") : null;
        }

        private static double Clock => OrbitalSimulation.System?.TotalRealTimeSeconds ?? 0.0;

        private static CultureInfo C => CultureInfo.InvariantCulture;

        private void Info(string line) => _log.LogInfo("LiveCheck: land " + line);

        private void Fail(string why)
        {
            if (!_failed)
            {
                _failed = true;
                Info("FAIL " + why);
                _stage = Stage.Done;
            }
        }

        private void Next(Stage stage)
        {
            _stage = stage;
            _stageClock = Clock;
        }

        private bool Overdue(string what)
        {
            if (Clock - _stageClock > StageSeconds)
            {
                Fail(what + " did not happen within " + StageSeconds + " s");
                return true;
            }
            return false;
        }

        // ---- main thread ------------------------------------------------------------------------------

        public void Step()
        {
            if (_stage == Stage.Done || _failed)
            {
                return;
            }
            try
            {
                HoldPower();
                Record();
                if (_load)
                {
                    StepLoad();
                }
                else
                {
                    StepMake();
                }
            }
            catch (Exception e)
            {
                Fail("the driver threw: " + e);
            }
        }

        private void StepMake()
        {
            switch (_stage)
            {
                case Stage.Setup when GameManager.GameTickCount >= StartTick:
                    SetUp();
                    break;
                case Stage.Flying:
                    WaitParked();
                    break;
                case Stage.Aim:
                    Aim();
                    break;
                case Stage.Order:
                    Order();
                    break;
                case Stage.Fired:
                    WaitFired();
                    break;
                case Stage.Sky:
                    WaitSky();
                    break;
                case Stage.Impact:
                    WaitImpact();
                    break;
                case Stage.Clear:
                    WaitClear();
                    break;
                case Stage.SaveWait when Clock - _firedAt >= SaveAfterSeconds:
                    Save();
                    break;
                case Stage.Saved when Clock - _stageClock >= 2.0:
                    Info("phase make done");
                    _stage = Stage.Done;
                    break;
            }
        }

        private void SetUp()
        {
            SpaceMap map = SpaceMap.Current;
            Info(string.Format(C, "parts | save {0} | sky body {1} | sky safe point {2} | aim readouts {3} | deorbit {4} | rules {5} | planet size {6:R} | world {7}",
                Installed(CometMap.SaveInstalled), Installed(CometSkyBodies.Installed), Installed(CometSkyBodies.SafePointInstalled), Installed(CometAim.ReadoutsInstalled),
                Installed(CometLand.Installed), CelestialRules.Verdict ?? "pass", CometMap.PlanetSize(), WorldSetting.Current?.Id));
            if (map?.EntryNode == null || !CometMap.SaveInstalled || !CometSkyBodies.Installed || !CometSkyBodies.SafePointInstalled || !CometAim.ReadoutsInstalled
                || !CometLand.Installed || CelestialRules.Verdict != null)
            {
                Fail("no space map, or a comet part is not installed");
                return;
            }
            SpaceMapNode site = Discover(map);
            if (site == null)
            {
                return;
            }
            double route = RouteDeltaV(site);
            _premix = CelestialRules.PremixKgPerMole(Mole.MolarMass(Chemistry.GasType.Methane), Mole.MolarMass(Chemistry.GasType.Oxygen));
            int index = CometMap.Sites().IndexOf(site) + 1;
            // A: a large reserve, to steer onto a target. B: a mineral comet with a small one. C, D and E: ice.
            var made = new (string Amount, string[] Contents, double Extra)[]
            {
                ("100000", new[] { "Water:50", "CarbonDioxide:50" }, 2000.0),
                ("2000", new[] { "Ore" }, 50.0),
                ("50000", new[] { "CarbonDioxide" }, 500.0),
                ("60000", new[] { "Nitrogen:50", "CarbonDioxide:50" }, 500.0),
                ("40000", new[] { "Water" }, 500.0),
            };
            foreach (var comet in made)
            {
                HashSet<int> before = new HashSet<int>(CometMap.Views().Select(v => v.Id));
                // Made with no fuel first, so its weight is the mod's own; then made again with the fuel its route takes.
                List<string> args = new List<string> { "comet", "make", index.ToString(C), comet.Amount };
                args.AddRange(comet.Contents);
                _command(args.Concat(new[] { "engines=8", "fuel=0" }).ToArray());
                CometView probe = CometMap.Views().FirstOrDefault(v => !before.Contains(v.Id));
                if (probe.Id == 0)
                {
                    Fail("the mod's make verb made no comet of " + comet.Amount + " " + string.Join(" ", comet.Contents));
                    return;
                }
                double fuel = 1.02 * CelestialRules.Shipped.FuelNeeded(probe.CometKg, 8, route, _premix) + comet.Extra;
                before.Add(probe.Id);
                _command(args.Concat(new[] { "engines=8", "fuel=" + fuel.ToString("R", C) }).ToArray());
                int id = CometMap.Views().Select(v => v.Id).FirstOrDefault(i => !before.Contains(i));
                if (id == 0)
                {
                    Fail("the mod's make verb made no comet with fuel");
                    return;
                }
                _command(new[] { "comet", "send", id.ToString(C) });
                _comets.Add(id);
            }

            LaunchMount pad = Spawn<LaunchMount>("StructureLaunchMount", new Vector3(300f, 300f, -400f));
            EngineFuselage mount = Spawn<EngineFuselage>("StructureEngineMountTypeA1", new Vector3(100f, 400f, 100f));
            RocketNetwork network = mount?.RocketNetwork;
            _rocket = network?.Rocket;
            _bay = Spawn<RocketPayloadBay>("StructureRocketLargePayloadBay", new Vector3(124f, 400f, 100f));
            _tracker = Spawn<RocketCelestialTracker>("StructureRocketCelestialTracker", new Vector3(100f, 424f, 100f));
            _telescope = Spawn<GroundTelescope>("StructureGroundBasedTelescope", new Vector3(40f, 350f, 40f));
            if (_rocket == null || _bay == null || _telescope == null || _tracker == null)
            {
                Fail(string.Format(C, "the rocket, its bay, its tracker or the telescope did not spawn (rocket {0}, bay {1}, tracker {2}, telescope {3})",
                    _rocket != null, _bay != null, _tracker != null, _telescope != null));
                return;
            }
            _bay.RocketData = new RocketData(network, _bay.ThingTransformPosition - mount.ThingTransformPosition);
            network.AdoptFromLoad(_bay);
            _tracker.RocketData = new RocketData(network, _tracker.ThingTransformPosition - mount.ThingTransformPosition);
            network.AdoptFromLoad(_tracker);
            network.RefreshRocket();
            _rocket.SetCurrentNode(map.EntryNode);
            _rocket.RocketState = RocketState.InSpace;
            HoldPower();
            List<Vector3> pads = CometAim.Pads(map.EntryNode);
            Info(string.Format(C, "set up | comets {0} from {1} ({2:R} delta-v) | pad {3} | rocket {4} bay {5} tracker {6} at {7} | telescope {8}",
                string.Join(",", _comets), site.DisplayName, route, pad != null && pads.Count > 0 ? "on the map" : "MISSING", _rocket.ReferenceId, _bay.ReferenceId,
                _tracker.ReferenceId, _rocket.CurrentNode?.DisplayName, _telescope.ReferenceId));
            if (pads.Count == 0 || network.RocketPayloadBays.Count != 1)
            {
                Fail("the launch pad is not on the map under Entry, or the rocket did not take its bay");
                return;
            }
            Next(Stage.Flying);
        }

        private static string Installed(bool installed) => installed ? "installed" : "MISSING";

        private void WaitParked()
        {
            CometView[] views = CometMap.Views();
            bool all = _comets.All(id => views.Any(v => v.Id == id && v.State == MapCometState.Arrived) && CometSkyBodies.For(id) != null);
            if (!all)
            {
                Overdue("the five comets parking at Entry and going into the sky");
                return;
            }
            Info("parked | " + string.Join(" | ", _comets.Select(id => string.Format(C, "comet {0} fuel {1:R} kg {2:R}", id,
                views.First(v => v.Id == id).FuelLeft, views.First(v => v.Id == id).CometKg))));
            Begin(0);
        }

        // ---- one case at a time -------------------------------------------------------------------------

        private void Begin(int which)
        {
            _case = which;
            _cometId = _comets[which];
            _before = CometMap.Views().First(v => v.Id == _cometId);
            _fuelAtEntry = _before.FuelLeft;
            _untracked = which == 2;
            _readouts.Clear();
            Next(Stage.Aim);
        }

        private string Case => "ABCD"[_case].ToString();

        /// <summary>The target for this case, from where the bay says the comet lands now.</summary>
        private void Aim()
        {
            if (CometLand.Busy())
            {
                Overdue("the last comet's show ending");
                return;
            }
            CometAim.Aim? found = CometAim.ForBay(_bay.ReferenceId);
            if (found == null || found.Value.CometId != _cometId)
            {
                // Untracked or not, a bay at Entry aims once the comet is first in the sky by id.
                _bay.SetLogicValue(LogicType.PositionX, 0.0);
                _bay.SetLogicValue(LogicType.PositionZ, 0.0);
                Overdue("the bay aiming comet " + _cometId);
                return;
            }
            CometAim.Aim aim = found.Value;
            if (_untracked)
            {
                _bay.SetLogicValue(LogicType.PositionX, double.NaN);
                _bay.SetLogicValue(LogicType.PositionZ, double.NaN);
                _targetX = float.NaN;
                _targetZ = float.NaN;
            }
            else
            {
                double dx = aim.PadX - aim.SkyX;
                double dz = aim.PadZ - aim.SkyZ;
                double toPad = Math.Sqrt(dx * dx + dz * dz);
                double ux = toPad > 600.0 ? dx / toPad : 1.0;
                double uz = toPad > 600.0 ? dz / toPad : 0.0;
                double step = _case == 1 ? aim.Reach + 500.0 : _case == 0 ? Math.Min(0.5 * aim.Reach, 300.0) : 0.0;
                if (_case == 0 && !(aim.Reach > 50.0))
                {
                    Fail(string.Format(C, "comet {0}'s reserve steers only {1:R} m, too little to aim within", _cometId, aim.Reach));
                    return;
                }
                _targetX = (float)(aim.SkyX + ux * step);
                _targetZ = (float)(aim.SkyZ + uz * step);
                _bay.SetLogicValue(LogicType.PositionX, _targetX);
                _bay.SetLogicValue(LogicType.PositionZ, _targetZ);
            }
            Next(Stage.Order);
        }

        /// <summary>Once the bay aims at the case's target: the tracker on the comet, and the order to burn.</summary>
        private void Order()
        {
            CometAim.Aim? found = CometAim.ForBay(_bay.ReferenceId);
            bool aimed = found != null && found.Value.CometId == _cometId && (_untracked ? !found.Value.Tracked
                : found.Value.Tracked && (float)found.Value.TargetX == _targetX && (float)found.Value.TargetZ == _targetZ);
            if (!aimed)
            {
                Overdue("the bay aiming at the case's target");
                return;
            }
            int index = BodyIndex(_cometId);
            if (index < 0)
            {
                Fail("comet " + _cometId + " is not in the game's list of bodies");
                return;
            }
            _tracker.Index = (byte)index;
            _shortened = CometSkyBodies.ShortenedAtSafePoint;
            _shortenedInFrame = CometSkyBodies.ShortenedInFrame;
            _planetBefore = PlanetMoles();
            _orderedAt = Clock;
            _rocket.RocketMode = CometModes.Deorbit;
            Info(string.Format(C, "order {0} | comet {1} | rocket mode {2} | tracker on body {3} of {4} (index {5}) | target ({6:R}, {7:R})",
                Case, _cometId, _rocket.RocketMode, index, OrbitalSimulation.GetCelestialCount(), _tracker.Index, _targetX, _targetZ));
            if (_rocket.RocketMode != CometModes.Deorbit || _tracker.Index != index)
            {
                Fail("the game did not take the Deorbit order, or the tracker its index");
                return;
            }
            Next(Stage.Fired);
        }

        private void WaitFired()
        {
            LandingView[] falling = CometLand.Falling();
            if (!falling.Any(l => l.CometId == _cometId))
            {
                Overdue("comet " + _cometId + "'s burn");
                return;
            }
            _firedAt = Clock;
            _landing = falling.First(l => l.CometId == _cometId);
            if (!JudgeBurn())
            {
                return;
            }
            Next(_case == 3 ? Stage.SaveWait : Stage.Sky);
        }

        /// <summary>The burn against the rule from the comet as it was parked and the aim the bay read out.</summary>
        private bool JudgeBurn()
        {
            LandingView l = _landing;
            CelestialTuning tuning = Effective.Celestial;
            bool gone = !CometMap.Views().Any(v => v.Id == _cometId);
            bool idle = _rocket.RocketMode != CometModes.Deorbit;
            bool readout = _readouts.TryGetValue(l.AimFrame, out var read);
            GroundPoint sky = new GroundPoint(l.SkyX, l.SkyZ);
            GroundPoint? target = _untracked ? (GroundPoint?)null : new GroundPoint(_targetX, _targetZ);
            Descent rule = CelestialRules.Shipped.Descend(sky, target, l.PadX, l.PadZ, l.Radius, _before.CometKg, _before.Engines, _fuelAtEntry,
                tuning.CorrectionMetresPerDeltaV, tuning.EntryWindowSeconds, _premix);
            bool event_ = CometLand.EventFor(_cometId, out double eventClock, out Vector3 eventImpact);
            bool landing = Math.Abs(l.ImpactX - rule.Land.X) < 1e-3 && Math.Abs(l.ImpactZ - rule.Land.Z) < 1e-3 && Math.Abs(l.Steered - rule.Steered) < 1e-6
                && Math.Abs(l.FuelSpent - rule.FuelSpent) <= 1e-9 * Math.Max(1.0, rule.FuelSpent) && Math.Abs(l.FuelLeft - rule.FuelLeft) <= 1e-9 * Math.Max(1.0, rule.FuelLeft)
                && Math.Abs(l.Fuel - _fuelAtEntry) <= 1e-9 * Math.Max(1.0, _fuelAtEntry) && Math.Abs(l.Reach - rule.Reach) < 1e-6;
            bool asRead = readout && Math.Abs(read.X - l.ImpactX) < 1e-3 && Math.Abs(read.Z - l.ImpactZ) < 1e-3 && Math.Abs(read.Reach - l.Reach) < 1e-9 && read.Spent == l.FuelSpent;
            double burnAt = l.ImpactClock - l.FallSeconds;
            bool fall = Math.Abs(l.FallSeconds - tuning.EntryWindowSeconds) < 1e-9 && burnAt >= _orderedAt - 1e-6 && burnAt <= Clock + 1e-6
                && event_ && Math.Abs(eventClock - l.ImpactClock) < 1e-6 && Math.Abs(eventImpact.x - l.ImpactX) < 1e-3 && Math.Abs(eventImpact.z - l.ImpactZ) < 1e-3;
            double gap = _untracked ? 0.0 : Math.Sqrt((_targetX - l.SkyX) * (_targetX - l.SkyX) + (_targetZ - l.SkyZ) * (_targetZ - l.SkyZ));
            double fromPad = Math.Sqrt((l.ImpactX - l.PadX) * (l.ImpactX - l.PadX) + (l.ImpactZ - l.PadZ) * (l.ImpactZ - l.PadZ));
            double toTarget = _untracked ? double.NaN : Math.Sqrt((l.ImpactX - _targetX) * (l.ImpactX - _targetX) + (l.ImpactZ - _targetZ) * (l.ImpactZ - _targetZ));
            bool shape;
            string what;
            switch (_case)
            {
                case 0:
                    shape = l.Tracked && gap < l.Reach && toTarget < 1.0;
                    what = string.Format(C, "tracked, in reach: lands {0:0.######} m from the target", toTarget);
                    break;
                case 1:
                    double along = Math.Sqrt((l.ImpactX - l.SkyX) * (l.ImpactX - l.SkyX) + (l.ImpactZ - l.SkyZ) * (l.ImpactZ - l.SkyZ));
                    double cross = Math.Abs((_targetX - l.SkyX) * (l.ImpactZ - l.SkyZ) - (_targetZ - l.SkyZ) * (l.ImpactX - l.SkyX)) / Math.Max(1e-9, gap);
                    double whole = CelestialRules.Shipped.SteerFuelMoles(_before.CometKg, _before.Engines, _fuelAtEntry, l.Reach, tuning.CorrectionMetresPerDeltaV, tuning.EntryWindowSeconds, _premix);
                    shape = l.Tracked && gap > l.Reach && Math.Abs(along - l.Reach) < 1e-2 && cross < 1e-2 && Math.Abs(l.FuelSpent - whole) <= 1e-9 * Math.Max(1.0, whole);
                    what = string.Format(C, "tracked, out of reach: the target {0:0.###} m off, lands {1:0.###} m along the line (reach {2:0.###}), {3:0.######} m off it",
                        gap, along, l.Reach, cross);
                    break;
                case 2:
                    shape = !l.Tracked && l.Steered == 0.0 && l.FuelSpent == 0.0 && Math.Abs(l.ImpactX - l.SkyX) < 1e-3 && Math.Abs(l.ImpactZ - l.SkyZ) < 1e-3
                        && fromPad <= l.Radius + 1e-3 && double.IsNaN(l.TargetX) && PadNearestOrigin(l.PadX, l.PadZ);
                    what = string.Format(C, "untracked: lands at the sky point, {0:0.###} m from the pad nearest the world's origin, inside the {1:R} m disc", fromPad, l.Radius);
                    break;
                default:
                    shape = l.Tracked;
                    what = "tracked, to be saved in its fall";
                    break;
            }
            bool pass = gone && idle && readout && landing && asRead && fall && shape;
            Info(string.Format(C,
                "burn {0} {1} | comet {2} | {3} | sky point ({4:R}, {5:R}) | target ({6:R}, {7:R}) | pad ({8:R}, {9:R}) disc {10:R} | reach {11:R} steered {12:R} | fuel {13:R} spent {14:R} left {15:R} | rule: lands ({16:R}, {17:R}) steered {18:R} spent {19:R} | lands ({20:R}, {21:R}, {22:R}) at clock {23:R} after {24:R} s | bay read out ({25:R}, {26:R}) in frame {27} | off the map {28} | rocket idle {29} | event {30}",
                Case, pass ? "PASS" : "FAIL", _cometId, what, l.SkyX, l.SkyZ, l.TargetX, l.TargetZ, l.PadX, l.PadZ, l.Radius, l.Reach, l.Steered, l.Fuel, l.FuelSpent, l.FuelLeft,
                rule.Land.X, rule.Land.Z, rule.Steered, rule.FuelSpent, l.ImpactX, l.ImpactY, l.ImpactZ, l.ImpactClock, l.FallSeconds,
                readout ? read.X : double.NaN, readout ? read.Z : double.NaN, l.AimFrame, gone, idle, event_ ? eventClock.ToString("R", C) : "none"));
            if (!pass)
            {
                Fail("the burn of comet " + _cometId + " is not the rule's, is not where the bay read out, or the comet is still on the map");
            }
            return pass;
        }

        /// <summary>Gone from the sky at the game tick's main-thread moment, the tracker on it set back, the telescope still there.</summary>
        private void WaitSky()
        {
            if (CometSkyBodies.For(_cometId) != null || BodyIndex(_cometId) >= 0)
            {
                if (Clock - _stageClock > 10.0)
                {
                    Fail("comet " + _cometId + " was still in the sky 10 s after its burn");
                }
                return;
            }
            int atSafePoint = CometSkyBodies.ShortenedAtSafePoint - _shortened;
            int inFrame = CometSkyBodies.ShortenedInFrame - _shortenedInFrame;
            bool telescope = _telescope != null && !_telescope.IsBeingDestroyed;
            bool pass = atSafePoint == 1 && inFrame == 0 && _tracker.Index == 0 && telescope && !CometMap.Views().Any(v => v.Id == _cometId);
            Info(string.Format(C, "sky {0} {1} | comet {2} out of the map and the sky's lists | list shortened {3} times at the game tick's main-thread moment, {4} in a frame | tracker index {5} | telescope {6} | {7} bodies",
                Case, pass ? "PASS" : "FAIL", _cometId, atSafePoint, inFrame, _tracker.Index, telescope ? "there" : "GONE", OrbitalSimulation.GetCelestialCount()));
            if (!pass)
            {
                Fail("comet " + _cometId + " did not leave the sky at the game tick's main-thread moment with the tracker on it set back");
                return;
            }
            Next(Stage.Impact);
        }

        private void WaitImpact()
        {
            LandingDelivery delivery = CometLand.Deliveries().LastOrDefault(d => d.CometId == _cometId);
            if (delivery == null)
            {
                if (Clock > _landing.ImpactClock + 30.0)
                {
                    Fail("comet " + _cometId + " did not land within 30 s of its impact clock");
                }
                return;
            }
            if (Clock - delivery.Clock < 3.0)
            {
                return;
            }
            if (JudgeDelivery(delivery, "impact " + Case))
            {
                Next(Stage.Clear);
            }
        }

        /// <summary>What the landing did to the planet, against the rule, read under the tank lock either side of it.</summary>
        private bool JudgeDelivery(LandingDelivery d, string what)
        {
            CelestialTuning tuning = Effective.Celestial;
            int count = CometLand.Deliveries().Count(x => x.CometId == _cometId);
            bool gone = !CometLand.Falling().Any(l => l.CometId == _cometId);
            double[] expected = new double[d.Types.Length];
            if (d.Kind == CometKind.Ice)
            {
                double[] arriving = _load ? _landing.Moles : CelestialRules.Shipped.ArrivingGas(_before.GasMoles, tuning.GasArrivingShare);
                Chemistry.GasType[] gases = _load ? _landing.Gases : _before.Gases;
                for (int i = 0; i < gases.Length; i++)
                {
                    expected[Array.IndexOf(d.Types, gases[i])] += arriving[i];
                }
            }
            else
            {
                List<int> air = Enumerable.Range(0, d.Types.Length).Where(i => Mole.MatterState(d.Types[i]) == AtmosphereHelper.MatterState.Gas && d.Before[i] > 0.0).ToList();
                double[] taken = CelestialRules.Shipped.MineralRemoval(air.Select(i => d.Before[i]).ToArray(), air.Select(i => (double)Mole.MolarMass(d.Types[i])).ToArray(),
                    _before.OreUnits * tuning.MineralKgPerUnit, tuning.MineralRemovalPerKg);
                for (int k = 0; k < air.Count; k++)
                {
                    expected[air[k]] = taken[k];
                }
            }
            double worst = 0.0;
            double total = 0.0;
            List<string> gasLines = new List<string>();
            for (int i = 0; i < d.Types.Length; i++)
            {
                double change = d.After[i] - d.Before[i];
                double want = d.Kind == CometKind.Ice ? expected[i] : -expected[i];
                double off = Math.Abs(change - want) / Math.Max(1.0, Math.Abs(want));
                worst = Math.Max(worst, Math.Max(off, Math.Abs(d.Rule[i] - expected[i]) / Math.Max(1.0, expected[i])));
                total += change;
                if (want != 0.0 || change != 0.0)
                {
                    gasLines.Add(string.Format(C, "{0} {1:R} -> {2:R} (rule {3:R})", d.Types[i], d.Before[i], d.After[i], want));
                }
            }
            bool proportional = true;
            if (d.Kind == CometKind.Mineral)
            {
                double share = double.NaN;
                for (int i = 0; i < d.Types.Length; i++)
                {
                    if (Mole.MatterState(d.Types[i]) == AtmosphereHelper.MatterState.Gas && d.Before[i] > 0.0)
                    {
                        double one = (d.Before[i] - d.After[i]) / d.Before[i];
                        share = double.IsNaN(share) ? one : share;
                        proportional &= Math.Abs(one - share) <= 1e-9 * Math.Max(1e-12, share);
                    }
                }
                proportional &= share > 0.0;
            }
            bool heat = Math.Abs(d.LatentKelvinAfter - d.LatentKelvinBefore) <= HeatTolerance * Math.Max(1.0, Math.Abs(d.LatentKelvinBefore))
                && Math.Abs(d.ExternalKelvinAfter - d.ExternalKelvinBefore) <= HeatTolerance * Math.Max(1.0, Math.Abs(d.ExternalKelvinBefore));
            double planetAfter = PlanetMoles();
            double planetChange = planetAfter - _planetBefore;
            bool planet = double.IsNaN(_planetBefore) || Math.Abs(planetChange - total) <= 0.1 * Math.Abs(total) + 100.0;
            bool pass = count == 1 && gone && worst <= 1e-9 && proportional && heat && planet;
            Info(string.Format(C,
                "{0} {1} | comet {2} {3} landed at clock {4:R} | {5} | worst {6:E3} of the rule | {7} | stored heat {8:R} -> {9:R} K latent, {10:R} -> {11:R} K outside | the planet and its reservoirs changed by {12:R} mol, the tank by {13:R} | deliveries {14}",
                what, pass ? "PASS" : "FAIL", _cometId, d.Kind, d.Clock, string.Join(", ", gasLines), worst,
                d.Kind == CometKind.Mineral ? (proportional ? "in the planet's own proportions" : "NOT in the planet's own proportions") : "each gas its arriving share",
                d.LatentKelvinBefore, d.LatentKelvinAfter, d.ExternalKelvinBefore, d.ExternalKelvinAfter, planetChange, total, count));
            if (!pass)
            {
                Fail("comet " + _cometId + "'s landing did not do to the planet what the rule says, once, with the stored heat kept");
            }
            return pass;
        }

        private void WaitClear()
        {
            if (CometLand.Busy())
            {
                Overdue("comet " + _cometId + "'s show ending");
                return;
            }
            Begin(_case + 1);
        }

        private void Save()
        {
            LandingView l = CometLand.Falling().FirstOrDefault(x => x.CometId == _cometId);
            if (l.CometId != _cometId)
            {
                Fail("comet " + _cometId + " landed before its save");
                return;
            }
            int parked = _comets[4];
            bool waiting = CometMap.Views().Any(v => v.Id == parked && v.State == MapCometState.Arrived) && CometSkyBodies.For(parked) != null;
            Info(string.Format(C, "falling {0} | comet {1} | lands at clock {2:R} at ({3:R}, {4:R}, {5:R}) | gases {6} | at clock {7:R}",
                waiting ? "PASS" : "FAIL", _cometId, l.ImpactClock, l.ImpactX, l.ImpactY, l.ImpactZ,
                string.Join(",", l.Gases.Select((g, i) => g + ":" + l.Moles[i].ToString("R", C))), Clock));
            Info(string.Format(C, "ids | falling {0} | parked {1} | bay {2} | rocket {3} | telescope {4} | impact {5}",
                _cometId, parked, _bay.ReferenceId, _rocket.ReferenceId, _telescope.ReferenceId, l.ImpactClock.ToString("R", C)));
            if (!waiting)
            {
                Fail("comet " + parked + " is not parked in the sky at the save");
                return;
            }
            Util.Commands.CommandLine.Process("file save");
            Next(Stage.Saved);
        }

        // ---- phase load -------------------------------------------------------------------------------

        private bool _loaded;

        private void StepLoad()
        {
            if (GameManager.GameTickCount < 3)
            {
                return;
            }
            if (!_loaded)
            {
                Loaded();
                return;
            }
            if (_stage == Stage.Impact)
            {
                WaitImpact();
                if (_stage == Stage.Clear)
                {
                    Info("done");
                    _stage = Stage.Done;
                }
            }
        }

        private void Loaded()
        {
            if (_stageClock == 0.0)
            {
                _stageClock = Clock;
            }
            _cometId = (int)Long("TR_LIVECHECK_COMETLAND_FALLING");
            int parked = (int)Long("TR_LIVECHECK_COMETLAND_PARKED");
            double impact = double.Parse(Environment.GetEnvironmentVariable("TR_LIVECHECK_COMETLAND_IMPACT") ?? "NaN", C);
            _telescope = Referencable.Find<GroundTelescope>(Long("TR_LIVECHECK_COMETLAND_TELESCOPE"));
            _bay = Referencable.Find<RocketPayloadBay>(Long("TR_LIVECHECK_COMETLAND_BAY"));
            LandingView l = CometLand.Falling().FirstOrDefault(x => x.CometId == _cometId);
            bool back = l.CometId == _cometId && !double.IsNaN(l.ImpactClock);
            bool event_ = CometLand.EventFor(_cometId, out double eventClock, out Vector3 eventImpact);
            if (!back || !event_ || CometSkyBodies.For(parked) == null)
            {
                if (Clock - _stageClock > 60.0)
                {
                    Fail(string.Format(C, "the load brought back no fall of comet {0} on the clock with its event, or no comet {1} in the sky (fall {2}, event {3}, parked {4})",
                        _cometId, parked, back, event_, CometSkyBodies.For(parked) != null));
                }
                return;
            }
            _loaded = true;
            _case = 3;
            _landing = l;
            _planetBefore = PlanetMoles();
            bool same = Math.Abs(l.ImpactClock - impact) <= 1e-6 && Math.Abs(eventClock - l.ImpactClock) < 1e-6 && Math.Abs(eventImpact.x - l.ImpactX) < 1e-3
                && Math.Abs(eventImpact.z - l.ImpactZ) < 1e-3 && Clock < l.ImpactClock && _telescope != null;
            bool parkedStill = CometMap.Views().Any(v => v.Id == parked && v.State == MapCometState.Arrived);
            Info(string.Format(C, "loaded {0} | comet {1} falls on, lands at clock {2:R} (saved to land at {3:R}), {4:R} s from now | its event lands at {5:R} | at ({6:R}, {7:R}, {8:R}) | gases {9} | comet {10} parked {11} and in the sky",
                same && parkedStill ? "PASS" : "FAIL", _cometId, l.ImpactClock, impact, l.ImpactClock - Clock, eventClock, l.ImpactX, l.ImpactY, l.ImpactZ,
                string.Join(",", l.Gases.Select((g, i) => g + ":" + l.Moles[i].ToString("R", C))), parked, parkedStill));
            if (!same || !parkedStill)
            {
                Fail("the fall did not come back to land at the clock time it was saved to, with its event, or the parked comet is gone");
                return;
            }
            Next(Stage.Impact);
        }

        // ---- helpers ----------------------------------------------------------------------------------

        /// <summary>The bay's readouts each frame, by the frame its aim was worked out in.</summary>
        private void Record()
        {
            if (_bay == null)
            {
                return;
            }
            CometAim.Aim? aim = CometAim.ForBay(_bay.ReferenceId);
            if (aim == null || _readouts.ContainsKey(aim.Value.Frame))
            {
                return;
            }
            _readouts[aim.Value.Frame] = (_bay.GetLogicValue(LogicType.TargetX), _bay.GetLogicValue(LogicType.TargetZ), _bay.GetLogicValue(LogicType.Maximum),
                aim.Value.Steered, aim.Value.FuelSpent);
            if (_readouts.Count > 600)
            {
                foreach (int old in _readouts.Keys.OrderBy(k => k).Take(300).ToList())
                {
                    _readouts.Remove(old);
                }
            }
        }

        private static int BodyIndex(int cometId)
        {
            int hash = Animator.StringToHash(CometSkyBodies.IdPrefix + cometId.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < OrbitalSimulation.GetCelestialCount(); i++)
            {
                if (OrbitalSimulation.GetCelestial(i)?.Hash == hash)
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool PadNearestOrigin(double padX, double padZ)
        {
            List<Vector3> pads = CometAim.Pads(SpaceMap.Current.EntryNode);
            if (pads.Count == 0)
            {
                return padX == 0.0 && padZ == 0.0;
            }
            Vector3 nearest = pads.OrderBy(p => p.x * p.x + p.z * p.z).First();
            return Math.Abs(nearest.x - padX) < 1e-3 && Math.Abs(nearest.z - padZ) < 1e-3;
        }

        /// <summary>Every mole of the planet: the tank and its three reservoirs, read under the tank lock.</summary>
        private static double PlanetMoles()
        {
            double total = double.NaN;
            Planet.UnderTankLock(() =>
            {
                GlobalGasMix tank = PlanetaryAtmosphereSimulation.GetGlobalGasMix();
                if (tank == null)
                {
                    return;
                }
                total = tank.TotalQuantity().ToDouble();
                foreach (GlobalGasMix reservoir in Planet.ReservoirMixes())
                {
                    total += reservoir?.TotalQuantity().ToDouble() ?? 0.0;
                }
            });
            return total;
        }

        private static long Long(string name) =>
            long.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0L;

        /// <summary>The parts have no cable: the driver keeps the telescope and the tracker on and powered, in place of a battery.</summary>
        private void HoldPower()
        {
            if (_telescope != null)
            {
                if (!_telescope.OnOff)
                {
                    OnServer.Interact(_telescope.InteractOnOff, 1);
                }
                if (!_telescope.Powered)
                {
                    OnServer.Interact(_telescope.InteractPowered, 1);
                }
                if (!_telescope.IsOpen)
                {
                    OnServer.Interact(_telescope.InteractOpen, 1);
                }
            }
            if (_tracker != null)
            {
                if (!_tracker.OnOff)
                {
                    OnServer.Interact(_tracker.InteractOnOff, 1);
                }
                if (!_tracker.Powered)
                {
                    OnServer.Interact(_tracker.InteractPowered, 1);
                }
            }
        }

        private static T Spawn<T>(string prefabName, Vector3 position) where T : Structure
        {
            Thing prefab = Prefab.Find(prefabName);
            if (prefab == null)
            {
                return null;
            }
            T made = OnServer.Create<T>(prefab, position, Quaternion.identity);
            if (made != null && made.BuildStates != null && made.BuildStates.Count > 0)
            {
                made.CurrentBuildStateIndex = made.BuildStates.Count - 1;
            }
            return made;
        }

        /// <summary>A new site with a deposit by the game's own discovery, at the nodes nearest the orbit first.</summary>
        private SpaceMapNode Discover(SpaceMap map)
        {
            foreach (SpaceMapNode parent in map.Nodes.Where(n => n?.DiscoverData != null && n.DiscoverData.SiteGenerations.Count > 0).OrderBy(n => RouteDeltaV(n)).ToList())
            {
                for (int tries = 0; tries < 6; tries++)
                {
                    HashSet<SpaceMapNode> before = new HashSet<SpaceMapNode>(CometMap.Sites());
                    parent.DiscoverPoints = parent.DiscoverData.Difficulty;
                    SpaceMapNode made = CometMap.Sites().FirstOrDefault(s => !before.Contains(s));
                    if (made == null)
                    {
                        break;
                    }
                    if (made.Deposit != null && made.Deposit.TotalOreAtLocation > 0)
                    {
                        return made;
                    }
                }
            }
            Fail("discovery made no site on the map");
            return null;
        }

        private static double RouteDeltaV(SpaceMapNode node)
        {
            if (node == SpaceMap.Current.EntryNode || !SpaceMapPathFinder.GetPath(node, SpaceMap.Current.EntryNode, out List<Assets.Scripts.Serialization.NodeTransit> path) || path == null)
            {
                return 0.0;
            }
            return path.Sum(t => (double)t.GetDistance());
        }
    }

    /// <summary>
    /// -CometLand, phase "vanilla": the save phase make wrote, a comet falling and one parked, loaded by the game
    /// without the mod. Only the game's own types. The rocket must be there and idle, the telescope there, the
    /// sky the world's own with no comet in it.
    /// </summary>
    internal sealed class CometLandVanilla
    {
        private readonly ManualLogSource _log;
        private bool _done;

        private CometLandVanilla(ManualLogSource log)
        {
            _log = log;
        }

        public static CometLandVanilla FromEnvironment(ManualLogSource log) =>
            Environment.GetEnvironmentVariable("TR_LIVECHECK_COMETLAND") == "vanilla" ? new CometLandVanilla(log) : null;

        public void Step()
        {
            if (_done || GameManager.GameTickCount < 5)
            {
                return;
            }
            _done = true;
            long.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_COMETLAND_TELESCOPE"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long telescopeId);
            long.TryParse(Environment.GetEnvironmentVariable("TR_LIVECHECK_COMETLAND_ROCKET"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long rocketId);
            GroundTelescope telescope = Referencable.Find<GroundTelescope>(telescopeId);
            Rocket rocket = Referencable.Find<Rocket>(rocketId);
            int bodies = OrbitalSimulation.IsValid ? OrbitalSimulation.GetCelestialCount() : -1;
            int comets = 0;
            for (int i = 0; i < Math.Max(0, bodies); i++)
            {
                if (OrbitalSimulation.GetCelestial(i)?.Id?.StartsWith("TRComet", StringComparison.Ordinal) == true)
                {
                    comets++;
                }
            }
            bool idle = rocket != null && Enum.IsDefined(typeof(RocketMode), rocket.RocketMode) && rocket.RocketMode != RocketMode.Mine;
            bool pass = telescope != null && rocket != null && idle && bodies > 0 && comets == 0;
            _log.LogInfo(string.Format(CultureInfo.InvariantCulture, "LiveCheck: land vanilla {0} | telescope {1} {2} | rocket {3} {4}, mode {5} | sky bodies {6}, comets among them {7} | tick {8}",
                pass ? "PASS" : "FAIL", telescopeId, telescope != null ? "loaded" : "MISSING", rocketId, rocket != null ? "loaded" : "MISSING", rocket != null ? rocket.RocketMode.ToString() : "-",
                bodies, comets, GameManager.GameTickCount));
        }
    }
}
